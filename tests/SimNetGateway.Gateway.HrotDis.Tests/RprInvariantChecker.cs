using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// The invariants of the HLA half (T-024, DD-009), checked at the moment a message is sent and after every step, against an independent
/// oracle (own FPW arithmetic, own angle measure, own marking rule):
/// <list type="bullet">
/// <item>origin rule (R-116): an update, a removal or an interaction names only objects of hrot entities; no entity of origin dis
/// or rpr holds an entity number of the pool (the one <c>dis.entity</c> pool serves both sides: one number per hrot entity on DIS and HLA),
/// none of origin rpr holds a <c>dis.entity</c> key, and none of origin dis a handle;</item>
/// <item>registration: tokens are unique, a handle the RTI gave is used for exactly one entity, nothing is sent for a handle before
/// its answer or after its removal, a ready entity of an open session is registered and sent;</item>
/// <item>one entity number for both sides: the entity identifier of an update equals the id of the Entity State PDU sent for the same entity;</item>
/// <item>full update: every field is the one the pivot says (type, force, marking, damage, entity identifier, location predicted to
/// now, velocities and orientation as single precision, the algorithm), never a partial update;</item>
/// <item>threshold invariant, per side: after every step, for every sent hrot entity either an update was sent this tick or the
/// federate's prediction of the last update is within <c>hla.positionThresholdM</c> / <c>hla.orientationThresholdDeg</c> of the truth and
/// the last send is younger than <c>hla.heartbeatS</c>; conversely every update is justified (first, identity or damage change, drift,
/// heartbeat) and there is at most one per entity per tick;</item>
/// <item>exactly one removal per object whose entity vanished, nothing after it; nothing is sent while the session is not Running or
/// Paused (a removal excepted), and nothing positional before the origin is known;</item>
/// <item>numbers: unique among live entities, in range, under the gateway's site and application, in use exactly while an entity holds
/// them.</item>
/// </list>
/// </summary>
public sealed class RprInvariantChecker
{
    private const double Epsilon = 1e-9;

    private readonly HrotDisHarness _h;
    private readonly double _heartbeat;
    private readonly double _positionThreshold;
    private readonly double _orientationThreshold;
    private readonly List<string> _violations = new();
    private readonly Dictionary<EntityId, Track> _tracks = new();
    private readonly Dictionary<long, DisEntityType> _registered = new();
    private readonly Dictionary<uint, int> _removed = new();
    private readonly Dictionary<EntityId, long> _readySince = new();
    private readonly HashSet<EntityId> _sentThisTick = new();
    private readonly List<RprObjectUpdate> _updates = new();
    private readonly List<IRprMessage> _others = new();
    private long _lastSendTick = -1;

    private sealed class Track
    {
        public double Time;
        public Vec3 Position, Velocity, Orientation;
        public byte Algorithm;
        public DisEntityType Type;
        public string Marking = string.Empty;
        public RprForceId Force;
        public RprDamageState Damage;
        public int Sends;
        public RprObjectHandle Handle;
    }

    public RprInvariantChecker(HrotDisHarness harness)
    {
        _h = harness;
        var hla = harness.Config.Hla!;
        _heartbeat = hla.HeartbeatS;
        _positionThreshold = hla.PositionThresholdM;
        _orientationThreshold = hla.OrientationThresholdDeg * Math.PI / 180.0;
    }

    /// <summary>When false a ready entity that was never registered or sent is not a violation (the pool is exhausted or the RTI is silent on purpose).</summary>
    public bool ExpectAllSent { get; set; } = true;

    /// <summary>When true an update without a justification of its own is not a violation (another federate asked for the object).</summary>
    public bool AllowRedundant { get; set; }

    /// <summary>Every update sent so far, in order.</summary>
    public IReadOnlyList<RprObjectUpdate> Updates => _updates;

    /// <summary>Every other message sent so far (registrations, removals, interactions), in order.</summary>
    public IReadOnlyList<IRprMessage> Others => _others;

