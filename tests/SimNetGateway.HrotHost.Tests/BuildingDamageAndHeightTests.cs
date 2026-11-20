using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Terrain;
using Fdp.Toolkit.Diagnostics.Gizmos;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>DPS round 2 (DD-018 3.6, 3.6a): the damage of buildings united per building with panels within the building, and HROT's ground height for 2-D primitives and heat marks.</summary>
public class BuildingDamageAndHeightTests
{
    /// <summary>Two brick houses, H1 at (20, 20) and H2 at (60, 20), 10 x 8 m, so H2's panels are 4 .. 7 globally and 0 .. 3 within it.</summary>
    private const string TwoHouses = """
        { "type": "FeatureCollection", "features": [
          { "type": "Feature", "properties": { "kind": "building", "label": "H1",
              "building": { "footprint": [[0,0],[10,0],[10,8],[0,8]],
                "storeys": [ { "height": 3, "walls": [
                  { "from": [0,0], "to": [10,0], "thickness": 0.3, "material": "brick" }, { "from": [10,0], "to": [10,8], "thickness": 0.3, "material": "brick" },
                  { "from": [10,8], "to": [0,8], "thickness": 0.3, "material": "brick" }, { "from": [0,8], "to": [0,0], "thickness": 0.3, "material": "brick" } ] } ] } },
            "geometry": { "type": "Point", "coordinates": [20, 20] } },
          { "type": "Feature", "properties": { "kind": "building", "label": "H2",
              "building": { "footprint": [[0,0],[10,0],[10,8],[0,8]],
                "storeys": [ { "height": 3, "walls": [
                  { "from": [0,0], "to": [10,0], "thickness": 0.3, "material": "brick" }, { "from": [10,0], "to": [10,8], "thickness": 0.3, "material": "brick" },
                  { "from": [10,8], "to": [0,8], "thickness": 0.3, "material": "brick" }, { "from": [0,8], "to": [0,0], "thickness": 0.3, "material": "brick" } ] } ] } },
            "geometry": { "type": "Point", "coordinates": [60, 20] } } ] }
        """;

    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public List<T> Of<T>() => All.OfType<T>().ToList();
    }

    private sealed class Stage : PackHarness
    {
        private double _now = 1000;

        public Stage(float ground = 7.5f, bool withTerrain = true)
            : base(dis: true)
        {
            Terrain = TerrainWorldParser.Parse(TwoHouses, "range");
            if (withTerrain)
            {
                Terrain = new TerrainWorld
                {
                    Name = "range", Panels = Terrain.Panels, Buildings = Terrain.Buildings, Prisms = Terrain.Prisms,
                    Height = new TerrainHeightGrid(new Vector2(-200f, -200f), 4f, 200, 200, Enumerable.Repeat(ground, 200 * 200).ToArray()),
                };
                World.SetSingletonManaged(Terrain);
            }

            if (!World.IsComponentTypeRegistered<WindowDamage>())
            {
                World.RegisterComponent<WindowDamage>();
            }

            if (!World.IsComponentTypeRegistered<GroundHeatMarks>())
            {
                World.RegisterComponent<GroundHeatMarks>();
            }

            Steps(3);
        }

        public TerrainWorld Terrain { get; }

        /// <summary>The panel of building <paramref name="building"/> that is its <paramref name="local"/>-th, as the global index HROT's damage entities carry.</summary>
        public int Panel(int building, int local) => BuildingPanelIndex.ToGlobal(Terrain, building, local);

        public Capture Gather(double simTime = 1.0)
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            _now += 0.1;
            Adapter.GatherEntities(World, inbound, _now, simTime);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }

        public Entity Breaches(int building, params WallBreach[] breaches)
        {
            var d = new BuildingDamage { Building = building };
            foreach (var b in breaches)
            {
                BuildingDamages.Add(ref d, in b, null);
            }

            var e = World.CreateEntity();
            World.AddComponent(e, d);
            return e;
        }

        public Entity Windows(int building, params WindowEntry[] windows)
        {
            var w = new WindowDamage { Building = building };
            foreach (var x in windows)
            {
                w.Set(w.Count, x);
                w.Count++;
            }

            var e = World.CreateEntity();
            World.AddComponent(e, w);
            return e;
        }
    }

    [Fact]
    public void The_damage_of_a_building_is_one_sample_with_panels_within_the_building()
    {
        using var h = new Stage();
        // H2 is building 1: its panels are 4 .. 7 globally, 0 .. 3 within it; two breaches and a cracked and a broken window
        h.Breaches(1, new WallBreach(h.Panel(1, 2), 1f, 3f, 0f, 2.2f, BreachKind.Passage), new WallBreach(h.Panel(1, 0), 4f, 4.6f, 1.1f, 1.6f, BreachKind.Hole));
        h.Windows(1, new WindowEntry(h.Panel(1, 1), 0, 1, 40, 50, 0, 0), new WindowEntry(h.Panel(1, 3), 2, 3, 0, 0, 0, 0));
        Assert.Equal(4, h.Panel(1, 0));   // the global numbering is NOT the within-building one

        var sent = Assert.Single(h.Gather().Of<HrotBuildingDamage>());

        Assert.Equal("range/H2", sent.Building);
        Assert.False(sent.Collapsed);
        Assert.Equal([new HrotBreach(0, 4f, 4.6f, 1.1f, 1.6f, "Hole"), new HrotBreach(2, 1f, 3f, 0f, 2.2f, "Passage")], sent.Breaches);   // within the building, sorted
        Assert.Equal([new HrotWindowDamage(1, 0, 1, 40, 50, 0, 0), new HrotWindowDamage(3, 2, 3, 0, 0, 0, 0)], sent.Windows);

        // nothing changed: nothing sent
        Assert.Empty(h.Gather(2.0).Of<HrotBuildingDamage>());
    }

    [Fact]
    public void Several_damage_entities_of_one_building_are_merged_and_other_buildings_stay_apart()
    {
        using var h = new Stage();
        h.Breaches(0, new WallBreach(h.Panel(0, 1), 1f, 2f, 0f, 2f, BreachKind.Hole));
        h.Breaches(1, new WallBreach(h.Panel(1, 0), 1f, 2f, 0f, 2f, BreachKind.Hole));
        h.Breaches(1, new WallBreach(h.Panel(1, 0), 1f, 2f, 0f, 2f, BreachKind.Hole), new WallBreach(h.Panel(1, 3), 5f, 6f, 0f, 2f, BreachKind.Passage));   // the capacity overflow: a second entity, with a duplicate
        h.Windows(1, new WindowEntry(h.Panel(1, 1), 0, 1, 40, 50, 0, 0));
        h.Windows(1, new WindowEntry(h.Panel(1, 1), 0, 3, 0, 0, 0, 0), new WindowEntry(h.Panel(1, 2), 1, 1, 9, 9, 0, 0));   // the same window, harder hit, wins

        var sent = h.Gather().Of<HrotBuildingDamage>().OrderBy(b => b.Building).ToList();

        Assert.Equal(["range/H1", "range/H2"], sent.Select(s => s.Building).ToArray());
        Assert.Equal([new HrotBreach(1, 1f, 2f, 0f, 2f, "Hole")], sent[0].Breaches);
        Assert.Empty(sent[0].Windows);
        Assert.Equal([new HrotBreach(0, 1f, 2f, 0f, 2f, "Hole"), new HrotBreach(3, 5f, 6f, 0f, 2f, "Passage")], sent[1].Breaches);   // distinct, from both entities
        Assert.Equal([new HrotWindowDamage(1, 0, 3, 0, 0, 0, 0), new HrotWindowDamage(2, 1, 1, 9, 9, 0, 0)], sent[1].Windows);
    }

    [Fact]
    public void A_collapse_is_sent_and_a_change_alone_is_sent_again()
    {
        using var h = new Stage();
        var e = h.Breaches(0);
        var collapsed = h.World.GetComponentRO<BuildingDamage>(e);
        collapsed.Collapsed = 1;
        h.World.SetComponent(e, collapsed);

        var first = Assert.Single(h.Gather().Of<HrotBuildingDamage>());
        Assert.Equal(("range/H1", true), (first.Building, first.Collapsed));
        Assert.Empty(first.Breaches);

        var more = h.Windows(0, new WindowEntry(h.Panel(0, 0), 0, 3, 0, 0, 0, 0));
        var second = Assert.Single(h.Gather(2.0).Of<HrotBuildingDamage>());
        Assert.Equal((true, 1), (second.Collapsed, second.Windows.Count));
        Assert.True(h.World.IsAlive(more));
    }

    [Fact]
    public void Damage_that_goes_is_sent_empty_once_and_everything_is_sent_again_after_a_boundary()
    {
        using var h = new Stage();
        var damage = h.Breaches(1, new WallBreach(h.Panel(1, 0), 1f, 2f, 0f, 2f, BreachKind.Hole));
        Assert.Single(h.Gather().Of<HrotBuildingDamage>());

        h.World.DestroyEntity(damage);   // a reload
        var gone = Assert.Single(h.Gather(2.0).Of<HrotBuildingDamage>());
        Assert.Equal(("range/H2", false, 0, 0), (gone.Building, gone.Collapsed, gone.Breaches.Count, gone.Windows.Count));
        Assert.Empty(h.Gather(3.0).Of<HrotBuildingDamage>());

        h.Breaches(1, new WallBreach(h.Panel(1, 0), 1f, 2f, 0f, 2f, BreachKind.Hole));
        Assert.Single(h.Gather(4.0).Of<HrotBuildingDamage>());
        h.Adapter.Reset(h.World);   // a world boundary: the gateway is new
        Assert.Single(h.Gather(5.0).Of<HrotBuildingDamage>());
    }

    [Fact]
    public void A_damage_entity_that_names_no_building_or_a_world_without_terrain_sends_nothing_and_the_entities_are_not_published()
    {
        using var h = new Stage();
        h.Breaches(99, new WallBreach(0, 1f, 2f, 0f, 2f, BreachKind.Hole));
        h.Breaches(0, new WallBreach(h.Panel(1, 0), 1f, 2f, 0f, 2f, BreachKind.Hole));   // a panel that belongs to another building
        var sent = h.Gather();
        Assert.Empty(sent.Of<HrotBuildingDamage>());
        Assert.Empty(sent.Of<HrotEntityState>());

        using var bare = new Stage(withTerrain: false);
        bare.Breaches(0, new WallBreach(0, 1f, 2f, 0f, 2f, BreachKind.Hole));
        Assert.Empty(bare.Gather().Of<HrotBuildingDamage>());
    }

    // ---- heights --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Ground_marks_stand_on_the_ground_height_of_hrots_terrain()
    {
        using var h = new Stage(ground: 7.5f);
        var cell = h.World.CreateEntity();
        h.World.AddComponent(cell, new NetworkIdentity(5001));
        var marks = default(GroundHeatMarks);
        marks.Set(0, new HeatMark(HeatMarkKind.BurnedArea, new Vector2(5, 6), 0.5f, 6f, 3f, HeatMarkEnd.NoHotEnd, new SimInstant(10.0), new SimInstant(20.0), 0f, ThermalPowerClass.NoPowerClass));
        marks.Count = 1;
        h.World.AddComponent(cell, marks);

        var sent = Assert.Single(h.Gather().Of<HrotGroundMarks>());

        Assert.Equal(new HrotVec3(5, 6, 7.5), Assert.Single(sent.Marks).Centre);
    }

    [Fact]
    public void Two_d_gizmo_primitives_get_the_ground_height_and_the_others_are_untouched()
    {
        using var h = new Stage(ground: 7.5f);
        var host = h.System.PlayerStation.Gizmos;
        host.Ground = Fdp.Toolkit.World.WorldQuery.Of(h.World);
        Assert.NotNull(host.Ground);
        var red = new Rgba32(255, 0, 0, 255);

        var box = host.Convert(DebugPrimitive.MakeBox2D(new Vector2(30, 40), new Vector2(2, 3), red, angleDeg: 15f))!.Value;
        var text = host.Convert(DebugPrimitive.MakeText(31, 41, new Fdp.Toolkit.Diagnostics.Gizmos.FixedString32("W aiming"), red))!.Value;

        Assert.Equal(SimNetGateway.Gateway.HrotDis.GizmoPrimitiveShape.Box, box.Shape);
        Assert.Equal(new HrotVec3(30, 40, 7.5), box.Points[0]);   // the centre stands on the ground
        Assert.Equal(new HrotVec3(2, 3, 15), box.Points[1]);      // the extents and the angle are not a point
        Assert.Equal((SimNetGateway.Gateway.HrotDis.GizmoPrimitiveShape.Text, "W aiming"), (text.Shape, text.Text));
        Assert.Equal(new HrotVec3(31, 41, 7.5), text.Points[0]);

        // no terrain in the world: z stays 0 (and nothing fails)
        host.Ground = null;
        Assert.Equal(0.0, host.Convert(DebugPrimitive.MakeBox2D(new Vector2(30, 40), new Vector2(2, 3), red))!.Value.Points[0].Z);
    }
}
