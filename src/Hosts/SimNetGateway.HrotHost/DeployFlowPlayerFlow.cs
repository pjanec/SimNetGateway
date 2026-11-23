using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Time;
using Fdp.Toolkit.Weather;
using Hrot.Common.Orders;
using Hrot.Gameplay;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using GwState = SimNetGateway.Gateway.HrotDis.PlayerSlotState;

namespace SimNetGateway.HrotHost;

/// <summary>Whether the node serves the deploy flow in-process (DD-023 D-14: "<c>ServerExists</c> = the editor composes the server"): the hub has an open server end (<c>LocalGameplayHub.HasServer</c> opens nothing).</summary>
internal static class DeployServer
{
    public static bool Exists(EntityRepository world) => Exists(LocalGameplayHub.Of(world));

    public static bool Exists(LocalGameplayHub hub) => hub.HasServer;
}

/// <summary>
/// The seat's flow over HROT's deploy server (DD-023 D-3, D-4, D-13, D-14; HROT <c>DESIGN_Player_Join.md</c> section 12): ONE <see cref="DeployFlowSystem"/> per seat on its own client end of
/// <c>LocalGameplayHub.Of(world)</c>, <c>register: false</c>, station = the seat's. The flow owns the state machine and the offers (HROT's), this class maps DPS onto its verbs (DD-023 3a) and its readouts
/// onto the seam. Built on the first frame of a world and again after every world boundary (D-13: the flow holds the world and the map).
/// <para>Environment (D-14): <c>GateOpen</c> = a scenario is loaded; <c>OperatingLive</c> = loaded and no recording is shown; <c>ServerExists</c> = the hub has a server end; <c>Possess</c> = the seat's own
/// <c>EntityOrders.Possess</c>. <b>Deviation from D-14, measured:</b> sim time advancing is NOT folded into <c>OperatingLive</c> (a pause would drop a request in flight to Waiting and forget it, see
/// <see cref="PauseAsWaiting"/>); it is folded into the flow's DEADLINE CLOCK instead: the flow's wall milliseconds count only while sim time advances, so a request made while paused waits (Requesting /
/// TakingControl), never times out as a refusal, and completes when the clock runs.</para>
/// </summary>
internal sealed class DeployFlowPlayerFlow : IPlayerFlow
{
    /// <summary>The most points of an area's outline the wire carries (<c>Offer.Outline</c>).</summary>
    public const int MaxOutlinePoints = 32;

    private readonly HrotServices _services;
    private readonly Func<EntityOrders?> _orders;
    private readonly int _station;
    private readonly DriveHandOver? _handOver;
    private readonly List<PlayerOffer> _scratch = new();
    private readonly Dictionary<int, OutlineCache> _outlines = new();
    private IReadOnlyList<PlayerOffer> _offers = Array.Empty<PlayerOffer>();
    private IReadOnlyList<DeployOffer>? _source;
    private EntityRepository? _world;
    private ISimClock? _clock;
    private LocalGameplayEnd? _end;
    private PlayerSlotReaderSystem? _slots;
    private DeployFlowSystem? _flow;
    private bool _live, _replaying;
    private long _runMs, _lastWall = -1;

    private sealed class OutlineCache
    {
        public object Shape = null!;
        public Vector3 At;
        public HrotVec3[] Points = Array.Empty<HrotVec3>();
    }

    /// <param name="handOver">The seat's HROT hand-over (DD-029 / T-072): while the flow is Playing the slot reports the unit the seat DRIVES (<see cref="SeatDrives"/>); HROT's deploy flow keeps the soldier it deployed (the server's slot stays Playing while the station holds him, riding or not).</param>
    public DeployFlowPlayerFlow(HrotServices services, Func<EntityOrders?> orders, int station, DriveHandOver? handOver = null)
    {
        _handOver = handOver;
        _services = services;
        _orders = orders;
        _station = station;
        NowMs = () => Environment.TickCount64;
    }

    /// <summary>Wall milliseconds (tests replace it); the flow's own deadlines count only the part of it in which sim time advances.</summary>
    public Func<long> NowMs { get; set; }

    /// <summary>Test hook for the measurement behind the deviation from D-14: true = <c>OperatingLive</c> also requires sim time to advance, as D-14 words it (a pause then sends the flow to Waiting).</summary>
    internal bool PauseAsWaiting { get; set; }

    /// <summary>How many area outlines were decimated to <see cref="MaxOutlinePoints"/> (evenly spaced points, order kept).</summary>
    internal int DecimatedOutlines { get; private set; }

    /// <summary>HROT's flow of this seat (null before the first frame of a world and after a boundary).</summary>
    internal DeployFlowSystem? Flow => _flow;

