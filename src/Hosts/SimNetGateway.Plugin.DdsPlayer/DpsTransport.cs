using CycloneDDS.Runtime;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>One outbound item of a DPS topic: a sample to write, or (<see cref="Dispose"/>) the instance of the key in <see cref="Value"/> to dispose.</summary>
public readonly record struct DpsItem<T>(T Value, bool Dispose = false)
    where T : struct;

/// <summary>
/// The writer of one topic as an outbound port channel: every item is written at once (the engine ticks on the host's main thread) - except in a render job, when a channel that takes part
/// in the barrier (<see cref="DpsHold{T}"/>) holds its items until the step that needs them is released (DD-022 D-B4 rev 6; <see cref="DpsBarrier"/>). Outside a job the held path costs one
/// field read and allocates nothing.
/// </summary>
internal sealed class DpsChannel<T> : IOutboundChannel<DpsItem<T>>, IHeldChannel, IDisposable
    where T : struct
{
    private readonly struct Held
    {
        public Held(T value, bool dispose, long segment)
        {
            Value = value;
            Dispose = dispose;
            Segment = segment;
        }

        public T Value { get; }

        public bool Dispose { get; }

        public long Segment { get; }
    }

    private readonly DdsWriter<T> _writer;
    private readonly Func<T, T>? _prepare;
    private readonly Action<T, bool>? _observe;
    private readonly DpsHold<T>? _hold;

    // the held entries of a job, in write order; the index finds the entry of a keyed instance to replace (an absolute position: the list's front is removed by flushes)
    private List<Held>? _held;
    private Dictionary<long, long>? _index;
    private HashSet<long>? _distinct;
    private long _base;

    public DpsChannel(DdsParticipant participant, Func<T, T>? prepare = null, Action<T, bool>? observe = null, DpsHold<T>? hold = null)
    {
        _writer = new DdsWriter<T>(participant);
        _prepare = prepare;
        _observe = observe;
        _hold = hold;
        _hold?.Barrier.Register(this);
    }

    public int Written { get; private set; }

    /// <summary>A test seam: sees every sample written (not the disposals).</summary>
    internal Action<T>? Tap { get; set; }

    public void Send(in DpsItem<T> item)
    {
        if (_hold is { Barrier.Active: true })
        {
            Hold(item);
            return;
        }

        WriteNow(item);
    }

    private void WriteNow(in DpsItem<T> item)
    {
        if (item.Dispose)
        {
            _observe?.Invoke(item.Value, true);
            _writer.DisposeInstance(item.Value);
            return;
        }

        var value = _prepare is null ? item.Value : _prepare(item.Value);
        _observe?.Invoke(value, false);
        _writer.Write(value);
        Written++;
        Tap?.Invoke(value);
    }

    public void Write(T value)
    {
        _writer.Write(value);
        Written++;
        Tap?.Invoke(value);
    }

    /// <summary>The held entries not yet written (tests).</summary>
    internal int Pending => _held?.Count ?? 0;

    private void Hold(in DpsItem<T> item)
    {
        var hold = _hold!;
        _held ??= new List<Held>();
        long segment = hold.Barrier.OpenSegment;
        if (hold.Kind is DpsCountKind.Keyed or DpsCountKind.Keyless)
        {
            long key = hold.Kind == DpsCountKind.Keyed && hold.Key is not null ? hold.Key(item.Value) : 0;
            _index ??= new Dictionary<long, long>();
            if (item.Dispose)
            {
                _held.Add(new Held(item.Value, true, segment));
                _index.Remove(key);
                return;
            }

            // a newer write of an instance replaces the held one of the same gather (a client's reader keeps the last sample per instance anyway)
            if (_index.TryGetValue(key, out long at) && at >= _base && _held[(int)(at - _base)] is { Dispose: false } old && old.Segment == segment)
            {
                _held[(int)(at - _base)] = new Held(item.Value, false, segment);
                return;
            }

            _held.Add(new Held(item.Value, false, segment));
            _index[key] = _base + _held.Count - 1;
            return;
        }

        _held.Add(new Held(item.Value, item.Dispose, segment));
    }

    public uint Flush(long upToSegment)
    {
        var held = _held;
        if (held is null || held.Count == 0)
        {
            return 0;
        }

        int n = 0;
        while (n < held.Count && held[n].Segment <= upToSegment)
        {
            n++;
        }

        if (n == 0)
        {
            return 0;
        }

        var hold = _hold!;
        // an event topic keeps the newest `depth` samples (a reader's history is that deep): what the client can receive is what is counted
        int from = hold.Kind == DpsCountKind.Event && hold.Depth > 0 ? Math.Max(0, n - hold.Depth) : 0;
        uint counted = 0;
        if (hold.Kind == DpsCountKind.Keyed)
        {
            _distinct ??= new HashSet<long>();
            _distinct.Clear();
        }

        for (int i = from; i < n; i++)
        {
            var entry = held[i];
            WriteNow(new DpsItem<T>(entry.Value, entry.Dispose));
            switch (hold.Kind)
            {
                case DpsCountKind.Event:
                    counted++;
                    break;
                case DpsCountKind.Keyed:
                    if (_distinct!.Add(hold.Key is null ? 0 : hold.Key(entry.Value)))
                    {
                        counted++;
                    }

                    break;
                case DpsCountKind.Keyless:
                    counted = 1;
                    break;
            }
        }

        held.RemoveRange(0, n);
        _base += n;
        if (held.Count == 0)
        {
            _index?.Clear();
            _base = 0;
        }

        return counted;
    }

    public void Dispose()
    {
        _hold?.Barrier.Unregister(this);
        _writer.Dispose();
    }
}

