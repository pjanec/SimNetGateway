using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>How a held topic is counted for <c>ClockStep.Samples</c> (DD-022 D-B4 rev 6).</summary>
internal enum DpsCountKind : byte
{
    /// <summary>Held in a job but not counted (another seat's topic: its client does not read it on the render seat).</summary>
    None,

    /// <summary>A keyless state topic: one if written.</summary>
    Keyless,

    /// <summary>A keyed state topic: one per instance written or disposed (a write and a dispose of one instance count once).</summary>
    Keyed,

    /// <summary>An event topic: one per sample, never more per gather than the topic's depth.</summary>
    Event,
}

/// <summary>How a channel takes part in the render barrier: the barrier it is held by, how it is counted, its history depth (events) and the key of an item (keyed topics).</summary>
internal sealed record DpsHold<T>(DpsBarrier Barrier, DpsCountKind Kind, int Depth = 0, Func<T, long>? Key = null)
    where T : struct;

/// <summary>A channel the barrier holds writes of during a render job.</summary>
internal interface IHeldChannel
{
    /// <summary>Writes, in order, every held entry of gathers up to and including <paramref name="upToSegment"/> and returns how many instances/events they count for (0 for an uncounted channel).</summary>
    uint Flush(long upToSegment);
}

/// <summary>
/// The barrier of a render job (DD-022 D-B4 rev 6, 3e): from <c>RenderJob</c> Starting to Done the gateway writes the counted world topics only in a step's gather, just before that step's
/// <c>ClockStep</c>, and tells the client how many instances and events that gather wrote (<c>Samples</c>). Outside a job nothing here is touched (<see cref="Active"/> false: a channel
/// writes at once, 0 bytes). Inside one, every channel holds its writes in <i>segments</i>: a clock step that arrives closes the segment of its gather; the barrier later releases the steps
/// the film frames need, one frame at a time, flushing exactly the segments up to the last released step. A step that arrives while a frame is being rendered waits for the next frame.
/// Lives as long as the host (a world boundary rebuilds the runtime and its channels; they register again). Main thread only.
/// </summary>
internal sealed class DpsBarrier
{
    private readonly List<IHeldChannel> _channels = new();
    private readonly Queue<(D.ClockStep Step, long Segment)> _steps = new();
    private Action<D.ClockStep>? _stepSink;

    /// <summary>True from the job's Starting to its end: counted writes are held.</summary>
    public bool Active { get; private set; }

    /// <summary>The segment writes are held in now: it grows with every step that is queued (the step closes the gather before it).</summary>
    public long OpenSegment { get; private set; }

    /// <summary>The steps waiting for a frame.</summary>
    public int QueuedSteps => _steps.Count;

    /// <summary>The ClockStep the last release ended with (0 = none yet).</summary>
    public long LastReleasedStepSeq { get; private set; }

    /// <summary>A channel registers when it is built (the runtime of the world) and goes away with it.</summary>
    public void Register(IHeldChannel channel) => _channels.Add(channel);

    public void Unregister(IHeldChannel channel) => _channels.Remove(channel);

    /// <summary>Where a released step is written: the live transport's ClockStep writer (the newest runtime's).</summary>
    public void SetStepSink(Action<D.ClockStep> sink) => _stepSink = sink;

    /// <summary>The transport that set <paramref name="sink"/> goes away; a newer runtime's sink is left alone.</summary>
    public void ClearStepSink(Action<D.ClockStep> sink)
    {
        if (ReferenceEquals(_stepSink, sink))
        {
            _stepSink = null;
        }
    }

    /// <summary>True when a released step can be written now.</summary>
    public bool HasSink => _stepSink is not null;

    /// <summary>The job starts: from now on counted writes are held.</summary>
    public void Begin() => Active = true;

    /// <summary>A step the module wrote during a job: it closes the open segment and waits for the frame that needs it.</summary>
    public void QueueStep(in D.ClockStep step)
    {
        _steps.Enqueue((step, OpenSegment));
        OpenSegment++;
    }

    /// <summary>
    /// Releases the <paramref name="count"/> oldest queued steps (at most what is queued): every held write of their gathers is flushed first, then the steps are written, the last with
    /// <c>AckRequired</c> and the samples of the flush. Returns the StepSeq of the last one released (0 = none released).
    /// </summary>
    public long Release(int count)
    {
        count = Math.Min(count, _steps.Count);
        if (count <= 0 || _stepSink is null)
        {
            return 0;
        }

        var released = new D.ClockStep[count];
        long upTo = 0;
        for (int i = 0; i < count; i++)
        {
            var (step, segment) = _steps.Dequeue();
            released[i] = step;
            upTo = segment;
        }

        uint samples = Flush(upTo);
        for (int i = 0; i < count; i++)
        {
            var step = released[i];
            step.AckRequired = true;
            step.Samples = i == count - 1 ? samples : 0;
            _stepSink(step);
        }

        LastReleasedStepSeq = released[count - 1].StepSeq;
        return LastReleasedStepSeq;
    }

    /// <summary>The job ended: every held write and every waiting step is written (nothing is counted any more), and the barrier opens.</summary>
    public void End()
    {
        Flush(long.MaxValue);
        while (_steps.Count > 0)
        {
            var (step, _) = _steps.Dequeue();
            _stepSink?.Invoke(step);   // AckRequired false, Samples 0, as in normal stepping
        }

        Active = false;
        LastReleasedStepSeq = 0;
    }

    private uint Flush(long upToSegment)
    {
        uint samples = 0;
        // a snapshot: a flush writes through a channel, which never registers or unregisters one
        for (int i = 0; i < _channels.Count; i++)
        {
            samples += _channels[i].Flush(upToSegment);
        }

        return samples;
    }
}
