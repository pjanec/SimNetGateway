using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Dis7;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// The invariants of the DIS half (H2b), checked at the moment a PDU is sent and after every step. Every Entity State PDU is
/// passed through the real codec first (encode, decode), and everything is judged on the decoded PDU against an independent
/// oracle (own FPW arithmetic, own angle measure; only the packed-type layout and the frame helpers of Common are shared):
/// <list type="bullet">
/// <item>G-1 no Entity State PDU describes an entity of origin dis; no PDU carries a foreign site or application; the entity
/// number lies in the pool's range;</item>
/// <item>full PDU: every field is the one the pivot says, the position dead-reckoned to now (never a partial update);</item>
/// <item>threshold invariant: after every step, for every sent hrot-origin entity either an Entity State PDU was sent this
/// tick or the DIS prediction of the last PDU is within the thresholds of the truth and the last send is younger than the
/// heartbeat; conversely every PDU is justified (first send, deactivation, identity or damage change, drift beyond a threshold,
/// heartbeat) - no redundant send;</item>
/// <item>exactly one final (Deactivated) PDU per removed entity and nothing after it; every vanished sent entity has one;</item>
/// <item>numbers: unique among live entities, in use exactly while an entity holds them, not reused within the quarantine;</item>
/// <item>nothing is sent while the session is not Running or Paused (except the final PDU of a removal), and nothing positional
/// before the origin is known; every ready entity of an open session has been sent (unless the pool is exhausted).</item>
/// </list>
/// </summary>
public sealed class DisInvariantChecker
{
    private const double Epsilon = 1e-9;

    private readonly HrotDisHarness _h;
    private readonly double _heartbeat;
    private readonly double _positionThreshold;
    private readonly double _orientationThreshold;
    private readonly List<string> _violations = new();
    private readonly Dictionary<EntityId, Track> _tracks = new();
    private readonly Dictionary<ushort, double> _releasedAt = new();
    private readonly List<(EntityStatePdu Pdu, double Wall)> _states = new();
    private readonly List<Pdu> _others = new();
    private long _lastSendTick = -1;
    private readonly HashSet<EntityId> _sentThisTick = new();

    /// <summary>What was last sent for an entity (decoded) and when.</summary>
    private sealed class Track
    {
        public double Time;
        public Vec3 Position, Velocity, Orientation;
        public byte Algorithm;
        public long Type;
        public string Marking = string.Empty;
        public int Force;
        public int Damage;
        public bool Final;
        public int Sends;
    }

    public DisInvariantChecker(HrotDisHarness harness)
    {
        _h = harness;
        var dis = harness.Config.Dis!;
        _heartbeat = dis.HeartbeatS;
        _positionThreshold = dis.PositionThresholdM;
        _orientationThreshold = dis.OrientationThresholdDeg * Math.PI / 180.0;
    }

    /// <summary>When false, a ready entity that was never sent is not a violation (the number pool is exhausted on purpose).</summary>
    public bool ExpectAllSent { get; set; } = true;

    /// <summary>Every Entity State PDU sent so far (decoded), with the wall time of the send.</summary>
    public IReadOnlyList<(EntityStatePdu Pdu, double Wall)> States => _states;

    /// <summary>Every other PDU sent so far (Fire, Detonation, Start/Resume, Stop/Freeze), in order.</summary>
    public IReadOnlyList<Pdu> Others => _others;

    public IEnumerable<T> Sent<T>()
        where T : Pdu => _others.OfType<T>();

    /// <summary>The Entity State PDUs sent for the DIS entity key, oldest first.</summary>
    public List<EntityStatePdu> StatesOf(DisEntityKey key) =>
        _states.Where(s => DisConversions.KeyOf(s.Pdu.EntityID) == key).Select(s => s.Pdu).ToList();

    public int SendsOf(EntityId id) => _tracks.TryGetValue(id, out var t) ? t.Sends : 0;

    // ---- at the moment of sending ----------------------------------------------------------------------------------------