    public IEnumerable<T> Sent<T>()
        where T : IRprMessage => _others.OfType<T>();

    /// <summary>Updates sent for the entity.</summary>
    public int SendsOf(EntityId id) => _tracks.TryGetValue(id, out var t) ? t.Sends : 0;

    // ---- at the moment of sending ----------------------------------------------------------------------------------------

    public void OnSent(IRprMessage message)
    {
        double now = _h.Engine.WallTime;
        if (_h.Engine.TickCount != _lastSendTick)
        {
            _lastSendTick = _h.Engine.TickCount;
            _sentThisTick.Clear();
        }

        switch (message)
        {
            case RprRegisterObject register:
                _others.Add(message);
                if (_registered.ContainsKey(register.Token))
                {
                    Violate("tokens", $"token {register.Token} was used twice");
                }

                _registered[register.Token] = register.EntityType;
                break;
            case RprObjectUpdate update:
                _updates.Add(update);
                CheckUpdate(update, now);
                break;
            case RprObjectRemoved removed:
                _others.Add(message);
                CheckRemoved(removed);
                break;
            case RprWeaponFire fire:
                _others.Add(message);
                CheckHandle(fire.FiringObject, "fire shooter");
                CheckHandle(fire.TargetObject, "fire target");
                CheckSession("a WeaponFire");
                break;
            case RprMunitionDetonation detonation:
                _others.Add(message);
                CheckHandle(detonation.FiringObject, "detonation shooter");
                CheckHandle(detonation.TargetObject, "detonation target");
                CheckSession("a MunitionDetonation");
                break;
            default:
                _others.Add(message);
                break;
        }
    }

    private bool IsOurHandle(RprObjectHandle handle) => _h.Answered.Values.Contains(handle);

    private void CheckHandle(RprObjectHandle? handle, string what)
    {
        if (handle is { } h)
        {
            if (!IsOurHandle(h))
            {
                Violate("origin", $"{what}: handle {h.Value} is not one the RTI gave to a registration of ours");
            }
            else if (_h.RprHandles!.TryResolve(h, out var id) && _h.Engine.State.Find(id) is { } record && record.Origin != _h.HrotSource)
            {
                Violate("origin", $"{what}: handle {h.Value} belongs to an entity of origin '{_h.Engine.Sources.NameOf(record.Origin)}'");
            }
        }
    }

    private void CheckSession(string what)
    {
        var session = _h.Engine.Session;
        if (session.State != SessionState.Running && session.State != SessionState.Paused)
        {
            Violate("session", $"{what} while the session is {session.State}");
        }
    }

