using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// RprUpdateRequested (R-105, DD-008 section 3.4): another federate asked for the attributes of one of our objects; the gateway
/// answers with the full update it also sends as a heartbeat. The module is RprUpdateRequestedResponder.
/// </summary>
public class UpdateRequestedTests
{
    private static (GatewayHarness H, RprObjectHandle Handle, NedGeoSpatial Geo) Published(double speed = 0)
    {
        var h = new GatewayHarness();
        var geo = T.Geo(T.Hawk, speed: speed);
        var entity = T.PublishNed(h, T.Hawk, "Hawk", 200, geo);
        h.Run(0.5);
        h.ClearOutput();
        return (h, h.HandleOf(entity), geo);
    }

    [Fact]
    public void A_request_for_a_published_entity_is_answered_with_one_full_update()
    {
        var (h, handle, geo) = Published();

        h.RprStep(new RprUpdateRequested(handle));
        h.Steps(2);

        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(handle, update.Handle);
        Assert.Equal("Hawk", update.Marking);
        Assert.Equal(T.Dis(200), update.EntityType);
        Assert.Equal(RprForceId.Friendly, update.ForceId);
        Assert.Equal(new DisEntityId(1, 3001, 1), update.EntityIdentifier);
        Assert.Equal(NedRprConversions.ToWorldLocation(T.Position(geo)), update.WorldLocation);
        Assert.Equal(NedRprConversions.ToRprOrientation(T.OrientationOf(geo)), update.Orientation);
        Assert.NotNull(update.DeadReckoningAlgorithm);
        Assert.Equal(1, h.RprOut.Count); // nothing else
    }

    [Fact]
    public void The_answer_is_the_same_full_update_the_heartbeat_sends()
    {
        var (h, handle, _) = Published();
        h.RprStep(new RprUpdateRequested(handle));
        h.Steps(2);
        var requested = Assert.Single(h.RprSent<RprObjectUpdate>());
        h.ClearOutput();

        h.Run(6); // past the heartbeat period

        var heartbeat = h.RprSent<RprObjectUpdate>().First();
        Assert.Equal(heartbeat, requested);
    }

    [Fact]
    public void Every_request_is_answered_and_each_answer_is_a_complete_update()
    {
        var (h, handle, _) = Published();

        h.RprStep(new RprUpdateRequested(handle));
        h.RprStep(new RprUpdateRequested(handle));

        var updates = h.RprSent<RprObjectUpdate>();
        Assert.Equal(2, updates.Count);
        Assert.Equal(updates[0], updates[1]);
        Assert.NotNull(updates[1].Marking);
    }

    [Fact]
    public void A_request_counts_as_a_send_for_the_heartbeat()
    {
        var (h, handle, _) = Published();
        h.Run(4); // 4.5 s after the last update: the heartbeat (5 s) is close
        h.ClearOutput();

        h.RprStep(new RprUpdateRequested(handle));
        h.Steps(2);
        h.ClearOutput();
        h.Run(2); // the heartbeat would have fired by now without the answer

        Assert.Empty(h.RprSent<RprObjectUpdate>());
    }

    [Fact]
    public void A_request_for_an_unknown_handle_or_an_RPR_origin_entity_sends_nothing()
    {
        var (h, _, _) = Published();
        h.RprStep(T.RprFull(0x9001, "Wolf", 400, 50.2, 14.2, 200, 270, 0, 5)); // an RPR-origin entity with a handle
        h.ClearOutput();

        h.RprStep(new RprUpdateRequested(new RprObjectHandle(0x7777))); // nobody's
        h.RprStep(new RprUpdateRequested(new RprObjectHandle(0x9001))); // the peer's own entity: not ours to publish
        h.Steps(2);

        Assert.Empty(h.RprSent<RprObjectUpdate>());
    }

    [Fact]
    public void A_request_for_an_entity_that_is_still_pending_sends_nothing()
    {
        var h = new GatewayHarness { DeliverRti = false };
        h.NedStep(T.Master(T.Hawk, "Hawk", 200));
        h.NedStep(T.Geo(T.Hawk));
        h.Steps(2);
        h.ClearOutput();

        h.RprStep(new RprUpdateRequested(new RprObjectHandle(SimulatedRti.FirstHandle)));
        h.Steps(2);

        Assert.Empty(h.RprSent<RprObjectUpdate>());
    }
}
