using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>NedGeoSpatial: geodetic degrees to ECEF position, geocentric orientation and ECEF velocity, plus DR parameters.</summary>
public sealed class NedGeoSpatialIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;

    public NedGeoSpatialIngress(PivotSchema schema, NedRprMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(NedGeoSpatialIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.Position, _schema.Orientation, _schema.Velocity, _schema.DrAlgorithm, _schema.StateTime);
        setup.OnMessage<NedGeoSpatial>(Handle);
    }

    private void Handle(in NedGeoSpatial m, IngressContext ctx)
    {
        var resolution = IngressEntities.Resolve(ctx, _maps.NedEntity, m.Id, create: true, out var id);
        if (resolution == EntityResolution.Echo)
        {
            return;
        }

        NedRprConversions.GeoToPivot(in m, out var position, out var orientation, out var velocity);
        var algorithm = m.Speed > 0 ? DisDeadReckoning.Fpw : DisDeadReckoning.Static;
        var changes = ctx.Changes;
        changes.Set(id, _schema.Position, AttrValue.From(position));
        changes.Set(id, _schema.Orientation, AttrValue.From(orientation));
        changes.Set(id, _schema.Velocity, AttrValue.From(velocity));
        changes.Set(id, _schema.DrAlgorithm, AttrValue.From((long)algorithm));
        changes.Set(id, _schema.StateTime, AttrValue.From(ctx.SimTime));
    }
}
