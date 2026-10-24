using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Processor (source <c>proc.aggregate</c>) that keeps the structure of RPR aggregates right when things arrive out of order
/// (C12, DD-001 3.7 "a processor links them when the parent arrives").
/// Rule "LinkLateMember": a new RPR entity whose DIS entity identifier is listed in the member list of an aggregate that is
/// already known is linked to it (<c>Structure.Parent</c>, <c>Structure.MemberIndex</c>) in one transaction. Rule
/// "UnlinkMembers": when an aggregate disappears its members are unlinked, so they are individuals again.
/// </summary>
public sealed class RprAggregateLinker : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly SourceId _rpr;
    private readonly SourceId _source;
    private readonly List<EntityRecord> _units = new();
    private readonly List<EntityRecord> _members = new();
    private IGatewayContext _gateway = null!;

    public RprAggregateLinker(PivotSchema schema, NedRprMaps maps, NedRprSources sources)
    {
        _schema = schema;
        _maps = maps;
        _rpr = sources.Rpr;
        _source = sources.ProcAggregate;
    }

    public string Name => "RprAggregateLinker";

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        setup.Produces(_schema.Parent, _schema.MemberIndex);
        setup.Consumes(_schema.AggregateMemberIds);
        setup.OnEntityCreated()
            .Named("LinkLateMember")
            .When(t => t.Entity.Origin == _rpr && !t.Entity.Has(_schema.AggregateMemberIds))
            .Do(LinkLateMember);
        setup.OnEntityRemoved()
            .Named("UnlinkMembers")
            .When(t => t.Entity.Origin == _rpr && t.Entity.Has(_schema.AggregateMemberIds))
            .Do(UnlinkMembers);
    }

    private void LinkLateMember(EntityTrigger t)
    {
        if (!_maps.RprEntityId.TryGetKey(t.Id, out var key))
        {
            return; // no DIS identifier (yet): nothing can name it
        }

        _gateway.State.Query().Has(_schema.AggregateMemberIds).CopyTo(_units);
        try
        {
            for (int u = 0; u < _units.Count; u++)
            {
                var ids = _units[u].Get(_schema.AggregateMemberIds).AsObject<DisEntityId[]>();
                for (int index = 0; index < ids.Length; index++)
                {
                    if (ids[index] != key)
                    {
                        continue;
                    }

                    var changes = _gateway.BeginChange(_source);
                    try
                    {
                        changes.Set(t.Id, _schema.Parent, AttrValue.From(_units[u].Id));
                        changes.Set(t.Id, _schema.MemberIndex, AttrValue.From(index));
                        changes.Commit();
                    }
                    finally
                    {
                        changes.Discard();
                    }

                    return;
                }
            }
        }
        finally
        {
            _units.Clear();
        }
    }

    private void UnlinkMembers(EntityTrigger t)
    {
        _gateway.State.Query().ReferencedBy(_schema.Parent, t.Id).CopyTo(_members);
        if (_members.Count == 0)
        {
            return;
        }

        var changes = _gateway.BeginChange(_source);
        try
        {
            for (int i = 0; i < _members.Count; i++)
            {
                changes.Clear(_members[i].Id, _schema.Parent);
                changes.Clear(_members[i].Id, _schema.MemberIndex);
            }

            changes.Commit();
        }
        finally
        {
            changes.Discard();
            _members.Clear();
        }
    }
}