    private void CheckUpdate(RprObjectUpdate u, double now)
    {
        string what = $"update of handle {u.Handle.Value}";
        if (!IsOurHandle(u.Handle))
        {
            Violate("origin", $"{what}: not a handle the RTI gave to a registration of ours");
            return;
        }

        if (_removed.ContainsKey(u.Handle.Value))
        {
            Violate("final", $"{what} after the object was removed");
            return;
        }

        if (!_h.RprHandles!.TryResolve(u.Handle, out var id) || _h.Engine.State.Find(id) is not { IsAlive: true } record)
        {
            Violate("origin", $"{what}: the handle is not bound to a live entity");
            return;
        }

        if (record.Origin != _h.HrotSource)
        {
            Violate("origin", $"{what} describes an entity of origin '{_h.Engine.Sources.NameOf(record.Origin)}' (relay or echo)");
            return;
        }

        CheckSession(what);
        if (_h.Frame is null)
        {
            Violate("geo", $"{what}: an update before the origin is known");
        }

        if (!_sentThisTick.Add(id))
        {
            Violate("once", $"{what}: more than one update for the entity in the tick");
        }

        CompareFull(u, record, now);

        var s = _h.Schema;
        _tracks.TryGetValue(id, out var track);
        string marking = DisInvariantChecker.Ascii(record.TryGet(s.Marking, out var m) ? m.AsString() : string.Empty);
        var type = RprConversions.ToRprType(record.Get(s.EntityType).AsInt());
        var force = RprConversions.ForceOf(record.Get(s.Force).AsInt());
        var damage = RprConversions.DamageOf(record.TryGet(s.Damage, out var d) ? d.AsInt() : 0);
        if (track is null)
        {
            track = new Track { Handle = u.Handle };
            _tracks[id] = track;
        }
        else if (!AllowRedundant)
        {
            bool beat = now - track.Time >= _heartbeat;
            bool drift = Drift(track, record, now, out double distance, out double angle) &&
                         (distance > _positionThreshold - Epsilon || angle > _orientationThreshold - Epsilon);
            bool identity = !track.Type.Equals(type) || track.Marking != marking || track.Force != force || track.Damage != damage;
            if (!beat && !drift && !identity)
            {
                Violate("redundant", $"{what}: an update although the drift ({distance:F6} m, {angle:F6} rad) is within the thresholds, " +
                                     $"the last send is {now - track.Time:F3} s old and identity and damage did not change");
            }
        }

        track.Sends++;
        track.Time = now;
        track.Position = RprConversions.ToVec(u.WorldLocation ?? default);
        track.Velocity = RprConversions.ToVec(u.VelocityVector ?? default);
        track.Orientation = RprConversions.ToVec(u.Orientation ?? default);
        track.Algorithm = u.DeadReckoningAlgorithm ?? 1;
        track.Type = type;
        track.Marking = marking;
        track.Force = force;
        track.Damage = damage;
    }

    private void CheckRemoved(RprObjectRemoved r)
    {
        string what = $"removal of handle {r.Handle.Value}";
        if (!IsOurHandle(r.Handle))
        {
            Violate("origin", $"{what}: not a handle the RTI gave to a registration of ours");
            return;
        }

        _removed[r.Handle.Value] = _removed.GetValueOrDefault(r.Handle.Value) + 1;
        if (_removed[r.Handle.Value] > 1)
        {
            Violate("final", $"{what}: the object was removed twice");
        }
    }

    // ---- the independent oracle --------------------------------------------------------------------------------------------