/// <summary>
/// The DDS side of the plugin for one gateway runtime (DD-023): the WORLD participant - every world topic, <c>Session</c>, <c>View</c>, <c>SimClock</c> / <c>ClockStep</c> and (interim, until
/// HROT Q114-I routes it per seat) <c>PlayerMessage</c> - on <c>&lt;base&gt;-*</c> with two or more seats and on <c>&lt;base&gt;</c> with one (D-6), plus one <see cref="DpsSeat"/> per seat on
/// <c>&lt;base&gt;-n</c> (with one seat the world participant itself). Disposing it ends every instance the gateway published (the world boundary), so a client never keeps a stale entity.
/// <c>Entity.Playable</c> comes from ONE offerable-unit set shared by every seat (the union of what the seats' stations offer; they share one world). Main thread only.
/// </summary>
public sealed class DpsTransport : IDisposable
{
    private readonly DdsParticipant _participant;
    private readonly DpsHostService? _service;
    private int _epoch;
    private Session _lastSession;
    private bool _hasSession;
    private int _resendIn;
    private readonly Dictionary<int, Entity> _entities = new();
    private readonly List<IDisposable> _channels = new();
    private readonly List<DpsSeat> _seats = new();
    private readonly IPlayerStation?[] _stations;
    private readonly DpsBarrier? _barrier;
    private readonly Action<ClockStep>? _stepSink;

    /// <summary>The history depth of the event topics (their QoS): a gather counts no more samples of one topic than a reader can hold.</summary>
    private const int EventDepth = 1024;

    // the units any seat offers, rebuilt once per frame without allocating (the steady state), and an order-independent fingerprint of it
    private readonly HashSet<int> _offerable = new();
    private ulong _offerHash;
    private bool _offerKnown;

