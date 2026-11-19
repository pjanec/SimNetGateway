using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Behavior.Possession;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Replication.Components;
using Hrot.Common;
using Hrot.Common.Orders;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using IGizmoSource = SimNetGateway.Gateway.HrotDis.IGizmoSource;

namespace SimNetGateway.HrotHost;

/// <summary>
/// Where the slot comes from (DD-017 3.2b): step 1 derives it from possession of placed playable units on this node; step 2 swaps in HROT's deploy flow on a
/// player node. The station facade owns the version, the control frames and the unit status; a flow owns the state machine and the offers.
/// </summary>
internal interface IPlayerFlow
{
    PlayerSlotState State { get; }

    int UnitId { get; }

    double RespawnAt { get; }

    IReadOnlyList<PlayerOffer> Offers { get; }

    /// <summary>Called once per frame on the main thread; <paramref name="live"/> = a scenario runs, <paramref name="replaying"/> = a recording is shown.</summary>
    void Frame(EntityRepository world, bool live, bool replaying);

    bool Join(PlayerJoin request);

    void Leave();

    /// <summary>The world boundary: forget everything about the old world.</summary>
    void Boundary();
}

/// <summary>
/// The host's <see cref="IPlayerStation"/> over HROT's player mode (DD-017 3.6, step 1: the editor node is the station). The flow is
/// <see cref="PossessionFlow"/>; control frames go through HROT's own <c>EntityOrders.Control</c>.
/// </summary>
public sealed class HrotPlayerStation : IPlayerStation, IGizmoSource
{
    private readonly HrotGizmoHost _gizmos = new();
    private readonly HrotServices _services;
    private readonly PossessionFlow _flow;
    private EntityRepository? _world;
    private EntityOrders? _orders;
    private PlayerSlotSnapshot _slot = new(PlayerSlotState.Waiting, 0, 0.0, 0);
    private PlayerUnitStatus _unit = PlayerUnitStatus.None;
    private int _useTarget;

    public HrotPlayerStation(HrotServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _flow = new PossessionFlow(services, () => Orders);
    }

    /// <summary>Wall milliseconds; tests replace it to move the timeouts.</summary>
    internal Func<long> NowMs
    {
        get => _flow.NowMs;
        set => _flow.NowMs = value;
    }

    private EntityOrders? Orders => _orders;

    public PlayerSlotSnapshot Slot => _slot;

    public IReadOnlyList<PlayerOffer> Offers => _flow.Offers;

    public PlayerUnitStatus Unit => _unit;

    /// <summary>Once per frame, from the host's system: follows HROT's own player state and refreshes the unit status.</summary>
    internal void Frame(EntityRepository world, bool live, bool replaying)
    {
        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            var map = _services.EntityMap;
            _orders = map is null ? null : new EntityOrders(world, map, null) { Station = _services.LocalNodeId };
            _flow.Boundary();
        }

        _flow.Frame(world, live, replaying);
        _gizmos.Frame(world, System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
        _unit = _flow.State == PlayerSlotState.Playing ? ReadUnit(world) : PlayerUnitStatus.None;
        Refresh();
    }

    internal void Boundary()
    {
        _flow.Boundary();
        _useTarget = 0;
        _unit = PlayerUnitStatus.None;
        Refresh();
    }

    /// <inheritdoc />
    public void SetView(GizmoViewRequest? view) => _gizmos.SetView(view);

    /// <inheritdoc />
    public IReadOnlyList<GizmoLayerFrame> Take() => _gizmos.Take();

    internal HrotGizmoHost Gizmos => _gizmos;

    public bool Join(PlayerJoin request)
    {
        bool ok = _flow.Join(request);
        Refresh();
        return ok;
    }

    public void Leave()
    {
        _flow.Leave();
        _useTarget = 0;
        Refresh();
    }

    public void Control(in PlayerControl frame)
    {
        if (_flow.State != PlayerSlotState.Playing || _orders is null)
        {
            return;
        }

        _useTarget = frame.UseTarget;
        _orders.Control(_flow.UnitId, new Fdp.Toolkit.Behavior.Possession.OperatorControlFrameEvent
        {
            Seq = frame.Seq,
            Forward = frame.Forward,
            Lateral = frame.Lateral,
            Steer = frame.Steer,
            Yaw = frame.Yaw,
            Vertical = frame.Vertical,
            Gait = frame.Gait,
            Trigger = (byte)(frame.Trigger ? 1 : 0),
            Mount = frame.Mount,
            AimPoint = V(frame.AimPoint),
            Stance = frame.Stance,
            ShotSeq = frame.ShotSeq,
            CancelSeq = frame.CancelSeq,
            AmmoSeq = frame.AmmoSeq,
            ShotTarget = frame.ShotTarget,
            UseSeq = frame.UseSeq,
            UseVerb = frame.UseVerb,
            UseTarget = frame.UseTarget,
            Lean = (byte)Math.Clamp((int)MathF.Round(frame.Lean), 0, 2),
            Binoculars = (byte)(frame.Binoculars ? 1 : 0),
            LookFrom = V(frame.LookFrom),
            LookDir = V(frame.LookDir),
            FirePref = frame.FirePref,
        });
    }

