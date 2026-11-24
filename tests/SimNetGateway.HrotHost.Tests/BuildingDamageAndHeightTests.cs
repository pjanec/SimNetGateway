using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Perception.Signatures;
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

        /// <summary>A damage entity whose breaches carry HROT's stamps (T-074): each is (breach, MadeAt, blast point); <paramref name="collapsedAt"/> &gt; 0 also collapses the building.</summary>
        public Entity Stamped(int building, double collapsedAt, params (WallBreach Breach, double At, Vector3 Blast)[] breaches)
        {
            var d = new BuildingDamage { Building = building };
            foreach (var (b, at, blast) in breaches)
            {
                BuildingDamages.Add(ref d, in b, null, at, blast);
            }

            if (collapsedAt > 0)
            {
                d.Collapsed = 1;
                d.CollapsedAt = collapsedAt;
            }

            var e = World.CreateEntity();
            World.AddComponent(e, d);
            return e;
        }

        /// <summary>A window entity whose entries carry the times of their last hit.</summary>
        public Entity StampedWindows(int building, params (WindowEntry Window, double HitAt)[] windows)
        {
            var w = new WindowDamage { Building = building };
            foreach (var (x, at) in windows)
            {
                w.Set(w.Count, x);
                w.SetHitAt(w.Count, at);
                w.Count++;
            }

            var e = World.CreateEntity();
            World.AddComponent(e, w);
            return e;
        }

        /// <summary>A ground-cell entity holding <paramref name="craters"/> in this order.</summary>
        public Entity Craters(int cellX, int cellY, params Crater[] craters)
        {
            var d = new GroundDamage { CellX = cellX, CellY = cellY };
            foreach (var c in craters)
            {
                GroundDamages.Add(ref d, in c, out _);
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

    /// <summary>T-073: a terrain's door (an entity carrying <c>DoorState</c>) is part of its house, never an entity to show: not gathered, not sent
    /// - while a unit beside it still is. Red-proof: the query without <c>Without&lt;DoorState&gt;</c>.</summary>
    [Fact]
    public void A_terrain_door_is_not_gathered_as_an_entity_but_a_unit_beside_it_is()
    {
        using var h = new Stage();
        if (!h.World.IsComponentTypeRegistered<DoorState>())
        {
            h.World.RegisterComponent<DoorState>();
        }

        var door = h.AddHrotEntity(7001, new Vector3(5, 5, 0), name: "door", tkb: 8805);
        h.World.AddComponent(door, new DoorState { State = TerrainDoorState.Closed });
        h.AddHrotEntity(7002, new Vector3(6, 5, 0), name: "unit", tkb: 100);

        var first = h.Gather(1.0).Of<HrotEntityState>();
        Assert.DoesNotContain(first, s => s.Id == 7001);
        Assert.Contains(first, s => s.Id == 7002);
        Assert.DoesNotContain(h.Gather(3.0).Of<HrotEntityState>(), s => s.Id == 7001);   // nor at the next resend
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

    // ---- T-074 (DD-030): HROT's times and blast points, and the craters -------------------------------------------------------------

    /// <summary>T-074: every stamp HROT records (a breach's MadeAt and blast point, a window's HitAt, the collapse time) reaches the pivot message exactly; 0 (from a save) stays 0.</summary>
    [Fact]
    public void Hrots_times_and_blast_points_are_copied_exactly_and_a_save_stays_zero()
    {
        using var h = new Stage();
        var passage = new WallBreach(h.Panel(1, 2), 1f, 3f, 0f, 2.2f, BreachKind.Passage);
        var hole = new WallBreach(h.Panel(1, 0), 4f, 4.6f, 1.1f, 1.6f, BreachKind.Hole);
        var fromSave = new WallBreach(h.Panel(1, 3), 0f, 1f, 0f, 1f, BreachKind.Hole);
        h.Stamped(1, 401.25, (passage, 312.4, new Vector3(10.5f, -20.25f, 3.75f)), (hole, 99.125, new Vector3(-1f, 2f, 3f)), (fromSave, 0d, default));
        h.StampedWindows(1, (new WindowEntry(h.Panel(1, 1), 0, 1, 40, 50, 0, 0), 55.5), (new WindowEntry(h.Panel(1, 3), 2, 3, 0, 0, 0, 0), 0d));

        var sent = Assert.Single(h.Gather().Of<HrotBuildingDamage>());

        Assert.Equal((true, 401.25), (sent.Collapsed, sent.CollapsedAt));
        Assert.Equal(
            [new HrotBreach(0, 4f, 4.6f, 1.1f, 1.6f, "Hole", 99.125, new HrotVec3(-1, 2, 3)), new HrotBreach(2, 1f, 3f, 0f, 2.2f, "Passage", 312.4, new HrotVec3(10.5, -20.25, 3.75)),
             new HrotBreach(3, 0f, 1f, 0f, 1f, "Hole", 0d, HrotVec3.Zero)], sent.Breaches);
        Assert.Equal([new HrotWindowDamage(1, 0, 1, 40, 50, 0, 0, 55.5), new HrotWindowDamage(3, 2, 3, 0, 0, 0, 0, 0d)], sent.Windows);
        Assert.Empty(h.Gather(2.0).Of<HrotBuildingDamage>());   // nothing changed: nothing sent
    }

    /// <summary>T-074: a change of a time alone (a breach widened, a window hit again) is a change - the building is sent again; merged entities keep the later breach time and the stamp of the harder-hit window.</summary>
    [Fact]
    public void A_time_alone_is_a_change_and_merged_entities_keep_the_later_breach_and_the_harder_hit_window()
    {
        using var h = new Stage();
        var breach = new WallBreach(h.Panel(1, 0), 1f, 2f, 0f, 2f, BreachKind.Hole);
        var first = h.Stamped(1, 0d, (breach, 10.0, new Vector3(1, 1, 1)));
        h.StampedWindows(1, (new WindowEntry(h.Panel(1, 1), 0, 1, 40, 50, 0, 0), 20.0));
        Assert.Equal(10.0, Assert.Single(Assert.Single(h.Gather().Of<HrotBuildingDamage>()).Breaches).MadeAt);

        // the same breach in a second entity, made later: the later time and its blast point win
        h.Stamped(1, 0d, (breach, 30.0, new Vector3(9, 8, 7)));
        var merged = Assert.Single(h.Gather(2.0).Of<HrotBuildingDamage>());
        var kept = Assert.Single(merged.Breaches);
        Assert.Equal((30.0, new HrotVec3(9, 8, 7)), (kept.MadeAt, kept.Blast));

        // the window is hit harder in another entity: its stamp follows the entry that wins; an equal hit keeps the first
        h.StampedWindows(1, (new WindowEntry(h.Panel(1, 1), 0, 3, 0, 0, 0, 0), 77.0));
        Assert.Equal((byte)3, Assert.Single(Assert.Single(h.Gather(3.0).Of<HrotBuildingDamage>()).Windows).Hits);
        h.StampedWindows(1, (new WindowEntry(h.Panel(1, 1), 0, 3, 0, 0, 0, 0), 99.0));
        Assert.Empty(h.Gather(4.0).Of<HrotBuildingDamage>());   // an equal hit changes nothing the pivot says
        var after = h.World.GetComponentRO<BuildingDamage>(first);
        Assert.Equal(10.0, after.MadeAtOf(0).Seconds);   // (HROT's own entity was not touched)

        // the first entity's time moves alone (a widening HROT records): sent again
        var moved = h.World.GetComponentRO<BuildingDamage>(first);
        moved.SetStamp(0, 40.0, new Vector3(5, 5, 5));
        h.World.SetComponent(first, moved);
        Assert.Equal(40.0, Assert.Single(Assert.Single(h.Gather(5.0).Of<HrotBuildingDamage>()).Breaches).MadeAt);
    }

    /// <summary>T-074: a collapse time alone is a change; a building not collapsed sends 0 whatever a stale value says.</summary>
    [Fact]
    public void The_collapse_time_travels_and_a_standing_building_sends_zero()
    {
        using var h = new Stage();
        var e = h.Stamped(0, 0d);
        var standing = h.World.GetComponentRO<BuildingDamage>(e);
        standing.CollapsedAt = 55.0;   // stale: HROT clears it when the building is not collapsed, the gateway does not trust it either
        h.World.SetComponent(e, standing);
        h.Breaches(0, new WallBreach(h.Panel(0, 1), 1f, 2f, 0f, 2f, BreachKind.Hole));
        var first = Assert.Single(h.Gather().Of<HrotBuildingDamage>());
        Assert.Equal((false, 0d), (first.Collapsed, first.CollapsedAt));

        var c = h.World.GetComponentRO<BuildingDamage>(e);
        c.Collapsed = 1;
        c.CollapsedAt = 123.5;
        h.World.SetComponent(e, c);
        var second = Assert.Single(h.Gather(2.0).Of<HrotBuildingDamage>());
        Assert.Equal((true, 123.5), (second.Collapsed, second.CollapsedAt));
    }

    private static Crater Hole(float x, float y, double born = 0d, CraterKind kind = CraterKind.Small, float radius = 2f) =>
        new(new Vector2(x, y), radius, 0.75f, 0.25f, new SimInstant(born), kind);

    /// <summary>T-074 (DD-030 D-2): a cell with craters is ONE message with the craters in HROT's slot order, z = the BARE ground under each centre; unchanged = nothing; a changed cell is sent whole again.</summary>
    [Fact]
    public void A_crater_cell_is_one_message_with_its_craters_in_hrots_order_standing_on_the_bare_ground()
    {
        using var h = new Stage(ground: 7.5f);
        var cell = h.Craters(1, -1, Hole(70f, -10f, 312.4, CraterKind.Large, 3.5f), Hole(65f, -20f, 100.0), Hole(66f, -21f, 0d));   // HROT's order is the order they were made in: NOT sorted by anything

        var sent = Assert.Single(h.Gather().Of<HrotGroundDamage>());

        Assert.Equal("1,-1", sent.Cell);
        Assert.Equal(
            [new HrotCrater(new HrotVec3(70, -10, 7.5), 3.5f, 0.75f, 0.25f, 312.4, "Large"), new HrotCrater(new HrotVec3(65, -20, 7.5), 2f, 0.75f, 0.25f, 100.0, "Small"),
             new HrotCrater(new HrotVec3(66, -21, 7.5), 2f, 0.75f, 0.25f, 0d, "Small")], sent.Craters);
        Assert.Empty(h.Gather(2.0).Of<HrotGroundDamage>());   // nothing changed: nothing sent

        var more = h.World.GetComponentRO<GroundDamage>(cell);
        GroundDamages.Add(ref more, Hole(80f, -30f, 500.0), out _);
        h.World.SetComponent(cell, more);
        var again = Assert.Single(h.Gather(3.0).Of<HrotGroundDamage>());
        Assert.Equal(4, again.Craters.Count);
        Assert.Equal(new HrotVec3(80, -30, 7.5), again.Craters[3].Centre);
    }

    /// <summary>T-074: two cells are two messages; the cell key is the record's own (negative cells included); no terrain = z 0; the z is the bare ground, a crater's bowl is not in it.</summary>
    [Fact]
    public void Cells_are_apart_a_world_without_terrain_has_z_zero_and_the_z_is_bare_ground()
    {
        using var h = new Stage(ground: 7.5f);
        h.Craters(0, 0, Hole(10f, 10f));
        h.Craters(-2, 5, Hole(-100f, 330f));
        var sent = h.Gather().Of<HrotGroundDamage>().OrderBy(g => g.Cell, StringComparer.Ordinal).ToList();
        Assert.Equal(["-2,5", "0,0"], sent.Select(g => g.Cell).ToArray());
        Assert.All(sent, g => Assert.Equal(7.5, Assert.Single(g.Craters).Centre.Z));   // the bowl (0.75 m deep) is not subtracted

        using var bare = new Stage(withTerrain: false);
        bare.Craters(0, 0, Hole(10f, 10f));
        Assert.Equal(0.0, Assert.Single(Assert.Single(bare.Gather().Of<HrotGroundDamage>()).Craters).Centre.Z);
    }

    /// <summary>T-074: a cell several entities hold (an overflow, another node's craters) is ONE message, content-sorted, a crater held twice once; the visiting order of the entities does not matter.</summary>
    [Fact]
    public void A_cell_held_by_several_entities_is_united_sorted_and_without_a_crater_twice()
    {
        using var h = new Stage();
        var late = Hole(5f, 5f, 300.0);
        var early = Hole(9f, 9f, 100.0);
        h.Craters(0, 0, late, early);
        h.Craters(0, 0, early, Hole(1f, 1f, 200.0));   // `early` again: the same crater

        var sent = Assert.Single(h.Gather().Of<HrotGroundDamage>());

        Assert.Equal([100.0, 200.0, 300.0], sent.Craters.Select(c => c.BornAt).ToArray());
        Assert.Equal(3, sent.Craters.Count);
    }

    /// <summary>T-074: a cell whose craters are gone is sent empty once; a world boundary sends everything again.</summary>
    [Fact]
    public void A_cell_that_is_emptied_is_sent_empty_once_and_everything_is_sent_again_after_a_boundary()
    {
        using var h = new Stage();
        var cell = h.Craters(3, 4, Hole(200f, 270f));
        Assert.Single(h.Gather().Of<HrotGroundDamage>());

        h.World.DestroyEntity(cell);   // a reload
        var gone = Assert.Single(h.Gather(2.0).Of<HrotGroundDamage>());
        Assert.Equal(("3,4", 0), (gone.Cell, gone.Craters.Count));
        Assert.Empty(h.Gather(3.0).Of<HrotGroundDamage>());

        h.Craters(3, 4, Hole(200f, 270f));
        Assert.Single(h.Gather(4.0).Of<HrotGroundDamage>());
        h.Adapter.Reset(h.World);   // a world boundary: the gateway is new
        Assert.Single(h.Gather(5.0).Of<HrotGroundDamage>());
    }

    /// <summary>T-074: an entity with no crater is no cell; the ground entities are still not entities to show.</summary>
    [Fact]
    public void An_empty_ground_entity_sends_nothing_and_the_entities_are_not_published()
    {
        using var h = new Stage();
        h.Craters(0, 0);
        var sent = h.Gather();
        Assert.Empty(sent.Of<HrotGroundDamage>());
        Assert.Empty(sent.Of<HrotEntityState>());
    }

    /// <summary>
    /// T-074, invariants (R-064), seeded: whatever the entities hold, the building message equals the brute-force union - per distinct breach the LATEST time and its blast point, per window the stamp of the
    /// entry with most hits, the collapse time the latest of the collapsed entities - and after it a steady gather sends nothing.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Random_damage_entities_unite_to_the_brute_force_union_of_their_stamps(int seed)
    {
        var rnd = new Random(seed);
        using var h = new Stage();
        double clock = 1;   // every stamp distinct, so "the latest" is unambiguous
        // the candidate breaches of building 1: 4 panels x 2 slots, never overlapping within a panel, the same geometry in every entity
        WallBreach Candidate(int panel, int slot) => new(h.Panel(1, panel), slot == 0 ? 1f : 4f, slot == 0 ? 2f : 5f, 0f, 2f, BreachKind.Hole);
        var expectedBreach = new Dictionary<(int, int), (double At, Vector3 Blast)>();
        var expectedWindow = new Dictionary<(int, int), (int Hits, List<double> Ats)>();
        double collapsedAt = 0;
        int entities = rnd.Next(1, 5);
        for (int n = 0; n < entities; n++)
        {
            var list = new List<(WallBreach, double, Vector3)>();
            for (int panel = 0; panel < 4; panel++)
            {
                for (int slot = 0; slot < 2; slot++)
                {
                    if (rnd.Next(3) != 0)
                    {
                        continue;
                    }

                    double at = rnd.Next(4) == 0 ? 0d : clock++;
                    var blast = at == 0 ? default : new Vector3(rnd.Next(100), rnd.Next(100), rnd.Next(10));
                    list.Add((Candidate(panel, slot), at, blast));
                    if (!expectedBreach.TryGetValue((panel, slot), out var have) || at > have.At)
                    {
                        expectedBreach[(panel, slot)] = (at, blast);
                    }
                }
            }

            double collapse = rnd.Next(3) == 0 ? clock++ : 0d;
            collapsedAt = Math.Max(collapsedAt, collapse);
            h.Stamped(1, collapse, [.. list]);

            var windows = new List<(WindowEntry, double)>();
            for (int panel = 0; panel < 4; panel++)
            {
                for (int opening = 0; opening < 2; opening++)
                {
                    if (rnd.Next(3) != 0)
                    {
                        continue;
                    }

                    int hits = rnd.Next(1, 4);
                    double at = rnd.Next(4) == 0 ? 0d : clock++;
                    windows.Add((new WindowEntry(h.Panel(1, panel), (byte)opening, (byte)hits, 0, 0, 0, 0), at));
                    var key = (panel, opening);
                    if (!expectedWindow.TryGetValue(key, out var w) || hits > w.Hits)
                    {
                        expectedWindow[key] = (hits, [at]);
                    }
                    else if (hits == w.Hits)
                    {
                        w.Ats.Add(at);   // equal hits: HROT's save rule keeps one of them, whichever the query visits first
                    }
                }
            }

            h.StampedWindows(1, [.. windows]);
        }

        var sent = h.Gather().Of<HrotBuildingDamage>();
        if (expectedBreach.Count == 0 && expectedWindow.Count == 0 && collapsedAt == 0)
        {
            Assert.Empty(sent);
            return;
        }

        var message = Assert.Single(sent);
        Assert.Equal(expectedBreach.Count, message.Breaches.Count);
        foreach (var b in message.Breaches)
        {
            int panel = Enumerable.Range(0, 4).First(p => h.Panel(1, p) == h.Panel(1, b.Panel));
            var want = expectedBreach[(panel, b.From < 3f ? 0 : 1)];
            Assert.Equal((want.At, new HrotVec3(want.Blast.X, want.Blast.Y, want.Blast.Z)), (b.MadeAt, b.Blast));
        }

        Assert.Equal(expectedWindow.Count, message.Windows.Count);
        foreach (var w in message.Windows)
        {
            var want = expectedWindow[(w.Panel, w.Opening)];
            Assert.Equal(want.Hits, w.Hits);
            Assert.Contains(w.HitAt, want.Ats);
        }

        Assert.Equal(message.Collapsed ? collapsedAt : 0d, message.CollapsedAt);
        Assert.Empty(h.Gather(2.0).Of<HrotBuildingDamage>());   // steady: nothing
    }

    /// <summary>
    /// T-074, invariants (R-064), seeded: per cell the message is the brute-force union of its entities' craters (a crater held twice once, a multi-entity cell content-sorted), nothing is sent while nothing
    /// changes, and after entities are removed exactly the cells that changed are sent again - a cell left with none empty, once.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public void Random_ground_entities_unite_per_cell_and_only_changed_cells_are_sent(int seed)
    {
        var rnd = new Random(seed);
        using var h = new Stage();
        double born = 1;
        var held = new List<(Entity Entity, (int X, int Y) Cell, Crater[] Craters)>();
        var pool = new Dictionary<(int, int), Crater[]>();
        Crater[] Pool((int X, int Y) cell)
        {
            if (!pool.TryGetValue(cell, out var p))
            {
                pool[cell] = p = [.. Enumerable.Range(0, 20).Select(k => Hole(cell.X * 64f + 1f + k * 3f, cell.Y * 64f + 5f, born++, k % 3 == 0 ? CraterKind.Large : CraterKind.Small, 1f + k * 0.1f))];
            }

            return p;
        }

        for (int n = rnd.Next(1, 9); n > 0; n--)
        {
            var cell = (rnd.Next(-2, 3), rnd.Next(-2, 3));
            var craters = Pool(cell).OrderBy(_ => rnd.Next()).Take(rnd.Next(1, 7)).ToArray();
            held.Add((h.Craters(cell.Item1, cell.Item2, craters), cell, craters));
        }

        Dictionary<string, HrotCrater[]> Expected()
        {
            var result = new Dictionary<string, HrotCrater[]>();
            foreach (var group in held.GroupBy(x => x.Cell))
            {
                var union = group.SelectMany(x => x.Craters).Distinct().ToList();
                if (group.Count() > 1)
                {
                    union = [.. union.OrderBy(c => c.BornAt.Seconds)];   // distinct births: the content order is the birth order
                }

                result[GroundDamageKey(group.Key)] = [.. union.Select(c => new HrotCrater(new HrotVec3(c.Centre.X, c.Centre.Y, 7.5), c.Radius, c.Depth, c.Rim, c.BornAt.Seconds, c.Kind.ToString()))];
            }

            return result;
        }

        var want = Expected();
        var sent = h.Gather().Of<HrotGroundDamage>();
        Assert.Equal(want.Count, sent.Count);
        foreach (var m in sent)
        {
            Assert.Equal(want[m.Cell], m.Craters);
        }

        Assert.Empty(h.Gather(2.0).Of<HrotGroundDamage>());   // steady

        // remove some entities: only the cells they were in change
        var before = want;
        var removed = held.Where(_ => rnd.Next(2) == 0).ToList();
        foreach (var r in removed)
        {
            h.World.DestroyEntity(r.Entity);
            held.Remove(r);
        }

        want = Expected();
        var after = h.Gather(3.0).Of<HrotGroundDamage>();
        foreach (string cell in before.Keys.Union(want.Keys))
        {
            bool changed = !want.TryGetValue(cell, out var now) ? true : !before.TryGetValue(cell, out var was) || !was.SequenceEqual(now);
            var got = after.Where(m => m.Cell == cell).ToList();
            if (!changed)
            {
                Assert.Empty(got);
            }
            else
            {
                Assert.Equal(want.TryGetValue(cell, out var expected) ? expected : [], Assert.Single(got).Craters);   // a cell left with none: empty, once
            }
        }

        Assert.Empty(h.Gather(4.0).Of<HrotGroundDamage>());
    }

    private static string GroundDamageKey((int X, int Y) cell) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{cell.X},{cell.Y}");

    /// <summary>
    /// T-074 (HOT path, 0 B steady): with damage and craters standing and nothing changing, a gather of the buildings and of the ground allocates nothing and sends nothing - the union is compared with
    /// what was sent without building a message (measured before: 2.3 KB a gather for one building). Mutation: build the message first and compare it (allocates), or name a kind with ToString (boxes).
    /// </summary>
    [Fact]
    public void A_steady_gather_of_building_damage_and_craters_allocates_nothing_and_sends_nothing()
    {
        using var h = new Stage();
        h.Stamped(1, 61.5, (new WallBreach(h.Panel(1, 0), 1f, 2f, 0f, 2f, BreachKind.Hole), 5.0, new Vector3(1, 2, 3)), (new WallBreach(h.Panel(1, 2), 3f, 5f, 0f, 2f, BreachKind.Passage), 6.0, new Vector3(4, 5, 6)));
        h.StampedWindows(1, (new WindowEntry(h.Panel(1, 1), 0, 1, 40, 50, 0, 0), 20.0));
        h.Craters(0, 0, Hole(1f, 1f, 3.0, CraterKind.Large), Hole(2f, 2f, 4.0));
        h.Craters(0, 0, Hole(9f, 9f, 8.0));   // a second entity of the same cell: the sorted union is compared too
        var inbound = new InMemoryInbound(new SourceId(99), "steady");
        var buildings = new BuildingDamageGather();
        var ground = new GroundDamageGather();
        buildings.Gather(h.World, inbound);
        ground.Gather(h.World, inbound);
        var first = new Capture();
        Assert.Equal(2, inbound.Drain(first, int.MaxValue));   // the building and the cell, once
        Assert.Equal(3, first.Of<HrotGroundDamage>().Single().Craters.Count);

        for (int i = 0; i < 50; i++)
        {
            buildings.Gather(h.World, inbound);
            ground.Gather(h.World, inbound);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            buildings.Gather(h.World, inbound);
            ground.Gather(h.World, inbound);
        }

        long perGather = (GC.GetAllocatedBytesForCurrentThread() - before) / 300;
        Assert.Equal(0L, perGather);
        Assert.Equal(0, inbound.Drain(new Capture(), int.MaxValue));
    }

    // ---- fire (DD-033, T-078) -------------------------------------------------------------------------------------------------

    /// <summary>A damage record of <paramref name="building"/> that also holds HROT's FireState, one spot per <c>(since, seconds)</c>, as <c>BuildingDamageSystem.ApplyFire</c> writes it.</summary>
    private static Entity Burning(Stage h, int building, params (double Since, float Seconds)[] spots)
    {
        if (!h.World.IsComponentTypeRegistered<FireState>())
        {
            h.World.RegisterComponent<FireState>();
        }

        var fire = new FireState();
        foreach (var (since, seconds) in spots)
        {
            Assert.True(fire.TryAdd(new FireSpot(1f, 2f, 3f, 2f, new SimInstant(since), seconds, 1f), since));
        }

        var e = h.World.CreateEntity();
        h.World.AddComponent(e, new BuildingDamage { Building = building });
        h.World.AddComponent(e, fire);
        return e;
    }

    [Fact]
    public void A_house_alight_carries_its_fire_spots_start_and_duration_and_one_never_burned_carries_none()
    {
        using var h = new Stage();
        Burning(h, 0, (15.0, 240f));
        h.Breaches(1, new WallBreach(h.Panel(1, 0), 1f, 2f, 0f, 2f, BreachKind.Hole));   // the other house: damaged, never burned

        var all = h.Gather().Of<HrotBuildingDamage>();

        var burnt = Assert.Single(all, d => d.Building.EndsWith("H1"));   // a record that holds only a fire is damage too
        Assert.Equal((15.0, 240f), (burnt.FireSince, burnt.FireSeconds));
        var brick = Assert.Single(all, d => d.Building.EndsWith("H2"));
        Assert.Equal((0d, 0f), (brick.FireSince, brick.FireSeconds));
        Assert.Empty(h.Gather(2.0).Of<HrotBuildingDamage>());   // steady: nothing
    }

    [Fact]
    public void Several_spots_or_records_of_one_building_give_the_earliest_start_to_the_latest_end_and_a_spot_that_never_burned_counts_for_nothing()
    {
        using var h = new Stage();
        Burning(h, 0, (30.0, 100f), (10.0, 50f));   // ends 130 and 60
        Burning(h, 0, (20.0, 300f));                 // another record of the same house: ends 320

        var sent = Assert.Single(h.Gather().Of<HrotBuildingDamage>());

        Assert.Equal((10.0, 310f), (sent.FireSince, sent.FireSeconds));   // 10 .. 320

        using var g = new Stage();
        Burning(g, 0, (0.0, 0f));   // a slot that never burned
        Assert.Empty(g.Gather().Of<HrotBuildingDamage>());
    }

    [Fact]
    public void A_fire_alone_changing_sends_the_building_again_and_a_record_that_goes_disposes_it()
    {
        using var h = new Stage();
        var e = Burning(h, 0, (15.0, 240f));
        Assert.Single(h.Gather().Of<HrotBuildingDamage>());

        var spread = h.World.GetComponentRO<FireState>(e);
        Assert.True(spread.TryAdd(new FireSpot(0f, 0f, 0f, 1f, new SimInstant(15.0), 500f, 1f), 16.0));   // burns longer now
        h.World.SetComponent(e, spread);
        var again = Assert.Single(h.Gather(2.0).Of<HrotBuildingDamage>());
        Assert.Equal((15.0, 500f), (again.FireSince, again.FireSeconds));

        h.World.DestroyEntity(e);
        var gone = Assert.Single(h.Gather(3.0).Of<HrotBuildingDamage>());
        Assert.Equal((false, 0, 0d, 0f), (gone.Collapsed, gone.Breaches.Count, gone.FireSince, gone.FireSeconds));
    }

    [Fact]
    public void A_steady_gather_of_a_burning_building_allocates_nothing()
    {
        using var h = new Stage();
        Burning(h, 0, (15.0, 240f));
        var inbound = new InMemoryInbound(new SourceId(99), "steady");
        var buildings = new BuildingDamageGather();
        buildings.Gather(h.World, inbound);
        Assert.Equal(1, inbound.Drain(new Capture(), int.MaxValue));
        for (int i = 0; i < 50; i++)
        {
            buildings.Gather(h.World, inbound);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            buildings.Gather(h.World, inbound);
        }

        Assert.Equal(0L, (GC.GetAllocatedBytesForCurrentThread() - before) / 300);
        Assert.Equal(0, inbound.Drain(new Capture(), int.MaxValue));
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
