using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Replication.Components;
using Hrot.Core.Tkb;
using Hrot.Map.Common;
using Hrot.Traffic;
using Sim.Core;
using SimNetGateway.Common.Tracks;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>DPS round 2, ambient traffic (DD-018 3.6b, 3.6c): movers, their tracks, the signal heads and the one-source rule, from HROT's own <c>TrafficFrame</c>.</summary>
public class TrafficGatherTests
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
            RegisterTemplate(TkbEntityTypes.TrafficCar, 1);
            RegisterTemplate(TrafficReplicas.PedestrianTypeOf(205), 3);
            foreach (var register in new Action[] { () => World.RegisterComponent<TrafficCar>(), () => World.RegisterComponent<ReplicationRoute>() })
            {
                try
                {
                    register();
                }
                catch (InvalidOperationException)
                {
                    // registered by the adapter
                }
            }

            Traffic = new TrafficFrame();
            Steps(3);
        }

        public TrafficFrame Traffic { get; }

        public void Show(IReadOnlyList<TrafficCarPose> cars, IReadOnlyList<TrafficPedPose>? peds = null, IReadOnlyDictionary<int, byte>? lights = null)
        {
            Traffic.Fill(cars, _ => false, lights ?? new Dictionary<int, byte>(), peds);
            World.SetSingletonManaged(Traffic);
        }

        public void Hide() => World.SetSingletonManaged<TrafficFrame>(null!);

        public Capture Gather(double simTime)
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            _now += 0.1;
            Adapter.GatherEntities(World, inbound, _now, simTime);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }
    }

    private static TrafficCarPose Car(int id, float x, float y, float yaw = 0f, long type = 0, uint colour = 0xB02A2A, float z = 0f, float length = 4.5f, float width = 1.8f) =>
        new(1, new VehicleHandle((uint)id, 0), id.ToString(), id, (TkbTypeId)type, colour, x, y, z, yaw, length, width);

    private static TrafficPedPose Ped(int id, float x, float y, float yaw = 0f, float z = 0f) => new(1, id, "p" + id, x, y, z, yaw);

    // ---- movers ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_mover_is_sent_at_its_spawn_with_the_type_colour_and_seed_hrot_gives_it_and_removed_when_it_goes()
    {
        using var h = new Stage();
        h.Show([Car(101, 10, 20, type: 0, colour: 0xB02A2A), Car(102, 30, 40, type: 7777, colour: 0x1B1B1D)], [Ped(205, 5, 6)]);

        var first = h.Gather(1.0);

        var movers = first.Of<HrotMover>().OrderBy(m => m.Id).ToList();
        Assert.Equal([101, 102, 205], movers.Select(m => m.Id).ToArray());
        Assert.Equal((HrotMoverKind.Car, (long)TkbEntityTypes.TrafficCar, 0xB02A2AFFu, 0u, 4.5f, 1.8f, HrotMoverSource.Sumo), (movers[0].Kind, movers[0].TypeId, movers[0].Colour, movers[0].Seed, movers[0].Length, movers[0].Width, movers[0].Source));   // type 0 is HROT's generic traffic car
        Assert.Equal((7777L, 0x1B1B1DFFu), (movers[1].TypeId, movers[1].Colour));   // SUMO's own type; the colour HROT already painted, with alpha
        Assert.Equal((HrotMoverKind.Pedestrian, TrafficReplicas.PedestrianTypeOf(205), 0u, 205u, HrotMoverSource.Sumo), (movers[2].Kind, movers[2].TypeId, movers[2].Colour, movers[2].Seed, movers[2].Source));
        Assert.Equal(CivilianTkbCatalog.TkbCivilianAdultFemale, movers[2].TypeId);   // 205 % 10 = 5: the id types him
        Assert.Contains(first.Of<HrotTypeRecord>(), r => r.TypeId == TkbEntityTypes.TrafficCar);   // the car type is a TKB type a client can read

        Assert.Empty(h.Gather(1.05).Of<HrotMover>());   // once

        // the car despawns and the pedestrian is turned into an entity of its own (no longer in the frame)
        h.Show([Car(102, 31, 40)]);
        var gone = h.Gather(1.1).Of<HrotMoverRemoved>().Select(m => m.Id).Order().ToArray();
        Assert.Equal([101, 205], gone);
        Assert.Empty(h.Gather(1.15).Of<HrotMoverRemoved>());
    }

    [Fact]
    public void A_car_taken_from_the_traffic_paint_keeps_the_colour_hrot_resolved_and_an_id_zero_is_no_mover()
    {
        using var h = new Stage();
        uint painted = TrafficPaint.Of("55", 0);   // what HROT's traffic mirror writes into the pose when SUMO gave no colour
        h.Show([Car(55, 0, 0, colour: painted), Car(0, 1, 1)]);

        var movers = h.Gather(1.0).Of<HrotMover>();

        var car = Assert.Single(movers);   // a pose with no network id has no durable identity
        Assert.Equal((55, ((painted & 0xFFFFFFu) << 8) | 0xFFu), (car.Id, car.Colour));
        Assert.Equal(0xB02A2Au << 8 | 0xFFu, TrafficGather.ColourOf(0xB02A2A));
    }

    [Fact]
    public void No_traffic_frame_sends_nothing_and_a_frame_that_goes_withdraws_what_was_sent()
    {
        using var h = new Stage();
        var none = h.Gather(1.0);
        Assert.Empty(none.Of<HrotMover>());
        Assert.Empty(none.Of<HrotMoverTracks>());

        h.Show([Car(101, 0, 0)], [Ped(205, 1, 1)]);
        Assert.Equal(2, h.Gather(2.0).Of<HrotMover>().Count);

        h.Hide();   // the traffic ended (a boundary inside HROT)
        var after = h.Gather(3.0);
        Assert.Equal([101, 205], after.Of<HrotMoverRemoved>().Select(m => m.Id).Order().ToArray());
        Assert.Empty(h.Gather(4.0).Of<HrotMoverRemoved>());
    }

    [Fact]
    public void A_world_boundary_starts_over()
    {
        using var h = new Stage();
        h.Show([Car(101, 0, 0)]);
        Assert.Single(h.Gather(1.0).Of<HrotMover>());
        Assert.Empty(h.Gather(1.1).Of<HrotMover>());

        h.Adapter.Reset(h.World);

        var again = h.Gather(1.2);
        Assert.Single(again.Of<HrotMover>());   // the gateway is new: the mover is new to it
        Assert.Contains(again.Of<HrotTypeRecord>(), r => r.TypeId == TkbEntityTypes.TrafficCar);
    }

    // ---- tracks ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_straight_car_makes_about_one_knot_a_second_in_batches_every_tenth_of_a_second_and_the_tolerance_holds()
    {
        using var h = new Stage();
        var batches = new List<HrotMoverTracks>();
        var poses = new List<TrackKnot>();
        for (int i = 0; i <= 20 * 20; i++)   // 20 s at 20 Hz
        {
            double t = i / 20.0;
            float x = 100f + (float)(14.0 * t);
            h.Show([Car(101, x, 20f)]);
            batches.AddRange(h.Gather(t).Of<HrotMoverTracks>());
            poses.Add(new TrackKnot(t, x, 20, 0, 0, 0, 0));
        }

        // every batch: at most the bound, a time not later than its own, 10 Hz
        Assert.All(batches, b => Assert.InRange(b.Knots.Count, 1, TrafficGather.MaxBatch));
        Assert.All(batches.SelectMany(b => b.Knots), k => Assert.True(k.Dt <= 0f, "a knot is never later than its batch"));
        var times = batches.Select(b => b.SimTime).ToArray();
        Assert.All(times.Zip(times.Skip(1)), p => Assert.True(p.Second - p.First >= TrafficGather.BatchPeriodS - 1e-9));

        var knots = batches.SelectMany(b => b.Knots.Select(k => (T: b.SimTime + k.Dt, K: k))).OrderBy(p => p.T).ToList();
        double perSecond = knots.Count / 20.0;
        Assert.InRange(perSecond, 0.9, 1.3);   // about one a second on a straight road

        // a client playing the knots is within 0.10 m of the pose at every pose time up to the newest knot
        var track = knots.Select(p => new TrackKnot(p.T, p.K.X, p.K.Y, p.K.Z, p.K.HeadingCentiDeg * Math.PI / 18000.0, p.K.PitchCentiDeg * Math.PI / 18000.0, p.K.Speed)).ToList();
        foreach (var pose in poses.Where(p => p.Time <= track[^1].Time))
        {
            Assert.True(TrackCurve.TryAt(track, pose.Time, out var shown));
            Assert.True(Math.Abs(shown.X - pose.X) <= 0.10 + 1e-3, $"t={pose.Time}: {shown.X - pose.X:F3} m");   // + float quantisation of the wire
        }

        Assert.Equal(14f, knots[^1].K.Speed, 3);
    }

    [Fact]
    public void A_turning_car_makes_more_knots_than_a_straight_one_and_a_pedestrian_has_no_pitch()
    {
        using var h = new Stage();
        int turnKnots = 0, straightKnots = 0;
        for (int i = 0; i <= 20 * 12; i++)
        {
            double t = i / 20.0;
            double a = t * 10.0 / 6.0;   // a roundabout: radius 6 m at 10 m/s
            h.Show([Car(101, (float)(6 * Math.Sin(a)), (float)(6 * (1 - Math.Cos(a))), yaw: (float)a), Car(102, (float)(10 * t), 50f)], [Ped(205, (float)(1.4 * t), 70f, z: (float)(0.3 * t))]);
            foreach (var b in h.Gather(t).Of<HrotMoverTracks>())
            {
                turnKnots += b.Knots.Count(k => k.MoverId == 101);
                straightKnots += b.Knots.Count(k => k.MoverId == 102);
                Assert.All(b.Knots.Where(k => k.MoverId == 205), k => Assert.Equal(0, k.PitchCentiDeg));   // a walker up a slope does not pitch
            }
        }

        Assert.True(turnKnots >= straightKnots + 5, $"{turnKnots} knots on the turn, {straightKnots} on the straight");
    }

    [Fact]
    public void The_wire_units_of_a_knot_are_hundredths_of_a_degree_wrapped_and_a_batch_is_bounded()
    {
        Assert.Equal((short)9000, TrafficGather.CentiDeg(Math.PI / 2));
        Assert.Equal((short)-9000, TrafficGather.CentiDeg(-Math.PI / 2));
        Assert.Equal((short)0, TrafficGather.CentiDeg(2 * Math.PI));
        Assert.Equal((short)-9000, TrafficGather.CentiDeg(3 * Math.PI / 2));   // wrapped
        Assert.Equal((short)18000, TrafficGather.CentiDeg(Math.PI));

        using var h = new Stage();
        var many = Enumerable.Range(1, 5000).Select(i => Car(i, i, 0)).ToArray();
        h.Show(many);
        var first = h.Gather(1.0);

        var batches = first.Of<HrotMoverTracks>();
        Assert.Equal([TrafficGather.MaxBatch, 5000 - TrafficGather.MaxBatch], batches.Select(b => b.Knots.Count).ToArray());   // every mover's first knot, split at the bound
        Assert.Equal(5000, first.Of<HrotMover>().Count);
    }

    // ---- the one-source rule --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_traffic_replica_entity_is_never_an_entity_and_an_ordinary_unit_still_is()
    {
        using var h = new Stage();
        long pedType = TrafficReplicas.PedestrianTypeOf(205);
        var replica = h.AddHrotEntity(205, new Vector3(5, 6, 0), tkb: pedType);   // HROT's local replica of the SUMO pedestrian
        h.World.AddComponent(replica, new ReplicationRoute(ReplicationRoute.SumoSharp));
        h.World.AddComponent(replica, new TrafficCar { VehicleIndex = 0, Kind = TrafficCar.KindPedestrian });
        var carReplica = h.AddHrotEntity(101, new Vector3(10, 20, 0), tkb: TkbEntityTypes.TrafficCar);
        h.World.AddComponent(carReplica, new ReplicationRoute(ReplicationRoute.SumoSharp));
        h.World.AddComponent(carReplica, new TrafficCar { VehicleIndex = 101, Generation = 0 });
        h.AddHrotEntity(2001, new Vector3(0, 0, 0), tkb: TkbEntityTypes.TrafficCar);   // a unit of the scenario with the same type: not a replica
        h.Show([Car(101, 10, 20)], [Ped(205, 5, 6)]);

        var sent = h.Gather(1.0);

        Assert.Equal([2001], sent.Of<HrotEntityState>().Select(s => s.Id).ToArray());   // neither replica is an entity (this is the entity gather the DIS side shares)
        Assert.Equal([101, 205], sent.Of<HrotMover>().Select(m => m.Id).Order().ToArray());   // each reaches a client only as a mover
    }

    // ---- signal heads ---------------------------------------------------------------------------------------------------------

    private static TrafficNetCache NetWithSignals()
    {
        var model = Sim.Ingest.NetworkParser.ParseXml(
            "<net><location netOffset=\"0,0\" convBoundary=\"0,0,200,200\" projParameter=\"!\"/>"
            + "<edge id=\"east\" from=\"a\" to=\"b\"><lane id=\"east_0\" index=\"0\" speed=\"13.9\" length=\"100\" shape=\"0,0 100,0\"/></edge>"
            + "<edge id=\"north\" from=\"c\" to=\"d\"><lane id=\"north_0\" index=\"0\" speed=\"13.9\" length=\"100\" shape=\"50,50 50,150\"/></edge></net>");
        var place = SumoNetPlacement.For(model, new Fdp.Toolkit.Terrain.TerrainSumoNet("x.net.xml", 0, 0), null);
        var signals = new[] { new TrafficSignal(0, new Vector3(100, -1.7f, 0), new Vector3(100, -1.7f, 5)), new TrafficSignal(1, new Vector3(51.7f, 150, 0), new Vector3(51.7f, 150, 5)) };
        return new TrafficNetCache("x.net.xml", "x.net.xml", model, "0", place, new TrafficRoadScene([], signals));
    }

    [Fact]
    public void A_signal_head_is_placed_by_the_host_with_its_lanes_heading_and_its_lamp_follows_the_frame()
    {
        using var h = new Stage();
        h.World.RegisterManagedComponent<TrafficNetCache>();
        h.World.SetSingletonManaged(NetWithSignals());
        h.Show([Car(101, 0, 0)], lights: new Dictionary<int, byte> { [0] = (byte)'G', [1] = (byte)'r' });

        var first = h.Gather(1.0).Of<HrotTrafficLight>().OrderBy(l => l.SignalId).ToList();

        Assert.Equal([0, 1], first.Select(l => l.SignalId).ToArray());
        Assert.Equal((new HrotVec3(100, -1.7f, 5), 0f, HrotTrafficLightState.Green), (first[0].Position, first[0].HeadingDeg, first[0].State));   // east: heading 0 (+x)
        Assert.Equal(90f, first[1].HeadingDeg, 3);   // north: heading 90
        Assert.Equal((new HrotVec3(51.7f, 150, 5), HrotTrafficLightState.Red), (first[1].Position, first[1].State));
        Assert.Empty(h.Gather(1.1).Of<HrotTrafficLight>());   // nothing changed

        h.Show([Car(101, 1, 0)], lights: new Dictionary<int, byte> { [0] = (byte)'y' });   // lane 1 has no signal byte any more: its lamp is off
        var changed = h.Gather(1.2).Of<HrotTrafficLight>().OrderBy(l => l.SignalId).ToList();
        Assert.Equal([(0, HrotTrafficLightState.Yellow), (1, HrotTrafficLightState.Off)], changed.Select(l => (l.SignalId, l.State)).ToArray());
        Assert.Equal(new HrotVec3(100, -1.7f, 5), changed[0].Position);   // a lamp change carries the same place

        // the traffic goes: the heads are withdrawn
        h.Hide();
        Assert.Equal([0, 1], h.Gather(2.0).Of<HrotTrafficLightRemoved>().Select(l => l.SignalId).Order().ToArray());

        // and a boundary starts over
        h.Show([Car(101, 1, 0)], lights: new Dictionary<int, byte> { [0] = (byte)'G' });
        Assert.Equal(2, h.Gather(3.0).Of<HrotTrafficLight>().Count);
        h.Adapter.Reset(h.World);
        Assert.Equal(2, h.Gather(4.0).Of<HrotTrafficLight>().Count);
    }

    [Fact]
    public void Lamp_names_are_hrots_lamps()
    {
        Assert.Equal([HrotTrafficLightState.Green, HrotTrafficLightState.Yellow, HrotTrafficLightState.Red, HrotTrafficLightState.Off], new[] { 2, 1, 0, -1 }.Select(TrafficGather.LampState).ToArray());
        Assert.Equal([2, 2, 1, 1, 0, 0, 0, -1], new[] { 'G', 'g', 'y', 'Y', 'r', 'R', 'u', 'o' }.Select(c => TrafficFrame.LampOf((byte)c)).ToArray());
    }
}