    /// <param name="partition">The BASE partition name (<see cref="Partition"/>).</param>
    /// <param name="stations">One player station per seat, in seat order (element 0 = seat 1); a seat with no element has no player mode (it only observes). Null = none.</param>
    /// <param name="gizmoSources">The gizmo source of each seat, in seat order; null or a missing element = none.</param>
    public DpsTransport(DpsSettings settings, string partition, IReadOnlyList<IPlayerStation>? stations, int epoch, Action<string>? log = null,
        IReadOnlyList<IGizmoSource?>? gizmoSources = null, DpsHostService? service = null, Func<long>? stationTicks = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Partition = partition;
        SeatCount = settings.Seats;
        WorldPartition = settings.Seats >= 2 ? partition + "-*" : partition;
        _service = service;
        _epoch = epoch;
        _stations = new IPlayerStation?[settings.Seats];
        for (int i = 0; i < _stations.Length; i++)
        {
            _stations[i] = stations is not null && i < stations.Count ? stations[i] : null;
        }

        var seatLog = log ?? (_ => { });
        _barrier = service?.Render.Barrier;
        _participant = new DdsParticipant((uint)settings.Domain, defaultPartition: WorldPartition);
        try
        {
            // every world topic the gateway writes is held in a render job and counted for ClockStep.Samples (DD-022 D-B4 rev 6): SimClock, ClockStep and GizmoFrame are not
            Session = Add(new DpsChannel<Session>(_participant, prepare: s =>
            {
                s.WorldEpoch = (uint)_epoch;
                s.StationName = partition.Length > 64 ? partition[..64] : partition;
                _lastSession = s;
                _hasSession = true;
                _service?.MarkPublished(s.WorldEpoch);
                return s;
            }, hold: Hold<Session>(DpsCountKind.Keyless)));
            EntityTopic = Add(new DpsChannel<Entity>(_participant, prepare: e =>
            {
                e.Playable = IsOffered(e.EntityId);
                return e;
            }, observe: (e, disposed) =>
            {
                if (disposed)
                {
                    _entities.Remove(e.EntityId);
                }
                else
                {
                    _entities[e.EntityId] = e;
                }
            }, hold: Hold<Entity>(DpsCountKind.Keyed, key: e => e.EntityId)));
            EntityState = Add(new DpsChannel<EntityState>(_participant, hold: Hold<EntityState>(DpsCountKind.Keyed, key: e => e.EntityId)));
            Articulation = Add(new DpsChannel<Articulation>(_participant, hold: Hold<Articulation>(DpsCountKind.Keyed, key: a => ((long)a.EntityId << 32) | (uint)a.PartId)));
            PersonState = Add(new DpsChannel<PersonState>(_participant, hold: Hold<PersonState>(DpsCountKind.Keyed, key: e => e.EntityId)));
            PersonAim = Add(new DpsChannel<PersonAim>(_participant, hold: Hold<PersonAim>(DpsCountKind.Keyed, key: e => e.EntityId)));
            Appearance = Add(new DpsChannel<Appearance>(_participant, hold: Hold<Appearance>(DpsCountKind.Keyed, key: e => e.EntityId)));
            Fire = Add(new DpsChannel<Fire>(_participant, hold: Hold<Fire>(DpsCountKind.Event, EventDepth)));
            Detonation = Add(new DpsChannel<Detonation>(_participant, hold: Hold<Detonation>(DpsCountKind.Event, EventDepth)));
            Effect = Add(new DpsChannel<Effect>(_participant, hold: Hold<Effect>(DpsCountKind.Event, EventDepth)));
            Impact = Add(new DpsChannel<Impact>(_participant, hold: Hold<Impact>(DpsCountKind.Event, EventDepth)));
            VehicleLook = Add(new DpsChannel<VehicleLook>(_participant, hold: Hold<VehicleLook>(DpsCountKind.Keyed, key: e => e.EntityId)));
            Round = Add(new DpsChannel<Round>(_participant, hold: Hold<Round>(DpsCountKind.Keyed, key: e => e.EntityId)));
            MunitionLook = Add(new DpsChannel<MunitionLook>(_participant, hold: Hold<MunitionLook>(DpsCountKind.Keyed, key: e => e.MunitionTypeId)));
            FlameJet = Add(new DpsChannel<FlameJet>(_participant, hold: Hold<FlameJet>(DpsCountKind.Keyed, key: j => ((long)j.EntityId << 32) | (uint)j.Mount)));
            FireSpots = Add(new DpsChannel<FireSpots>(_participant, hold: Hold<FireSpots>(DpsCountKind.Keyed, key: e => e.EntityId)));
            GroundMarks = Add(new DpsChannel<GroundMarks>(_participant, hold: Hold<GroundMarks>(DpsCountKind.Keyed, key: e => e.EntityId)));
            Animal = Add(new DpsChannel<Animal>(_participant, hold: Hold<Animal>(DpsCountKind.Keyed, key: e => e.EntityId)));
            BuildingDamage = Add(new DpsChannel<BuildingDamage>(_participant, hold: Hold<BuildingDamage>(DpsCountKind.Keyed, key: b => KeyOf(b.Building))));
            Mover = Add(new DpsChannel<Mover>(_participant, hold: Hold<Mover>(DpsCountKind.Keyed, key: m => m.MoverId)));
            MoverTracks = Add(new DpsChannel<MoverTracks>(_participant, hold: Hold<MoverTracks>(DpsCountKind.Event, 16)));
            TrafficLights = Add(new DpsChannel<TrafficLights>(_participant, hold: Hold<TrafficLights>(DpsCountKind.Keyed, key: t => t.SignalId)));
            TkbType = Add(new DpsChannel<TkbType>(_participant, hold: Hold<TkbType>(DpsCountKind.Keyed, key: t => t.TypeId)));
            ManDowned = Add(new DpsChannel<ManDowned>(_participant, hold: Hold<ManDowned>(DpsCountKind.Event, EventDepth)));
            Status = Add(new DpsChannel<Status>(_participant, hold: Hold<Status>(DpsCountKind.Keyed, key: t => t.StatusId)));
            Exercise = Add(new DpsChannel<Exercise>(_participant, hold: Hold<Exercise>(DpsCountKind.Keyless)));
            View = Add(new DpsChannel<View>(_participant, hold: Hold<View>(DpsCountKind.Keyless)));
            SimClock = Add(new DpsChannel<SimClock>(_participant));
            ClockStep = Add(new DpsChannel<ClockStep>(_participant));
            ClockStepPort = new DpsStepGate(ClockStep, _service?.Render);
            if (_barrier is not null)
            {
                _stepSink = step => ClockStep.Send(new DpsItem<ClockStep>(step));
                _barrier.SetStepSink(_stepSink);
            }

            for (int i = 0; i < settings.Seats; i++)
            {
                bool own = settings.Seats >= 2;
                string seatPartition = own ? $"{partition}-{i + 1}" : partition;
                var participant = own ? new DdsParticipant((uint)settings.Domain, defaultPartition: seatPartition) : _participant;
                var gizmo = gizmoSources is not null && i < gizmoSources.Count ? gizmoSources[i] : null;
                try
                {
                    _seats.Add(new DpsSeat(i + 1, seatPartition, participant, own, settings, _stations[i], gizmo, service, stationTicks, seatLog));
                }
                catch
                {
                    if (own)
                    {
                        participant.Dispose();
                    }

                    throw;
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private DpsHold<T>? Hold<T>(DpsCountKind kind, int depth = 0, Func<T, long>? key = null)
        where T : struct => _barrier is null ? null : new DpsHold<T>(_barrier, kind, depth, key);

    /// <summary>A stable 64-bit key of a string (FNV-1a): the instance key of a topic keyed by a name.</summary>
    internal static long KeyOf(string? text)
    {
        ulong hash = 14695981039346656037UL;
        if (text is not null)
        {
            foreach (char c in text)
            {
                hash = (hash ^ c) * 1099511628211UL;
            }
        }

        return (long)hash;
    }

    /// <summary>The BASE partition name: what <c>Session.StationName</c> says and what a seat's partition is derived from (<c>&lt;base&gt;-n</c>).</summary>
    public string Partition { get; }

    /// <summary>The partition of the world participant: <c>&lt;base&gt;-*</c> with two or more seats, <c>&lt;base&gt;</c> with one.</summary>
    public string WorldPartition { get; }

    /// <summary>How many seats this transport serves.</summary>
    public int SeatCount { get; }

    /// <summary>The seats in order (tests and diagnostics).</summary>
    internal IReadOnlyList<DpsSeat> Seats => _seats;

    /// <summary>The client id of seat 1's player (0 = nobody plays; tests and diagnostics).</summary>
    internal ulong InboundOwner => _seats.Count > 0 ? _seats[0].InboundOwner : 0;

    internal DpsChannel<Session> Session { get; } = null!;

    internal DpsChannel<Entity> EntityTopic { get; } = null!;

    internal DpsChannel<EntityState> EntityState { get; } = null!;

    internal DpsChannel<Articulation> Articulation { get; } = null!;

    internal DpsChannel<PersonState> PersonState { get; } = null!;

    internal DpsChannel<PersonAim> PersonAim { get; } = null!;

    internal DpsChannel<Appearance> Appearance { get; } = null!;

    internal DpsChannel<Fire> Fire { get; } = null!;

    internal DpsChannel<Detonation> Detonation { get; } = null!;

    internal DpsChannel<Effect> Effect { get; } = null!;

    internal DpsChannel<Impact> Impact { get; } = null!;

    internal DpsChannel<VehicleLook> VehicleLook { get; } = null!;

    internal DpsChannel<Round> Round { get; } = null!;

    internal DpsChannel<MunitionLook> MunitionLook { get; } = null!;

    internal DpsChannel<FlameJet> FlameJet { get; } = null!;

    internal DpsChannel<FireSpots> FireSpots { get; } = null!;

    internal DpsChannel<GroundMarks> GroundMarks { get; } = null!;

    internal DpsChannel<Animal> Animal { get; } = null!;

    internal DpsChannel<BuildingDamage> BuildingDamage { get; } = null!;

    internal DpsChannel<Mover> Mover { get; } = null!;

    internal DpsChannel<MoverTracks> MoverTracks { get; } = null!;

    internal DpsChannel<TrafficLights> TrafficLights { get; } = null!;

    internal DpsChannel<TkbType> TkbType { get; } = null!;

    internal DpsChannel<ManDowned> ManDowned { get; } = null!;

    /// <summary>The player message port (T-054, D-15): routes each message to the seats the host's addressing rule picks; each is written on that seat's participant.</summary>
    internal IOutboundChannel<DpsItem<PlayerMessage>> PlayerMessage => _messages ??= new DpsMessageRouter(_seats);

    private DpsMessageRouter? _messages;

    internal DpsChannel<Status> Status { get; } = null!;

    internal DpsChannel<Exercise> Exercise { get; } = null!;

    internal DpsChannel<View> View { get; } = null!;

    /// <summary>Seat 1's slot writer (the seat-less view of the one-player layout; per seat: <see cref="Seats"/>).</summary>
    internal DpsChannel<PlayerSlot> PlayerSlot => _seats[0].PlayerSlot;

    /// <summary>Seat 1's own-unit writer.</summary>
    internal DpsChannel<OwnUnit> OwnUnit => _seats[0].OwnUnit;

    /// <summary>Seat 1's gizmo writer.</summary>
    internal DpsChannel<GizmoFrame> GizmoFrame => _seats[0].GizmoFrame;

    internal DpsChannel<SimClock> SimClock { get; } = null!;

    internal DpsChannel<ClockStep> ClockStep { get; } = null!;

    /// <summary>The port of the module's ClockStep: the writer, or in a render job the barrier's queue (DD-022 D-B4).</summary>
    internal IOutboundChannel<DpsItem<ClockStep>> ClockStepPort { get; } = null!;

    /// <summary>The world epoch this station publishes now (tests and diagnostics).</summary>
    internal int Epoch => _epoch;

    /// <summary>Clock probes answered by the echo threads of every seat (tests).</summary>
    internal int ProbesAnswered
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _seats.Count; i++)
            {
                n += _seats[i].ProbesAnswered;
            }

            return n;
        }
    }

    /// <summary>Once per frame: the world epoch, every seat's client topics, and an entity whose playable flag changed with the seats' offers is written again.</summary>
    public void Pump()
    {
        // a forced epoch (the host's world gate) is announced one frame after it was taken: the host gathers the new world's terrain into this frame's tick, and the Session of an epoch
        // must name the terrain the runtime knows (T-050 finding A). A Session the tick writes meanwhile (a changed terrain) already carries the epoch and makes this one unnecessary.
        if (_resendIn > 0 && --_resendIn == 0 && _hasSession && _lastSession.WorldEpoch != (uint)_epoch)
        {
            Session.Send(new DpsItem<Session>(_lastSession));
        }

        // a load committed (the host's world gate): the station moves to the new world epoch, whether or not it saw a boundary itself
        if (_service is not null && _service.TryTakeForced(_epoch, out int forced))
        {
            _epoch = forced;
            _resendIn = 1;
        }

        for (int i = 0; i < _seats.Count; i++)
        {
            _seats[i].Pump();
        }

        if (!RefreshPlayable())
        {
            return;
        }

        foreach (var (id, entity) in _entities.ToArray())
        {
            if (entity.Playable != IsOffered(id))
            {
                EntityTopic.Send(new DpsItem<Entity>(entity));
            }
        }
    }

    /// <summary>
    /// Rebuilds the set of offerable units - the union of every seat's offers (the seats share one world, so it is one list for all of them, D-8) - and says whether it differs from the
    /// last one. Once per frame, no LINQ and no allocation in the steady state (the set and its buffers are reused).
    /// </summary>
    internal bool RefreshPlayable()
    {
        _offerable.Clear();
        ulong hash = 0;
        for (int s = 0; s < _stations.Length; s++)
        {
            var station = _stations[s];
            if (station is null)
            {
                continue;
            }

            var offers = station.Offers;
            for (int i = 0; i < offers.Count; i++)
            {
                if (offers[i].Kind != PlayerOfferKind.Unit)
                {
                    continue;   // a role or a spawn area is an entity of the world but no unit to take: Playable is for placed units
                }

                int id = offers[i].UnitId;
                if (_offerable.Add(id))
                {
                    hash += (ulong)(uint)id * 0x9E3779B97F4A7C15UL + 1;
                }
            }
        }

        if (_offerKnown && hash == _offerHash)
        {
            return false;
        }

        _offerKnown = true;
        _offerHash = hash;
        return true;
    }

    private bool IsOffered(int id) => _offerable.Contains(id);

    private DpsChannel<T> Add<T>(DpsChannel<T> channel)
        where T : struct
    {
        _channels.Add(channel);
        return channel;
    }

    public void Dispose()
    {
        if (_stepSink is not null)
        {
            _barrier?.ClearStepSink(_stepSink);
        }

        for (int i = _seats.Count - 1; i >= 0; i--)
        {
            _seats[i].Dispose();
        }

        _seats.Clear();
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }

        _channels.Clear();
        _participant.Dispose();
    }
}
