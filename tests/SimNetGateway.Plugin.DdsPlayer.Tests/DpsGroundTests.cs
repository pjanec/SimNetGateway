using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>
/// T-074 (DD-030): the pivot damage records' times and craters become DPS samples - every appended field copied exactly (the 0 = from-a-save rule included), a crater cell is ONE keyed state instance with
/// its craters in the order HROT sent, change is written once on the world participant whatever the seats, a render job holds the write, a late client gets the current state, an emptied cell is disposed once.
/// </summary>
public class DpsGroundTests
{
    private static HrotCrater Crater(double x, double y, double z, double born, string kind = "Small", float radius = 2f) => new(new HrotVec3(x, y, z), radius, 0.75f, 0.25f, born, kind);

    [Fact]
    public void A_building_samples_carry_hrots_times_and_blast_points_exactly_and_zero_stays_zero()
    {
        uint domain = Watch.NewDomain();
        using var damage = new Watch.Watch<D.BuildingDamage>(domain, "station-1", b => b.Building);
        using var rig = new Rig(domain);

        rig.Send(new HrotBuildingDamage("range/H2", true,
            [new HrotBreach(0, 4f, 4.6f, 1.1f, 1.6f, "Hole", 312.4, new HrotVec3(10.5, -20.25, 3.75)), new HrotBreach(2, 1f, 3f, 0f, 2.2f, "Passage", 0d, HrotVec3.Zero)],
            [new HrotWindowDamage(1, 0, 1, 40, 50, 60, 70, 55.5), new HrotWindowDamage(3, 2, 3, 0, 0, 0, 0, 0d)], 401.25));
        rig.Send(new HrotBuildingDamage("range/H1", false, [], [new HrotWindowDamage(0, 0, 3, 0, 0, 0, 0, 7.25)]));   // CollapsedAt defaults to 0

        Assert.True(damage.Until(w => w.Alive.Count == 2));
        var h2 = damage.Alive["range/H2"];
        Assert.Equal((true, 401.25), (h2.Collapsed, h2.CollapsedAt));
        Assert.Equal([(312.4, 10.5, -20.25, 3.75), (0d, 0d, 0d, 0d)], h2.Breaches.Select(b => (b.MadeAt, b.Blast.X, b.Blast.Y, b.Blast.Z)).ToArray());
        Assert.Equal([55.5, 0d], h2.Windows.Select(w => w.HitAt).ToArray());
        var h1 = damage.Alive["range/H1"];
        Assert.Equal((false, 0d, 7.25), (h1.Collapsed, h1.CollapsedAt, h1.Windows[0].HitAt));
    }

    [Fact]
    public void A_crater_cell_is_one_instance_with_every_field_copied_in_hrots_order_and_unknown_kinds_are_unknown()
    {
        uint domain = Watch.NewDomain();
        using var cells = new Watch.Watch<D.GroundDamage>(domain, "station-1", g => g.Cell);
        using var rig = new Rig(domain);

        rig.Send(new HrotGroundDamage("1,-1", [Crater(70, -10, 7.5, 312.4, "Large", 3.5f), Crater(65, -20, 8.25, 100.0), Crater(66, -21, 0, 0d, "Huge")]));
        rig.Send(new HrotGroundDamage("0,0", [Crater(1, 2, 3, 4)]));

        Assert.True(cells.Until(w => w.Alive.Count == 2));
        var c = cells.Alive["1,-1"];
        Assert.Equal(
            [(70.0, -10.0, 7.5, 3.5f, 0.75f, 0.25f, 312.4, D.CraterKind.CraterKindLarge), (65.0, -20.0, 8.25, 2f, 0.75f, 0.25f, 100.0, D.CraterKind.CraterKindSmall),
             (66.0, -21.0, 0.0, 2f, 0.75f, 0.25f, 0.0, D.CraterKind.CraterKindUnknown)],   // a kind HROT appends later is not another kind
            c.Craters.Select(x => (x.Centre.X, x.Centre.Y, x.Centre.Z, x.Radius, x.Depth, x.Rim, x.BornAt, x.Kind)).ToArray());
        Assert.Single(cells.Alive["0,0"].Craters);
    }

