using SimNetGateway.Engine;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

/// <summary>
/// The showcase "Convoy Ambush" (DD-002, R-043): acts 0 - 11 with every success condition S-* as a <see cref="CheckStep"/>. The cast
/// and timeline are those of DD-002 section 4; the acts' nominal times are runner times (the manual clock), the report prints the
/// sim time next to them. Sim time: free-running in Idle (act 1), reset to 0 by Load at 6.0 s, running from Start at 6.5 s, frozen
/// by the pause from 90 s to 99 s.
/// <para>
/// Cast: blue convoy "Viper" (NED formation 7, three trucks), blue drone "Hawk" (NED 21, turns at 40 s), checkpoint "Gate" (NED 22,
/// static), later the drone "Kestrel" (NED 23) that replaces Hawk; red tank "T-1" (RPR object 0x601), red squad "Wolf" (RPR
/// aggregate 0x700 with the platforms 0x701 .. 0x704, listed by DIS identifier (5,7,1..4)) and six red reinforcements (0x801 ..
/// 0x806). RPR entities are kept alive by quiet keep-alive updates (the gateway reaps RPR entities that are silent for 12 s);
/// "Wolf-1" and "Wolf-2" fall silent for 10 s around t = 50 s on purpose (S-4.5) and "Wolf-3" stops for good at t = 101 s (S-9.2).
/// </para>
/// <para>
/// Members of a RPR aggregate are counted from 1 on the RPR side (Wolf-1 .. Wolf-4, DIS entity numbers 1 .. 4); NED knows them by
/// their position in the list (member index 0 .. 3). A formation message can only name members by index.
/// </para>
/// </summary>
public sealed partial class ConvoyAmbushScript
{
    // ---- the cast -------------------------------------------------------------------------------------------------------

    private const ulong ViperId = 7;
    private const ulong HawkId = 21;
    private const ulong GateId = 22;
    private const ulong KestrelId = 23;

    private const uint T1Handle = 0x601;
    private const uint WolfHandle = 0x700;
    private const uint WolfFirstMember = 0x701;
    private const uint ReinforcementFirst = 0x801;

    private const ushort RedSite = 5;
    private const ushort RedApplication = 7;
    private const ushort T1Number = 100;

    private const int TkbViperUnit = 100;
    private const int TkbTruck = 101;
    private const int TkbUav = 200;
    private const int TkbCheckpoint = 300;
    private const int TkbEnemyTank = 400;
    private const int TkbMunitionA = 501;
    private const int TkbMunitionB = 502;

    private static readonly Guid RprFireE1 = Guid.Parse("e1000000-0000-4000-8000-000000000001");
    private static readonly Guid NedDetonationD1 = Guid.Parse("d1000000-0000-4000-8000-000000000001");
    private static readonly Guid NedFireF1 = Guid.Parse("f1000000-0000-4000-8000-000000000001");
    private static readonly Guid NedFireF2 = Guid.Parse("f2000000-0000-4000-8000-000000000002");
    private static readonly Guid NedFireF3 = Guid.Parse("f3000000-0000-4000-8000-000000000003");

    // ---- times (runner seconds) -----------------------------------------------------------------------------------------

    private const double LoadAt = 6.0;
    private const double StartAt = 6.5;
    private const double PauseAt = 90.0;
    private const double ResumeAt = 99.0;
    private const double UnloadAt = 125.0;
    private const double ReplayLoadAt = 127.0;
    private const double ReplayUnloadAt = 135.0;
    private const double EndAt = 140.0;

    /// <summary>The sim time of the engine at a runner time (Idle runs free, Load resets, Start runs, the pause freezes).</summary>
    private static double Sim(double r) => r < LoadAt ? r : r < StartAt ? 0 : r < PauseAt ? r - StartAt : r < ResumeAt ? PauseAt - StartAt : r - (ResumeAt - (PauseAt - StartAt));

    /// <summary>The time of the simulated world: it runs with the free play, stands still while loaded and paused, runs with the sim time.</summary>
    private static double World(double r) => r <= LoadAt ? r : LoadAt + Sim(r);

    // ---- the NED inputs of acts 3 - 5 that the debrief replays (act 11) ---------------------------------------------------

    private readonly List<object> _recording = new();

    private readonly List<ActBuilder> _acts = new();

    private ConvoyAmbushScript()
    {
    }

