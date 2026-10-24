using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>Rule "GeoSpatial": position, orientation and velocity back to geodetic degrees, for entities NED already knows.</summary>
public sealed class NedSpatialEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly SourceId _ned;
    private readonly NedIntroductions _introductions;
    private readonly IOutboundChannel<INedMessage> _output;

    public NedSpatialEgress(PivotSchema schema, NedRprMaps maps, NedRprSources sources, NedIntroductions introductions,
        IOutboundChannel<INedMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _ned = sources.Ned;
        _introductions = introductions;
        _output = output;
    }

    public string Name => nameof(NedSpatialEgress);

    public void Register(ModuleSetup setup) =>
        setup.OnAttributesChanged(_schema.Position, _schema.Orientation, _schema.Velocity)
            .Named("GeoSpatial")
            .When(t => t.Entity.Origin != _ned && !NedStructure.IsStructural(_schema, t.Entity) && !_introductions.IsCurrent(t.Id, t.Correlation))
            .Do(t =>
            {
                if (_maps.NedEntity.TryGetKey(t.Id, out ulong id))
                {
                    NedWire.SendGeoSpatial(_schema, _output, id, t.Entity);
                }
            });
}