    private static Vector3 V(HrotVec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private void Refresh()
    {
        if (_slot.State != _flow.State || _slot.UnitId != _flow.UnitId || _slot.RespawnAt != _flow.RespawnAt)
        {
            _slot = new PlayerSlotSnapshot(_flow.State, _flow.UnitId, _flow.RespawnAt, _slot.Version + 1);
        }
    }

    /// <summary>Health, ammunition per mount and the use verdict of the held unit, each from HROT's own readers; a missing component leaves its part empty.</summary>
    private PlayerUnitStatus ReadUnit(EntityRepository world)
    {
        var map = _services.EntityMap;
        if (map is null || !map.TryGetEntity(_flow.UnitId, out var unit) || !world.IsAlive(unit))
        {
            return PlayerUnitStatus.None;
        }

        double health = 0.0, max = 0.0;
        if (world.IsComponentTypeRegistered<Health>() && world.HasComponent<Health>(unit))
        {
            var h = world.GetComponentRO<Health>(unit);
            health = h.Current;
            max = h.Max;
        }

        var ammo = new List<(byte Mount, long Ammo, int Rounds)>();
        if (world.IsComponentTypeRegistered<WeaponAmmo>() && world.HasComponent<WeaponAmmo>(unit))
        {
            var a = world.GetComponentRO<WeaponAmmo>(unit);
            int[] rounds = [a.Ammo0, a.Ammo1, a.Ammo2, a.Ammo3];
            long[] loaded = [a.Load0, a.Load1, a.Load2, a.Load3];
            for (int i = 0; i < a.Count && i < WeaponAmmo.MaxMounts; i++)
            {
                ammo.Add(((byte)i, loaded[i], rounds[i]));
            }
        }

        byte verb = 0;
        int useTarget = 0;
        var target = Entity.Null;
        if (_useTarget != 0)
        {
            map.TryGetEntity(_useTarget, out target);
        }

        foreach (var action in OperatorUseActions.For(world, unit, target))
        {
            if (action.Available)
            {
                verb = (byte)action.Verb;
                if (!action.Target.IsNull && world.IsAlive(action.Target) && world.HasComponent<NetworkIdentity>(action.Target))
                {
                    useTarget = world.GetComponentRO<NetworkIdentity>(action.Target).Value;
                }

                break;
            }
        }

        return new PlayerUnitStatus(health, max, ammo, verb, useTarget);
    }
}

/// <summary>
/// Step 1: the editor node is the station. Offers are the placed units carrying <c>Playable</c>; joining a unit is HROT's <c>EntityOrders.Possess</c> and the
/// slot follows <c>PossessedBy</c> (TakingControl until this station holds the unit, Playing, Dead when the unit is out of the fight). Roles come with the deploy flow.
/// </summary>
internal sealed class PossessionFlow : IPlayerFlow
{
    private const long TimeoutMs = 10_000;

    private readonly HrotServices _services;
    private readonly Func<EntityOrders?> _orders;
    private readonly List<PlayerOffer> _offers = new();
    private EntityRepository? _world;
    private EntityQuery? _query;
    private bool _live, _replaying;
    private long _deadline;
    private int _releaseUnit;
    private long _releaseDeadline;

    public PossessionFlow(HrotServices services, Func<EntityOrders?> orders)
    {
        _services = services;
        _orders = orders;
        NowMs = () => Environment.TickCount64;
    }

    public Func<long> NowMs { get; set; }

    public PlayerSlotState State { get; private set; } = PlayerSlotState.Waiting;

    public int UnitId { get; private set; }

    public double RespawnAt => 0.0;

    public IReadOnlyList<PlayerOffer> Offers => _offers;

    public void Boundary()
    {
        _world = null;
        _query = null;
        _offers.Clear();
        _releaseUnit = 0;
        UnitId = 0;
        State = PlayerSlotState.Waiting;
    }

    public void Frame(EntityRepository world, bool live, bool replaying)
    {
        _live = live;
        _replaying = replaying;
        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            if (!world.IsComponentTypeRegistered<Playable>())
            {
                world.RegisterComponent<Playable>();
            }

            _query = world.Query().With<Playable>().With<NetworkIdentity>().Build();
        }

        if (!live)
        {
            _offers.Clear();
            UnitId = 0;
            _releaseUnit = 0;
            _replayObserving = false;
            State = PlayerSlotState.Waiting;
            return;
        }

        if (replaying)
        {
            // a recording is shown: no HROT call, the player only observes
            _offers.Clear();
            UnitId = 0;
            _releaseUnit = 0;
            _replayObserving = true;
            State = PlayerSlotState.Observing;
            return;
        }

        if (State == PlayerSlotState.Waiting || (State == PlayerSlotState.Observing && _replayObserving))
        {
            State = PlayerSlotState.Choosing;
        }

