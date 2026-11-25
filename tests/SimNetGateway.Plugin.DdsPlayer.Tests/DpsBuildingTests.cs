using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>DPS round 2, building damage (DD-018 3.6a): the sample per building, its bounds, and its disposal.</summary>
public class DpsBuildingTests
{
    [Fact]
    public void A_building_damage_sample_maps_every_field_and_is_disposed_when_the_damage_goes()
    {
        uint domain = Watch.NewDomain();
        using var damage = new Watch.Watch<D.BuildingDamage>(domain, "station-1", b => b.Building);
        using var rig = new Rig(domain);

        rig.Send(new HrotBuildingDamage("range/H2", true,
            [new HrotBreach(0, 4f, 4.6f, 1.1f, 1.6f, HrotBreachKind.Hole), new HrotBreach(2, 1f, 3f, 0f, 2.2f, HrotBreachKind.Passage), new HrotBreach(3, 0f, 1f, 0f, 1f, HrotBreachKind.Unknown)],
            [new HrotWindowDamage(1, 0, 1, 40, 50, 60, 70), new HrotWindowDamage(3, 2, 3, 0, 0, 0, 0)]));
        rig.Send(new HrotBuildingDamage("range/H1", false, [], [new HrotWindowDamage(0, 0, 3, 0, 0, 0, 0)]));

        Assert.True(damage.Until(w => w.Alive.Count == 2));
        var h2 = damage.Alive["range/H2"];
        Assert.True(h2.Collapsed);
        Assert.Equal([(0, 4f, 4.6f, 1.1f, 1.6f, D.BreachKind.BreachKindHole), (2, 1f, 3f, 0f, 2.2f, D.BreachKind.BreachKindPassage), (3, 0f, 1f, 0f, 1f, D.BreachKind.BreachKindUnknown)],
            h2.Breaches.Select(b => (b.Panel, b.From, b.To, b.Bottom, b.Top, b.Kind)).ToArray());   // a kind HROT appends later is not another kind
        Assert.Equal([(1, 0, (byte)1, (byte)40, (byte)50, (byte)60, (byte)70), (3, 2, (byte)3, (byte)0, (byte)0, (byte)0, (byte)0)],
            h2.Windows.Select(w => (w.Panel, w.Opening, w.Hits, w.U0, w.V0, w.U1, w.V1)).ToArray());
        Assert.False(damage.Alive["range/H1"].Collapsed);

        // the damage of H2 grows: the same instance is replaced
        rig.Send(new HrotBuildingDamage("range/H2", true, [], []));   // collapsed and nothing else: still damage
        Assert.True(damage.Until(w => w.Alive["range/H2"].Breaches.Count == 0));

        // the damage goes (a reload): disposed; a building that was never written disposes nothing
        rig.Send(new HrotBuildingDamage("range/H2", false, [], []));
        Assert.True(damage.Until(w => w.Alive.Count == 1 && w.Alive.ContainsKey("range/H1")));
        rig.Send(new HrotBuildingDamage("range/Nowhere", false, [], []));
        damage.Pump();
        Assert.Single(damage.Alive);
    }

