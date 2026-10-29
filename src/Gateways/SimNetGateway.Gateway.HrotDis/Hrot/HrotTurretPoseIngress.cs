using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotTurretPose (stage H4): stores the pose of one articulated part in <c>Articulation.Parts</c> (the part is replaced by id or
/// added). Nothing is sent to DIS yet. A pose for an unknown entity is ignored.
/// </summary>
public sealed class HrotTurretPoseIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;

    public HrotTurretPoseIngress(PivotSchema schema, HrotDisMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(HrotTurretPoseIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.ArticulationParts);
        setup.Consumes(_schema.ArticulationParts); // read to merge the part
        setup.OnMessage<HrotTurretPose>(Handle);
    }

    private void Handle(in HrotTurretPose m, IngressContext ctx)
    {
        if (!double.IsFinite(m.Azimuth) || !double.IsFinite(m.Elevation))
        {
            ctx.Abort($"turret pose of entity {m.Id}: a value is not finite");
            return;
        }

        if (HrotIngress.Resolve(ctx, _maps, m.Id, create: false, out var id) != EntityResolution.Existing)
        {
            return;
        }

        // One message is one transaction and this is the only writer of the attribute, so the stored value is the current one.
        ArticulationParts current = ArticulationParts.Empty;
        if (ctx.State.Find(id) is { } record && record.TryGet(_schema.ArticulationParts, out var stored))
        {
            current = stored.AsObject<ArticulationParts>();
        }

        ctx.Changes.Set(id, _schema.ArticulationParts, AttrValue.FromObject(current.With(new ArticulationPart(m.PartId, m.Azimuth, m.Elevation))));
    }
}