    public void OnSent(Pdu message)
    {
        double now = _h.Engine.WallTime;
        if (_h.Engine.TickCount != _lastSendTick)
        {
            _lastSendTick = _h.Engine.TickCount;
            _sentThisTick.Clear();
        }

        var bytes = PduCodec.Encode(message);
        var decoded = PduCodec.Decode(bytes);
        if (decoded.HasErrors || decoded.Items.Count != 1 || decoded.Items[0].Pdu is null)
        {
            Violate("codec", $"a {message.GetType().Name} does not survive the codec");
            return;
        }

        var pdu = decoded.Items[0].Pdu!;
        if (pdu.ExerciseID != _h.Config.Dis!.ExerciseId)
        {
            Violate("exercise", $"{pdu.GetType().Name} carries exercise {pdu.ExerciseID}");
        }

        if (pdu is EntityStatePdu state)
        {
            _states.Add((state, now));
            CheckEntityState(state, now);
        }
        else
        {
            _others.Add(pdu);
        }
    }

    private void CheckEntityState(EntityStatePdu pdu, double now)
    {
        var key = DisConversions.KeyOf(pdu.EntityID);
        var shared = _h.Config.Shared;
        if (key.Site != shared.Site || key.Application != shared.Application)
        {
            Violate("G-1", $"Entity State PDU {key} does not carry the gateway's site and application");
        }

        if (key.Number < _h.Numbering.First || key.Number > _h.Numbering.Last)
        {
            Violate("numbers", $"entity number {key.Number} is outside {_h.Numbering.First} .. {_h.Numbering.Last}");
        }

        if (!_h.DisIds!.TryResolve(key, out var id))
        {
            Violate("G-5", $"Entity State PDU {key}: the id is not bound");
            return;
        }

        bool deactivated = EntityAppearance.GetDeactivated(pdu.EntityAppearance);
        _tracks.TryGetValue(id, out var track);
        if (track is { Final: true })
        {
            Violate("final", $"entity {key}: a PDU after the final one");
        }

        if (deactivated)
        {
            CheckFinal(pdu, key, id, track, now); // the entity is already gone from the repository: judged against what was sent before
            return;
        }

        if (_h.Engine.State.Find(id) is not { } record)
        {
            Violate("G-5", $"Entity State PDU {key}: the id is not bound to a live entity");
            return;
        }

        if (record.Origin != _h.HrotSource)
        {
            Violate("G-1", $"Entity State PDU {key} describes an entity of origin '{_h.Engine.Sources.NameOf(record.Origin)}' (echo)");
            return;
        }

        var session = _h.Engine.Session;
        bool open = session.State == SessionState.Running || session.State == SessionState.Paused;
        if (!deactivated && !open)
        {
            Violate("session", $"entity {key}: a PDU while the session is {session.State}");
        }

        if (_h.Frame is null)
        {
            Violate("geo", $"entity {key}: a PDU before the origin is known");
        }

        CompareFull(pdu, record, key, now, deactivated);

        string marking = Marking(record);
        long type = record.Has(_h.Schema.EntityType) ? record.Get(_h.Schema.EntityType).AsInt() : 0;
        int force = (int)record.Get(_h.Schema.Force).AsInt();
        int damage = record.TryGet(_h.Schema.Damage, out var d) ? (int)d.AsInt() : 0;

        if (track is null)
        {
            if (deactivated)
            {
                Violate("final", $"entity {key}: a Deactivated PDU for an entity that was never sent");
            }

            if (_releasedAt.TryGetValue(key.Number, out double released) && now - released < _h.Numbering.QuarantineSeconds)
            {
                Violate("numbers", $"entity number {key.Number} was released at {released:F3} and is reused at {now:F3}, inside the {_h.Numbering.QuarantineSeconds} s quarantine");
            }

            track = new Track();
            _tracks[id] = track;
        }
        else if (!deactivated)
        {
            bool beat = now - track.Time >= _heartbeat;
            bool drift = Drift(track, record, now, out double distance, out double angle) &&
                         (distance > _positionThreshold - Epsilon || angle > _orientationThreshold - Epsilon);
            bool identity = track.Type != type || track.Marking != marking || track.Force != force || track.Damage != damage;
            if (!beat && !drift && !identity)
            {
                Violate("redundant", $"entity {key}: a PDU although the drift ({distance:F6} m, {angle:F6} rad) is within the thresholds, " +
                                     $"the last send is {now - track.Time:F3} s old and identity and damage did not change");
            }
        }

        track.Sends++;
        track.Time = now;
        track.Position = DisConversions.ToVec(pdu.EntityLocation);
        track.Velocity = DisConversions.ToVec(pdu.EntityLinearVelocity);
        track.Orientation = DisConversions.ToVec(pdu.EntityOrientation);
        track.Algorithm = pdu.DeadReckoningParameters.DeadReckoningAlgorithm;
        track.Type = type;
        track.Marking = marking;
        track.Force = force;
        track.Damage = damage;
        _sentThisTick.Add(id);
        if (deactivated)
        {
            track.Final = true;
            _releasedAt[key.Number] = now;
        }
    }

