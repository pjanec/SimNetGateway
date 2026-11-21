using System.Numerics;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>How a render job ended (DD-022 3e): every frame written, a failure, or stopped by the user.</summary>
public enum RenderEnd : byte { Ok = 0, Failed = 1, Stopped = 2 }

/// <summary>What a film frame is (DD-022 3e, P-7): a Scene to render, the Repeat of the previous scene (slow motion), or Silence under a title card.</summary>
public enum RenderFrameKind : byte { Scene = 0, Repeat = 1, Silence = 2 }

/// <summary>Where a subtitle stands on the frame.</summary>
public enum RenderSubtitlePosition : byte { Bottom = 0, Top = 1, Middle = 2 }

/// <summary>What a film graphic draws.</summary>
public enum RenderGraphicKind : byte { Ring = 0, Arrow = 1, Route = 2, Area = 3, Label = 4 }

/// <summary>
/// One render job as the host states it, with no product type in it: its id, what it films, the film's rate and size, the folder its frames go to (the host's own path; the gate makes it
/// relative to the render root) and whether the client builds the soundtrack.
/// </summary>
public sealed record RenderJobSpec(Guid JobId, bool Reel, string What, int Fps, int Width, int Height, int Supersample, string Dir, bool ClientAudio);

/// <summary>The subtitle burned over a scene frame.</summary>
public sealed record RenderSubtitle(string Text, RenderSubtitlePosition Position, float Alpha);

/// <summary>One film graphic of a frame, already resolved to world points (metres, x east, y north, z up); <paramref name="ColourRgb"/> is 0xRRGGBB, <paramref name="Alpha"/> 0..1.</summary>
public sealed record RenderGraphic(RenderGraphicKind Kind, IReadOnlyList<Vector3> Points, float Radius, string? Text, uint ColourRgb, float Alpha);

/// <summary>
/// One film frame (DD-022 3e). The camera is a position plus a unit forward and a unit up (the sim frame: x east, y north, z up); the gate turns it into the wire's quaternion.
/// <paramref name="Step"/> is the host director's own count of the steps it issued in the reel item so far (NOT a clock frame id): the gate pairs the frame with the clock steps by it.
/// </summary>
public sealed record RenderFrameSpec(Guid JobId, int Index, RenderFrameKind Kind, double SimTime, double FilmRate, long Step, Vector3 Position, Vector3 Forward, Vector3 Up, float FovDeg,
    int Width, int Height, int Supersample, string Path, RenderSubtitle? Subtitle, IReadOnlyList<RenderGraphic> Graphics, float Fade, IReadOnlyList<string> LayersOn, IReadOnlyList<string> LayersOff);

/// <summary>The client's soundtrack of a job: the file as a full path under the render root, its samples per channel, the sample rate and the channel count.</summary>
public sealed record RenderTrackSpec(string Path, long Samples, int SampleRate, int Channels);

/// <summary>
/// The film half of the render client behind the gateway (DD-022 3e, 3f; HROT's linked-client film members forward to it): whether a client can film, and a job's begin, frames and end.
/// The members are safe to call from any thread: they queue the work and return a task; the work is done, and every task completes, on the gateway's pump (the host's main thread),
/// with continuations run asynchronously. A failure is a faulted task whose message is the reason. A host reaches it through the plugin's host service
/// (<c>HostServices.Of("dps") as IRenderGate</c>).
/// </summary>
public interface IRenderGate
{
    /// <summary>True when a render client is announced (its sample alive, its liveliness held) and ready; the job's limits are checked at <see cref="BeginJob"/>.</summary>
    bool CanFilm { get; }

    /// <summary>Starts a job: faults at once when it does not fit the client (the limit is named), else completes when the client is warmed up for it.</summary>
    Task BeginJob(RenderJobSpec job);

    /// <summary>One frame of the job, in <see cref="RenderFrameSpec.Index"/> order: completes when the client wrote it, faults with the client's reason.</summary>
    Task Frame(RenderFrameSpec frame);

    /// <summary>The job ended: the client is told, and its soundtrack (when the job asked for one and ended Ok) is the result; null = none.</summary>
    Task<RenderTrackSpec?> EndJob(RenderJobSpec job, RenderEnd end, string? reason);
}