    /// <summary>
    /// DD-034 (T-075): every appended field reaches the DPS sample exactly - the breach's cause (mapped BY NAME, an unknown name is Unknown), kg, actor, centre and normal, the window's centre and normal, the
    /// collapse's cause, blast point and kg - and a cause alone, an actor alone or a normal alone is an instance replaced.
    /// </summary>
    [Fact]
    public void A_breachs_cause_kg_actor_centre_and_normal_and_the_collapse_fields_are_copied_exactly()
    {
        uint domain = Watch.NewDomain();
        using var damage = new Watch.Watch<D.BuildingDamage>(domain, "station-1", b => b.Building);
        using var rig = new Rig(domain);

        HrotBreach Breach(int panel, HrotBreachCause cause, float kg, int actor) => new(panel, 1f, 2f, 0f, 2f, HrotBreachKind.Hole, 12.5, new HrotVec3(1, 2, 3), cause, kg, actor, new HrotVec3(60.25, 21.5, 1.0), new HrotVec3(0, -1, 0));
        var window = new HrotWindowDamage(1, 0, 3, 0, 0, 0, 0, 9.5, new HrotVec3(63.5, 20.0, 1.5), new HrotVec3(0, -1, 0));
        rig.Send(new HrotBuildingDamage("range/H2", true, [Breach(0, HrotBreachCause.Detonation, 12.5f, 0), Breach(1, HrotBreachCause.Ram, 0f, 4711), Breach(2, HrotBreachCause.Blade, 0f, 4712), Breach(3, HrotBreachCause.Push, 0f, 4713), Breach(4, HrotBreachCause.Unknown, 0f, 0), Breach(5, HrotBreachCause.Unknown, 0f, 0)], [window],
            401.25, 0d, 0f, HrotBreachCause.Detonation, new HrotVec3(61.5, 22.25, 4), 250f));

        Assert.True(damage.Until(w => w.Alive.Count == 1));
        var h2 = damage.Alive["range/H2"];
        Assert.Equal([(D.BreachCause.BreachCauseDetonation, 12.5f, 0), (D.BreachCause.BreachCauseRam, 0f, 4711), (D.BreachCause.BreachCauseBlade, 0f, 4712), (D.BreachCause.BreachCausePush, 0f, 4713),
                      (D.BreachCause.BreachCauseUnknown, 0f, 0), (D.BreachCause.BreachCauseUnknown, 0f, 0)], h2.Breaches.Select(b => (b.Cause, b.ExplosiveKg, b.Actor)).ToArray());   // a cause HROT appends later is not another cause
        Assert.All(h2.Breaches, b =>
        {
            Assert.Equal((60.25, 21.5, 1.0), (b.Centre.X, b.Centre.Y, b.Centre.Z));
            Assert.Equal((0f, -1f, 0f), (b.Normal.X, b.Normal.Y, b.Normal.Z));
            Assert.Equal((12.5, 1.0, 2.0, 3.0), (b.MadeAt, b.Blast.X, b.Blast.Y, b.Blast.Z));   // (the earlier fields are untouched)
        });
        Assert.Equal((63.5, 20.0, 1.5, 0f, -1f, 0f), (h2.Windows[0].Centre.X, h2.Windows[0].Centre.Y, h2.Windows[0].Centre.Z, h2.Windows[0].Normal.X, h2.Windows[0].Normal.Y, h2.Windows[0].Normal.Z));
        Assert.Equal((D.BreachCause.BreachCauseDetonation, 61.5, 22.25, 4.0, 250f), (h2.CollapseCause, h2.CollapseBlast.X, h2.CollapseBlast.Y, h2.CollapseBlast.Z, h2.CollapseKg));

        // a standing building: Unknown / zero
        rig.Send(new HrotBuildingDamage("range/H1", false, [], [window]));
        Assert.True(damage.Until(w => w.Alive.Count == 2));
        Assert.Equal((D.BreachCause.BreachCauseUnknown, 0.0, 0f), (damage.Alive["range/H1"].CollapseCause, damage.Alive["range/H1"].CollapseBlast.X, damage.Alive["range/H1"].CollapseKg));

        // a new cause alone replaces the instance
        rig.Send(new HrotBuildingDamage("range/H2", true, [Breach(0, HrotBreachCause.Ram, 12.5f, 0)], [window], 401.25, 0d, 0f, HrotBreachCause.Detonation, new HrotVec3(61.5, 22.25, 4), 250f));
        Assert.True(damage.Until(w => w.Alive["range/H2"].Breaches.Count == 1 && w.Alive["range/H2"].Breaches[0].Cause == D.BreachCause.BreachCauseRam));
    }