    /// <summary>The final PDU of a removed entity: exactly once, for an entity that was sent, with the identity it last had and the Deactivated bit.</summary>
    private void CheckFinal(EntityStatePdu pdu, DisEntityKey key, EntityId id, Track? track, double now)
    {
        if (track is null)
        {
            Violate("final", $"entity {key}: a Deactivated PDU for an entity that was never sent");
            return;
        }

        if (DisConversions.PackType(pdu.EntityType) != track.Type || EntityMarkingText.Get(pdu.Marking) != track.Marking || pdu.ForceId != track.Force)
        {
            Violate("final", $"entity {key}: the final PDU does not carry the identity the entity last had");
        }

        uint expected = ((uint)track.Damage << 3) | (1u << 23);
        if (pdu.EntityAppearance != expected)
        {
            Violate("final", $"entity {key}: final appearance {pdu.EntityAppearance:X} is not {expected:X}");
        }

        track.Sends++;
        track.Final = true;
        track.Time = now;
        _sentThisTick.Add(id);
        _releasedAt[key.Number] = now;
    }

    // ---- the independent oracle --------------------------------------------------------------------------------------------

    /// <summary>The marking as DIS carries it: printable ASCII only (else '?'), at most 11 characters.</summary>
    public string Marking(EntityRecord record) => record.TryGet(_h.Schema.Marking, out var m) ? Ascii(m.AsString()) : string.Empty;



    public static string Ascii(string text)
    {
        var chars = new List<char>();
        foreach (char c in text)
        {
            if (chars.Count == 11)
            {
                break;
            }

            chars.Add(c is >= ' ' and <= '~' ? c : '?');
        }

        return new string(chars.ToArray());
    }

