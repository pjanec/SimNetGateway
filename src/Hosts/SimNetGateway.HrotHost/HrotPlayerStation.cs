using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Behavior.Possession;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Perception.Radar;
using Fdp.Toolkit.Time;
using Fdp.Toolkit.Tkb.Domain;
using Fdp.Toolkit.Replication.Components;
using Hrot.Common;
using Hrot.Common.Orders;
using Hrot.Look.Hud;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using IGizmoSource = SimNetGateway.Gateway.HrotDis.IGizmoSource;

namespace SimNetGateway.HrotHost;

/// <summary>
/// Where the slot comes from (DD-017 3.2b): step 1 derives it from possession of placed playable units on this node (<see cref="PossessionFlow"/>, where no deploy server runs); step 2
/// is HROT's deploy flow over the node's deploy server (<see cref="DeployFlowPlayerFlow"/>, T-053). The station facade owns the version, the control frames and the unit status; a flow owns the
/// state machine and the offers.
/// </summary>
internal interface IPlayerFlow
{
    PlayerSlotState State { get; }

    int UnitId { get; }

    double RespawnAt { get; }

    IReadOnlyList<PlayerOffer> Offers { get; }

    /// <summary>HROT's own text for the last refused join or timeout of this seat (empty = none); cleared by the next join.</summary>
    string Reason { get; }

    /// <summary>The DIS force of the side the seat plays while it holds a unit (0 = not stated; the slot then derives it from the offer of the unit).</summary>
    byte Side { get; }

    /// <summary>The DIS force of the side the seat last asked for (joined or observed), also before it holds a unit (0 = none asked; a flow with no such notion says 0).</summary>
    byte RequestedSide { get; }

    /// <summary>Wall milliseconds; tests replace it to move the timeouts.</summary>
    Func<long> NowMs { get; set; }

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
    private readonly Action<string>? _log;
    private IPlayerFlow _flow;
    private Func<long>? _nowMs;
    private bool _choose = true;
    private bool? _logged;
    private EntityRepository? _world;
    private EntityOrders? _orders;
    private PlayerSlotSnapshot _slot = new(PlayerSlotState.Waiting, 0, 0.0, 0);
    private PlayerUnitStatus _unit = PlayerUnitStatus.None;
    private int _useTarget;
    private byte _mount, _firePref;
    private bool _detached;
    private readonly List<PlayerWeapon> _ammo = new();
    private readonly UseAction[] _actions = new UseAction[OperatorUseActions.MaxActions];
    private readonly UseAction[] _pressActions = new UseAction[OperatorUseActions.MaxActions];
    private readonly List<PlayerUseAction> _useList = new();
    private readonly DriveHandOver _handOver;
    private long _stepMs = -1;
    private int _controlUnit;
    private byte _clientUseSeq, _fwdUseSeq, _getOutSeq;

    /// <summary>The station number of seat <paramref name="seat"/> (1 ..) of the node <paramref name="node"/> (DD-023 D-11): the node in the low 16 bits, the seat in the high 16; seat 0 is the node itself (the editor's own user).</summary>
    public static int StationOf(int node, int seat) => node | (seat << 16);

    /// <param name="services">What the station reads from the node.</param>
    /// <param name="station">The station this seat possesses as (<see cref="StationOf"/>): the one number every "is this mine" check and the orders' <c>Station</c> use.</param>
    /// <param name="log">Where the one-line decision of the seat's flow goes (null = nowhere).</param>
    public HrotPlayerStation(HrotServices services, int station, Action<string>? log = null)
    {
        _log = log;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Station = station;
        // ⭐ DD-029 D-1 — HROT's own hand-over (CE-11041), one per seat, over this seat's orders (its station number is the identity)
        _handOver = new DriveHandOver(() => _world, () => Orders);
        _flow = new PossessionFlow(services, () => Orders, station, _handOver);
        _gizmos.AnchorMapped = IsPublishedEntity;
    }

    /// <summary>A card's anchor is an entity of the node's map that is alive and not a replica: those are the entities the gateway publishes to DPS under their network id.</summary>
    private bool IsPublishedEntity(int id)
    {
        var map = _services.EntityMap;
        var world = _world;
        if (map is null || world is null)
        {
            return id != 0;
        }

        return map.TryGetEntity(id, out var e) && world.IsAlive(e)
            && !(world.IsComponentTypeRegistered<ReplicationRoute>() && world.HasComponent<ReplicationRoute>(e));
    }