    /// <summary>The success conditions of DD-002 section 5, plus the checks this script adds (S-9.5: the R-047 hold).</summary>
    public static IReadOnlyList<SuccessCondition> Conditions { get; } = new SuccessCondition[]
    {
        new("S-0.1", "Validate() reports no errors and exactly the expected warnings (none)"),
        new("S-0.2", "the identity maps ned.entity, ned.member, ned.fire, rpr.handle, rpr.entityId, display.label exist with their key types"),
        new("S-1.1", "geo-spatial before master: Hawk exists, RPR and display get nothing"),
        new("S-1.2", "after the master: registration, handle one tick later, ONE full update for Hawk"),
        new("S-1.3", "RPR tank T-1 reaches NED as master + geo-spatial with a gateway-range id, never RPR (echo)"),
        new("S-1.4", "sim time advances with wall time in Idle"),
        new("S-2.1", "session Idle -> Loaded -> Running, sim time 0 at Load and frozen until Start"),
        new("S-2.2", "RPR gets exactly one StartResume, NED gets no control message"),
        new("S-2.3", "free-play entities survive Load"),
        new("S-3.1", "one formation message = one transaction creating unit + 3 members"),
        new("S-3.2", "RPR gets one platform object per member (coalesced), none for the unit"),
        new("S-3.3", "display gets Viper-0, Viper-1, Viper-2 and Gate"),
        new("S-3.4", "Wolf members first without parent; aggregate links them; NED gets ONE formation"),
        new("S-3.5", "member 4 arrives: NED gets one updated formation with 4 members"),
        new("S-4.1", "RPR gets Hawk updates only at the turn and as 5 s heartbeats at the analytic position"),
        new("S-4.2", "display gets Hawk and Viper members at 10 Hz at the analytic position, Gate once"),
        new("S-4.3", "Gate's unchanged re-send: OnAttributesWritten fires, no OnAttributesChanged, RPR only heartbeats"),
        new("S-4.4", "received Spatial.* change only at NED inputs, predictions only in Derived.*"),
        new("S-4.5", "time-window queries at t = 50 s (R-113: position changes = the updated set; ChangedWithin adds the derived predictions)"),
        new("S-5.1", "NED gets the fire with target (formation 7, member 1)"),
        new("S-5.2", "detonation: damage state before the event, RPR gets update + detonation"),
        new("S-5.3", "split fire F1 assembled: one RprWeaponFire, ammunition 28 visible"),
        new("S-5.4", "split fire F2 completed by timeout with the selected munition"),
        new("S-5.5", "damage 0 -> 2: descriptor + notification; 2 -> 1: descriptor only"),
        new("S-5.6", "no event is sent back to the side it came from"),
        new("S-6.1", "operator override: Gate damage as 33 percent"),
        new("S-6.2", "the other attributes still come from the default contributors"),
        new("S-6.3", "the default rule is frozen disabled, the custom rule enabled and counting"),
        new("S-7.1", "out-of-range damage aborts: repository unchanged, nothing sent, one failure diagnostic"),
        new("S-7.2", "unknown message type is counted, nothing else happens"),
        new("S-7.3", "throwing handler: transaction discarded, next message processed"),
        new("S-7.4", "rogue cascade stopped at the depth limit, other rules ran normally"),
        new("S-7.5", "loopback is ignored: echo suppression, nothing caused"),
        new("S-7.6", "id range exhaustion: 4 of 6 published, 2 warnings, the 2 published after ids free up"),
        new("S-8.1", "pause: StopFreeze, sim time frozen, no display positions"),
        new("S-8.2", "heartbeats continue during the pause at the frozen predicted position"),
        new("S-8.3", "resume: StartResume, display updates resume within one processor period"),
        new("S-9.1", "formation without member 1: object removed, marker removed, keys released (DIS id after the quarantine)"),
        new("S-9.2", "Wolf member 3 silent for 12 s: reaped, NED gets one formation without it"),
        new("S-9.3", "R-113: released DIS numbers stay unavailable for the quarantine (new entity gets another number), available afterwards"),
        new("S-9.4", "the 2 reinforcements without NED id are published after ids freed up"),
        new("S-9.5", "R-047: a fire by an entity whose handle is pending is held and sent with the handle"),
        new("S-10.1", "Unload: removal for everything published, on every side"),
        new("S-10.2", "after Unload: repository empty, maps empty, allocators whole, Idle"),
        new("S-10.3", "journal and event log still hold the history"),
        new("S-11.1", "debrief: Loaded in Replay mode, state reaches RPR and display"),
        new("S-11.2", "no weapon effects into RPR during replay; events still dispatched and logged"),
        new("S-11.3", "Unload leaves the gateway empty again"),
    };

    /// <summary>Builds the script (a new instance every time, so a run does not depend on an earlier one).</summary>
    public static ScenarioScript Create() => new ConvoyAmbushScript().Build();

    private ScenarioScript Build()
    {
        double[] starts = [0, 1, LoadAt, 7, 20, 60, 75, 80, PauseAt, 100, UnloadAt, ReplayLoadAt];
        string[] titles =
        [
            "Setup and validation", "Free play (Idle)", "Load and Start", "Forces appear", "Movement and timing", "Engagement",
            "Operator override", "Faults and loopback", "Pause and resume", "Losses and timeouts", "Unload", "Debrief replay",
        ];
        for (int i = 0; i < titles.Length; i++)
        {
            _acts.Add(new ActBuilder(i, titles[i], starts[i]));
        }

        Act0(_acts[0]);
        Act1(_acts[1]);
        Act2(_acts[2]);
        Act3(_acts[3]);
        Act4(_acts[4]);
        Act5(_acts[5]);
        Act6(_acts[6]);
        Act7(_acts[7]);
        Act8(_acts[8]);
        Act9(_acts[9]);
        Act10(_acts[10]);
        Act11(_acts[11]);
        KeepAlives();
        var acts = new List<Act>();
        foreach (var builder in _acts)
        {
            acts.Add(builder.Build());
        }

        return new ScenarioScript("Convoy Ambush", acts, starts, Conditions, EndAt);
    }

    /// <summary>The act builder whose time range contains the runner time.</summary>
    private ActBuilder ActAt(double time)
    {
        var chosen = _acts[0];
        foreach (var act in _acts)
        {
            if (act.Start <= time + 1e-9)
            {
                chosen = act;
            }
        }

        return chosen;
    }
}
