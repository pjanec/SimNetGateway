using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotOrigin: sets the geodetic frame (DD-006 6.4). The first valid origin and every change publish <c>Hrot.FrameRebased</c>,
/// after which the replica egress re-sends every replica in the new frame; the ECEF values of the pivot are not touched.
/// An unacceptable origin (not finite, out of range, a zero origin that means "not set") aborts the message.
/// </summary>
public sealed class HrotOriginIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly GeoFrame _geo;

    public HrotOriginIngress(PivotSchema schema, HrotDisServices services)
    {
        _schema = schema;
        _geo = services.Geo;
    }

    public string Name => nameof(HrotOriginIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.FrameGeneration);
        setup.Produces(_schema.FrameRebased);
        setup.OnMessage<HrotOrigin>(Handle);
    }

    private void Handle(in HrotOrigin m, IngressContext ctx)
    {
        if (!_geo.TryApply(in m, out var problem))
        {
            if (problem is not null)
            {
                ctx.Abort(problem);
            }

            return;
        }

        var bag = ctx.RentPayload();
        bag.Set(_schema.FrameGeneration, AttrValue.From((long)_geo.Generation));
        ctx.Publish(_schema.FrameRebased, bag);
    }
}
