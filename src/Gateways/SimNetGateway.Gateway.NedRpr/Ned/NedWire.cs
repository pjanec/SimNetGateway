using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>Messages built from the pivot that more than one NED egress module sends.</summary>
internal static class NedWire
{
    public static void SendGeoSpatial(PivotSchema s, IOutboundChannel<INedMessage> output, ulong id, EntityRecord e)
    {
        if (!e.TryGet(s.Position, out var position))
        {
            return;
        }

        var orientation = e.TryGet(s.Orientation, out var o) ? o.AsVec3() : Vec3.Zero;
        var velocity = e.TryGet(s.Velocity, out var v) ? v.AsVec3() : Vec3.Zero;
        output.Send(NedRprConversions.PivotToGeo(id, position.AsVec3(), orientation, velocity));
    }

    public static void SendDamage(PivotSchema s, IOutboundChannel<INedMessage> output, ulong id, EntityRecord e)
    {
        if (e.TryGet(s.Damage, out var damage))
        {
            output.Send(new NedEntityDamage(id, (int)damage.AsInt()));
        }
    }
}
