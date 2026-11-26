using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Terrain;
using Hrot.Map.Common;
using Hrot.UI.Common.Map3D;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// DD-035 (T-083): laid bridges and the bridge layer. The properties: <c>HrotDeck</c> equals HROT's own rule <c>TerrainObstacles.DeckParts</c> of the same world's components (position, heading, <c>ObstacleShape</c>), its kind
/// is the TKB type's (assault 8825 / floating 8826), the obstacle is still an entity, a Constructing bridge is held, a steady bridge sends nothing and allocates nothing, a moved or reshaped one is one new sample, a bridge that
/// is gone (recovered or fallen) is one dispose; <c>VehicleLook.BridgeLayer</c> equals <c>BridgeLayerLook.Of</c> for every state and NoSpan for a plain vehicle.
/// </summary>
public class DeckGatherTests
{
    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public List<T> Of<T>() => All.OfType<T>().ToList();
    }

    private sealed class Stage : PackHarness
    {
        private double _now = 1000;

        public Stage()
            : base(dis: true)
        {
            Ensure<StaticObstacle>();
            Ensure<ObstacleShape>();
            Ensure<BridgeCarried>();
            RegisterTemplate(100, 1);   // a platform: its VehicleLook is gathered
            Steps(3);
        }

        private void Ensure<T>()
            where T : unmanaged
        {
            if (!World.IsComponentTypeRegistered<T>())
            {
                World.RegisterComponent<T>();
            }
        }

        public Capture Gather(double simTime = 1.0)
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            _now += 0.1;
            Adapter.GatherEntities(World, inbound, _now, simTime);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }

        /// <summary>A laid bridge as HROT's obstacle translator makes it: a replicated entity of the bridge's TKB type with a transform, a StaticObstacle of the deck kind and its ObstacleShape.</summary>
        public Entity Bridge(int id, long type, Vector3 at, float yaw, float length, float width, float height, ObstacleKind kind = ObstacleKind.WalkableDeck)
        {
            var e = AddHrotEntity(id, at, "bridge", type);
            World.SetComponent(e, new SimTransform { Position = at, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw) });
            World.AddComponent(e, new StaticObstacle { Kind = kind });
            World.AddComponent(e, new ObstacleShape { Length = length, Width = width, Height = height });
            return e;
        }
    }

    private static readonly (long Type, HrotDeckKind Kind, Vector3 At, float Yaw, float Length, float Width, float Height)[] Spans =
    [
        (TkbEntityTypes.Obstacle_AssaultBridge, HrotDeckKind.Assault, new Vector3(215, 200, 2), 0f, 34f, 4f, 1.5f),
        (TkbEntityTypes.Obstacle_FloatingBridge, HrotDeckKind.Floating, new Vector3(-40, 12, 0.5f), 0.7f, 22f, 3.2f, 0.6f),
    ];

    [Fact]
    public void A_deck_equals_HROTs_rule_on_the_same_world_for_an_assault_and_a_floating_span_and_the_bridge_is_still_an_entity()
    {
        using var h = new Stage();
        var entities = new List<Entity>();
        for (int i = 0; i < Spans.Length; i++)
        {
            var s = Spans[i];
            entities.Add(h.Bridge(8001 + i, s.Type, s.At, s.Yaw, s.Length, s.Width, s.Height));
        }

        var all = h.Gather();
        for (int i = 0; i < Spans.Length; i++)
        {
            var s = Spans[i];
            var deck = Assert.Single(all.Of<HrotDeck>(), d => d.Id == 8001 + i);
            var tf = h.World.GetComponentRO<SimTransform>(entities[i]);
            var shape = h.World.GetComponentRO<ObstacleShape>(entities[i]);
            var reference = TerrainObstacles.DeckParts(tf.Position, TerrainObstacles.Yaw(tf.Rotation), shape);   // HROT's one rule, on the same world
            Assert.Equal((s.Kind, s.Length, s.Width, s.Height, reference.Length), (deck.Kind, deck.Length, deck.Width, deck.Height, deck.Parts.Count));
            for (int p = 0; p < reference.Length; p++)
            {
                Assert.Equal(reference[p].Kind == TerrainWalkableKind.Slab ? HrotDeckPartKind.Deck : HrotDeckPartKind.Ramp, deck.Parts[p].Kind);
                for (int c = 0; c < 4; c++)
                {
                    var want = reference[p][c];
                    var got = c switch { 0 => deck.Parts[p].C0, 1 => deck.Parts[p].C1, 2 => deck.Parts[p].C2, _ => deck.Parts[p].C3 };
                    Assert.Equal(new HrotVec3(want.At.X, want.At.Y, want.Above), got);   // x, y in the sim frame; z the height above the ground under the corner
                }
            }

            Assert.Single(all.Of<HrotEntityState>(), st => st.Id == 8001 + i);   // D-4: the obstacle stays a plain entity too
        }
    }

    [Fact]
    public void A_steady_bridge_sends_nothing_and_a_move_or_a_reshape_is_one_new_deck()
    {
        using var h = new Stage();
        var s = Spans[0];
        var e = h.Bridge(8001, s.Type, s.At, s.Yaw, s.Length, s.Width, s.Height);
        Assert.Single(h.Gather().Of<HrotDeck>());
        for (int i = 0; i < 50; i++)
        {
            Assert.Empty(h.Gather().Of<HrotDeck>());
        }

        h.World.SetComponent(e, new SimTransform { Position = s.At + new Vector3(1, 0, 0), Rotation = Quaternion.Identity });
        Assert.Single(h.Gather().Of<HrotDeck>());   // moved: one new deck
        Assert.Empty(h.Gather().Of<HrotDeck>());
        h.World.SetComponent(e, new ObstacleShape { Length = 40f, Width = s.Width, Height = s.Height });
        var longer = Assert.Single(h.Gather().Of<HrotDeck>());   // reshaped: one new deck
        Assert.Equal(40f, longer.Length);
        Assert.Empty(h.Gather().Of<HrotDeck>());
    }

    /// <summary>0 B: the deck gather's steady path alone (the rule runs only on a change).</summary>
    [Fact]
    public void The_deck_gather_steady_path_allocates_nothing()
    {
        using var h = new Stage();
        var s = Spans[1];
        var e = h.Bridge(8001, s.Type, s.At, s.Yaw, s.Length, s.Width, s.Height);
        var gather = new DeckGather();
        var inbound = new InMemoryInbound(new SourceId(99), "deck");
        void Pass()
        {
            gather.Begin();
            gather.Gather(h.World, e, 8001, inbound);
            gather.End(inbound);
        }

        Pass();
        Assert.Equal(1, inbound.Drain(new Capture(), int.MaxValue));
        for (int i = 0; i < 50; i++)
        {
            Pass();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            Pass();
        }

        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, inbound.Drain(new Capture(), int.MaxValue));
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void A_bridge_that_is_gone_is_disposed_once_and_a_constructing_one_is_held_until_it_stands()
    {
        using var h = new Stage();
        var s = Spans[0];
        var e = h.Bridge(8001, s.Type, s.At, s.Yaw, s.Length, s.Width, s.Height);
        h.World.SetLifecycleState(e, EntityLifecycle.Constructing);   // held until its bake commits, as HROT's terrain draws it
        Assert.Empty(h.Gather().Of<HrotDeck>());
        h.World.SetLifecycleState(e, EntityLifecycle.Active);
        Assert.Single(h.Gather().Of<HrotDeck>());

        h.RemoveHrotEntity(e);   // recovered, or fallen at Health 0 (the obstacle's owner removes it): the same removal
        var gone = h.Gather();
        Assert.Equal(8001, Assert.Single(gone.Of<HrotDeckEnded>()).Id);
        Assert.Single(gone.Of<HrotEntityRemoved>());
        Assert.Empty(h.Gather().Of<HrotDeckEnded>());   // once
    }

    [Fact]
    public void A_solid_obstacle_and_a_plain_vehicle_have_no_deck()
    {
        using var h = new Stage();
        h.Bridge(8003, TkbEntityTypes.Obstacle_AssaultBridge, new Vector3(1, 2, 0), 0f, 10f, 2f, 1f, ObstacleKind.Solid);
        h.AddHrotEntity(2001, new Vector3(5, 5, 0));
        Assert.Empty(h.Gather().Of<HrotDeck>());
    }

    /// <summary>Mutation: a part's corners from the wrong end (swap a and b in the rule's output) fails the corner rows of the first test; the layer state not copied fails every non-NoSpan row here.</summary>
    [Fact]
    public void The_bridge_layer_is_HROTs_rule_for_every_state_and_no_span_for_a_plain_vehicle_and_a_change_alone_is_sent()
    {
        using var h = new Stage();
        var engineer = h.AddHrotEntity(2100, Vector3.Zero);
        var plain = h.AddHrotEntity(2101, new Vector3(50, 0, 0));
        (BridgeOrder Order, byte Count, BridgeLayerState Want)[] states =
        [
            (BridgeOrder.None, 1, BridgeLayerState.Carried), (BridgeOrder.Lay, 1, BridgeLayerState.Laying), (BridgeOrder.Recover, 0, BridgeLayerState.Recovering), (BridgeOrder.None, 0, BridgeLayerState.NoSpan),
        ];
        h.World.AddComponent(engineer, new BridgeCarried { Order = BridgeOrder.None, Count = 1 });
        double t = 1.0;
        bool first = true;
        foreach (var (order, count, want) in states)
        {
            h.World.SetComponent(engineer, new BridgeCarried { Order = order, Count = count });
            t += 1.0;
            var looks = h.Gather(t).Of<HrotVehicleLook>();
            var look = Assert.Single(looks, l => l.Id == 2100);
            Assert.Equal(want, BridgeLayerLook.Of(h.World, engineer));   // the rule's own answer on this world
            Assert.Equal(want.ToString(), look.BridgeLayer.ToString());
            if (first)
            {
                Assert.Equal(HrotBridgeLayer.NoSpan, Assert.Single(looks, l => l.Id == 2101).BridgeLayer);   // a plain vehicle
                first = false;
            }

            Assert.Empty(h.Gather(t + 0.5).Of<HrotVehicleLook>());   // steady: nothing
        }

        Assert.Equal(BridgeLayerState.NoSpan, BridgeLayerLook.Of(h.World, plain));
    }

    // ---- T-084: the size of a roadblock -----------------------------------------------------------------------------------------

    /// <summary>
    /// <c>HrotObstacle</c> equals the entity's own <c>ObstacleShape</c> (a per-instance size that differs from the type's default is the one sent: two walls of one type, two sizes), a bridge has none, a steady obstacle sends nothing,
    /// an edited size is one new sample. Mutations: size not copied (a constant) fails the equality; a bridge not excluded fails the no-Obstacle row.
    /// </summary>
    [Fact]
    public void An_obstacle_sends_its_own_per_instance_size_never_a_bridges_and_a_change_is_one_new_sample()
    {
        using var h = new Stage();
        var shortWall = h.Bridge(8101, TkbEntityTypes.Obstacle_SandbagWall, new Vector3(5, 5, 1), 0.3f, 3f, 0.6f, 1.0f, ObstacleKind.Solid);
        var longWall = h.Bridge(8102, TkbEntityTypes.Obstacle_SandbagWall, new Vector3(9, 5, 1), 0f, 12f, 0.6f, 1.4f, ObstacleKind.Solid);   // the same type, stretched by the scenario
        h.Bridge(8103, TkbEntityTypes.Obstacle_AssaultBridge, new Vector3(50, 5, 0), 0f, 34f, 4f, 1.5f);

        var all = h.Gather();
        foreach (var (id, wall) in new[] { (8101, shortWall), (8102, longWall) })
        {
            var shape = h.World.GetComponentRO<ObstacleShape>(wall);
            var o = Assert.Single(all.Of<HrotObstacle>(), x => x.Id == id);
            Assert.Equal((shape.Length, shape.Width, shape.Height), (o.Length, o.Width, o.Height));
            Assert.Single(all.Of<HrotEntityState>(), st => st.Id == id);   // still an entity
        }

        Assert.NotEqual(Assert.Single(all.Of<HrotObstacle>(), x => x.Id == 8101).Length, Assert.Single(all.Of<HrotObstacle>(), x => x.Id == 8102).Length);
        Assert.DoesNotContain(all.Of<HrotObstacle>(), x => x.Id == 8103);   // a bridge keeps its Deck
        Assert.Single(all.Of<HrotDeck>(), d => d.Id == 8103);
        for (int i = 0; i < 20; i++)
        {
            Assert.Empty(h.Gather().Of<HrotObstacle>());
        }

        h.World.SetComponent(shortWall, new ObstacleShape { Length = 5f, Width = 0.6f, Height = 1.0f });   // edited
        var edited = Assert.Single(h.Gather().Of<HrotObstacle>());
        Assert.Equal((8101, 5f), (edited.Id, edited.Length));
        Assert.Empty(h.Gather().Of<HrotObstacle>());
    }

    [Fact]
    public void A_bulldozed_obstacle_is_disposed_once_when_it_is_removed_at_zero_health()
    {
        using var h = new Stage();
        var wall = h.Bridge(8101, TkbEntityTypes.Obstacle_SandbagWall, new Vector3(5, 5, 1), 0f, 3f, 0.6f, 1.0f, ObstacleKind.Solid);
        Assert.Single(h.Gather().Of<HrotObstacle>());
        h.World.SetComponent(wall, new Fdp.Toolkit.Combat.Components.Health { Current = 0f, Max = 100f });   // pushed down (ObstacleDamage)
        Assert.Empty(h.Gather().Of<HrotObstacleEnded>());                                                     // not gone until its owner removes it
        h.RemoveHrotEntity(wall);                                                                             // ObstacleRemovalSystem
        var gone = h.Gather();
        Assert.Equal(8101, Assert.Single(gone.Of<HrotObstacleEnded>()).Id);
        Assert.Single(gone.Of<HrotEntityRemoved>());
        Assert.Empty(h.Gather().Of<HrotObstacleEnded>());
    }

    [Fact]
    public void The_obstacle_gather_steady_path_allocates_nothing()
    {
        using var h = new Stage();
        var wall = h.Bridge(8101, TkbEntityTypes.Obstacle_SandbagWall, new Vector3(5, 5, 1), 0f, 3f, 0.6f, 1.0f, ObstacleKind.Solid);
        var gather = new ObstacleGather();
        var inbound = new InMemoryInbound(new SourceId(99), "obstacle");
        void Pass()
        {
            gather.Begin();
            gather.Gather(h.World, wall, 8101, inbound);
            gather.End(inbound);
        }

        Pass();
        Assert.Equal(1, inbound.Drain(new Capture(), int.MaxValue));
        for (int i = 0; i < 50; i++)
        {
            Pass();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            Pass();
        }

        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, inbound.Drain(new Capture(), int.MaxValue));
        Assert.Equal(0L, bytes);
    }
}
