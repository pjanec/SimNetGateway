using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>Rule "Damage": the damage level of entities NED already knows.</summary>
public sealed class NedDamageEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly SourceId _ned;
    private readonly NedIntroductions _introductions;
    private readonly IOutboundChannel<INedMessage> _output;

    public NedDamageEgress(PivotSchema schema, NedRprMaps maps, NedRprSources sources, NedIntroductions introductions,
        IOutboundChannel<INedMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _ned = sources.Ned;
        _introductions = introductions;
        _output = output;
    }

    public string Name => nameof(NedDamageEgress);

    public void Register(ModuleSetup setup) =>
        setup.OnAttributesChanged(_schema.Damage)
            .Named("Damage")
            .When(t => t.Entity.Origin != _ned && !NedStructure.IsStructural(_schema, t.Entity) && !_introductions.IsCurrent(t.Id, t.Correlation))
            .Do(t =>
            {
                if (_maps.NedEntity.TryGetKey(t.Id, out ulong id))
                {
                    NedWire.SendDamage(_schema, _output, id, t.Entity);
                }
            });
}