    /// <summary>The station number of this seat.</summary>
    public int Station { get; }

    /// <summary>Wall milliseconds; tests replace it to move the timeouts.</summary>
    internal Func<long> NowMs
    {
        get => _flow.NowMs;
        set
        {
            _nowMs = value;
            _flow.NowMs = value;
        }
    }

    /// <summary>The seat's flow (tests read it): <see cref="DeployFlowPlayerFlow"/> where the node serves the deploy flow, else today's <see cref="PossessionFlow"/>.</summary>
    internal IPlayerFlow Flow => _flow;

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
            _orders = map is null ? null : new EntityOrders(world, map, null) { Station = Station };
            _flow.Boundary();
            _choose = true;
        }

        if (_choose)
        {
            ChooseFlow(world);
        }

        StepHandOver(live && !replaying);
        _flow.Frame(world, live, replaying);
        _gizmos.Frame(world, System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
        _unit = _flow.State == PlayerSlotState.Playing ? ReadUnit(world) : PlayerUnitStatus.None;
        Refresh();
    }

    /// <summary>
    /// THE decision point of the seat's flow (T-053, once per world and after every boundary, logged once): the deploy flow when the node composed the deploy server over its in-process hub
    /// (the editor, HROT Q114-B), else today's placed-unit possession. The flow it replaces is ended first (its client end closes; a deploy flow frees the seat's slot on the server).
    /// </summary>
    private void ChooseFlow(EntityRepository world)
    {
        _choose = false;
        bool deploy = _services.EntityMap is not null && DeployServer.Exists(world);
        if (deploy != _flow is DeployFlowPlayerFlow)
        {
            _flow.Boundary();
            _flow = deploy ? new DeployFlowPlayerFlow(_services, () => Orders, Station) : new PossessionFlow(_services, () => Orders, Station, _handOver);
            if (_nowMs is not null)
            {
                _flow.NowMs = _nowMs;
            }
        }

        if (_logged is { } said && said == deploy)
        {
            return;   // the same decision as the one already logged (a boundary that changes nothing says nothing)
        }

        _logged = deploy;
        _log?.Invoke($"player seat {Station}: {(deploy ? "the deploy flow of HROT's deploy server" : "possession of placed playable units (no deploy server on this node)")}");
    }

    /// <summary>The world boundary: the slot starts over and the gizmo view ends (its client re-sends it to the next runtime, DD-017 3.11).</summary>
    internal void Boundary()
    {
        _flow.Boundary();
        _choose = true;
        _gizmos.SetView(null);
        _useTarget = 0;
        _mount = 0;
        _firePref = 0;
        ResetPress();
        _handOver.Reset();
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

    /// <inheritdoc />
    /// <remarks>HROT's one addressing rule (<c>PlayerMessageAddressing.Addressed</c>) over this seat's viewer: the unit it holds (Playing), its side (the held unit's, else the side the seat asked for, else Neutral) and its station. No second filter here: the only work is the contract's DIS force back to HROT's force id.</remarks>
    public bool Addressed(string kind, int value)
    {
        int unit = _flow.State == PlayerSlotState.Playing ? _flow.UnitId : 0;
        byte dis = _flow.Side;
        if (dis == 0 && unit != 0)
        {
            var offers = _flow.Offers;
            for (int i = 0; i < offers.Count; i++)
            {
                if (offers[i].UnitId == unit)
                {
                    dis = (byte)offers[i].Force;
                }
            }
        }

        if (dis == 0)
        {
            dis = _flow.RequestedSide;   // a seat that has not deployed: the side it chose (T-055)
        }

        var side = dis switch { 1 => ForceId.Friend, 2 => ForceId.Hostile, _ => ForceId.Neutral };
        if (kind == "Side")
        {
            value = value switch { 1 => (int)ForceId.Friend, 2 => (int)ForceId.Hostile, _ => (int)ForceId.Neutral };
        }

        return Hrot.Common.Messages.PlayerMessageAddressing.Addressed(kind, value, new Hrot.Common.Messages.MessageViewer(unit, side, (StationId)Station));
    }

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
        ResetPress();
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
        byte useVerb = UseVerbOf(in frame);
        int useTarget = frame.UseTarget;
        byte useSeq = HandOverPress(in frame, ref useVerb, ref useTarget);
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
            UseSeq = useSeq,
            UseVerb = useVerb,
            UseTarget = useTarget,
            Lean = (byte)Math.Clamp((int)MathF.Round(frame.Lean), 0, 2),
            Binoculars = (byte)(frame.Binoculars ? 1 : 0),
            LookFrom = V(frame.LookFrom),
            LookDir = V(frame.LookDir),
            FirePref = frame.FirePref,
        });
    }

    /// <summary>Wall seconds since the last step, through the seat's own clock (<see cref="NowMs"/>: tests move it), into HROT's hand-over (DD-029 D-1).</summary>
    private void StepHandOver(bool running)
    {
        long now = _flow.NowMs();
        float dt = _stepMs < 0 ? 0f : Math.Max(0, now - _stepMs) / 1000f;
        _stepMs = now;
        if (running && !_detached)
        {
            _handOver.Step(dt);
        }
    }

    private void ResetPress()
    {
        _controlUnit = 0;
        _clientUseSeq = 0;
        _fwdUseSeq = 0;
    }

    /// <summary>
    /// The use press of a control frame against HROT's hand-over (DD-029 D-1; the same three rules as HROT's own station, <c>PossessionStation</c> §8h.9):
    /// a NEW press (the client's <c>UseSeq</c> moved) while the seat drives a vehicle with its soldier aboard is Get out (<see cref="DriveHandOver.TryGetOut"/>) and is not
    /// forwarded; the soldier's first frame after it carries that press (his only action aboard); a press that resolves to Drive or Pilot arms the hand-over. Returns the
    /// <c>UseSeq</c> to forward: the client's own, except where a press is withheld or the unit just changed (then the new unit's own counter, so a stale press is not replayed on it).
    /// </summary>
    private byte HandOverPress(in PlayerControl frame, ref byte useVerb, ref int useTarget)
    {
        var map = _services.EntityMap;
        var world = _world;
        if (map is null || world is null || _flow is not PossessionFlow || !map.TryGetEntity(_flow.UnitId, out var unit) || !world.IsAlive(unit))
        {
            _clientUseSeq = frame.UseSeq;
            _fwdUseSeq = frame.UseSeq;
            return frame.UseSeq;
        }

        bool press = frame.UseSeq != _clientUseSeq;
        _clientUseSeq = frame.UseSeq;
        if (_controlUnit != _flow.UnitId)
        {
            // the seat now plays another unit (the vehicle after the hand-over, the soldier after a Get out): its brain counts presses per unit
            _controlUnit = _flow.UnitId;
            _fwdUseSeq = world.IsComponentTypeRegistered<OperatorUseStatus>() && world.HasComponent<OperatorUseStatus>(unit)
                ? world.GetComponentRO<OperatorUseStatus>(unit).UseSeq : (byte)0;
        }

        if (!_handOver.GetOutRider.IsNull && _handOver.GetOutRider == unit)
        {
            // back in the soldier after a Get out: his first frame carries the Get-out press, as HROT's station queues it
            int vehicle = world.IsComponentTypeRegistered<IsEmbarkedTag>() && world.HasComponent<IsEmbarkedTag>(unit)
                && map.TryGetNetworkId(world.GetComponentRO<IsEmbarkedTag>(unit).VehicleEntity, out int v) ? v : 0;
            _handOver.GetOutSent();
            useVerb = 0;
            useTarget = vehicle;
            _fwdUseSeq = _getOutSeq;
            return _fwdUseSeq;
        }

        if (!press)
        {
            return _fwdUseSeq;
        }

        if (_handOver.TryGetOut(unit))
        {
            _getOutSeq = frame.UseSeq;   // the press is Get out: withheld from the vehicle, sent by the soldier's first frame
            return _fwdUseSeq;
        }

        // the action the press resolves to, the way HROT's brain resolves it (the verb is an index into the offered list)
        map.TryGetEntity(useTarget, out var target);
        int n = OperatorUseActions.For(world, unit, target, _pressActions);
        if (useVerb < n && _pressActions[useVerb] is { Available: true, Verb: UseVerbKind.Drive or UseVerbKind.Pilot } drive && !drive.Target.IsNull)
        {
            _handOver.Arm(unit, drive.Target);
        }

        _fwdUseSeq = frame.UseSeq;
        return _fwdUseSeq;
    }

    private static readonly string[] FireModeNames = BuildNames();

    private static string[] BuildNames()
    {
        var values = Enum.GetValues<OperatorFireMode>();
        var names = new string[values.Max(v => (int)v) + 1];
        foreach (var v in values)
        {
            names[(int)v] = v.ToString();
        }

        return names;
    }

    /// <summary>The name of a fire mode without the string an enum's <c>ToString</c> makes every call (hot path).</summary>
    private static string NameOf(OperatorFireMode mode) => (int)mode >= 0 && (int)mode < FireModeNames.Length && FireModeNames[(int)mode] is { } n ? n : mode.ToString();

    private static readonly string[] UseVerbNames = NamesOf<UseVerbKind>();
    private static readonly string[] UseReasonNames = NamesOf<UseReason>();

    private static string[] NamesOf<T>()
        where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        var names = new string[values.Max(v => Convert.ToInt32(v)) + 1];
        foreach (var v in values)
        {
            names[Convert.ToInt32(v)] = v.ToString();
        }

        return names;
    }

    private static string NameOf(UseVerbKind verb) => (int)verb < UseVerbNames.Length && UseVerbNames[(int)verb] is { } n ? n : verb.ToString();

    private static string NameOf(UseReason reason) => (int)reason < UseReasonNames.Length && UseReasonNames[(int)reason] is { } n ? n : reason.ToString();

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
        string reason = _flow.Reason;
        byte side = _flow.Side;
        if (_slot.State != _flow.State || _slot.UnitId != _flow.UnitId || _slot.RespawnAt != _flow.RespawnAt || (_slot.Reason ?? string.Empty) != reason || _slot.Side != side)
        {
            _slot = new PlayerSlotSnapshot(_flow.State, _flow.UnitId, _flow.RespawnAt, _slot.Version + 1, reason, side);
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
        var ammo = _ammo;   // reused: the consumer copies what it keeps (hot path: no allocation per frame)
        ammo.Clear();
        if (world.IsComponentTypeRegistered<WeaponAmmo>() && world.HasComponent<WeaponAmmo>(unit))
        {
            var a = world.GetComponentRO<WeaponAmmo>(unit);
            for (int i = 0; i < a.Count && i < WeaponAmmo.MaxMounts; i++)
            {
                // the weapon line of HROT's HUD (Hud.WeaponLine), field by field
                var dto = CombatTkb.MountOf(world, unit, i);
                long loaded = a.LoadedOf(i) != 0 ? a.LoadedOf(i) : unchecked((long)(dto?.AmmoGuid ?? 0UL));
                string kind = dto is { Hands: true } ? HudMounts.HandsKind : HudWords.KindOf(dto?.Category ?? WeaponCategory.Unknown);
                ammo.Add(new PlayerWeapon((byte)i, kind, HudWords.RoundName(world, loaded) ?? string.Empty, loaded, a.AmmoOf(i), a.MaxOf(i), a.MagazineOf(i), a.LoadedRoundsOf(i),
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

        int actions = OperatorUseActions.For(world, unit, target, _actions);   // the span overload, into a reused buffer
        var useList = _useList;   // reused: the whole offered list in HROT's order (DD-029 D-3), the consumer copies what it keeps
        useList.Clear();
        for (int i = 0; i < actions; i++)
        {
            var offered = _actions[i];
            useList.Add(new PlayerUseAction(NameOf(offered.Verb), HudWords.Label(world, offered), offered.Available, NameOf(offered.Reason), offered.HoldOnly));
        }

        for (int i = 0; i < actions; i++)
        {
            var action = _actions[i];
            if (action.Available)
            {
                verb = (byte)action.Verb;
                useLabel = HudWords.Label(world, action);
                if (!action.Target.IsNull && world.IsAlive(action.Target) && world.HasComponent<NetworkIdentity>(action.Target))
                {
                    useTarget = world.GetComponentRO<NetworkIdentity>(action.Target).Value;
                }

                break;
            }
        }

        // the selected weapon: a controlled weapon system's mount 0 for a slot of 32 or more, else the unit's own mount
        var (holder, local) = HudMounts.Resolve(world, unit, _mount);
        string fireMode = nameof(OperatorFireMode.Sight);
        float spread = 0f;
        if (!holder.IsNull && world.IsAlive(holder))
        {
            var mount = CombatTkb.MountOf(world, holder, local);
            fireMode = NameOf(OperatorFireModes.Of(world, holder, mount, 0L, _firePref));
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
            timeToImpact, seeker, verb == 0 ? string.Empty : ((UseVerbKind)verb).ToString(), useList);
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
    private readonly int _station;
    private readonly DriveHandOver? _handOver;
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

    /// <param name="handOver">The seat's HROT hand-over (DD-029 D-2): the unit the slot reports follows the unit the seat DRIVES (<see cref="DriveHandOver.HeldHere(out int)"/>), so after a Drive the slot names the vehicle. Null = the slot only follows its joined unit.</param>
    public PossessionFlow(HrotServices services, Func<EntityOrders?> orders, int station, DriveHandOver? handOver = null)
    {
        _services = services;
        _orders = orders;
        _station = station;
        _handOver = handOver;
        NowMs = () => Environment.TickCount64;
    }

    public Func<long> NowMs { get; set; }

    /// <summary>The seat's side is not tracked here: the slot derives it from the offer of the held unit.</summary>
    public byte Side => 0;

    public byte RequestedSide => 0;

    public PlayerSlotState State { get; private set; } = PlayerSlotState.Waiting;

    public int UnitId { get; private set; }

    public double RespawnAt => 0.0;

    /// <summary>HROT's own text for the last refused join of this station (empty = none); cleared by the next join.</summary>
    public string Reason { get; private set; } = string.Empty;

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
        FollowDriven();
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

    /// <summary>
    /// DD-029 D-2: a seat that is Playing reports the unit it DRIVES. HROT's hand-over (Drive: the soldier rides, the vehicle is possessed; Get out: the other way) moves the Fps
    /// possession from one unit to another; the slot names whichever unit the station holds as Fps now. While the hand-over is between its steps nothing is held as Fps and the
    /// slot stays on its unit.
    /// </summary>
    private void FollowDriven()
    {
        if (_handOver is null || State != PlayerSlotState.Playing || UnitId == 0)
        {
            return;
        }

        var held = _handOver.HeldHere(out int id);
        if (!held.IsNull && id != UnitId)
        {
            UnitId = id;
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
                else if (!gone && PossessionSystem.HolderOf(world, unit) == _station)
                {
                    State = PlayerSlotState.Playing;
                }
                else if (!gone && PossessionSystem.HolderOf(world, unit) is { } other && other != _station && _orders() is { } orders
                    && StationClaims.ForPossess(world, unit, _station, out _) != ClaimRefusal.None)
                {
                    // the race of two seats for one unit (DD-023 5b): the other station was granted it. Asking again is refused with HROT's own text (a refused possess changes nothing)
                    // and the seat is back to choosing at once instead of waiting out the timeout.
                    var refused = orders.Possess(UnitId);
                    Reason = refused.Accepted ? string.Empty : refused.Detail ?? string.Empty;
                    UnitId = 0;
                    State = PlayerSlotState.Choosing;
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
                else if (PossessionSystem.HolderOf(world, unit) != _station && _handOver is not { Handing: true })   // a hand-over in progress moves the possession between two units: wait for it
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
            if (!world.IsAlive(e) || PossessionSystem.HolderOf(world, e) != _station)
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
        if (map is not null && map.TryGetEntity(_releaseUnit, out var unit) && world.IsAlive(unit) && PossessionSystem.HolderOf(world, unit) == _station)
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

                var asked = orders.Possess(request.UnitId);
                if (!asked.Accepted)
                {
                    Reason = asked.Detail ?? string.Empty;   // HROT's refusal text (a unit another station holds, the wrong side ...)
                    return false;
                }

                Reason = string.Empty;

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
