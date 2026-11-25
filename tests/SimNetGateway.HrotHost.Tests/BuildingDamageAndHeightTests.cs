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

        public Stage(float ground = 7.5f, bool withTerrain = true, string? json = null)
            : base(dis: true)
        {
            Terrain = TerrainWorldParser.Parse(json ?? TwoHouses, "range");
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

        /// <summary>A damage entity whose breaches carry HROT's full origin (T-075): each is (breach, origin = time, blast, cause, kg, actor); the collapse, when given, is (time, cause, blast, kg).</summary>
        public Entity Origins((double At, BreachCause Cause, Vector3 Blast, float Kg)? collapse, int building, params (WallBreach Breach, BreachOrigin Origin)[] breaches)
        {
            var d = new BuildingDamage { Building = building };
            foreach (var (b, origin) in breaches)
            {
                BuildingDamages.Add(ref d, in b, null, in origin);
            }

            if (collapse is { } c)
            {
                d.Collapsed = 1;
                d.CollapsedAt = c.At;
                d.SetCollapse(c.Cause, c.Blast, c.Kg);
            }

            var e = World.CreateEntity();
            World.AddComponent(e, d);
            return e;
        }

        /// <summary>The pivot breach HROT's record and rules say <paramref name="global"/> (a breach with the GLOBAL panel) must become: the panel within the building, HROT's centre and outward normal, the origin's fields.</summary>
        public HrotBreach Expect(WallBreach global, double at = 0d, Vector3 blast = default, BreachCause cause = BreachCause.Unknown, float kg = 0f, int actor = 0)
        {
            var centre = WallGroups.BreachCentre(Terrain, in global);
            return new HrotBreach(BuildingPanelIndex.ToLocal(Terrain, global.Panel), global.From, global.To, global.Bottom, global.Top, global.Kind.ToString(), at, new HrotVec3(blast.X, blast.Y, blast.Z),
                cause.ToString(), kg, actor, new HrotVec3(centre.X, centre.Y, centre.Z), NormalOf(global.Panel));
        }

        /// <summary>HROT's outward normal of a wall panel (global index) as the pivot carries it: horizontal, z 0.</summary>
        public HrotVec3 NormalOf(int globalPanel)
        {
            var n = WallGroups.OutwardNormal(Terrain, globalPanel);
            return new HrotVec3(n.X, n.Y, 0d);
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
        Assert.Equal([h.Expect(new WallBreach(h.Panel(1, 0), 4f, 4.6f, 1.1f, 1.6f, BreachKind.Hole)), h.Expect(new WallBreach(h.Panel(1, 2), 1f, 3f, 0f, 2.2f, BreachKind.Passage))], sent.Breaches);   // within the building, sorted
        // (this world's panels have no openings, so no window centre: the normal is still the wall's)
        Assert.Equal([new HrotWindowDamage(1, 0, 1, 40, 50, 0, 0, 0d, default, h.NormalOf(h.Panel(1, 1))), new HrotWindowDamage(3, 2, 3, 0, 0, 0, 0, 0d, default, h.NormalOf(h.Panel(1, 3)))], sent.Windows);

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
        Assert.Equal([h.Expect(new WallBreach(h.Panel(0, 1), 1f, 2f, 0f, 2f, BreachKind.Hole))], sent[0].Breaches);
        Assert.Empty(sent[0].Windows);
        Assert.Equal([h.Expect(new WallBreach(h.Panel(1, 0), 1f, 2f, 0f, 2f, BreachKind.Hole)), h.Expect(new WallBreach(h.Panel(1, 3), 5f, 6f, 0f, 2f, BreachKind.Passage))], sent[1].Breaches);   // distinct, from both entities
        Assert.Equal([new HrotWindowDamage(1, 0, 3, 0, 0, 0, 0, 0d, default, h.NormalOf(h.Panel(1, 1))), new HrotWindowDamage(2, 1, 1, 9, 9, 0, 0, 0d, default, h.NormalOf(h.Panel(1, 2)))], sent[1].Windows);
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
        Assert.Equal([h.Expect(hole, 99.125, new Vector3(-1, 2, 3)), h.Expect(passage, 312.4, new Vector3(10.5f, -20.25f, 3.75f)), h.Expect(fromSave)], sent.Breaches);
        Assert.Equal([new HrotWindowDamage(1, 0, 1, 40, 50, 0, 0, 55.5, default, h.NormalOf(h.Panel(1, 1))), new HrotWindowDamage(3, 2, 3, 0, 0, 0, 0, 0d, default, h.NormalOf(h.Panel(1, 3)))], sent.Windows);
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

    // ---- T-075 (DD-034): what made a breach, and where it is in the world ------------------------------------------------------------

    private const string WindowHouse = """
        { "type": "FeatureCollection", "features": [
          { "type": "Feature", "properties": { "kind": "building", "label": "W1",
              "building": { "footprint": [[0,0],[10,0],[10,8],[0,8]],
                "storeys": [ { "height": 3, "walls": [
                  { "from": [0,0], "to": [10,0], "thickness": 0.3, "material": "brick", "openings": [ { "kind": "window", "at": 3, "width": 1.4 }, { "kind": "window", "at": 6, "width": 1 } ] },
                  { "from": [10,0], "to": [10,8], "thickness": 0.3, "material": "brick", "openings": [ { "kind": "window", "at": 2, "width": 2 } ] },
                  { "from": [10,8], "to": [0,8], "thickness": 0.3, "material": "brick", "openings": [ { "kind": "window", "at": 1, "width": 1.5 } ] },
                  { "from": [0,8], "to": [0,0], "thickness": 0.3, "material": "brick" } ] } ] } },
            "geometry": { "type": "Point", "coordinates": [100, 20] } } ] }
        """;

    private static BreachOrigin Origin(double at, Vector3 blast, BreachCause cause, float kg = 0f, int actor = 0) => new(new SimInstant(at), blast, cause, kg, actor);

    /// <summary>
    /// T-075 (D-1): every breach's cause, kg and actor equal HROT's record on the same world - a blast (Detonation, kg, actor 0), a ram (Ram, the hull's id), a blade, a push, a breach from a save (cause kept,
    /// actor 0); and its Centre / Normal equal HROT's ONE rules WallGroups.BreachCentre / OutwardNormal on the same panel; a new cause alone is a change that is sent, a steady gather sends nothing.
    /// </summary>
    [Fact]
    public void Each_breach_carries_hrots_cause_kg_actor_and_hrots_own_centre_and_outward_normal()
    {
        using var h = new Stage();
        var blast = new WallBreach(h.Panel(1, 0), 1f, 2f, 0f, 2f, BreachKind.Hole);
        var ram = new WallBreach(h.Panel(1, 1), 1f, 3f, 0f, 2.2f, BreachKind.Passage);
        var blade = new WallBreach(h.Panel(1, 2), 4f, 6f, 0f, 2.5f, BreachKind.Passage);
        var push = new WallBreach(h.Panel(0, 3), 2f, 3f, 0.5f, 1.5f, BreachKind.Hole);
        var save = new WallBreach(h.Panel(1, 3), 5f, 6f, 0f, 1f, BreachKind.Hole);
        var e = h.Origins(null, 1, (blast, Origin(10.5, new Vector3(5, 6, 7), BreachCause.Detonation, 12.5f)), (ram, Origin(20.0, new Vector3(1, 2, 3), BreachCause.Ram, 0f, 4711)),
            (blade, Origin(30.0, new Vector3(8, 8, 1), BreachCause.Blade, 0f, 4712)), (save, Origin(0d, default, BreachCause.Detonation, 3f)));
        h.Origins(null, 0, (push, Origin(40.0, new Vector3(0, 0, 0), BreachCause.Push, 0f, 4713)));

        var sent = h.Gather().Of<HrotBuildingDamage>().OrderBy(b => b.Building, StringComparer.Ordinal).ToList();

        var h2 = sent.Single(b => b.Building == "range/H2").Breaches;
        Assert.Equal([h.Expect(blast, 10.5, new Vector3(5, 6, 7), BreachCause.Detonation, 12.5f), h.Expect(ram, 20.0, new Vector3(1, 2, 3), BreachCause.Ram, 0f, 4711),
                      h.Expect(blade, 30.0, new Vector3(8, 8, 1), BreachCause.Blade, 0f, 4712), h.Expect(save, 0d, default, BreachCause.Detonation, 3f)], h2);
        Assert.Equal([h.Expect(push, 40.0, default, BreachCause.Push, 0f, 4713)], sent.Single(b => b.Building == "range/H1").Breaches);
        // the property over every breach sent: Centre / Normal ARE HROT's rules, the normal is a unit horizontal vector pointing away from the house's centre, the centre lies on its wall panel
        foreach (var b in sent.SelectMany(s => s.Breaches.Select(x => (s, x))))
        {
            int building = b.s.Building == "range/H1" ? 0 : 1;
            int panel = h.Panel(building, b.x.Panel);
            var centre = WallGroups.BreachCentre(h.Terrain, new WallBreach(panel, b.x.From, b.x.To, b.x.Bottom, b.x.Top, BreachKind.Hole));
            Assert.Equal(new HrotVec3(centre.X, centre.Y, centre.Z), b.x.Centre);
            Assert.Equal(0d, b.x.Normal.Z);
            Assert.Equal(1.0, Math.Sqrt(b.x.Normal.X * b.x.Normal.X + b.x.Normal.Y * b.x.Normal.Y), 5);
            var house = h.Terrain.Buildings[building].Position + new Vector2(5f, 4f);   // the footprint's middle (10 x 8 m from the position)
            Assert.True((centre.X - house.X) * b.x.Normal.X + (centre.Y - house.Y) * b.x.Normal.Y > 0, "outward");
        }

        Assert.Empty(h.Gather(2.0).Of<HrotBuildingDamage>());   // steady: nothing

        // a cause alone is a change: the ram is re-recorded as a blade (same time, blast, kg, actor) - sent again; and so is the actor alone, and the kg alone
        var d = h.World.GetComponentRO<BuildingDamage>(e);
        int slotOfRam = Enumerable.Range(0, d.Count).First(i => d.Get(i).Panel == ram.Panel);
        var o = d.OriginOf(slotOfRam);
        d.SetOrigin(slotOfRam, o with { Cause = BreachCause.Blade });
        h.World.SetComponent(e, d);
        Assert.Equal(nameof(BreachCause.Blade), Assert.Single(h.Gather(3.0).Of<HrotBuildingDamage>()).Breaches.Single(x => x.Actor == 4711).Cause);
        d.SetOrigin(slotOfRam, o with { Cause = BreachCause.Blade, Actor = 4799 });
        h.World.SetComponent(e, d);
        Assert.Single(h.Gather(4.0).Of<HrotBuildingDamage>());
        d.SetOrigin(slotOfRam, o with { Cause = BreachCause.Blade, Actor = 4799, ExplosiveKg = 1f });
        h.World.SetComponent(e, d);
        Assert.Single(h.Gather(5.0).Of<HrotBuildingDamage>());
        Assert.Empty(h.Gather(6.0).Of<HrotBuildingDamage>());
    }

    /// <summary>T-075: HROT's merge reaches the pivot - a ram widened by a blast is one Detonation hole with the larger kg and the newest contact's actor; two entities holding one breach merge the same way.</summary>
    [Fact]
    public void A_ram_widened_by_a_blast_is_a_detonation_with_the_larger_kg_and_two_entities_merge_by_hrots_rule()
    {
        using var h = new Stage();
        var rammed = new WallBreach(h.Panel(1, 0), 1f, 3f, 0f, 2f, BreachKind.Hole);
        var blasted = new WallBreach(h.Panel(1, 0), 2.5f, 4f, 0f, 2f, BreachKind.Hole);
        var d = new BuildingDamage { Building = 1 };
        BuildingDamages.Add(ref d, in rammed, null, Origin(10.0, new Vector3(1, 1, 1), BreachCause.Ram, 0f, 4711));
        BuildingDamages.Add(ref d, in blasted, null, Origin(20.0, new Vector3(2, 2, 2), BreachCause.Detonation, 8f));   // overlaps: ONE hole 1..4
        var e = h.World.CreateEntity();
        h.World.AddComponent(e, d);
        var merged = new WallBreach(h.Panel(1, 0), 1f, 4f, 0f, 2f, BreachKind.Hole);

        var sent = Assert.Single(h.Gather().Of<HrotBuildingDamage>());
        Assert.Equal([h.Expect(merged, 20.0, new Vector3(2, 2, 2), BreachCause.Detonation, 8f, 4711)], sent.Breaches);   // HROT keeps the ram's actor (the newer names none)

        // the same hole in a second entity, a bigger charge earlier: Detonation stays, the larger kg wins, the later time and its blast point stay
        h.Origins(null, 1, (merged, Origin(5.0, new Vector3(9, 9, 9), BreachCause.Detonation, 30f)));
        var both = Assert.Single(h.Gather(2.0).Of<HrotBuildingDamage>());
        Assert.Equal([h.Expect(merged, 20.0, new Vector3(2, 2, 2), BreachCause.Detonation, 30f, 4711)], both.Breaches);
    }

    /// <summary>T-075 (D-3): a collapse carries HROT's cause, blast point and kg; a standing building sends Unknown / 0 whatever stale values the record holds; the collapse fields alone are a change; a loaded save keeps the cause with a zero blast.</summary>
    [Fact]
    public void A_collapse_carries_hrots_cause_blast_point_and_kg_and_a_standing_building_sends_none()
    {
        using var h = new Stage();
        var e = h.Origins((401.25, BreachCause.Detonation, new Vector3(61.5f, 22.25f, 4f), 250f), 1);
        var sent = Assert.Single(h.Gather().Of<HrotBuildingDamage>());
        Assert.Equal((true, 401.25, nameof(BreachCause.Detonation), new HrotVec3(61.5, 22.25, 4), 250f), (sent.Collapsed, sent.CollapsedAt, sent.CollapseCause, sent.CollapseBlast, sent.CollapseKg));
        Assert.Empty(h.Gather(2.0).Of<HrotBuildingDamage>());

        var d = h.World.GetComponentRO<BuildingDamage>(e);
        d.SetCollapse(BreachCause.Detonation, new Vector3(61.5f, 22.25f, 4f), 300f);   // the kg alone
        h.World.SetComponent(e, d);
        Assert.Equal(300f, Assert.Single(h.Gather(3.0).Of<HrotBuildingDamage>()).CollapseKg);
        d.SetCollapse(BreachCause.Detonation, new Vector3(62f, 22.25f, 4f), 300f);   // the blast point alone
        h.World.SetComponent(e, d);
        Assert.Equal(62.0, Assert.Single(h.Gather(4.0).Of<HrotBuildingDamage>()).CollapseBlast.X);
        d.SetCollapse(BreachCause.Unknown, new Vector3(62f, 22.25f, 4f), 300f);   // the cause alone
        h.World.SetComponent(e, d);
        Assert.Equal(nameof(BreachCause.Unknown), Assert.Single(h.Gather(5.0).Of<HrotBuildingDamage>()).CollapseCause);
        Assert.Empty(h.Gather(6.0).Of<HrotBuildingDamage>());

        // a building that is not collapsed: the record's stale collapse fields are not sent
        using var standing = new Stage();
        var s = standing.Origins(null, 0, (new WallBreach(standing.Panel(0, 1), 1f, 2f, 0f, 2f, BreachKind.Hole), Origin(1.0, default, BreachCause.Ram, 0f, 9)));
        var stale = standing.World.GetComponentRO<BuildingDamage>(s);
        stale.SetCollapse(BreachCause.Detonation, new Vector3(1, 2, 3), 99f);
        standing.World.SetComponent(s, stale);
        var one = Assert.Single(standing.Gather().Of<HrotBuildingDamage>());
        Assert.Equal((false, nameof(BreachCause.Unknown), HrotVec3.Zero, 0f), (one.Collapsed, one.CollapseCause, one.CollapseBlast, one.CollapseKg));

        // collapsed in a loaded save: time 0, blast 0, the cause and kg kept
        using var saved = new Stage();
        saved.Origins((0d, BreachCause.Detonation, default, 80f), 1);
        var loaded = Assert.Single(saved.Gather().Of<HrotBuildingDamage>());
        Assert.Equal((true, 0d, nameof(BreachCause.Detonation), HrotVec3.Zero, 80f), (loaded.Collapsed, loaded.CollapsedAt, loaded.CollapseCause, loaded.CollapseBlast, loaded.CollapseKg));
    }

    /// <summary>T-075 (D-2): a window's Centre is the middle of its opening on its panel (A + dir x (At + Width / 2), z = (Sill + Head) / 2, computed here independently from the panel) and its Normal is HROT's outward normal.</summary>
    [Fact]
    public void A_windows_centre_is_the_middle_of_its_opening_and_its_normal_is_the_walls_outward_normal()
    {
        using var h = new Stage(json: WindowHouse);
        var windows = new List<(int Panel, int Opening)>();
        for (int local = 0; local < 4; local++)
        {
            int panel = h.Panel(0, local);
            for (int o = 0; o < h.Terrain.Panels[panel].Openings.Count; o++)
            {
                windows.Add((panel, o));
            }
        }

        Assert.Equal(4, windows.Count);   // (the fixture is what the property runs over)
        h.Windows(0, [.. windows.Select(w => new WindowEntry(w.Panel, (byte)w.Opening, 3, 0, 0, 0, 0))]);

        var sent = Assert.Single(h.Gather().Of<HrotBuildingDamage>());

        Assert.Equal(4, sent.Windows.Count);
        foreach (var w in sent.Windows)
        {
            var p = h.Terrain.Panels[h.Panel(0, w.Panel)];
            var o = p.Openings[w.Opening];
            var dir = Vector2.Normalize(p.B - p.A);
            var at = p.A + dir * (o.At + o.Width / 2f);
            Assert.Equal(new HrotVec3(at.X, at.Y, (o.SillZ + o.HeadZ) / 2f), w.Centre);
            Assert.Equal(h.NormalOf(h.Panel(0, w.Panel)), w.Normal);
            var house = h.Terrain.Buildings[0].Position + new Vector2(5f, 4f);
            Assert.True((at.X - house.X) * w.Normal.X + (at.Y - house.Y) * w.Normal.Y > 0, "outward");
        }

        Assert.Empty(h.Gather(2.0).Of<HrotBuildingDamage>());
    }

    /// <summary>T-075: the steady gather of a building with causes, actors, centres, windows and a collapse allocates nothing.</summary>
    [Fact]
    public void A_steady_gather_with_causes_centres_and_a_collapse_allocates_nothing()
    {
        using var h = new Stage(json: WindowHouse);
        h.Origins((50.0, BreachCause.Detonation, new Vector3(1, 2, 3), 100f), 0, (new WallBreach(h.Panel(0, 0), 1f, 2f, 0f, 2f, BreachKind.Hole), Origin(10.0, new Vector3(1, 1, 1), BreachCause.Ram, 0f, 4711)),
            (new WallBreach(h.Panel(0, 2), 1f, 3f, 0f, 2f, BreachKind.Passage), Origin(12.0, new Vector3(2, 2, 2), BreachCause.Detonation, 5f)));
        h.Windows(0, new WindowEntry(h.Panel(0, 0), 1, 3, 0, 0, 0, 0));
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