    /// <summary>DD-033 (T-078): the fire fields are copied, a fire alone is a record that exists (no breaches, no windows), and its end disposes the instance.</summary>
    [Fact]
    public void A_buildings_fire_is_copied_and_a_fire_alone_is_damage()
    {
        uint domain = Watch.NewDomain();
        using var damage = new Watch.Watch<D.BuildingDamage>(domain, "station-1", b => b.Building);
        using var rig = new Rig(domain);

        rig.Send(new HrotBuildingDamage("range/H1", false, [], [], 0d, 15.0, 240f));
        Assert.True(damage.Until(w => w.Alive.ContainsKey("range/H1")));
        Assert.Equal((15.0, 240f), (damage.Alive["range/H1"].FireSince, damage.Alive["range/H1"].FireSeconds));

        rig.Send(new HrotBuildingDamage("range/H1", false, [], [], 0d, 15.0, 500f));
        Assert.True(damage.Until(w => w.Alive["range/H1"].FireSeconds == 500f));

        rig.Send(new HrotBuildingDamage("range/H1", false, [], []));   // no damage and no fire any more
        Assert.True(damage.Until(w => !w.Alive.ContainsKey("range/H1")));
    }

    [Fact]
    public void The_bounds_are_64_breaches_and_256_windows_and_a_long_key_is_cut()
    {
        Assert.Equal((64, 256, 256), (DpsBuildingOutbound.MaxBreaches, DpsBuildingOutbound.MaxWindows, DpsBuildingOutbound.MaxKey));
        uint domain = Watch.NewDomain();
        using var damage = new Watch.Watch<D.BuildingDamage>(domain, "station-1", b => b.Building);
        using var rig = new Rig(domain);
        string longKey = "range/" + new string('b', 300);

        rig.Send(new HrotBuildingDamage("range/Big", false,
            [.. Enumerable.Range(0, 70).Select(i => new HrotBreach(i, 0f, 1f, 0f, 1f, HrotBreachKind.Hole))],
            [.. Enumerable.Range(0, 300).Select(i => new HrotWindowDamage(i % 20, i / 20, 3, 0, 0, 0, 0))]));
        rig.Send(new HrotBuildingDamage(longKey, true, [], []));

        Assert.True(damage.Until(w => w.Alive.Count == 2));
        Assert.Equal((64, 256), (damage.Alive["range/Big"].Breaches.Count, damage.Alive["range/Big"].Windows.Count));
        Assert.Equal(63, damage.Alive["range/Big"].Breaches[63].Panel);   // the first ones are kept
        Assert.Contains(longKey[..256], damage.Alive.Keys);
    }

    [Fact]
    public void A_building_beyond_the_bounds_is_warned_about_once_and_one_within_them_not_at_all()
    {
        uint domain = Watch.NewDomain();
        var log = new List<string>();
        using var rig = new Rig(domain, log: log.Add);
        HrotBreach Breach(int i) => new(i, 0f, 1f, 0f, 1f, HrotBreachKind.Hole);
        HrotWindowDamage Window(int i) => new(i % 20, i / 20, 3, 0, 0, 0, 0);

        rig.Send(new HrotBuildingDamage("range/Fine", false, [.. Enumerable.Range(0, 64).Select(Breach)], [.. Enumerable.Range(0, 256).Select(Window)]));   // exactly at the bounds
        Assert.DoesNotContain(log, l => l.Contains("range/Fine"));

        rig.Send(new HrotBuildingDamage("range/Big", false, [.. Enumerable.Range(0, 65).Select(Breach)], []));
        rig.Send(new HrotBuildingDamage("range/Big", false, [.. Enumerable.Range(0, 66).Select(Breach)], [.. Enumerable.Range(0, 300).Select(Window)]));   // the same building again: no second line
        rig.Send(new HrotBuildingDamage("range/Glass", false, [], [.. Enumerable.Range(0, 257).Select(Window)]));

        var warnings = log.Where(l => l.StartsWith("dps: warning: building")).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains("'range/Big' has 65 breaches and 0 windows; only the first 64 breaches and 256 windows are sent", warnings[0]);
        Assert.Contains("'range/Glass' has 0 breaches and 257 windows", warnings[1]);
    }

}
