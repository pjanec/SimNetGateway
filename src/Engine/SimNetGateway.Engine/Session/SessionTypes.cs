namespace SimNetGateway.Engine;

public enum SessionState
{
    Idle,
    Loaded,
    Running,
    Paused,
}

public enum SessionMode
{
    Live,
    Replay,
}

public enum SessionCommandKind
{
    Load,
    Start,
    Pause,
    Resume,
    Unload,
}

/// <summary>A protocol-neutral session control command (DD-001 5.3).</summary>
public sealed record SessionCommand(SessionCommandKind Kind, string? Scenario = null, SessionMode Mode = SessionMode.Live)
{
    public static readonly SessionCommand Start = new(SessionCommandKind.Start);
    public static readonly SessionCommand Pause = new(SessionCommandKind.Pause);
    public static readonly SessionCommand Resume = new(SessionCommandKind.Resume);
    public static readonly SessionCommand Unload = new(SessionCommandKind.Unload);

    public static SessionCommand Load(string scenario, SessionMode mode = SessionMode.Live) =>
        new(SessionCommandKind.Load, scenario, mode);
}

/// <summary>Read access to the session state.</summary>
public interface ISessionView
{
    SessionState State { get; }
    SessionMode Mode { get; }
    string? Scenario { get; }
    double SimTime { get; }
}
