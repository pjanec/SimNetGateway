using System.Collections.Concurrent;
using System.Numerics;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The film half of the render client behind the gateway (DD-022 3e, 3f; DD-023 D-12), as the host service keeps it for the life of the host: which render client is announced (its
/// <c>RenderClient</c> sample and liveliness), the one job in flight, and the <see cref="DpsBarrier"/> that holds the counted world writes during it. The three <see cref="IRenderGate"/> calls
/// are safe from any thread: they queue the work, which is done (and every task completed, with continuations run asynchronously) on the gateway's pump. Time is the injected wall clock,
/// never simulation time. The DDS side is a <see cref="DpsRenderEndpoint"/> that attaches for each gateway runtime.
/// <para>
/// <b>Frames and steps.</b> A frame is released together with the clock steps the director issued before it: the director's <c>Step</c> counts steps in the reel item (it is not a clock frame
/// id), so the number of steps a frame owns is the difference of the director's counts between consecutive frames (none for the first frame of a job and after a count that goes backwards,
/// a new reel item). Those steps' held world writes are flushed, then their <c>ClockStep</c>s written (the last with <c>AckRequired</c> and the barrier's <c>Samples</c>), then the
/// <c>FrameRequest</c> naming the last one. A step that arrives while a frame is being rendered waits for the next frame, so nothing counted is written between a ClockStep and its FrameDone.
/// </para>
/// </summary>
internal sealed class DpsRenderService : IRenderGate
{
    /// <summary>Seconds from <c>RenderJob</c> Starting to <c>ReadyJobId</c>: the first frame after a rebuild hitched 28 s (DD-022 3e).</summary>
    public const double WarmUpSeconds = 180;

    /// <summary>Seconds a frame may wait for its <c>FrameDone</c> (and for the soundtrack's result at the end).</summary>
    public const double FrameSeconds = 30;

    /// <summary>The soundtrack's default format (DD-022 D-F2): 48 000 Hz, stereo.</summary>
    public const int DefaultSampleRate = 48000, DefaultChannels = 2;

    private sealed class ClientEntry
    {
        public ulong ClientId;
        public string Name = string.Empty;
        public bool Ready;
        public int MaxWidth, MaxHeight;
        public byte MaxSupersample;
        public uint Formats, AudioFormats;
        public int MaxSampleRate;
        public string ReadyJobId = string.Empty;
    }

    private sealed class PendingFrame
    {
        public PendingFrame(RenderFrameSpec spec, int need, TaskCompletionSource tcs, double deadline)
        {
            Spec = spec;
            Need = need;
            Tcs = tcs;
            Deadline = deadline;
        }

        public RenderFrameSpec Spec { get; }

        public int Need { get; }

        public TaskCompletionSource Tcs { get; }

        public double Deadline { get; set; }
    }

    private sealed class Job
    {
        public required RenderJobSpec Spec { get; init; }

        public required string Id { get; init; }

        public required ulong ClientId { get; init; }

        public required D.RenderJob Wire { get; set; }

        public required TaskCompletionSource BeginTcs { get; init; }

        public double BeginDeadline { get; init; }

        public bool Ready { get; set; }

        public bool Lost { get; set; }

        public string? FaultReason { get; set; }

        public Queue<PendingFrame> Frames { get; } = new();

        public PendingFrame? Outstanding { get; set; }

        public int NextIndex { get; set; }

        public long PrevStep { get; set; } = -1;

        public TaskCompletionSource<RenderTrackSpec?>? EndTcs { get; set; }

        public double EndDeadline { get; set; }

        public D.RenderJobResult? Result { get; set; }
    }

    private readonly IClock _clock;
    private readonly DpsSettings _settings;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly List<ClientEntry> _clients = new();
    private readonly Dictionary<long, ulong> _byWriter = new();
    private DpsRenderEndpoint? _endpoint;
    private Job? _job;
    private volatile bool _canFilm;
    private bool _rootWarned;

    public DpsRenderService(IClock clock, DpsSettings settings, Action<string>? log = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? (_ => { });
    }

    /// <summary>The barrier that holds the counted writes in a job.</summary>
    public DpsBarrier Barrier { get; } = new();

    /// <inheritdoc />
    public bool CanFilm => _canFilm;

    /// <summary>True from a job's Starting to its end (tests).</summary>
    internal bool JobActive => Barrier.Active;

    // ---- the gate: thread-safe, queued ------------------------------------------------------------------------------------------------

    public Task BeginJob(RenderJobSpec job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var tcs = NewTcs();
        _commands.Enqueue(() => DoBegin(job, tcs));
        return tcs.Task;
    }

    public Task Frame(RenderFrameSpec frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var tcs = NewTcs();
        _commands.Enqueue(() => DoFrame(frame, tcs));
        return tcs.Task;
    }

    public Task<RenderTrackSpec?> EndJob(RenderJobSpec job, RenderEnd end, string? reason)
    {
        ArgumentNullException.ThrowIfNull(job);
        var tcs = new TaskCompletionSource<RenderTrackSpec?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(() => DoEnd(job, end, reason, tcs));
        return tcs.Task;
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- the endpoint of the live runtime ---------------------------------------------------------------------------------------------

    internal void Attach(DpsRenderEndpoint endpoint)
    {
        _endpoint = endpoint;
        if (_job is { } job)
        {
            // a world boundary rebuilt the runtime in the middle of a job: the new writers say the job's state again and the frames that waited go out
            endpoint.WriteJob(job.Wire);
            Advance();
        }
    }

    internal void Detach(DpsRenderEndpoint endpoint)
    {
        if (ReferenceEquals(_endpoint, endpoint))
        {
            _endpoint = null;
        }
    }

    // ---- what the client says ---------------------------------------------------------------------------------------------------------

    internal void OnClient(in D.RenderClient sample, long writer)
    {
        if (sample.ClientId == 0)
        {
            return;
        }

        ulong id = sample.ClientId;
        var entry = _clients.Find(c => c.ClientId == id);
        if (entry is null)
        {
            entry = new ClientEntry { ClientId = id };
            _clients.Add(entry);
        }

        entry.Name = sample.Name ?? string.Empty;
        entry.Ready = sample.Ready;
        entry.MaxWidth = sample.MaxWidth;
        entry.MaxHeight = sample.MaxHeight;
        entry.MaxSupersample = sample.MaxSupersample;
        entry.Formats = sample.Formats;
        entry.AudioFormats = sample.AudioFormats;
        entry.MaxSampleRate = sample.MaxSampleRate;
        entry.ReadyJobId = sample.ReadyJobId ?? string.Empty;
        _byWriter[writer] = sample.ClientId;
        Refresh();
        CheckReady();
    }

    internal void OnClientLost(long writer)
    {
        if (_byWriter.Remove(writer, out ulong client) && !_byWriter.ContainsValue(client))
        {
            OnClientGone(client);
        }
    }

    internal void OnClientGone(ulong clientId)
    {
        _clients.RemoveAll(c => c.ClientId == clientId);
        foreach (var key in _byWriter.Where(kv => kv.Value == clientId).Select(kv => kv.Key).ToList())
        {
            _byWriter.Remove(key);
        }

        Refresh();
        if (_job is { } job && job.ClientId == clientId && !job.Lost)
        {
            job.Lost = true;
            Fault(job, "the render client was lost (its liveliness ended)");
        }
    }

    internal void OnFrameDone(in D.FrameDone done)
    {
        if (_job is not { Outstanding: { } pending } job || job.ClientId != done.ClientId || done.JobId != job.Id || done.FrameIndex != pending.Spec.Index)
        {
            return;   // not the job's, or not the frame being rendered
        }

        job.Outstanding = null;
        if (done.Status == D.FrameStatus.FrameStatusWritten)
        {
            pending.Tcs.TrySetResult();
        }
        else
        {
            string reason = string.IsNullOrEmpty(done.Reason) ? $"the render client failed frame {done.FrameIndex}" : done.Reason;
            pending.Tcs.TrySetException(new InvalidOperationException(reason));
            job.FaultReason ??= reason;
        }

        Advance();
    }

    internal void OnJobResult(in D.RenderJobResult result)
    {
        if (_job is not { } job || job.ClientId != result.ClientId || result.JobId != job.Id)
        {
            return;
        }

        if (job.EndTcs is { } tcs)
        {
            job.Result = result;
            Finish(job, tcs, result);
            return;
        }

        if (result.Status == D.FrameStatus.FrameStatusFailed)
        {
            // the client gives up the job before it ended (a failed warm-up, a track it cannot build): whatever is pending fails with its reason
            string reason = string.IsNullOrEmpty(result.Reason) ? "the render client failed the job" : result.Reason;
            Fault(job, reason);
        }
        else
        {
            job.Result = result;
        }
    }

    // ---- the pump ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Once per frame, on the gateway's main thread: queued calls are done, deadlines checked, waiting frames sent.</summary>
    public void Pump()
    {
        while (_commands.TryDequeue(out var command))
        {
            try
            {
                command();
            }
            catch (Exception ex)
            {
                _log($"dps: render command failed: {ex.Message}");
            }
        }

        Refresh();
        if (_job is { } job)
        {
            CheckReady();
            double now = _clock.Now;
            if (!job.Ready && job.FaultReason is null && now > job.BeginDeadline)
            {
                Fault(job, $"the render client did not report ready for the job within {WarmUpSeconds:0} s");
            }

            if (job.Outstanding is { } outstanding && now > outstanding.Deadline)
            {
                job.Outstanding = null;
                Fault(job, $"no FrameDone for frame {outstanding.Spec.Index} within {FrameSeconds:0} s", outstanding);
            }

            if (job.Frames.Count > 0 && job.Frames.Peek() is var head && now > head.Deadline && job.Outstanding is null)
            {
                Fault(job, $"frame {head.Spec.Index} waited more than {FrameSeconds:0} s for the clock step(s) it owns ({Barrier.QueuedSteps} of {head.Need} arrived)");
            }

            if (job.EndTcs is { } tcs && now > job.EndDeadline)
            {
                var reason = $"no RenderJobResult within {FrameSeconds:0} s of the job's end";
                tcs.TrySetException(new InvalidOperationException(reason));
                job.EndTcs = null;
                _job = null;
            }

            Advance();
        }
    }

    /// <summary>The module queued a step during a job (called by the step gate, in the engine's tick): a frame waiting for it may go out.</summary>
    internal void OnStepQueued(in D.ClockStep step)
    {
        Barrier.QueueStep(step);
        Advance();
    }

    // ---- begin ------------------------------------------------------------------------------------------------------------------------

    private void DoBegin(RenderJobSpec spec, TaskCompletionSource tcs)
    {
        if (_job is { } running)
        {
            tcs.TrySetException(new InvalidOperationException($"a render job ({running.Id}) is already running"));
            return;
        }

        var client = Pick();
        if (client is null)
        {
            tcs.TrySetException(new InvalidOperationException("no render client is announced"));
            return;
        }

        string? misfit = Misfit(spec, client, out string dir, out int sampleRate);
        if (misfit is not null)
        {
            tcs.TrySetException(new InvalidOperationException(misfit));
            return;
        }

        string id = spec.JobId.ToString("N");
        var wire = new D.RenderJob
        {
            JobId = id,
            ClientId = client.ClientId,
            Kind = spec.Reel ? D.RenderKind.RenderKindMovie : D.RenderKind.RenderKindStills,
            What = Cut(spec.What, 128),
            Fps = spec.Fps,
            Width = spec.Width,
            Height = spec.Height,
            Supersample = (byte)Math.Clamp(spec.Supersample, 1, 255),
            Dir = dir,
            Audio = spec.ClientAudio,
            SampleRate = spec.ClientAudio ? sampleRate : 0,
            Channels = spec.ClientAudio ? (byte)DefaultChannels : (byte)0,
            State = D.RenderState.RenderStateStarting,
            Reason = string.Empty,
        };
        var job = new Job { Spec = spec, Id = id, ClientId = client.ClientId, Wire = wire, BeginTcs = tcs, BeginDeadline = _clock.Now + WarmUpSeconds };
        _job = job;
        Barrier.Begin();
        _endpoint?.WriteJob(wire);
        CheckReady();
    }

    /// <summary>The reason a job does not fit the client or the configuration (the limit is named), or null.</summary>
    private string? Misfit(RenderJobSpec spec, ClientEntry client, out string dir, out int sampleRate)
    {
        dir = string.Empty;
        sampleRate = DefaultSampleRate;
        if (!client.Ready)
        {
            return $"the render client '{client.Name}' is not ready";
        }

        if (string.IsNullOrWhiteSpace(_settings.RenderRoot))
        {
            return "dps.renderRoot is not configured: the render client and the gateway need a folder both reach";
        }

        if (client.MaxWidth > 0 && spec.Width > client.MaxWidth)
        {
            return $"the job is {spec.Width} px wide, the render client renders at most {client.MaxWidth}";
        }

        if (client.MaxHeight > 0 && spec.Height > client.MaxHeight)
        {
            return $"the job is {spec.Height} px high, the render client renders at most {client.MaxHeight}";
        }

        if (client.MaxSupersample > 0 && spec.Supersample > client.MaxSupersample)
        {
            return $"the job supersamples {spec.Supersample}x, the render client at most {client.MaxSupersample}x";
        }

        if ((client.Formats & 1u) == 0)
        {
            return "the render client writes no PNG (Formats has no PNG bit)";
        }

        if (spec.ClientAudio)
        {
            if (client.AudioFormats == 0)
            {
                return "the job asks for the client's soundtrack, the render client builds none (AudioFormats is 0)";
            }

            if (client.MaxSampleRate > 0)
            {
                sampleRate = Math.Min(DefaultSampleRate, client.MaxSampleRate);
            }
        }

        if (!TryRelative(spec.Dir, out dir, out string why))
        {
            return $"the job's folder {why}";
        }

        if (dir.Length > 256)
        {
            return $"the job's folder relative to the render root is {dir.Length} characters, the wire holds 256";
        }

        return null;
    }

    private void CheckReady()
    {
        if (_job is not { Ready: false, FaultReason: null } job)
        {
            return;
        }

        var client = _clients.Find(c => c.ClientId == job.ClientId);
        if (client is null || client.ReadyJobId != job.Id)
        {
            return;
        }

        job.Ready = true;
        job.Wire = job.Wire with { State = D.RenderState.RenderStateRunning };
        _endpoint?.WriteJob(job.Wire);
        job.BeginTcs.TrySetResult();
        Advance();
    }

    // ---- frames -----------------------------------------------------------------------------------------------------------------------

    private void DoFrame(RenderFrameSpec spec, TaskCompletionSource tcs)
    {
        if (_job is not { } job || job.Spec.JobId != spec.JobId || job.EndTcs is not null)
        {
            tcs.TrySetException(new InvalidOperationException("no render job is running for this frame"));
            return;
        }

        if (job.FaultReason is not null || job.Lost)
        {
            tcs.TrySetException(new InvalidOperationException(job.FaultReason ?? "the render client was lost"));
            return;
        }

        if (spec.Index != job.NextIndex)
        {
            string reason = $"frame {spec.Index} came out of order (expected {job.NextIndex})";
            tcs.TrySetException(new InvalidOperationException(reason));
            job.FaultReason ??= reason;
            return;
        }

        job.NextIndex++;
        int need = job.PrevStep >= 0 && spec.Step >= job.PrevStep ? (int)Math.Min(spec.Step - job.PrevStep, int.MaxValue) : 0;
        job.PrevStep = spec.Step;
        job.Frames.Enqueue(new PendingFrame(spec, need, tcs, _clock.Now + FrameSeconds));
        Advance();
    }

    /// <summary>Sends the next frame when the job is warm, none is being rendered and the steps it owns have arrived.</summary>
    private void Advance()
    {
        while (_job is { Ready: true, Outstanding: null, FaultReason: null, Lost: false } job && job.Frames.Count > 0 && _endpoint is not null)
        {
            var next = job.Frames.Peek();
            if (Barrier.QueuedSteps < next.Need || (next.Need > 0 && !Barrier.HasSink))
            {
                return;   // the director has not seen the clock steps of this frame yet
            }

            if (!TryBuild(job, next.Spec, next.Need > 0 ? Barrier.Release(next.Need) : Barrier.LastReleasedStepSeq, out var wire, out string? why))
            {
                job.Frames.Dequeue();
                next.Tcs.TrySetException(new InvalidOperationException(why!));
                Fault(job, why!);
                return;
            }

            job.Frames.Dequeue();
            job.Outstanding = next;
            next.Deadline = _clock.Now + FrameSeconds;
            _endpoint.WriteFrame(wire);
        }
    }

    /// <summary>The wire frame of a spec: bounds applied, the camera as a quaternion, the file relative to the render root.</summary>
    private bool TryBuild(Job job, RenderFrameSpec spec, long stepSeq, out D.FrameRequest wire, out string? why)
    {
        why = null;
        string file = string.Empty;
        if (!string.IsNullOrEmpty(spec.Path) && !TryRelative(spec.Path, out file, out string reason))
        {
            wire = default;
            why = $"frame {spec.Index}'s file {reason}";
            return false;
        }

        if (file.Length > 256)
        {
            wire = default;
            why = $"frame {spec.Index}'s file relative to the render root is {file.Length} characters, the wire holds 256";
            return false;
        }

        var q = OrientationOf(spec.Forward, spec.Up);
        var graphics = new List<D.FilmGraphic>(Math.Min(spec.Graphics.Count, 64));
        for (int i = 0; i < spec.Graphics.Count && i < 64; i++)
        {
            var g = spec.Graphics[i];
            var points = new List<D.Vec3d>(Math.Min(g.Points.Count, 64));
            for (int p = 0; p < g.Points.Count && p < 64; p++)
            {
                points.Add(new D.Vec3d { X = g.Points[p].X, Y = g.Points[p].Y, Z = g.Points[p].Z });
            }

            graphics.Add(new D.FilmGraphic
            {
                Kind = (D.FilmGraphicKind)((int)g.Kind + 1),
                Points = points,
                Radius = g.Radius,
                Text = Cut(g.Text, 64),
                ColourRgb = g.ColourRgb & 0xFFFFFFu,
                Alpha = g.Alpha,
            });
        }

        wire = new D.FrameRequest
        {
            JobId = job.Id,
            FrameIndex = spec.Index,
            Kind = spec.Kind switch
            {
                RenderFrameKind.Repeat => D.FrameKind.FrameKindRepeat,
                RenderFrameKind.Silence => D.FrameKind.FrameKindSilence,
                _ => D.FrameKind.FrameKindScene,
            },
            StepSeq = stepSeq,
            SimTime = spec.SimTime,
            FilmRate = (float)spec.FilmRate,
            Position = new D.Vec3d { X = spec.Position.X, Y = spec.Position.Y, Z = spec.Position.Z },
            Orientation = new D.Quat { X = q.X, Y = q.Y, Z = q.Z, W = q.W },
            FovDeg = spec.FovDeg,
            Width = spec.Width > 0 ? spec.Width : job.Spec.Width,
            Height = spec.Height > 0 ? spec.Height : job.Spec.Height,
            Supersample = (byte)Math.Clamp(spec.Supersample > 0 ? spec.Supersample : job.Spec.Supersample, 1, 255),
            File = file,
            Subtitle = Cut(spec.Subtitle?.Text, 512),
            SubtitlePosition = spec.Subtitle is null ? D.SubtitlePosition.SubtitlePositionUnknown : (D.SubtitlePosition)((int)spec.Subtitle.Position + 1),
            SubtitleAlpha = spec.Subtitle?.Alpha ?? 0f,
            Graphics = graphics,
            LayersOn = Names(spec.LayersOn),
            LayersOff = Names(spec.LayersOff),
            Fade = Math.Clamp(spec.Fade, 0f, 1f),
        };
        return true;
    }

    private static List<D.LayerName> Names(IReadOnlyList<string> names)
    {
        var list = new List<D.LayerName>(Math.Min(names.Count, 32));
        for (int i = 0; i < names.Count && i < 32; i++)
        {
            list.Add(new D.LayerName { Name = Cut(names[i], 64) });
        }

        return list;
    }

    /// <summary>
    /// The camera as the wire's quaternion: the rotation that takes the body's forward axis (+x) to <paramref name="forward"/> and its up axis (+z) to <paramref name="up"/> (x east, y north,
    /// z up; the convention of <c>EntityState.Orientation</c>). Up is made orthogonal to forward; a degenerate pair falls back to the world's up.
    /// </summary>
    internal static Quaternion OrientationOf(Vector3 forward, Vector3 up)
    {
        var f = forward.LengthSquared() > 1e-12f ? Vector3.Normalize(forward) : Vector3.UnitX;
        var u = up - Vector3.Dot(up, f) * f;
        if (u.LengthSquared() < 1e-10f)
        {
            u = Vector3.UnitZ - Vector3.Dot(Vector3.UnitZ, f) * f;
            if (u.LengthSquared() < 1e-10f)
            {
                u = Vector3.UnitY;   // looking straight up or down
            }
        }

        u = Vector3.Normalize(u);
        var left = Vector3.Cross(u, f);   // +y of the body: z x x
        // a row-vector rotation matrix: the rows are where the body's x, y and z axes go
        var m = new Matrix4x4(f.X, f.Y, f.Z, 0, left.X, left.Y, left.Z, 0, u.X, u.Y, u.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    // ---- end --------------------------------------------------------------------------------------------------------------------------

    private void DoEnd(RenderJobSpec spec, RenderEnd end, string? reason, TaskCompletionSource<RenderTrackSpec?> tcs)
    {
        if (_job is not { } job || job.Spec.JobId != spec.JobId)
        {
            tcs.TrySetResult(null);   // no job was started for it (a begin that was refused): nothing to end
            return;
        }

        var cut = new InvalidOperationException("the job ended");
        job.BeginTcs.TrySetException(cut);
        job.Outstanding?.Tcs.TrySetException(cut);
        job.Outstanding = null;
        while (job.Frames.Count > 0)
        {
            job.Frames.Dequeue().Tcs.TrySetException(cut);
        }

        job.Wire = job.Wire with
        {
            State = end switch
            {
                RenderEnd.Ok => D.RenderState.RenderStateDone,
                RenderEnd.Failed => D.RenderState.RenderStateFailed,
                _ => D.RenderState.RenderStateCancelled,
            },
            Reason = Cut(reason ?? job.FaultReason, 256),
        };
        _endpoint?.WriteJob(job.Wire);
        Barrier.End();

        if (end == RenderEnd.Ok && job.Spec.ClientAudio && job.FaultReason is null && !job.Lost)
        {
            job.EndTcs = tcs;
            job.EndDeadline = _clock.Now + FrameSeconds;
            if (job.Result is { } result)
            {
                Finish(job, tcs, result);
            }

            return;
        }

        _job = null;
        tcs.TrySetResult(null);
    }

    private void Finish(Job job, TaskCompletionSource<RenderTrackSpec?> tcs, in D.RenderJobResult result)
    {
        job.EndTcs = null;
        _job = null;
        if (result.Status == D.FrameStatus.FrameStatusFailed)
        {
            tcs.TrySetException(new InvalidOperationException(string.IsNullOrEmpty(result.Reason) ? "the render client failed the job's soundtrack" : result.Reason));
            return;
        }

        if (string.IsNullOrEmpty(result.TrackFile))
        {
            tcs.TrySetResult(null);
            return;
        }

        // the track is reported relative to the render root: the host gets the full path (the root is configured here), and a track outside the root is refused
        if (!TryRelative(result.TrackFile, out string relative, out string why))
        {
            tcs.TrySetException(new InvalidOperationException($"the soundtrack {why}"));
            return;
        }

        string full = Path.GetFullPath(Path.Combine(_settings.RenderRoot, relative));
        tcs.TrySetResult(new RenderTrackSpec(full, result.TrackSamples, job.Wire.SampleRate, job.Wire.Channels));
    }

    // ---- faults -----------------------------------------------------------------------------------------------------------------------

    private void Fault(Job job, string reason, PendingFrame? also = null)
    {
        job.FaultReason ??= reason;
        var error = new InvalidOperationException(reason);
        if (!job.Ready)
        {
            job.BeginTcs.TrySetException(error);
        }

        also?.Tcs.TrySetException(error);
        if (job.Outstanding is { } outstanding)
        {
            outstanding.Tcs.TrySetException(error);
            job.Outstanding = null;
        }

        while (job.Frames.Count > 0)
        {
            job.Frames.Dequeue().Tcs.TrySetException(error);
        }

        if (job.EndTcs is { } tcs)
        {
            tcs.TrySetException(error);
            job.EndTcs = null;
            _job = null;
        }
    }

    /// <summary>The host ends: whatever a task of the director waits for fails (nothing is left pending), and the barrier opens.</summary>
    internal void Shutdown()
    {
        while (_commands.TryDequeue(out _))
        {
        }

        if (_job is { } job)
        {
            Fault(job, "the gateway ended");
            Barrier.End();
            _job = null;
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------------

    private void Refresh() => _canFilm = Pick() is { Ready: true };

    private ClientEntry? Pick() => _clients.Count > 0 ? _clients[0] : null;

    /// <summary>A path made relative to <c>dps.renderRoot</c>, with forward slashes; false (with the reason) when it lies outside the root or the root is not set.</summary>
    internal bool TryRelative(string path, out string relative, out string why)
    {
        relative = string.Empty;
        why = string.Empty;
        string root = _settings.RenderRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            why = "cannot be made relative: dps.renderRoot is not configured";
            if (!_rootWarned)
            {
                _rootWarned = true;
                _log("dps: a film job needs dps.renderRoot (the folder the render client and the gateway both reach)");
            }

            return false;
        }

        string full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
        string rel = Path.GetRelativePath(Path.GetFullPath(root), full);
        if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            why = $"'{path}' lies outside the render root '{root}'";
            return false;
        }

        relative = rel == "." ? string.Empty : rel.Replace('\\', '/');
        return true;
    }

    private static string Cut(string? text, int max) => text is null ? string.Empty : text.Length > max ? text[..max] : text;
}

/// <summary>
/// The ClockStep port of the module: outside a render job the step is written at once; inside one it waits in the barrier until the frame that owns it releases it (DD-022 D-B4 rev 6).
/// </summary>
internal sealed class DpsStepGate : IOutboundChannel<DpsItem<D.ClockStep>>
{
    private readonly DpsChannel<D.ClockStep> _inner;
    private readonly DpsRenderService? _service;

    public DpsStepGate(DpsChannel<D.ClockStep> inner, DpsRenderService? service)
    {
        _inner = inner;
        _service = service;
    }

    public void Send(in DpsItem<D.ClockStep> item)
    {
        if (_service is { JobActive: true })
        {
            _service.OnStepQueued(item.Value);
            return;
        }

        _inner.Send(item);
    }
}
