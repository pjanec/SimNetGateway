namespace SimNetGateway.Engine;

/// <summary>
/// The session state machine (DD-001 5.3) and the simulation clock. Sim time starts at 0, follows wall time
/// while Idle or Running, is frozen while Loaded or Paused and is reset to 0 by Load and Unload.
/// </summary>
public sealed class SessionController : ISessionView
{
    private double _lastWall;

    public SessionController(double startWallTime = 0) => _lastWall = startWallTime;

    public SessionState State { get; private set; } = SessionState.Idle;
    public SessionMode Mode { get; private set; } = SessionMode.Live;
    public string? Scenario { get; private set; }
    public double SimTime { get; private set; }

    /// <summary>Whether the command is a valid transition in the current state.</summary>
    public bool IsValid(SessionCommand command) => command.Kind switch
    {
        SessionCommandKind.Load => State == SessionState.Idle,
        SessionCommandKind.Start => State == SessionState.Loaded,
        SessionCommandKind.Pause => State == SessionState.Running,
        SessionCommandKind.Resume => State == SessionState.Paused,
        SessionCommandKind.Unload => State != SessionState.Idle,
        _ => false,
    };

    /// <summary>Applies the command; false (and no change) when the transition is not valid.</summary>
    public bool TryApply(SessionCommand command)
    {
        if (!IsValid(command))
        {
            return false;
        }

        switch (command.Kind)
        {
            case SessionCommandKind.Load:
                State = SessionState.Loaded;
                Mode = command.Mode;
                Scenario = command.Scenario;
                SimTime = 0;
                break;
            case SessionCommandKind.Start:
            case SessionCommandKind.Resume:
                State = SessionState.Running;
                break;
            case SessionCommandKind.Pause:
                State = SessionState.Paused;
                break;
            case SessionCommandKind.Unload:
                State = SessionState.Idle;
                Mode = SessionMode.Live;
                Scenario = null;
                SimTime = 0;
                break;
        }

        return true;
    }

    /// <summary>Moves sim time by the wall time elapsed since the previous call, if the state lets it run.</summary>
    internal void Advance(double wallNow)
    {
        double dt = wallNow - _lastWall;
        _lastWall = wallNow;
        if (dt > 0 && (State == SessionState.Idle || State == SessionState.Running))
        {
            SimTime += dt;
        }
    }
}
