using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Behavior.Possession;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Perception.Radar;
using Fdp.Toolkit.Time;
using Fdp.Toolkit.Tkb.Domain;
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
    private byte _mount, _firePref;
    private bool _detached;

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

    /// <summary>The world boundary: the slot starts over and the gizmo view ends (its client re-sends it to the next runtime, DD-017 3.11).</summary>
    internal void Boundary()
    {
        _flow.Boundary();
        _gizmos.SetView(null);
        _useTarget = 0;
        _mount = 0;
        _firePref = 0;
        _unit = PlayerUnitStatus.None;
        Refresh();
    }

    /// <summary>
    /// The node's teardown: from now on the station never reads or writes the world (it may run after the simulation thread stopped). Join answers false, Control and
    /// Leave do nothing, the gizmo view ends and no new one is taken. Called before the gateway's farewell tick, whose plugin pumps still call the seam.
    /// </summary>
    internal void Detach()
    {
        _detached = true;
        _orders = null;
        _world = null;
        _gizmos.SetView(null);
    }

    /// <inheritdoc />
    public void SetView(GizmoViewRequest? view)
    {
        if (!_detached)
        {
            _gizmos.SetView(view);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<GizmoLayerFrame> Take() => _gizmos.Take();

    internal HrotGizmoHost Gizmos => _gizmos;

    public bool Join(PlayerJoin request)
    {
        if (_detached)
        {
            return false;
        }

        bool ok = _flow.Join(request);
        Refresh();
        return ok;
    }

    public void Leave()
    {
        if (_detached)
        {
            return;
        }

        _flow.Leave();
        _useTarget = 0;
        _mount = 0;
        _firePref = 0;
        Refresh();
    }

    public void Control(in PlayerControl frame)
    {
        if (_detached || _flow.State != PlayerSlotState.Playing || _orders is null)
        {
            return;
        }

        _useTarget = frame.UseTarget;
        _mount = frame.Mount;
        _firePref = frame.FirePref;
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
            UseVerb = UseVerbOf(in frame),
            UseTarget = frame.UseTarget,
            Lean = (byte)Math.Clamp((int)MathF.Round(frame.Lean), 0, 2),
            Binoculars = (byte)(frame.Binoculars ? 1 : 0),
            LookFrom = V(frame.LookFrom),
            LookDir = V(frame.LookDir),
            FirePref = frame.FirePref,
        });
    }

    private static Vector3 V(HrotVec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    /// <summary>
    /// The use verb of a frame: the verb named by the client (DD-020 3.3) when it names one, else the number it sent. A name HROT has no such verb for is no verb (a
    /// client must not get a different action than it asked for).
    /// </summary>
    internal static byte UseVerbOf(in PlayerControl frame) =>
        string.IsNullOrEmpty(frame.UseVerbName) ? frame.UseVerb
        : Enum.TryParse<UseVerbKind>(frame.UseVerbName, out var verb) && Enum.IsDefined(verb) && verb.ToString() == frame.UseVerbName ? (byte)verb : (byte)0;

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

        double now = SimClock.TotalTimeOf(world);
        var ammo = new List<PlayerWeapon>();
        if (world.IsComponentTypeRegistered<WeaponAmmo>() && world.HasComponent<WeaponAmmo>(unit))
        {
            var a = world.GetComponentRO<WeaponAmmo>(unit);
            for (int i = 0; i < a.Count && i < WeaponAmmo.MaxMounts; i++)
            {
                // the weapon line of HROT's HUD (Hud.WeaponLine), field by field
                var dto = CombatTkb.MountOf(world, unit, i);
                long loaded = a.LoadedOf(i) != 0 ? a.LoadedOf(i) : unchecked((long)(dto?.AmmoGuid ?? 0UL));
                string kind = dto is { Hands: true } ? HudNames.HandsKind : HudNames.KindOf(dto?.Category ?? WeaponCategory.Unknown);
                ammo.Add(new PlayerWeapon((byte)i, kind, HudNames.RoundName(world, loaded) ?? string.Empty, loaded, a.AmmoOf(i), a.MaxOf(i), a.MagazineOf(i), a.LoadedRoundsOf(i),
                    a.IsBusy(i), a.IsHot(i), a.HeatPercentAt(i, now)));
            }
        }

        byte verb = 0;
        int useTarget = 0;
        string useLabel = string.Empty;
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
                useLabel = HudNames.Label(world, action);
                if (!action.Target.IsNull && world.IsAlive(action.Target) && world.HasComponent<NetworkIdentity>(action.Target))
                {
                    useTarget = world.GetComponentRO<NetworkIdentity>(action.Target).Value;
                }

                break;
            }
        }

        // the selected weapon: a controlled weapon system's mount 0 for a slot of 32 or more, else the unit's own mount
        var (holder, local) = HudNames.Resolve(world, unit, _mount);
        string fireMode = nameof(OperatorFireMode.Sight);
        float spread = 0f;
        if (!holder.IsNull && world.IsAlive(holder))
        {
            var mount = CombatTkb.MountOf(world, holder, local);
            fireMode = OperatorFireModes.Of(world, holder, mount, 0L, _firePref).ToString();
            spread = HitModel.Sigma(world, holder, mount, now);
        }

        // the use press the brain last answered, shown by the client when it is its own last press
        byte useSeq = 0;
        string outcome = string.Empty, reason = string.Empty;
        if (world.IsComponentTypeRegistered<OperatorUseStatus>() && world.HasComponent<OperatorUseStatus>(unit))
        {
            var status = world.GetComponentRO<OperatorUseStatus>(unit);
            useSeq = status.UseSeq;
            outcome = status.Outcome.ToString();
            reason = status.Reason.ToString();
        }

        // the aircraft warnings; a missile warning is a launch cue while its time to impact (counted from its arrival) has not run out, as HROT's fighter HUD reads it
        string radar = string.Empty;
        float radarBearing = 0f;
        if (world.IsComponentTypeRegistered<RadarWarning>() && world.HasComponent<RadarWarning>(unit))
        {
            var warning = world.GetComponentRO<RadarWarning>(unit);
            radar = warning.Level.ToString();
            radarBearing = warning.BearingDeg;
        }

        bool launch = false;
        float missileBearing = 0f, timeToImpact = 0f;
        string seeker = string.Empty;
        if (world.IsComponentTypeRegistered<MissileWarning>() && world.HasComponent<MissileWarning>(unit))
        {
            var warning = world.GetComponentRO<MissileWarning>(unit);
            float left = warning.TimeToImpactS - (float)(now - warning.At.Seconds);
            if (left > 0f)
            {
                launch = true;
                missileBearing = warning.BearingDeg;
                timeToImpact = left;
                seeker = warning.Seeker.ToString();
            }
        }

        return new PlayerUnitStatus(health, max, ammo, verb, useTarget, _mount, fireMode, useLabel, useSeq, outcome, reason, spread, radar, radarBearing, launch, missileBearing,
            timeToImpact, seeker, verb == 0 ? string.Empty : ((UseVerbKind)verb).ToString());
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
    private bool _sweep;
    private readonly Dictionary<int, long> _sweepAsked = new();

    // Units a player of this station has asked to possess; kept across world boundaries, so the sweep never releases a unit this node holds for another reason
    // (the editor operator possessing a playable unit, or a host where no player plugin is installed).
    private readonly HashSet<int> _joined = new();

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
        _sweep = true;
        _sweepAsked.Clear();
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
        Sweep(world);
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

    /// <summary>
    /// After a world boundary nobody plays here, but an in-place restore (a checkpoint, a keyframe, the end of a dry run) puts back the world's <c>PossessedBy</c>, which
    /// HROT keeps in its binary snapshots: a playable unit held by this node would stay possessed, its AI paused, with no player. So on the live frames after a boundary
    /// every playable unit this node holds that a player of this station once joined - other than the one a new player is taking - is released, asked again at most
    /// once a second (HROT applies a release only while time runs), until none is held. Units no player joined are not this station's to release.
    /// </summary>
    private void Sweep(EntityRepository world)
    {
        if (!_sweep)
        {
            return;
        }

        bool held = false;
        foreach (var e in _query!)
        {
            if (!world.IsAlive(e) || PossessionSystem.HolderOf(world, e) != _services.LocalNodeId)
            {
                continue;
            }

            int id = world.GetComponentRO<NetworkIdentity>(e).Value;
            if (!_joined.Contains(id))
            {
                continue;   // held by this node, but not through a player of this station
            }

            if (id == UnitId || id == _releaseUnit)
            {
                continue;   // the new player's unit, or one already being let go
            }

            held = true;
            long now = NowMs();
            if (!_sweepAsked.TryGetValue(id, out long asked) || now - asked >= 1000)
            {
                _sweepAsked[id] = now;
                _orders()?.Unpossess(id);
            }
        }

        if (!held)
        {
            _sweep = false;
            _sweepAsked.Clear();
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

                _joined.Add(request.UnitId);
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