    private void CompareFull(RprObjectUpdate u, EntityRecord e, double now)
    {
        var s = _h.Schema;
        string what = $"update of handle {u.Handle.Value} (entity {e.Id.Value})";
        if (!e.TryGet(s.EntityType, out var type) || !e.Has(s.Position) || !e.Has(s.StateTime))
        {
            Violate("ready", $"{what}: an update for an entity without type, position or state time");
            return;
        }

        if (u.EntityType is not { } sentType || RprConversions.PackType(sentType) != type.AsInt())
        {
            Violate("full", $"{what}: type {u.EntityType} is not the pivot's {type.AsInt():X}");
        }

        if (u.ForceId != RprConversions.ForceOf(e.Get(s.Force).AsInt()))
        {
            Violate("full", $"{what}: force {u.ForceId} is not the pivot's {e.Get(s.Force).AsInt()}");
        }

        string marking = DisInvariantChecker.Ascii(e.TryGet(s.Marking, out var m) ? m.AsString() : string.Empty);
        if (DisInvariantChecker.Ascii(u.Marking ?? "<null>") != marking)
        {
            Violate("full", $"{what}: marking '{u.Marking}' is not '{marking}'");
        }

        var damage = RprConversions.DamageOf(e.TryGet(s.Damage, out var d) ? d.AsInt() : 0);
        if (u.DamageState != damage)
        {
            Violate("full", $"{what}: damage {u.DamageState} is not {damage}");
        }

        if (!_h.DisIds!.TryGetKey(e.Id, out var key) || u.EntityIdentifier != new DisEntityId(key.Site, key.Application, key.Number))
        {
            Violate("full", $"{what}: entity identifier {u.EntityIdentifier} is not the one bound ({(_h.DisIds.TryGetKey(e.Id, out var k2) ? k2.ToString() : "none")})");
        }
        else if (key.Site != _h.Config.Shared.Site || key.Application != _h.Config.Shared.Application ||
                 key.Number < _h.Numbering.First || key.Number > _h.Numbering.Last)
        {
            Violate("numbers", $"{what}: identifier {key} is outside the gateway's site/application or number range");
        }

        // one entity number on both sides (DIS and RPR identifiers are one identifier space): when the DIS side runs, an Entity State PDU
        // with exactly this identifier was sent for the same entity (judged on the PDUs as they went on the wire, not on the map)
        if (_h.DisChecker is { } dis && u.EntityIdentifier is { } sentId)
        {
            var wireKey = new DisEntityKey(sentId.Site, sentId.Application, sentId.Entity);
            var pdus = dis.StatesOf(wireKey);
            if (pdus.Count == 0)
            {
                Violate("same-id", $"{what}: the identifier {wireKey} was never sent to DIS for this entity - HLA and DIS name it differently");
            }
            else if (DisConversions.PackType(pdus[^1].EntityType) != type.AsInt())
            {
                Violate("same-id", $"{what}: the identifier {wireKey} names a different entity on DIS");
            }
        }

        var (position, velocity, orientation) = Truth(e, now);
        if (u.WorldLocation is not { } location || (RprConversions.ToVec(location) - position).Length > 1e-6)
        {
            Violate("full", $"{what}: location {u.WorldLocation} is not {position}");
        }

        CheckSingle(u.VelocityVector, velocity, $"{what}: velocity");
        CheckSingle(u.Orientation is { } o ? new RprVector(o.Psi, o.Theta, o.Phi) : null, orientation, $"{what}: orientation");
        CheckSingle(u.AccelerationVector, Vector(e, s.Acceleration), $"{what}: acceleration");
        CheckSingle(u.AngularVelocity, Vector(e, s.AngularVelocity), $"{what}: angular velocity");
        long algorithm = e.TryGet(s.DrAlgorithm, out var a) ? a.AsInt() : DrAlgorithms.Static;
        if (u.DeadReckoningAlgorithm != algorithm)
        {
            Violate("full", $"{what}: DR algorithm {u.DeadReckoningAlgorithm} is not {algorithm}");
        }
    }

    private void CheckSingle(RprVector? actual, Vec3 expected, string what)
    {
        if (actual is not { } v || Math.Abs(v.X - (double)(float)expected.X) > Epsilon || Math.Abs(v.Y - (double)(float)expected.Y) > Epsilon ||
            Math.Abs(v.Z - (double)(float)expected.Z) > Epsilon)
        {
            Violate("full", $"{what} {actual} is not the float of {expected}");
        }
    }

