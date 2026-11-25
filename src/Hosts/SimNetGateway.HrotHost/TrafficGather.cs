using Fdp.Core;
using Hrot.Traffic;
using SimNetGateway.Common.Tracks;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The ambient traffic of the world (DD-018 3.6b, 3.6c): every SUMO car and pedestrian of HROT's <c>TrafficFrame</c> (the poses its traffic mirror resolved this frame, in
/// terrain-local metres) as a <see cref="HrotMover"/> at its spawn, a track of knots (<see cref="TrackBuilder"/>: a knot when the Hermite curve through the sent knots would
/// miss a pose by 0.10 m or 2 degrees, at least one a second) sent in batches of at most 4096 at 10 Hz, and a <see cref="HrotMoverRemoved"/> when it leaves the frame
/// (despawned, or turned into an entity of its own). The signal heads of the road net (<see cref="TrafficRoadScene.Signals"/>) go out as <see cref="HrotTrafficLight"/> with the
/// lamp of the frame. A world with no traffic has no frame: nothing is sent. The traffic replica entities (<see cref="TrafficCar"/>, on the SumoSharp route) are never published
/// as entities by the adapter, so a car is only ever a mover. Main thread only.
/// </summary>
internal sealed class TrafficGather
{
    /// <summary>The batch period (simulation seconds) and its bound; the contract's sequence bound is the same.</summary>
    public const double BatchPeriodS = 0.1;

    public const int MaxBatch = 4096;

    private sealed class Mover
    {
        public TrackBuilder Builder = null!;
        public bool Seen;
    }

    private readonly Dictionary<int, Mover> _movers = new();
    private readonly Dictionary<int, HrotTrafficLight> _lights = new();
    private readonly HashSet<int> _lightsSeen = new();
    private readonly List<(int Id, TrackKnot Knot)> _pending = new();
    private readonly List<TrackKnot> _made = new();
    private readonly List<int> _gone = new();
    private double _lastBatch = double.NegativeInfinity;

    /// <summary>Knots sent so far (a counter for the tests and the logs).</summary>
    public long KnotsSent { get; private set; }

    public void Clear()
    {
        _movers.Clear();
        _lights.Clear();
        _pending.Clear();
        _lastBatch = double.NegativeInfinity;
    }

    public void Gather(EntityRepository world, InMemoryInbound inbound, double simTime, TkbGather types)
    {
        var frame = TrafficFrame.Of(world);
        if (frame is null)
        {
            Withdraw(inbound);   // no traffic (any more): what was sent goes
            return;
        }

        foreach (var m in _movers.Values)
        {
            m.Seen = false;
        }

        var cars = frame.Cars;
        for (int i = 0; i < cars.Count; i++)
        {
            var car = cars[i];
            if (car.NetworkId == 0 || !Touch(car.NetworkId))
            {
                continue;
            }

            if (!_movers.ContainsKey(car.NetworkId))
            {
                long type = TrafficReplicas.CarTypeOf(car);
                types.Reference(type);
                inbound.Enqueue(new HrotMover(car.NetworkId, HrotMoverKind.Car, type, ColourOf(car.Color), 0u, car.Length, car.Width, HrotMoverSource.Sumo));
                _movers[car.NetworkId] = new Mover { Builder = new TrackBuilder(), Seen = true };
            }

            Track(car.NetworkId, simTime, car.X, car.Y, car.Z, car.YawRad);
        }

        var peds = frame.Pedestrians;
        for (int i = 0; i < peds.Count; i++)
        {
            var ped = peds[i];
            if (ped.NetworkId == 0 || !Touch(ped.NetworkId))
            {
                continue;
            }

            if (!_movers.ContainsKey(ped.NetworkId))
            {
                long type = TrafficReplicas.PedestrianTypeOf(ped.NetworkId);
                types.Reference(type);
                inbound.Enqueue(new HrotMover(ped.NetworkId, HrotMoverKind.Pedestrian, type, 0u, (uint)ped.NetworkId, 0f, 0f, HrotMoverSource.Sumo));
                _movers[ped.NetworkId] = new Mover { Builder = new TrackBuilder(flat: true), Seen = true };
            }

            Track(ped.NetworkId, simTime, ped.X, ped.Y, ped.Z, ped.YawRad);
        }

        _gone.Clear();
        foreach (var (id, m) in _movers)
        {
            if (!m.Seen)
            {
                _gone.Add(id);
            }
        }

        foreach (int id in _gone)
        {
            _movers.Remove(id);
            _pending.RemoveAll(p => p.Id == id);
            inbound.Enqueue(new HrotMoverRemoved(id));
        }

        Flush(inbound, simTime);
        GatherLights(world, frame, inbound);
    }

