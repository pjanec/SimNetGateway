using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotSession: maps the cluster state and the clock to the engine session (the table of DD-007 section 6, R-099) and queues
/// the commands that lead there. Idle unloads; Edit is Loaded (silent to DIS, replicas shown); Preview and Live are Running
/// while time advances and Paused when it does not; Replay and Review are the Replay mode (Running / Paused likewise); Degraded
/// is Paused. A change of mode, or a return to Loaded from Running / Paused, goes through Unload (which removes every pivot
/// entity - HROT reports its entities again, DIS entities arrive again with the next PDU).
/// </summary>
public sealed class HrotSessionIngress : IGatewayModule
{
    /// <summary>The scenario name of the session loaded for the cluster.</summary>
    public const string ScenarioName = "hrot";

    private bool _warnedTimeScale;

    public string Name => nameof(HrotSessionIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotSession>(Handle);

    /// <summary>The session state and mode that a cluster phase and clock stand for (the table of DD-007 section 6).</summary>
    public static (SessionState State, SessionMode Mode) Target(ClusterPhase phase, bool advancing) => phase switch
    {
        ClusterPhase.Idle => (SessionState.Idle, SessionMode.Live),
        ClusterPhase.Edit => (SessionState.Loaded, SessionMode.Live),
        ClusterPhase.Preview or ClusterPhase.Live => (advancing ? SessionState.Running : SessionState.Paused, SessionMode.Live),
        ClusterPhase.Replay or ClusterPhase.Review => (advancing ? SessionState.Running : SessionState.Paused, SessionMode.Replay),
        ClusterPhase.Degraded => (SessionState.Paused, SessionMode.Live),
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown cluster phase."),
    };

    /// <summary>The commands that lead from the current session to the target (empty when it is already there).</summary>
    public static IReadOnlyList<SessionCommand> Plan(SessionState state, SessionMode mode, SessionState target, SessionMode targetMode)
    {
        var commands = new List<SessionCommand>();
        if (state == target && (mode == targetMode || target == SessionState.Idle))
        {
            return commands;
        }

        if (target == SessionState.Idle)
        {
            commands.Add(SessionCommand.Unload);
            return commands;
        }

        bool reload = state != SessionState.Idle &&
            (mode != targetMode || (target == SessionState.Loaded && state != SessionState.Loaded));
        if (reload)
        {
            commands.Add(SessionCommand.Unload);
            state = SessionState.Idle;
        }

        if (state == SessionState.Idle)
        {
            commands.Add(SessionCommand.Load(ScenarioName, targetMode));
            state = SessionState.Loaded;
        }

        if (target == SessionState.Loaded)
        {
            return commands;
        }

        if (state == SessionState.Loaded)
        {
            commands.Add(SessionCommand.Start);
            state = SessionState.Running;
        }

        if (target == SessionState.Running && state == SessionState.Paused)
        {
            commands.Add(SessionCommand.Resume);
        }
        else if (target == SessionState.Paused && state == SessionState.Running)
        {
            commands.Add(SessionCommand.Pause);
        }

        return commands;
    }

    private void Handle(in HrotSession m, IngressContext ctx)
    {
        if (!Enum.IsDefined(m.Phase))
        {
            ctx.Abort($"unknown cluster phase {(int)m.Phase}");
            return;
        }

        var (state, mode) = Target(m.Phase, m.Advancing);
        foreach (var command in Plan(ctx.Session.State, ctx.Session.Mode, state, mode))
        {
            ctx.Command(command);
        }

        if (state == SessionState.Running && m.TimeScale != 1.0 && !_warnedTimeScale)
        {
            _warnedTimeScale = true;
            ctx.Diagnostics.Warn(Name, $"HROT time scale is {m.TimeScale}; DIS has no time scale (the gateway runs in real time).");
        }
    }
}
