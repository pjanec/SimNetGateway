using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>T-064 (DD-026): the pivot voice messages become the Utterance, Report and Order event topics - every field mapped (HROT's numbers are the contract enums' numbers), the names bounded, written once on the world participant whatever the seats, a late client hears nothing old.</summary>
public class DpsVoiceTests
{
    [Fact]
    public void Every_field_reaches_the_three_topics_and_an_out_of_range_number_is_none()
    {
        uint domain = Watch.NewDomain();
        using var u = new Watch.Watch<D.Utterance>(domain, "station-1", x => Guid.NewGuid().ToString());
        using var r = new Watch.Watch<D.Report>(domain, "station-1", x => Guid.NewGuid().ToString());
        using var o = new Watch.Watch<D.Order>(domain, "station-1", x => Guid.NewGuid().ToString());
        using var rig = new Rig(domain);

        rig.Send(new HrotUtterance(3001, "CallContact", 3002, 12.5));
        rig.Send(new HrotUtterance(3001, new string('k', 40), 0, 13.0));
        rig.Send(new HrotReport(3004, 0, 2, 3, 4, 3, 2, 65, 2, 3, 5, 6, true, 14.0));
        rig.Send(new HrotReport(3004, 3002, 99, 0, 0, 99, 99, 0, 99, 99, 0, 0, false, 15.0));
        rig.Send(new HrotOrder(3002, 3004, "Attack", 16.0));
        rig.Send(new HrotOrder(3002, 3004, new string('n', 50), 17.0));

        Assert.True(u.Until(w => w.Events.Count == 2) && r.Until(w => w.Events.Count == 2) && o.Until(w => w.Events.Count == 2));
        var u1 = u.Events[0];
        Assert.Equal((12.5, 3001, "CallContact", 3002), (u1.SimTime, u1.SpeakerId, u1.Kind, u1.TargetId));
        Assert.Equal(new string('k', 24), u.Events[1].Kind);
        var r1 = r.Events[0];
        Assert.Equal((14.0, 3004, 0, D.ReportKind.ReportKindSpot, (byte)3, (byte)4, D.ElementSize.ElementSizeSquad, D.ElementActivity.ElementActivityMoving, (byte)65, D.StatusColour.StatusColourAmber, D.StatusColour.StatusColourRed, (byte)5, (byte)6, true),
            (r1.SimTime, r1.FromId, r1.ToId, r1.Kind, r1.Sub, r1.Count, r1.Size, r1.Activity, r1.Equipment, r1.Strength, r1.Ammo, r1.Wia, r1.Kia, r1.Synthetic));
        var r2 = r.Events[1];
        Assert.Equal((D.ReportKind.ReportKindNone, D.ElementSize.ElementSizeUnknown, D.ElementActivity.ElementActivityUnknown, D.StatusColour.StatusColourUnknown, D.StatusColour.StatusColourUnknown),
            (r2.Kind, r2.Size, r2.Activity, r2.Strength, r2.Ammo));
        Assert.Equal((16.0, 3002, 3004, "Attack"), (o.Events[0].SimTime, o.Events[0].FromId, o.Events[0].ToId, o.Events[0].Name));
        Assert.Equal(new string('n', 32), o.Events[1].Name);
    }

    /// <summary>The contract's enums are numbered as HROT's (the module keeps the numbers): a change of either side breaks here.</summary>
    [Fact]
    public void The_contract_enums_are_numbered_as_the_numbers_the_pivot_carries()
    {
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], Enum.GetValues<D.ReportKind>().Select(v => (int)v).Order().ToArray());
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<D.ElementSize>().Select(v => (int)v).Order().ToArray());
        Assert.Equal([0, 1, 2, 3, 4, 5], Enum.GetValues<D.ElementActivity>().Select(v => (int)v).Order().ToArray());
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<D.StatusColour>().Select(v => (int)v).Order().ToArray());
    }

    [Fact]
    public void An_event_is_written_once_on_the_world_participant_whatever_the_seats()
    {
        uint domain = Watch.NewDomain();
        using var one = new Watch.Watch<D.Order>(domain, "station-1-1", x => x.Name);
        using var two = new Watch.Watch<D.Order>(domain, "station-1-2", x => x.Name);
        using var rig = new Rig(domain, seats: 2);
        int written = 0;
        rig.Transport.Order.Tap = _ => written++;

        rig.Send(new HrotOrder(1, 2, "Attack", 1.0));

        Assert.Equal(1, written);
        foreach (var watch in new[] { one, two })
        {
            Assert.True(watch.Until(w => w.Events.Count >= 1));
            Thread.Sleep(200);
            watch.Pump();
            Assert.Single(watch.Events);
        }
    }

    [Fact]
    public void A_late_client_hears_no_old_traffic()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        rig.Send(new HrotUtterance(1, "Talk", 0, 1.0));
        rig.Send(new HrotOrder(1, 2, "Attack", 1.0));

        using var late = new Watch.Watch<D.Order>(domain, "station-1", x => x.Name);
        Thread.Sleep(400);
        late.Pump();
        Assert.Empty(late.Events);   // volatile events: only what is written after it joined
        rig.Send(new HrotOrder(1, 2, "Move", 2.0));
        Assert.True(late.Until(w => w.Events.Count == 1));
        Assert.Equal("Move", late.Events[0].Name);
    }
}
