using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>DD-032 (T-077): the Carried topic in the plugin - every field copied, a change is sent, the carriage ends with a dispose, and an entity that leaves the world takes its carriage with it.</summary>
public class DpsCarriedTests
{
    private static string Id(int id) => id.ToString();

    private static HrotEntityState State(int id) =>
        new(id, 616, 0, "X", 2, 0, new HrotVec3(10, 20, 3), HrotQuat.Identity, HrotVec3.Zero, HrotVec3.Zero, 100, 100, 1.0);

    [Fact]
    public void A_carriage_is_copied_each_field_alone_is_a_change_and_the_end_of_it_disposes_the_instance()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.Carried>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(State(4002));

        rig.Send(new HrotCarried(4002, 4001, 3, false));
        Assert.True(watch.Until(w => w.Alive.TryGetValue("4002", out var c) && c.CarrierId == 4001));
        var c = watch.Alive["4002"];
        Assert.Equal((4001, (byte)3, false), (c.CarrierId, c.SeatIndex, c.Stowed));

        rig.Send(new HrotCarried(4002, 4001, 3, true));   // stowed alone
        Assert.True(watch.Until(w => w.Alive["4002"].Stowed));
        rig.Send(new HrotCarried(4002, 4001, 9, true));   // the seat alone
        Assert.True(watch.Until(w => w.Alive["4002"].SeatIndex == 9));
        rig.Send(new HrotCarried(4002, 4003, 9, true));   // the carrier alone
        Assert.True(watch.Until(w => w.Alive["4002"].CarrierId == 4003));

        rig.Send(new HrotCarried(4002, 0, 0, false));     // dropped
        Assert.True(watch.Until(w => !w.Alive.ContainsKey("4002")));
    }

    /// <summary>DD-033 (T-078): a cloud is copied field by field, changes are samples, the end disposes, and the pivot cloud kinds mirror the contract's.</summary>
    [Fact]
    public void A_cloud_is_copied_field_by_field_and_disposed_when_it_ends()
    {
        Assert.Equal(Enum.GetNames<HrotCloudKind>().Select(n => "CloudKind" + n), Enum.GetNames<D.CloudKind>());

        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.Cloud>(domain, "station-1", e => Id(e.CloudId));
        using var rig = new Rig(domain);

        rig.Send(new HrotCloud(5001, HrotCloudKind.TearGas, 150.0, new HrotVec3(10, 20, 3), new HrotVec3(2, -1, 0), 8f, 6f, 0.25f, 100.0, 90f));
        Assert.True(watch.Until(w => w.Alive.ContainsKey("5001")));
        var c = watch.Alive["5001"];
        Assert.Equal((D.CloudKind.CloudKindTearGas, 150.0, 10.0, 20.0, 3.0, 2f, -1f, 0f, 8f, 6f, 0.25f, 100.0, 90f),
            (c.Kind, c.SimTime, c.Centre.X, c.Centre.Y, c.Centre.Z, c.Drift.X, c.Drift.Y, c.Drift.Z, c.Radius, c.Height, c.SightTransmittance, c.BornAt, c.Seconds));

        rig.Send(new HrotCloud(5001, HrotCloudKind.TearGas, 151.0, new HrotVec3(12, 19, 3), new HrotVec3(-3, 0, 0), 8f, 6f, 0.25f, 100.0, 90f));   // the wind turned
        Assert.True(watch.Until(w => w.Alive["5001"].Drift.X == -3f));

        rig.Send(new HrotCloudEnded(5001));
        Assert.True(watch.Until(w => !w.Alive.ContainsKey("5001")));
    }
}