    public GwState State => _flow is null ? GwState.Waiting : _flow.State switch
    {
        DeployFlowState.Observing => GwState.Observing,
        DeployFlowState.Choosing => GwState.Choosing,
        DeployFlowState.Requesting => GwState.Requesting,
        DeployFlowState.TakingControl => GwState.TakingControl,
        DeployFlowState.Playing => GwState.Playing,
        DeployFlowState.Dead => GwState.Dead,
        _ => GwState.Waiting,   // DPS Spawning has no twin in the flow and is unused
    };

    public int UnitId => _flow is { State: DeployFlowState.TakingControl or DeployFlowState.Playing or DeployFlowState.Dead } f
        ? (f.State == DeployFlowState.Playing ? SeatDrives.UnitOf(_handOver, f.UnitNetId) : f.UnitNetId) : 0;

    public double RespawnAt => _flow?.RespawnAtSim ?? 0.0;

    public string Reason => _flow?.LastRefusal ?? string.Empty;

    public byte Side => UnitId == 0 || _flow is null ? (byte)0 : DisForce(_flow.Side);

    /// <summary>The side of the seat's last request or observe (0 = none yet: the flow's side is Neutral until it asked).</summary>
    public byte RequestedSide => _flow is null || _flow.Side == ForceId.Neutral ? (byte)0 : DisForce(_flow.Side);

    public IReadOnlyList<PlayerOffer> Offers => _offers;

    public void Frame(EntityRepository world, bool live, bool replaying)
    {
        _live = live;
        _replaying = replaying;
        if (_flow is null || !ReferenceEquals(world, _world))
        {
            Build(world);
        }

        bool advancing = _clock!.IsAdvancing;
        long wall = NowMs();
        if (_lastWall >= 0 && advancing && live && !replaying)
        {
            _runMs += wall - _lastWall;
        }

        _lastWall = wall;
        _slots!.Execute(world, 0f);
        _flow!.Execute(world, 0f);
        RefreshOffers(world);
    }

    private void Build(EntityRepository world)
    {
        Teardown();
        _world = world;
        _clock = SimClock.Of(world);   // one boxed clock per world (hot path: no per-frame allocation)
        var map = _services.EntityMap ?? throw new InvalidOperationException("a deploy flow needs the node's entity map");
        _end = LocalGameplayHub.Of(world).ClientEnd();
        var env = new DeployFlowEnvironment(
            GateOpen: () => _live,
            OperatingLive: () => !_replaying && (!PauseAsWaiting || _clock!.IsAdvancing),
            ServerExists: () => _world is { } w && DeployServer.Exists(w),
            Possess: unit =>
            {
                if (_orders() is not { } orders)
                {
                    return (false, "the seat has no orders");
                }

                // HROT's possession system runs only while sim time advances and an event published while it is paused is gone before it runs again: a possess sent now would be lost and the seat would
                // wait out its deadline for nothing. A LOCAL refusal is retried by the flow every 500 ms of its run clock (which stands still while paused), so the possess is sent in the first frames of running time.
                if (!_clock!.IsAdvancing)
                {
                    return (false, "sim time is paused");
                }

                var result = orders.Possess(unit);
                return (result.Accepted, result.Detail ?? string.Empty);
            },
            NowMs: () => _runMs);
        _flow = new DeployFlowSystem(_end, world, map, _station, env, register: false);
        _slots = new PlayerSlotReaderSystem(_end, () => _flow!.ServerExists);
        _flow.Slots = _slots.TryGetSlot;
    }

    /// <summary>Ends the flow: it is forgotten by <c>DeployFlowSystem.Of</c> and its client end stops receiving.</summary>
    private void Teardown()
    {
        _flow?.Unregister();
        _end?.Dispose();
        _flow = null;
        _slots = null;
        _end = null;
        _source = null;
        _offers = Array.Empty<PlayerOffer>();
        _outlines.Clear();
        _runMs = 0;
        _lastWall = -1;
    }

    public void Boundary()
    {
        // the server (when it still runs) frees the seat's slot and releases every unit the station holds, as the possession sweep of step 1 did
        if (_flow is { State: not DeployFlowState.Waiting })
        {
            _flow.Leave();
        }

        Teardown();
        _world = null;
    }

    public bool Join(PlayerJoin request)
    {
        if (_flow is not { } flow)
        {
            return false;
        }

        switch (request.Kind)
        {
            case PlayerJoinKind.Observe:
                return flow.Observe(ForceOf(request.Side, ForceId.Neutral));
            case PlayerJoinKind.Unit:
            {
                var offer = Find(flow, o => !o.IsRole && !o.IsArea && o.UnitNetId == request.UnitId);
                return Ask(flow, ForceOf(request.Side, offer?.Side ?? ForceId.Neutral), request.UnitId, 0, 0);
            }

            case PlayerJoinKind.Role:
            {
                var forceStated = request.Side != 0;
                var offer = request.UnitId != 0
                    ? Find(flow, o => o.IsRole && o.UnitNetId == request.UnitId)
                    : Find(flow, o => o.IsRole && o.SlotName == request.Role && (!forceStated || o.Side == ForceOf(request.Side, ForceId.Neutral)));
                if (offer is not { } role)
                {
                    return false;   // no such role offered
                }

                return Ask(flow, ForceOf(request.Side, role.Side), role.UnitNetId, request.Kit, request.AreaId);
            }

            default:
                return false;
        }
    }