    private void CompareFull(EntityStatePdu pdu, EntityRecord e, DisEntityKey key, double now, bool deactivated)
    {
        var s = _h.Schema;
        string what = $"entity {key}";
        if (!e.TryGet(s.EntityType, out var type) || !e.Has(s.Position) || !e.Has(s.StateTime))
        {
            Violate("ready", $"{what}: a PDU for an entity without type, position or state time");
            return;
        }

        if (DisConversions.PackType(pdu.EntityType) != type.AsInt())
        {
            Violate("full", $"{what}: type {DisConversions.PackType(pdu.EntityType):X} is not the pivot's {type.AsInt():X}");
        }

        if (pdu.ForceId != (byte)e.Get(s.Force).AsInt())
        {
            Violate("full", $"{what}: force {pdu.ForceId} is not the pivot's {e.Get(s.Force).AsInt()}");
        }

        string marking = EntityMarkingText.Get(pdu.Marking);
        if (marking != Marking(e))
        {
            Violate("full", $"{what}: marking '{marking}' is not '{Marking(e)}'");
        }

        int damage = e.TryGet(s.Damage, out var d) ? (int)d.AsInt() : 0;
        uint expectedAppearance = ((uint)damage << 3) | (deactivated ? 1u << 23 : 0u);
        if (pdu.EntityAppearance != expectedAppearance)
        {
            Violate("full", $"{what}: appearance {pdu.EntityAppearance:X} is not {expectedAppearance:X}");
        }

        var (position, velocity, orientation) = Truth(e, now);
        if ((DisConversions.ToVec(pdu.EntityLocation) - position).Length > 1e-6)
        {
            Violate("full", $"{what}: location {DisConversions.ToVec(pdu.EntityLocation)} is not {position}");
        }

        CheckFloats(pdu.EntityLinearVelocity, velocity, $"{what}: velocity");
        CheckFloats(pdu.EntityOrientation.Psi, pdu.EntityOrientation.Theta, pdu.EntityOrientation.Phi, orientation, $"{what}: orientation");
        var dr = pdu.DeadReckoningParameters;
        CheckFloats(dr.EntityLinearAcceleration, Vector(e, s.Acceleration), $"{what}: acceleration");
        CheckFloats(dr.EntityAngularVelocity, Vector(e, s.AngularVelocity), $"{what}: angular velocity");
        long algorithm = e.TryGet(s.DrAlgorithm, out var a) ? a.AsInt() : DrAlgorithms.Static;
        if (dr.DeadReckoningAlgorithm != algorithm)
        {
            Violate("full", $"{what}: DR algorithm {dr.DeadReckoningAlgorithm} is not {algorithm}");
        }

        if (pdu.NumberOfVariableParameters != 0 || pdu.VariableParameters.Count != 0)
        {
            Violate("full", $"{what}: variable parameters before the articulation stage");
        }
    }

    private void CheckFloats(Vector3Float actual, Vec3 expected, string what) =>
        CheckFloats(actual.X, actual.Y, actual.Z, expected, what);

    private void CheckFloats(float x, float y, float z, Vec3 expected, string what)
    {
        if (Math.Abs(x - (double)(float)expected.X) > Epsilon || Math.Abs(y - (double)(float)expected.Y) > Epsilon ||
            Math.Abs(z - (double)(float)expected.Z) > Epsilon)
        {
            Violate("full", $"{what} ({x}, {y}, {z}) is not the float of {expected}");
        }
    }

    private Vec3 Vector(EntityRecord e, AttrId attr) => e.TryGet(attr, out var v) ? v.AsVec3() : Vec3.Zero;

    /// <summary>The pivot's state at <paramref name="time"/>: the dead-reckoning of the pivot values from their state time (own arithmetic, FPW and Static only).</summary>
    private (Vec3 Position, Vec3 Velocity, Vec3 Orientation) Truth(EntityRecord e, double time)
    {
        var s = _h.Schema;
        var position = e.Get(s.Position).AsVec3();
        var velocity = Vector(e, s.Velocity);
        var orientation = Vector(e, s.Orientation);
        long algorithm = e.TryGet(s.DrAlgorithm, out var a) ? a.AsInt() : DrAlgorithms.Static;
        double dt = time - e.Get(s.StateTime).AsDouble();
        return algorithm switch
        {
            DrAlgorithms.Fpw => (position + velocity * dt, velocity, orientation),
            DrAlgorithms.Static => (position, Vec3.Zero, orientation),
            _ => throw new InvalidOperationException($"the checker's oracle knows FPW and Static only, not {algorithm}"),
        };
    }

    private bool Drift(Track held, EntityRecord e, double now, out double distance, out double angle)
    {
        var (position, _, orientation) = Truth(e, now);
        double dt = now - held.Time;
        var predicted = held.Algorithm == DrAlgorithms.Fpw ? held.Position + held.Velocity * dt : held.Position;
        distance = (position - predicted).Length;
        angle = AngleBetween(orientation, held.Orientation);
        return true;
    }

    /// <summary>The angle between two orientations from the trace of the relative rotation (not the quaternion route of the gateway).</summary>
    public static double AngleBetween(Vec3 a, Vec3 b)
    {
        var ra = Orientation.ToMatrix(a);
        var rb = Orientation.ToMatrix(b);
        double trace = ra.M00 * rb.M00 + ra.M01 * rb.M01 + ra.M02 * rb.M02 +
                       ra.M10 * rb.M10 + ra.M11 * rb.M11 + ra.M12 * rb.M12 +
                       ra.M20 * rb.M20 + ra.M21 * rb.M21 + ra.M22 * rb.M22;
        return Math.Acos(Math.Clamp((trace - 1.0) / 2.0, -1.0, 1.0));
    }

