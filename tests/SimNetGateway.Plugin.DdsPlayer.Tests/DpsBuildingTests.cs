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
            [new HrotBreach(0, 4f, 4.6f, 1.1f, 1.6f, "Hole"), new HrotBreach(2, 1f, 3f, 0f, 2.2f, "Passage"), new HrotBreach(3, 0f, 1f, 0f, 1f, "Crater")],
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

    [Fact]
    public void The_bounds_are_64_breaches_and_256_windows_and_a_long_key_is_cut()
    {
        Assert.Equal((64, 256, 256), (DpsBuildingOutbound.MaxBreaches, DpsBuildingOutbound.MaxWindows, DpsBuildingOutbound.MaxKey));
        uint domain = Watch.NewDomain();
        using var damage = new Watch.Watch<D.BuildingDamage>(domain, "station-1", b => b.Building);
        using var rig = new Rig(domain);
        string longKey = "range/" + new string('b', 300);

        rig.Send(new HrotBuildingDamage("range/Big", false,
            [.. Enumerable.Range(0, 70).Select(i => new HrotBreach(i, 0f, 1f, 0f, 1f, "Hole"))],
            [.. Enumerable.Range(0, 300).Select(i => new HrotWindowDamage(i % 20, i / 20, 3, 0, 0, 0, 0))]));
        rig.Send(new HrotBuildingDamage(longKey, true, [], []));

        Assert.True(damage.Until(w => w.Alive.Count == 2));
        Assert.Equal((64, 256), (damage.Alive["range/Big"].Breaches.Count, damage.Alive["range/Big"].Windows.Count));
        Assert.Equal(63, damage.Alive["range/Big"].Breaches[63].Panel);   // the first ones are kept
        Assert.Contains(longKey[..256], damage.Alive.Keys);
    }

    [Fact]
    public void Breach_kinds_map_by_name()
    {
        Assert.Equal(D.BreachKind.BreachKindHole, LookNames.ByName<D.BreachKind>("BreachKind", "Hole"));
        Assert.Equal(D.BreachKind.BreachKindPassage, LookNames.ByName<D.BreachKind>("BreachKind", "Passage"));
        Assert.Equal(D.BreachKind.BreachKindUnknown, LookNames.ByName<D.BreachKind>("BreachKind", "Crater"));
    }
}
