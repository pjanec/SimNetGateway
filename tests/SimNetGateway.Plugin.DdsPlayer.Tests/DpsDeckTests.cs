using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>DD-035 (T-083): the Deck topic and VehicleLook.BridgeLayer in the plugin - every field copied, the pivot enums mirror the contract's, a changed deck is a new sample, the end disposes it.</summary>
public class DpsDeckTests
{
    private static string Id(int id) => id.ToString();

    private static HrotEntityState Bridge(int id, float health) =>
        new(id, 8825, 0, "bridge", 0, 0, new HrotVec3(10, 20, 3), HrotQuat.Identity, HrotVec3.Zero, HrotVec3.Zero, health, 100, 1.0);

    private static HrotDeck Deck(int id, HrotDeckKind kind = HrotDeckKind.Assault, float length = 34f) => new(id, kind, length, 4f, 1.5f,
    [
        new HrotDeckPart(HrotDeckPartKind.Deck, new HrotVec3(0, -2, 1.5), new HrotVec3(34, -2, 1.5), new HrotVec3(34, 2, 1.5), new HrotVec3(0, 2, 1.5)),
        new HrotDeckPart(HrotDeckPartKind.Ramp, new HrotVec3(-6, -2, 0), new HrotVec3(0, -2, 1.5), new HrotVec3(0, 2, 1.5), new HrotVec3(-6, 2, 0)),
        new HrotDeckPart(HrotDeckPartKind.Ramp, new HrotVec3(34, -2, 1.5), new HrotVec3(40, -2, 0), new HrotVec3(40, 2, 0), new HrotVec3(34, 2, 1.5)),
    ]);

    [Fact]
    public void The_pivot_deck_enums_mirror_the_contracts_and_a_deck_is_copied_part_by_part()
    {
        Assert.Equal(Enum.GetNames<HrotDeckKind>().Select(n => "DeckKind" + n), Enum.GetNames<D.DeckKind>());
        Assert.Equal(Enum.GetNames<HrotDeckPartKind>().Select(n => "DeckPartKind" + n), Enum.GetNames<D.DeckPartKind>());
        Assert.Equal(Enum.GetNames<HrotBridgeLayer>().Select(n => "BridgeLayer" + n), Enum.GetNames<D.BridgeLayer>());

        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.Deck>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        var sent = Deck(8001, HrotDeckKind.Floating, 22f);
        rig.Send(sent);
        Assert.True(watch.Until(w => w.Alive.ContainsKey("8001")));
        var d = watch.Alive["8001"];
        Assert.Equal((D.DeckKind.DeckKindFloating, 22f, 4f, 1.5f, 3), (d.Kind, d.Length, d.Width, d.Height, d.Parts.Count));
        for (int i = 0; i < 3; i++)
        {
            var p = sent.Parts[i];
            var w = d.Parts[i];
            Assert.Equal((D.DeckPartKind)(byte)p.Kind, w.Kind);
            Assert.Equal(new[] { p.C0, p.C1, p.C2, p.C3 }, new[] { w.C0, w.C1, w.C2, w.C3 }.Select(c => new HrotVec3(c.X, c.Y, c.Z)));   // the corners, z = the height above the ground
        }

        rig.Send(Deck(8001, HrotDeckKind.Floating, 30f));   // the span changed
        Assert.True(watch.Until(w => w.Alive["8001"].Length == 30f));
        rig.Send(new HrotDeckEnded(8001));
        Assert.True(watch.Until(w => !w.Alive.ContainsKey("8001")));
        rig.Send(new HrotDeckEnded(8001));   // a second end writes nothing (never written: no dispose of a dead instance)
    }

    /// <summary>A bridge that fell is told from one that was recovered: its entity's Damage is destroyed before the deck is disposed. Mutation: the dispose dropped fails the last row.</summary>
    [Fact]
    public void A_fallen_bridge_shows_destroyed_on_its_entity_before_the_deck_is_disposed()
    {
        uint domain = Watch.NewDomain();
        using var decks = new Watch.Watch<D.Deck>(domain, "station-1", e => Id(e.EntityId));
        using var states = new Watch.Watch<D.EntityState>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(Bridge(8002, 100f));
        rig.Send(Deck(8002));
        Assert.True(decks.Until(w => w.Alive.ContainsKey("8002")));
        rig.Send(Bridge(8002, 0f));   // the charge dropped it
        Assert.True(states.Until(w => w.Alive.TryGetValue("8002", out var s) && s.Damage == D.PartCondition.PartConditionDestroyed));
        Assert.True(decks.Alive.ContainsKey("8002"));   // the deck outlives the Damage sample by the host's order: destroyed first ...
        rig.Send(new HrotDeckEnded(8002));              // ... then the dispose
        Assert.True(decks.Until(w => !w.Alive.ContainsKey("8002")));
    }

    [Fact]
    public void A_vehicle_look_carries_the_bridge_layer_state_and_a_change_alone_is_a_sample()
    {
        uint domain = Watch.NewDomain();
        using var looks = new Watch.Watch<D.VehicleLook>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(Bridge(2001, 100f));   // the entity first: its look follows it
        foreach (var layer in Enum.GetValues<HrotBridgeLayer>())
        {
            rig.Send(new HrotVehicleLook(2001, HrotKillKind.None, false, false, false, 0, [], [], BridgeLayer: layer));
            Assert.True(looks.Until(w => w.Alive.TryGetValue("2001", out var l) && l.BridgeLayer == (D.BridgeLayer)(byte)layer), layer.ToString());
        }
    }

    /// <summary>T-084: an obstacle's size is copied, a changed size is a new sample, the end disposes it (never a dispose of an instance not written).</summary>
    [Fact]
    public void An_obstacles_size_is_copied_a_change_is_a_sample_and_the_end_disposes_it()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.Obstacle>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(new HrotObstacle(8101, 3f, 0.6f, 1.2f));
        Assert.True(watch.Until(w => w.Alive.ContainsKey("8101")));
        var o = watch.Alive["8101"];
        Assert.Equal((3f, 0.6f, 1.2f), (o.Length, o.Width, o.Height));
        rig.Send(new HrotObstacle(8101, 9f, 0.6f, 1.2f));
        Assert.True(watch.Until(w => w.Alive["8101"].Length == 9f));
        rig.Send(new HrotObstacleEnded(8101));
        Assert.True(watch.Until(w => !w.Alive.ContainsKey("8101")));
        rig.Send(new HrotObstacleEnded(8101));
    }
}
