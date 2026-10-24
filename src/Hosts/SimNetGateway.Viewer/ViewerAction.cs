using SimNetGateway.Engine;

namespace SimNetGateway.Viewer;

/// <summary>
/// An operator action (DD-005 P-51, R-074). The panels never touch the engine: they enqueue one of these on the
/// <see cref="ViewerSession"/>, which applies them in order between ticks, never during a dispatch.
/// </summary>
public abstract record ViewerAction
{
    /// <summary>Viewer play: the gateway ticks at the chosen speed.</summary>
    public sealed record Play : ViewerAction;

    /// <summary>Viewer pause (a debugger pause: nothing ticks); distinct from the session's Pause command.</summary>
    public sealed record Pause : ViewerAction;

    /// <summary>Exactly one tick; only while the viewer is paused.</summary>
    public sealed record Step : ViewerAction;

    /// <summary>Playback speed, clamped to <see cref="ViewerSession.MinSpeed"/> .. <see cref="ViewerSession.MaxSpeed"/>.</summary>
    public sealed record SetSpeed(double Speed) : ViewerAction;

    /// <summary>Enables or disables a rule by its full name (R-046).</summary>
    public sealed record SetRuleEnabled(string FullName, bool Enabled) : ViewerAction;

    /// <summary>A session command through <see cref="GatewayEngine.Execute"/> with the source <c>operator</c>.</summary>
    public sealed record ExecuteSession(SessionCommand Command) : ViewerAction;

    /// <summary>Replaces the hosted gateway by a fresh one of this scenario (entities: size of the traffic generator).</summary>
    public sealed record SwitchScenario(ViewerScenario Scenario, int Entities = 0) : ViewerAction;

    /// <summary>Restarts the current scenario with a fresh engine.</summary>
    public sealed record Restart : ViewerAction;

    /// <summary>The tracer records the inputs that touch this entity.</summary>
    public sealed record WatchEntity(EntityId Entity) : ViewerAction;

    public sealed record UnwatchEntity(EntityId Entity) : ViewerAction;

    /// <summary>The tracer records every unit of work (costly with big populations).</summary>
    public sealed record WatchAll(bool On) : ViewerAction;

    /// <summary>The tracer records the unit of work with this correlation.</summary>
    public sealed record WatchCorrelation(CorrelationId Correlation) : ViewerAction;

    public sealed record ClearTraces : ViewerAction;
}

/// <summary>What the session did with one action, for the operator log.</summary>
public sealed record ActionOutcome(ViewerAction Action, bool Accepted, string Text, long Tick, long Frame);