    private static bool Ask(DeployFlowSystem flow, ForceId side, int unit, byte kit, int area)
    {
        if (flow.State == DeployFlowState.Observing)
        {
            flow.StopObserving();   // a seat that observes may choose (the placed-unit flow allowed it too)
        }

        return flow.Request(side, unit, kit, area);
    }

    private static DeployOffer? Find(DeployFlowSystem flow, Func<DeployOffer, bool> match)
    {
        var offers = flow.Offers;
        for (int i = 0; i < offers.Count; i++)
        {
            if (match(offers[i]))
            {
                return offers[i];
            }
        }

        return null;
    }

    public void Leave() => _flow?.Leave();

    // ---- offers ---------------------------------------------------------------------------------------------------------------

    /// <summary>The flow rebuilds its offer list when the server publishes one (about once a second): only then are the seam's offers mapped, and the list is replaced only when its CONTENT changed.</summary>
    private void RefreshOffers(EntityRepository world)
    {
        var source = _flow!.Offers;
        if (ReferenceEquals(source, _source))
        {
            return;
        }

        _source = source;
        _scratch.Clear();
        for (int i = 0; i < source.Count; i++)
        {
            _scratch.Add(Map(world, source[i]));
        }

        bool same = _scratch.Count == _offers.Count;
        for (int i = 0; same && i < _scratch.Count; i++)
        {
            same = _scratch[i].Equals(_offers[i]);
        }

        if (!same)
        {
            _offers = _scratch.ToArray();
        }
    }

    private PlayerOffer Map(EntityRepository world, DeployOffer o)
    {
        int force = DisForce(o.Side);
        if (o.IsArea)
        {
            return new PlayerOffer(o.UnitNetId, NameOf(o), force, PlayerOfferKind.Area, Outline: OutlineOf(world, o.UnitNetId));
        }

        if (o.IsRole)
        {
            // a full role is not free to take (the role's holder is always 0: it is a count against a maximum)
            bool free = o.Max == 0 || o.Count < o.Max;
            return new PlayerOffer(o.UnitNetId, NameOf(o), force, PlayerOfferKind.Role, o.Count, o.Max, free, o.Kits);
        }

        return new PlayerOffer(o.UnitNetId, NameOf(o), force, PlayerOfferKind.Unit, Free: o.HolderStation == 0);
    }

    private static string? NameOf(DeployOffer o) => string.IsNullOrEmpty(o.SlotName) ? null : o.SlotName;

    /// <summary>
    /// The area entity's outline in world metres (its position plus the relative points of its one area shape, as HROT's own <c>WeatherField.TryOutline</c> reads any area), the entity's Z for every
    /// point; more than <see cref="MaxOutlinePoints"/> points are decimated evenly (order kept, first point kept). Cached per area against the shape object and the position, so an unchanged area costs nothing.
    /// </summary>
    private IReadOnlyList<HrotVec3>? OutlineOf(EntityRepository world, int areaNetId)
    {
        var map = _services.EntityMap;
        if (map is null || !map.TryGetEntity(areaNetId, out var area) || !world.IsAlive(area) || !world.HasComponent<SimTransform>(area) || !WeatherField.TryOutline(world, area, out var shape))
        {
            return null;
        }

        var at = world.GetComponentRO<SimTransform>(area).Position;
        if (_outlines.TryGetValue(areaNetId, out var cached) && ReferenceEquals(cached.Shape, shape) && cached.At == at)
        {
            return cached.Points;
        }

        int count = shape.Points.Count, take = Math.Min(count, MaxOutlinePoints);
        var points = new HrotVec3[take];
        for (int i = 0; i < take; i++)
        {
            var p = shape.Points[take == count ? i : (int)((long)i * count / take)];
            points[i] = new HrotVec3(at.X + p.X, at.Y + p.Y, at.Z);
        }

        if (take < count)
        {
            DecimatedOutlines++;
        }

        _outlines[areaNetId] = new OutlineCache { Shape = shape, At = at, Points = points };
        return points;
    }

    // ---- sides ----------------------------------------------------------------------------------------------------------------

    /// <summary>DIS force numbering (1 friendly, 2 opposing, 3 neutral), the same rule as every other gather.</summary>
    private static byte DisForce(ForceId f) => f switch { ForceId.Friend => 1, ForceId.Hostile => 2, _ => 3 };

    /// <summary>The side a DIS force names; 0 (not stated) = <paramref name="fallback"/>.</summary>
    private static ForceId ForceOf(byte dis, ForceId fallback) => dis switch { 1 => ForceId.Friend, 2 => ForceId.Hostile, 3 => ForceId.Neutral, _ => fallback };
}
