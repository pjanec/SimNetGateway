using CycloneDDS.Runtime;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>One outbound item of a DPS topic: a sample to write, or (<see cref="Dispose"/>) the instance of the key in <see cref="Value"/> to dispose.</summary>
public readonly record struct DpsItem<T>(T Value, bool Dispose = false)
    where T : struct;

/// <summary>The writer of one topic as an outbound port channel: every item is written at once (the engine ticks on the host's main thread).</summary>
internal sealed class DpsChannel<T> : IOutboundChannel<DpsItem<T>>, IDisposable
    where T : struct
{
    private readonly DdsWriter<T> _writer;
    private readonly Func<T, T>? _prepare;
    private readonly Action<T, bool>? _observe;

    public DpsChannel(DdsParticipant participant, Func<T, T>? prepare = null, Action<T, bool>? observe = null)
    {
        _writer = new DdsWriter<T>(participant);
        _prepare = prepare;
        _observe = observe;
    }

    public int Written { get; private set; }

    /// <summary>A test seam: sees every sample written (not the disposals).</summary>
    internal Action<T>? Tap { get; set; }

    public void Send(in DpsItem<T> item)
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

    public void Dispose() => _writer.Dispose();
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
    private readonly Dictionary<int, Entity> _entities = new();
    private readonly List<IDisposable> _channels = new();
    private readonly List<DpsSeat> _seats = new();
    private readonly IPlayerStation?[] _stations;

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
        _participant = new DdsParticipant((uint)settings.Domain, defaultPartition: WorldPartition);
        try
        {
            Session = Add(new DpsChannel<Session>(_participant, prepare: s =>
            {
                s.WorldEpoch = (uint)_epoch;
                s.StationName = partition.Length > 64 ? partition[..64] : partition;
                _lastSession = s;
                _hasSession = true;
                _service?.MarkPublished(s.WorldEpoch);
                return s;
            }));
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
            }));
            EntityState = Add(new DpsChannel<EntityState>(_participant));
            Articulation = Add(new DpsChannel<Articulation>(_participant));
            PersonState = Add(new DpsChannel<PersonState>(_participant));
            PersonAim = Add(new DpsChannel<PersonAim>(_participant));
            Appearance = Add(new DpsChannel<Appearance>(_participant));
            Fire = Add(new DpsChannel<Fire>(_participant));
            Detonation = Add(new DpsChannel<Detonation>(_participant));
            Effect = Add(new DpsChannel<Effect>(_participant));
            Impact = Add(new DpsChannel<Impact>(_participant));
            VehicleLook = Add(new DpsChannel<VehicleLook>(_participant));
            Round = Add(new DpsChannel<Round>(_participant));
            MunitionLook = Add(new DpsChannel<MunitionLook>(_participant));
            FlameJet = Add(new DpsChannel<FlameJet>(_participant));
            FireSpots = Add(new DpsChannel<FireSpots>(_participant));
            GroundMarks = Add(new DpsChannel<GroundMarks>(_participant));
            Animal = Add(new DpsChannel<Animal>(_participant));
            BuildingDamage = Add(new DpsChannel<BuildingDamage>(_participant));
            Mover = Add(new DpsChannel<Mover>(_participant));
            MoverTracks = Add(new DpsChannel<MoverTracks>(_participant));
            TrafficLights = Add(new DpsChannel<TrafficLights>(_participant));
            TkbType = Add(new DpsChannel<TkbType>(_participant));
            ManDowned = Add(new DpsChannel<ManDowned>(_participant));
            PlayerMessage = Add(new DpsChannel<PlayerMessage>(_participant));   // INTERIM (DD-023 section 9): on the world participant until HROT Q114-I routes it per seat
            Status = Add(new DpsChannel<Status>(_participant));
            Exercise = Add(new DpsChannel<Exercise>(_participant));
            View = Add(new DpsChannel<View>(_participant));
            SimClock = Add(new DpsChannel<SimClock>(_participant));
            ClockStep = Add(new DpsChannel<ClockStep>(_participant));
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

    internal DpsChannel<PlayerMessage> PlayerMessage { get; } = null!;

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
        // a load committed (the host's world gate): the station moves to the new world epoch and says so at once, whether or not it saw a boundary itself
        if (_service is not null && _service.TryTakeForced(_epoch, out int forced))
        {
            _epoch = forced;
            if (_hasSession)
            {
                Session.Send(new DpsItem<Session>(_lastSession));
            }
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