    private void Violate(string id, string detail) => _violations.Add($"{id}: {detail}");

    // ---- after every step ----------------------------------------------------------------------------------------------

    public void Check()
    {
        double now = _h.Engine.WallTime;
        var s = _h.Schema;
        var session = _h.Engine.Session;
        bool open = session.State == SessionState.Running || session.State == SessionState.Paused;
        bool forwardUnits = _h.Config.Shared.ForwardUnitEntities;
        int holders = 0;
        var liveKeys = new HashSet<DisEntityKey>();
        foreach (var e in _h.Engine.State.Entities)
        {
            bool hasKey = _h.DisIds!.TryGetKey(e.Id, out var key);
            if (e.Origin == _h.DisSource)
            {
                if (_tracks.ContainsKey(e.Id))
                {
                    Violate("G-1", $"entity {e.Id.Value} of origin dis was sent to DIS");
                }

                continue;
            }

            if (hasKey)
            {
                holders++;
                if (!liveKeys.Add(key))
                {
                    Violate("numbers", $"DIS id {key} is held by two entities");
                }

                if (!_tracks.ContainsKey(e.Id))
                {
                    Violate("numbers", $"entity {e.Id.Value} holds DIS id {key} but no PDU was ever sent for it");
                }
            }

            if (e.Origin != _h.HrotSource)
            {
                continue;
            }

            bool ready = _h.Frame is not null && e.TryGet(s.EntityType, out var type) && e.Has(s.Position) && e.Has(s.StateTime) &&
                         (forwardUnits || DisTypeCode.Unpack(type.AsInt()).Kind != 11);
            _tracks.TryGetValue(e.Id, out var track);
            if (open && ready && track is null && ExpectAllSent)
            {
                Violate("liveness", $"ready entity {e.Id.Value} of an open session was never sent");
            }

            if (track is not null && open)
            {
                bool sentNow = _sentThisTick.Contains(e.Id) && _lastSendTick == _h.Engine.TickCount;
                if (!sentNow && Drift(track, e, now, out double distance, out double angle))
                {
                    if (distance > _positionThreshold + Epsilon)
                    {
                        Violate("threshold", $"entity {e.Id.Value}: the DIS prediction is {distance:F6} m from the truth (threshold {_positionThreshold}) and nothing was sent");
                    }

                    if (angle > _orientationThreshold + Epsilon)
                    {
                        Violate("threshold", $"entity {e.Id.Value}: the DIS prediction is {angle:F6} rad from the truth (threshold {_orientationThreshold}) and nothing was sent");
                    }
                }

                if (!sentNow && now - track.Time >= _heartbeat)
                {
                    Violate("heartbeat", $"entity {e.Id.Value}: the last send is {now - track.Time:F3} s old (heartbeat {_heartbeat}) and nothing was sent");
                }
            }
        }

        foreach (var id in _tracks.Keys.ToList())
        {
            var record = _h.Engine.State.Find(id);
            if (record is not null && record.IsAlive)
            {
                continue;
            }

            if (!_tracks[id].Final)
            {
                Violate("final", $"entity {id.Value} vanished without a Deactivated PDU");
            }

            _tracks.Remove(id);
        }

        var pool = (NumberPool)((MappedAllocator<DisEntityKey>)_h.DisIds!.Allocator!).Numbers;
        if (pool.InUse != holders)
        {
            Violate("numbers", $"the pool has {pool.InUse} numbers in use but {holders} entities hold one (leak or double release)");
        }

        if (_violations.Count > 0)
        {
            var all = string.Join(System.Environment.NewLine, _violations);
            _violations.Clear();
            Assert.Fail($"DIS invariant violation(s) at tick {_h.Engine.TickCount}:{System.Environment.NewLine}{all}");
        }
    }
}