    [Fact]
    public void The_bounds_are_16_craters_and_a_long_key_is_cut_and_a_cell_beyond_them_is_warned_about_once()
    {
        Assert.Equal((16, 32), (DpsGroundOutbound.MaxCraters, DpsGroundOutbound.MaxKey));
        uint domain = Watch.NewDomain();
        var log = new List<string>();
        using var cells = new Watch.Watch<D.GroundDamage>(domain, "station-1", g => g.Cell);
        using var rig = new Rig(domain, log: log.Add);
        string longKey = "1," + new string('9', 60);

        rig.Send(new HrotGroundDamage("0,0", [.. Enumerable.Range(0, 16).Select(i => Crater(i, i, 0, i))]));   // exactly at the bound: no warning
        rig.Send(new HrotGroundDamage("5,5", [.. Enumerable.Range(0, 20).Select(i => Crater(i, i, 0, i))]));
        rig.Send(new HrotGroundDamage("5,5", [.. Enumerable.Range(0, 21).Select(i => Crater(i, i, 0, i))]));   // the same cell again: no second line
        rig.Send(new HrotGroundDamage(longKey, [Crater(1, 1, 0, 1)]));

        Assert.True(cells.Until(w => w.Alive.Count == 3));
        Assert.Equal((16, 16), (cells.Alive["0,0"].Craters.Count, cells.Alive["5,5"].Craters.Count));
        Assert.Equal(15.0, cells.Alive["5,5"].Craters[15].Centre.X);   // the first ones are kept
        Assert.Contains(longKey[..32], cells.Alive.Keys);
        var warnings = log.Where(l => l.StartsWith("dps: warning: ground cell")).ToList();
        Assert.Single(warnings);
        Assert.Contains("'5,5' has 20 craters; only the first 16 are sent", warnings[0]);
    }

    [Fact]
    public void A_late_client_gets_the_current_craters_and_an_emptied_cell_is_disposed_once()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var writes = new List<string>();
        rig.Transport.GroundDamage.Tap = g => writes.Add(g.Cell);
        rig.Send(new HrotGroundDamage("2,2", [Crater(130, 130, 0, 10)]));
        rig.Send(new HrotGroundDamage("2,2", [Crater(130, 130, 0, 10), Crater(131, 131, 0, 20)]));   // the state is the last value
        rig.Send(new HrotGroundDamage("3,3", [Crater(200, 200, 0, 30)]));
        rig.Send(new HrotGroundDamage("3,3", []));
        rig.Send(new HrotGroundDamage("3,3", []));   // already gone
        rig.Send(new HrotGroundDamage("9,9", []));   // never written
        Assert.Equal(["2,2", "2,2", "3,3"], writes);   // a disposal and an empty of an unknown cell write no sample

        using var late = new Watch.Watch<D.GroundDamage>(domain, "station-1", g => g.Cell);   // joins after the writes: TransientLocal
        Assert.True(late.Until(w => w.Alive.Count == 1));
        Assert.Equal([10.0, 20.0], late.Alive["2,2"].Craters.Select(c => c.BornAt).ToArray());

        rig.Send(new HrotGroundDamage("2,2", []));
        Assert.True(late.Until(w => w.Alive.Count == 0));
    }

    [Fact]
    public void Craters_are_written_once_on_the_world_participant_whatever_the_seats()
    {
        uint domain = Watch.NewDomain();
        using var one = new Watch.Watch<D.GroundDamage>(domain, "station-1-1", g => g.Cell);
        using var two = new Watch.Watch<D.GroundDamage>(domain, "station-1-2", g => g.Cell);
        using var rig = new Rig(domain, seats: 2);
        int written = 0;
        rig.Transport.GroundDamage.Tap = _ => written++;

        rig.Send(new HrotGroundDamage("0,0", [Crater(1, 1, 0, 1)]));

        Assert.Equal(1, written);
        foreach (var watch in new[] { one, two })
        {
            Assert.True(watch.Until(w => w.Events.Count >= 1));
            Thread.Sleep(200);
            watch.Pump();
            Assert.Single(watch.Events);
        }
    }

    /// <summary>A render job holds the write like every counted world topic (nothing lands between a step and its FrameDone) and writes it, once, when the job ends. Mutation: a channel built without the hold.</summary>
    [Fact]
    public void A_render_job_holds_the_craters_and_the_damage_times_until_it_ends()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var barrier = ((DpsHostService)rig.Service!).Render.Barrier;
        var cells = new List<string>();
        var buildings = new List<string>();
        rig.Transport.GroundDamage.Tap = g => cells.Add(g.Cell);
        rig.Transport.BuildingDamage.Tap = b => buildings.Add(b.Building);

        barrier.Begin();
        rig.Send(new HrotGroundDamage("0,0", [Crater(1, 1, 0, 1)]));
        rig.Send(new HrotGroundDamage("0,0", [Crater(1, 1, 0, 1), Crater(2, 2, 0, 2)]));   // a newer write of an instance replaces the held one
        rig.Send(new HrotBuildingDamage("range/H1", true, [], [], 12.5));
        Assert.Empty(cells);
        Assert.Empty(buildings);
        Assert.Equal(1, rig.Transport.GroundDamage.Pending);

        barrier.End();

        Assert.Equal(["0,0"], cells);
        Assert.Equal(["range/H1"], buildings);
    }
}