    private bool Touch(int id)
    {
        // one mover per id: the first of this frame wins
        if (_movers.TryGetValue(id, out var m))
        {
            if (m.Seen)
            {
                return false;
            }

            m.Seen = true;
        }

        return true;
    }

    private void Track(int id, double simTime, float x, float y, float z, float yawRad)
    {
        _made.Clear();
        _movers[id].Builder.Add(simTime, x, y, z, TrackCurve.WrapPi(yawRad), _made);
        foreach (var knot in _made)
        {
            _pending.Add((id, knot));
        }
    }

    private void Flush(InMemoryInbound inbound, double simTime)
    {
        if (_pending.Count == 0 || (simTime - _lastBatch < BatchPeriodS && _pending.Count < MaxBatch))
        {
            return;
        }

        _lastBatch = simTime;
        for (int from = 0; from < _pending.Count; from += MaxBatch)
        {
            int n = Math.Min(MaxBatch, _pending.Count - from);
            var knots = new HrotMoverKnot[n];
            for (int i = 0; i < n; i++)
            {
                var (id, k) = _pending[from + i];
                knots[i] = new HrotMoverKnot(id, (float)(k.Time - simTime), (float)k.X, (float)k.Y, (float)k.Z, CentiDeg(k.HeadingRad), CentiDeg(k.PitchRad), (float)k.Speed);
            }

            inbound.Enqueue(new HrotMoverTracks(simTime, knots));
            KnotsSent += n;
        }

        _pending.Clear();
    }

    private void Withdraw(InMemoryInbound inbound)
    {
        foreach (int id in _movers.Keys)
        {
            inbound.Enqueue(new HrotMoverRemoved(id));
        }

        foreach (int id in _lights.Keys)
        {
            inbound.Enqueue(new HrotTrafficLightRemoved(id));
        }

        Clear();
    }

    /// <summary>An angle in radians as hundredths of a degree, wrapped to -180 .. 180 degrees.</summary>
    internal static short CentiDeg(double radians) => (short)Math.Clamp((int)Math.Round(TrackCurve.WrapPi(radians) * (18000.0 / Math.PI)), -18000, 18000);

    /// <summary>HROT's car colour 0xRRGGBB (its paint, <c>TrafficPaint</c> applied when SUMO gave none) as 0xRRGGBBAA.</summary>
    internal static uint ColourOf(uint rgb) => ((rgb & 0xFFFFFFu) << 8) | 0xFFu;

    private void GatherLights(EntityRepository world, TrafficFrame frame, InMemoryInbound inbound)
    {
        _lightsSeen.Clear();
        var net = TrafficNetCache.Of(world);
        if (net is not null)
        {
            foreach (var signal in net.Roads.Signals)
            {
                int id = signal.LaneHandle;
                _lightsSeen.Add(id);
                var state = frame.Lights.TryGetValue(id, out byte lamp) ? LampState(TrafficFrame.LampOf(lamp)) : HrotTrafficLightState.Off;
                if (_lights.TryGetValue(id, out var sent))
                {
                    if (sent.State == state)
                    {
                        continue;
                    }

                    var changed = sent with { State = state };
                    _lights[id] = changed;
                    inbound.Enqueue(in changed);
                    continue;
                }

                var message = new HrotTrafficLight(id, new HrotVec3(signal.Head.X, signal.Head.Y, signal.Head.Z), HeadingOf(net, id), state);
                _lights[id] = message;
                inbound.Enqueue(in message);
            }
        }

        _gone.Clear();
        foreach (int id in _lights.Keys)
        {
            if (!_lightsSeen.Contains(id))
            {
                _gone.Add(id);
            }
        }

        foreach (int id in _gone)
        {
            _lights.Remove(id);
            inbound.Enqueue(new HrotTrafficLightRemoved(id));
        }
    }

    internal static HrotTrafficLightState LampState(int lamp) => lamp switch { 2 => HrotTrafficLightState.Green, 1 => HrotTrafficLightState.Yellow, 0 => HrotTrafficLightState.Red, _ => HrotTrafficLightState.Off };

    /// <summary>
    /// The direction of travel (degrees, 0 = +x, counter-clockwise, in terrain-local metres) of lane <paramref name="handle"/> at its downstream end, where its signal head
    /// stands; 0 when the lane is unknown or a single point.
    /// </summary>
    internal static float HeadingOf(TrafficNetCache net, int handle)
    {
        var lanes = net.Model.LanesByHandle;
        if ((uint)handle >= (uint)lanes.Count || lanes[handle].Shape is not { Count: >= 2 } shape)
        {
            return 0f;
        }

        var (px, py) = net.Placement.ToLocal(shape[^2].Item1, shape[^2].Item2);
        var (ex, ey) = net.Placement.ToLocal(shape[^1].Item1, shape[^1].Item2);
        return (float)(Math.Atan2(ey - py, ex - px) * 180.0 / Math.PI);
    }
}