        _replayObserving = false;
        RefreshOffers(world);
        Follow(world);
        LetGoOfLateGrant(world);
    }

    private bool _replayObserving;

    private void RefreshOffers(EntityRepository world)
    {
        var fresh = new List<PlayerOffer>();
        foreach (var e in _query!)
        {
            if (!world.IsAlive(e))
            {
                continue;
            }

            var playable = world.GetComponentRO<Playable>(e);
            int force = playable.Side switch { ForceId.Friend => 1, ForceId.Hostile => 2, _ => 3 };
            string name = playable.SlotName.ToString();
            fresh.Add(new PlayerOffer(world.GetComponentRO<NetworkIdentity>(e).Value, string.IsNullOrEmpty(name) ? null : name, force));
        }

        fresh.Sort((a, b) => a.UnitId.CompareTo(b.UnitId));
        if (!fresh.SequenceEqual(_offers))
        {
            _offers.Clear();
            _offers.AddRange(fresh);
        }
    }

    /// <summary>The slot follows HROT's own possession state of the unit.</summary>
    private void Follow(EntityRepository world)
    {
        if (State is not (PlayerSlotState.TakingControl or PlayerSlotState.Playing or PlayerSlotState.Dead))
        {
            return;
        }

        var map = _services.EntityMap;
        bool known = map is not null && map.TryGetEntity(UnitId, out _);
        Entity unit = default;
        if (known)
        {
            map!.TryGetEntity(UnitId, out unit);
        }

        bool gone = !known || !world.IsAlive(unit);
        switch (State)
        {
            case PlayerSlotState.TakingControl:
                if (!gone && !PossessionSystem.Alive(world, unit))
                {
                    State = PlayerSlotState.Dead;
                }
                else if (!gone && PossessionSystem.HolderOf(world, unit) == _services.LocalNodeId)
                {
                    State = PlayerSlotState.Playing;
                }
                else if (gone || NowMs() >= _deadline)
                {
                    if (!gone)
                    {
                        _releaseUnit = UnitId;   // a grant that comes late is let go again
                        _releaseDeadline = NowMs() + TimeoutMs;
                    }

                    UnitId = 0;
                    State = PlayerSlotState.Choosing;
                }

                break;
            case PlayerSlotState.Playing:
                if (gone)
                {
                    UnitId = 0;
                    State = PlayerSlotState.Choosing;
                }
                else if (!PossessionSystem.Alive(world, unit))
                {
                    State = PlayerSlotState.Dead;
                }
                else if (PossessionSystem.HolderOf(world, unit) != _services.LocalNodeId)
                {
                    UnitId = 0;   // released by HROT or taken by another station
                    State = PlayerSlotState.Choosing;
                }

                break;
            case PlayerSlotState.Dead:
                if (gone)
                {
                    UnitId = 0;
                    State = PlayerSlotState.Choosing;
                }

                break;
        }
    }

    private void LetGoOfLateGrant(EntityRepository world)
    {
        if (_releaseUnit == 0)
        {
            return;
        }

        var map = _services.EntityMap;
        if (map is not null && map.TryGetEntity(_releaseUnit, out var unit) && world.IsAlive(unit) && PossessionSystem.HolderOf(world, unit) == _services.LocalNodeId)
        {
            _orders()?.Unpossess(_releaseUnit);
            _releaseUnit = 0;
        }
        else if (NowMs() >= _releaseDeadline)
        {
            _releaseUnit = 0;
        }
    }

    public bool Join(PlayerJoin request)
    {
        if (_replaying)
        {
            State = PlayerSlotState.Observing;
            return false;
        }

        if (!_live || State is not (PlayerSlotState.Choosing or PlayerSlotState.Observing or PlayerSlotState.Dead))
        {
            return false;
        }

        switch (request.Kind)
        {
            case PlayerJoinKind.Observe:
                UnitId = 0;
                State = PlayerSlotState.Observing;
                return true;
            case PlayerJoinKind.Unit:
                if (!_offers.Any(o => o.UnitId == request.UnitId) || _orders() is not { } orders)
                {
                    return false;
                }

                if (!orders.Possess(request.UnitId).Accepted)
                {
                    return false;
                }

                UnitId = request.UnitId;
                _deadline = NowMs() + TimeoutMs;
                State = PlayerSlotState.TakingControl;
                return true;
            default:
                return false;   // roles come with the deploy flow of a player node
        }
    }

    public void Leave()
    {
        switch (State)
        {
            case PlayerSlotState.Playing:
                _orders()?.Unpossess(UnitId);
                break;
            case PlayerSlotState.TakingControl:
                _releaseUnit = UnitId;
                _releaseDeadline = NowMs() + TimeoutMs;
                break;
            case PlayerSlotState.Observing or PlayerSlotState.Dead:
                break;
            default:
                return;
        }

        UnitId = 0;
        State = PlayerSlotState.Choosing;
    }
}