    private Vec3 Vector(EntityRecord e, AttrId attr) => e.TryGet(attr, out var v) ? v.AsVec3() : Vec3.Zero;

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
        angle = DisInvariantChecker.AngleBetween(orientation, held.Orientation);
        return true;
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
        var liveHandles = new HashSet<RprObjectHandle>();
        long tick = _h.Engine.TickCount;
        foreach (var e in _h.Engine.State.Entities)
        {
            bool hasKey = _h.DisIds!.TryGetKey(e.Id, out var key);
            bool hasHandle = _h.RprHandles!.TryGetKey(e.Id, out var handle);
            if (hasKey && e.Origin != _h.DisSource)
            {
                // (entities of origin dis hold the key their PDUs named, not a number of the pool)
                holders++;
                if (!liveKeys.Add(key))
                {
                    Violate("numbers", $"entity id {key} is held by two entities");
                }
            }

            if (hasHandle && !liveHandles.Add(handle))
            {
                Violate("tokens", $"handle {handle.Value} is bound to two entities");
            }

            if (e.Origin != _h.HrotSource)
            {
                // the origin rule: only hrot entities go to HLA
                if (hasKey && e.Origin != _h.DisSource)
                {
                    Violate("origin", $"entity {e.Id.Value} of origin '{_h.Engine.Sources.NameOf(e.Origin)}' holds an entity identifier of the pool");
                }

                if (_tracks.ContainsKey(e.Id))
                {
                    Violate("origin", $"entity {e.Id.Value} of origin '{_h.Engine.Sources.NameOf(e.Origin)}' was sent to HLA");
                }

                if (e.Origin == _h.DisSource && hasHandle)
                {
                    Violate("origin", $"entity {e.Id.Value} of origin dis holds an RPR handle");
                }

                if (_h.DisIds is { } disIds && e.Origin == _h.RprSource && disIds.TryGetKey(e.Id, out var disKey))
                {
                    Violate("origin", $"entity {e.Id.Value} of origin rpr holds the DIS id {disKey} (it would be published to DIS)");
                }

                continue;
            }

            bool ready = _h.Frame is not null && e.TryGet(s.EntityType, out var type) && e.Has(s.Position) && e.Has(s.StateTime) &&
                         (forwardUnits || DisTypeCode.Unpack(type.AsInt()).Kind != 11);
            if (open && ready)
            {
                _readySince.TryAdd(e.Id, tick);
            }
            else
            {
                _readySince.Remove(e.Id);
            }

            _tracks.TryGetValue(e.Id, out var track);
            if (open && ready && track is null && ExpectAllSent && _h.DeliverRti && _h.AutoRegister &&
                _readySince.TryGetValue(e.Id, out long since) && tick - since >= 3)
            {
                Violate("liveness", $"ready entity {e.Id.Value} of an open session was never sent (ready for {tick - since} ticks)");
            }

            if (track is not null && open)
            {
                bool sentNow = _sentThisTick.Contains(e.Id) && _lastSendTick == tick;
                if (!sentNow && Drift(track, e, now, out double distance, out double angle))
                {
                    if (distance > _positionThreshold + Epsilon)
                    {
                        Violate("threshold", $"entity {e.Id.Value}: the federate's prediction is {distance:F6} m from the truth (threshold {_positionThreshold}) and nothing was sent");
                    }

                    if (angle > _orientationThreshold + Epsilon)
                    {
                        Violate("threshold", $"entity {e.Id.Value}: the federate's prediction is {angle:F6} rad from the truth (threshold {_orientationThreshold}) and nothing was sent");
                    }
                }

                if (!sentNow && now - track.Time >= _heartbeat)
                {
                    Violate("heartbeat", $"entity {e.Id.Value}: the last send is {now - track.Time:F3} s old (heartbeat {_heartbeat}) and nothing was sent");
                }
            }
        }

        // every object the RTI gave: alive and bound, or removed exactly once
        foreach (var handle in _h.Answered.Values)
        {
            if (liveHandles.Contains(handle))
            {
                if (_removed.ContainsKey(handle.Value))
                {
                    var holder = _h.RprHandles!.TryResolve(handle, out var holderId) ? _h.Engine.State.Find(holderId) : null;
                    Violate("final", $"handle {handle.Value} is removed but still bound to a live entity ({holder?.Id.Value}, origin {(holder is null ? "?" : _h.Engine.Sources.NameOf(holder.Origin))}, alive {holder?.IsAlive}, tick {_h.Engine.TickCount}, session {_h.Engine.Session.State})");
                }

                continue;
            }

            if (!_removed.TryGetValue(handle.Value, out int times) || times != 1)
            {
                Violate("final", $"handle {handle.Value}: the entity vanished and the object was removed {(times == 0 ? "never" : times + " times")}");
            }
        }

        foreach (var id in _tracks.Keys.ToList())
        {
            if (_h.Engine.State.Find(id) is not { IsAlive: true })
            {
                _tracks.Remove(id);
            }
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
            Assert.Fail($"RPR invariant violation(s) at tick {_h.Engine.TickCount}:{System.Environment.NewLine}{all}");
        }
    }
}
