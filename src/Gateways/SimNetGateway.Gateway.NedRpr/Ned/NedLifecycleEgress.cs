using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Rule "Dispose": an entity that NED knows was removed (members and units are not individuals: the formation egress tells NED
/// about them). Rule "Withdraw": an entity that NED knows as an individual and that becomes a member of a unit (its aggregate
/// arrived) is disposed of with its individual id, which is released; from then on it appears only inside the formation.
/// </summary>
public sealed class NedLifecycleEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly SourceId _ned;
    private readonly IOutboundChannel<INedMessage> _output;

    public NedLifecycleEgress(PivotSchema schema, NedRprMaps maps, NedRprSources sources, IOutboundChannel<INedMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _ned = sources.Ned;
        _output = output;
    }

    public string Name => nameof(NedLifecycleEgress);

    public void Register(ModuleSetup setup)
    {
        setup.Consumes(_schema.AggregateMemberIds);
        setup.OnAttributesChanged(_schema.Parent)
            .Named("Withdraw")
            .When(t => t.Entity.Origin != _ned && NedStructure.IsMember(_schema, t.Entity))
            .Do(t =>
            {
                if (_maps.NedEntity.TryGetKey(t.Id, out ulong id))
                {
                    _output.Send(new NedEntityDisposed(id));
                    _maps.NedEntity.Unbind(t.Id);
                }
            });
        setup.OnEntityRemoved()
            .Named("Dispose")
            .When(t => t.Entity.Origin != _ned && !NedStructure.IsStructural(_schema, t.Entity))
            .Do(t =>
            {
                // The key is still resolvable during the removal dispatch (released at the end of the drain).
                if (_maps.NedEntity.TryGetKey(t.Id, out ulong id))
                {
                    _output.Send(new NedEntityDisposed(id));
                }
            });
    }
}
