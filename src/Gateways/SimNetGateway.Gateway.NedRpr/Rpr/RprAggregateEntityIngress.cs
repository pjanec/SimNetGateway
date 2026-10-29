using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// RprAggregateEntity (N:1, R-041): resolves or creates the unit of the handle (origin rpr, no position), stores its marking,
/// type and force and the member list it received (<c>RprAggregate.MemberIds</c>, kept as received so that members that arrive
/// later can be linked, see <see cref="RprAggregateLinker"/>), and in the same transaction links every listed member that is
/// already known by its DIS entity identifier (<c>Structure.Parent</c> = the unit, <c>Structure.MemberIndex</c> = its position
/// in the list). Members that the list no longer names are unlinked. Identifiers that resolve to nothing are left alone; an
/// identifier that names an entity of another origin than RPR is not linked (it is the gateway's own publication).
/// </summary>
public sealed class RprAggregateEntityIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly TypeCatalog _catalog;
    private readonly List<EntityRecord> _scratch = new();

    public RprAggregateEntityIngress(PivotSchema schema, NedRprMaps maps, TypeCatalog catalog)
    {
        _schema = schema;
        _maps = maps;
        _catalog = catalog;
    }

    public string Name => nameof(RprAggregateEntityIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.Name, _schema.TypeTkb, _schema.Force, _schema.AggregateMemberIds, _schema.Parent, _schema.MemberIndex);
        setup.OnMessage<RprAggregateEntity>(Handle);
    }

    private void Handle(in RprAggregateEntity m, IngressContext ctx)
    {
        var resolution = IngressEntities.Resolve(ctx, _maps.RprHandle, m.Handle, create: true, out var unit);
        if (resolution == EntityResolution.Echo)
        {
            return;
        }

        var changes = ctx.Changes;
        var memberIds = m.MemberIds ?? Array.Empty<DisEntityId>();
        changes.Set(unit, _schema.Name, AttrValue.From(m.Marking ?? string.Empty));
        if (!_catalog.TryToTkb(m.EntityType, out int tkb))
        {
            tkb = TypeCatalog.DefaultTkb;
            if (_catalog.NoteUnknownDis(m.EntityType))
            {
                ctx.Diagnostics.Warn(Name, $"Unknown DIS entity type {m.EntityType}; using TKB type {TypeCatalog.DefaultTkb}.");
            }
        }

        changes.Set(unit, _schema.TypeTkb, AttrValue.From(tkb));
        if (NedRprConversions.TryForceFromRpr(m.ForceId, out int force))
        {
            changes.Set(unit, _schema.Force, AttrValue.From(force));
        }

        if (!SameList(ctx, unit, memberIds))
        {
            changes.Set(unit, _schema.AggregateMemberIds, AttrValue.FromObject((DisEntityId[])memberIds.Clone()));
        }

        var linked = new HashSet<EntityId>();
        for (int index = 0; index < memberIds.Length; index++)
        {
            if (!_maps.RprEntityId.TryResolve(changes, memberIds[index], out var member) || member == unit || linked.Contains(member))
            {
                continue;
            }

            var record = ctx.State.Find(member);
            if (record is null || record.Origin != ctx.Source || record.Has(_schema.AggregateMemberIds))
            {
                continue; // unknown here (or pending), the gateway's own, or itself a unit
            }

            linked.Add(member);
            if (AlreadyLinked(record, unit, index))
            {
                // a re-sent aggregate (an HLA unit's heartbeat) re-asserts the membership; it is the UNIT that spoke, so the member is not
                // written (R-068: staleness counts only the writes of the entity itself - a silent member of a live unit must still age out)
                continue;
            }

            changes.Set(member, _schema.Parent, AttrValue.From(unit));
            changes.Set(member, _schema.MemberIndex, AttrValue.From(index));
        }

        if (resolution == EntityResolution.Existing)
        {
            ctx.State.Query().ReferencedBy(_schema.Parent, unit).CopyTo(_scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                var record = _scratch[i];
                if (!linked.Contains(record.Id))
                {
                    changes.Clear(record.Id, _schema.Parent);
                    changes.Clear(record.Id, _schema.MemberIndex);
                }
            }

            _scratch.Clear();
        }
    }

    /// <summary>True when the member is already linked to this unit at this position.</summary>
    private bool AlreadyLinked(EntityRecord member, EntityId unit, int index) =>
        member.TryGet(_schema.Parent, out var parent) && !parent.IsNone && parent.AsEntity() == unit &&
        member.TryGet(_schema.MemberIndex, out var stored) && !stored.IsNone && stored.AsInt() == index;

    /// <summary>True when the unit already holds exactly this member list (the stored object is then left untouched).</summary>
    private bool SameList(IngressContext ctx, EntityId unit, DisEntityId[] received)
    {
        var record = ctx.State.Find(unit);
        if (record is null || !record.TryGet(_schema.AggregateMemberIds, out var stored))
        {
            return false;
        }

        var current = stored.AsObject<DisEntityId[]>();
        if (current.Length != received.Length)
        {
            return false;
        }

        for (int i = 0; i < current.Length; i++)
        {
            if (current[i] != received[i])
            {
                return false;
            }
        }

        return true;
    }
}
