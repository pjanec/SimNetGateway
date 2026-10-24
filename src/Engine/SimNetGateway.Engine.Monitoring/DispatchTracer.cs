namespace SimNetGateway.Engine.Monitoring;

/// <summary>How a trace began.</summary>
public enum TraceKind
{
    /// <summary>An ingress message.</summary>
    Input,

    /// <summary>A commit made from outside any dispatch (a host writing directly).</summary>
    Commit,

    /// <summary>An event published from outside any dispatch, or by a rule or processor (see <see cref="DispatchTrace.CausedBy"/>).</summary>
    Event,

    /// <summary>A session command executed from outside any dispatch.</summary>
    SessionCommand,

    /// <summary>A timer or tick-end rule firing.</summary>
    Timer,

    /// <summary>The commit of a processor that ran inside a dispatch (always has <see cref="DispatchTrace.CausedBy"/> unless it ran in a timer rule).</summary>
    Cascade,
}

/// <summary>How a trace ended.</summary>
public enum TraceStatus
{
    /// <summary>Recorded without a problem (an input that committed nothing counts: it changed no state).</summary>
    Completed,

    /// <summary>The transaction was rejected (validation or cascade overflow).</summary>
    Rejected,

    /// <summary>The handlers aborted the transaction or threw.</summary>
    Aborted,

    /// <summary>No handler for the message type.</summary>
    Unhandled,
}

/// <summary>The kinds of entry in <see cref="DispatchTrace.Steps"/>, in the order they happened.</summary>
public enum TraceStepKind
{
    /// <summary>A trigger was raised (see <see cref="TraceStep.Trigger"/>, entity or event type, depth).</summary>
    Trigger,

    /// <summary>A rule evaluated the trigger raised last (<see cref="TraceStep.Rule"/>, <see cref="TraceStep.Outcome"/>).</summary>
    Rule,

    /// <summary>A message the host sent while this was dispatched (<see cref="TraceStep.Text"/> = side and description).</summary>
    Outbound,

    /// <summary>A session command was applied (<see cref="TraceStep.Text"/>).</summary>
    SessionApplied,

    /// <summary>A session command was refused (<see cref="TraceStep.Text"/> = command and reason).</summary>
    SessionRejected,
}

/// <summary>One entry of a trace: plain values only.</summary>
public readonly record struct TraceStep(
    TraceStepKind Kind,
    DispatchTriggerKind Trigger,
    EntityId Entity,
    EventTypeId EventType,
    int Depth,
    string Rule,
    int RuleIndex,
    RuleOutcome Outcome,
    string Text);

/// <summary>A trigger with the outcomes of the rules that evaluated it (a grouped view of <see cref="DispatchTrace.Steps"/>).</summary>
public sealed record TraceTrigger(
    DispatchTriggerKind Kind, EntityId Entity, EventTypeId EventType, int Depth, IReadOnlyList<TraceRuleOutcome> Rules);

/// <summary>What one rule did with one trigger.</summary>
public readonly record struct TraceRuleOutcome(string Rule, int RuleIndex, RuleOutcome Outcome);

/// <summary>
/// The recorded course of one correlation (DD-005 section 4.3): the input, its commit, the triggers raised with the outcome of
/// every rule that evaluated them, the messages sent while it was dispatched. A cascade (the commit or event of a processor
/// that ran inside a dispatch) is a trace of its own whose <see cref="CausedBy"/> is the correlation of the work that caused it.
/// A trace holds plain values only and never changes after the tracer put it into its ring.
/// </summary>
public sealed class DispatchTrace
{
    private readonly List<TraceStep> _steps = new();

    internal DispatchTrace()
    {
    }

    /// <summary>Running number in recording order.</summary>
    public long Id { get; internal set; }

    public CorrelationId Correlation { get; internal set; }

    public CorrelationId CausedBy { get; internal set; }

    public TraceKind Kind { get; internal set; }

    public TraceStatus Status { get; internal set; }

    /// <summary>The reason of a rejection or abort, or the module that aborted.</summary>
    public string? Detail { get; internal set; }

    public SourceId Source { get; internal set; }

    public string SourceName { get; internal set; } = string.Empty;

    /// <summary>Name of the ingress message type or other kind of input.</summary>
    public string InputType { get; internal set; } = string.Empty;

    /// <summary>The transaction committed a change set.</summary>
    public bool Committed { get; internal set; }

    /// <summary>Entities of the committed change set.</summary>
    public int EntityCount { get; internal set; }

    public double WallTime { get; internal set; }

    public double SimTime { get; internal set; }

    /// <summary>Cascade depth of the first trigger (0 for an input).</summary>
    public int Depth { get; internal set; }

    /// <summary>Everything that happened, in order.</summary>
    public IReadOnlyList<TraceStep> Steps => _steps;

    internal List<TraceStep> StepList => _steps;

    /// <summary>The triggers with their rule outcomes (builds the grouping on each call).</summary>
    public List<TraceTrigger> Triggers()
    {
        var result = new List<TraceTrigger>();
        List<TraceRuleOutcome>? rules = null;
        TraceStep trigger = default;
        bool open = false;
        foreach (var step in _steps)
        {
            if (step.Kind == TraceStepKind.Trigger)
            {
                Flush();
                trigger = step;
                rules = new List<TraceRuleOutcome>();
                open = true;
            }
            else if (step.Kind == TraceStepKind.Rule && open)
            {
                rules!.Add(new TraceRuleOutcome(step.Rule, step.RuleIndex, step.Outcome));
            }
        }

        Flush();
        return result;

        void Flush()
        {
            if (open)
            {
                result.Add(new TraceTrigger(trigger.Trigger, trigger.Entity, trigger.EventType, trigger.Depth, rules!));
            }
        }
    }

    /// <summary>The messages the host reported as sent while this was dispatched.</summary>
    public List<string> Outbound()
    {
        var result = new List<string>();
        foreach (var step in _steps)
        {
            if (step.Kind == TraceStepKind.Outbound)
            {
                result.Add(step.Text);
            }
        }

        return result;
    }

    /// <summary>Whether a trigger of this trace concerned the entity.</summary>
    public bool Touches(EntityId entity)
    {
        foreach (var step in _steps)
        {
            if (step.Kind == TraceStepKind.Trigger && step.Entity == entity)
            {
                return true;
            }
        }

        return false;
    }

    internal void Reset()
    {
        Id = 0;
        Correlation = default;
        CausedBy = default;
        Kind = TraceKind.Input;
        Status = TraceStatus.Completed;
        Detail = null;
        Source = default;
        SourceName = string.Empty;
        InputType = string.Empty;
        Committed = false;
        EntityCount = 0;
        WallTime = 0;
        SimTime = 0;
        Depth = 0;
        _steps.Clear();
    }
}

/// <summary>
/// Records the course of watched entities and correlations (DD-005 section 4.3, R-078) from the engine's
/// <see cref="IDispatchObserver"/> hooks into a bounded ring of <see cref="DispatchTrace"/>.
/// <list type="bullet">
/// <item>A unit of work (an input and all its cascades) is recorded into scratch traces; when it ends it is kept - every
/// trace of it - if it is of interest, else its scratch is recycled. Interest: it touched a watched entity (a trigger
/// concerned it), one of its correlations is watched, <see cref="WatchAll"/> is on, or it failed (rejected, aborted,
/// unhandled) and <see cref="KeepFailures"/> is on. Timer units are kept for <see cref="WatchAll"/> only with <see cref="IncludeTimers"/>.</item>
/// <item>Watching nothing makes every hook return at once: no allocation and no bookkeeping. A watch that matches nothing
/// recycles scratch objects, so the steady state allocates nothing either.</item>
/// <item>Outbound messages are reported by the host (<see cref="OutboundSent"/>) with the engine's current correlation.</item>
/// </list>
/// Not thread-safe; used from the engine thread and between ticks.
/// </summary>
public sealed class DispatchTracer : IDispatchObserver
{
    private readonly HashSet<EntityId> _entities = new();
    private readonly HashSet<CorrelationId> _correlations = new();
    private readonly List<DispatchTrace> _ring = new();
    private readonly Stack<DispatchTrace> _pool = new();
    private readonly Dictionary<CorrelationId, DispatchTrace> _open = new();
    private readonly Dictionary<Type, string> _typeNames = new();
    private readonly List<DispatchTrace> _segment = new();
    private DispatchTrace? _current;
    private bool _segmentActive;
    private bool _interesting;
    private bool _failure;
    private bool _timerSegment;
    private bool _active;
    private bool _watchAll;
    private long _nextId = 1;
    private SourceRegistry? _sources;
    private EventTypeRegistry? _eventTypes;

    public DispatchTracer(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>Keep every unit of work (except timers, see <see cref="IncludeTimers"/>), watched or not.</summary>
    public bool WatchAll
    {
        get => _watchAll;
        set
        {
            _watchAll = value;
            UpdateActive();
        }
    }

    /// <summary>Whether <see cref="WatchAll"/> also keeps timer and tick-end units (default no: they fire every tick).</summary>
    public bool IncludeTimers { get; set; }

    /// <summary>
    /// Also keep units that were rejected, aborted or unhandled, whatever they touched, while the tracer is active (default no:
    /// they are kept for <see cref="WatchAll"/> and for a watched correlation).
    /// </summary>
    public bool KeepFailures { get; set; }

    /// <summary>The kept traces, oldest first.</summary>
    public IReadOnlyList<DispatchTrace> Traces => _ring;

    public IReadOnlyCollection<EntityId> WatchedEntities => _entities;

    public IReadOnlyCollection<CorrelationId> WatchedCorrelations => _correlations;

    /// <summary>Whether any watch (or <see cref="WatchAll"/>) is set; otherwise every hook returns at once.</summary>
    public bool IsActive => _active;

    /// <summary>Makes this the engine's observer and lets the tracer resolve source names.</summary>
    public void Attach(GatewayEngine engine)
    {
        UseNamesOf(engine);
        engine.DispatchObserver = this;
    }

    /// <summary>Lets the tracer resolve source and event type names of the engine (needed when it was passed through <see cref="GatewayOptions"/>).</summary>
    public void UseNamesOf(GatewayEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _sources = engine.Sources;
        _eventTypes = engine.EventTypes;
    }

    public void Watch(EntityId entity)
    {
        _entities.Add(entity);
        UpdateActive();
    }

    public void Unwatch(EntityId entity)
    {
        _entities.Remove(entity);
        UpdateActive();
    }

    public void WatchCorrelation(CorrelationId correlation)
    {
        _correlations.Add(correlation);
        UpdateActive();
    }

    public void UnwatchCorrelation(CorrelationId correlation)
    {
        _correlations.Remove(correlation);
        UpdateActive();
    }

    public void ClearWatches()
    {
        _entities.Clear();
        _correlations.Clear();
        _watchAll = false;
        UpdateActive();
    }

    public void Clear() => _ring.Clear();

    private void UpdateActive() => _active = _watchAll || _entities.Count > 0 || _correlations.Count > 0;

    // ---- queries ----------------------------------------------------------------------------------------------

    /// <summary>The most recent kept trace of the correlation, or null.</summary>
    public DispatchTrace? TraceOf(CorrelationId correlation)
    {
        for (int i = _ring.Count - 1; i >= 0; i--)
        {
            if (_ring[i].Correlation == correlation)
            {
                return _ring[i];
            }
        }

        return null;
    }

    /// <summary>The kept traces caused directly by the correlation, in recording order.</summary>
    public List<DispatchTrace> Cascades(CorrelationId parent)
    {
        var result = new List<DispatchTrace>();
        foreach (var trace in _ring)
        {
            if (trace.CausedBy == parent && !parent.IsNone)
            {
                result.Add(trace);
            }
        }

        return result;
    }

    /// <summary>The kept traces caused by the correlation, directly or through other cascades.</summary>
    public List<DispatchTrace> Descendants(CorrelationId root)
    {
        var result = new List<DispatchTrace>();
        var frontier = new List<CorrelationId> { root };
        for (int i = 0; i < frontier.Count; i++)
        {
            foreach (var trace in Cascades(frontier[i]))
            {
                if (!result.Contains(trace))
                {
                    result.Add(trace);
                    frontier.Add(trace.Correlation);
                }
            }
        }

        return result;
    }

    /// <summary>The kept traces with a trigger for the entity.</summary>
    public List<DispatchTrace> TracesOf(EntityId entity)
    {
        var result = new List<DispatchTrace>();
        foreach (var trace in _ring)
        {
            if (trace.Touches(entity))
            {
                result.Add(trace);
            }
        }

        return result;
    }

    // ---- host reports -----------------------------------------------------------------------------------------

    /// <summary>
    /// The host reports a message the gateway sent: <paramref name="correlation"/> is the engine's current correlation (the
    /// recorders stamp it). It is attached to the trace of that correlation, or to the unit being dispatched when none.
    /// </summary>
    public void OutboundSent(CorrelationId correlation, string side, string description)
    {
        if (!_active || !_segmentActive)
        {
            return;
        }

        var trace = Find(correlation);
        trace?.StepList.Add(new TraceStep(TraceStepKind.Outbound, default, default, default, 0, string.Empty, 0, default,
            side + ": " + description));
    }

    // ---- IDispatchObserver ------------------------------------------------------------------------------------

    void IDispatchObserver.InputStarted(CorrelationId correlation, SourceId source, Type messageType)
    {
        if (!_active)
        {
            return;
        }

        _segmentActive = true;
        _interesting = false;
        _failure = false;
        _timerSegment = messageType == typeof(TimerTrigger);
        var kind = messageType == typeof(SimEvent) ? TraceKind.Event
            : messageType == typeof(SessionCommand) ? TraceKind.SessionCommand
            : messageType == typeof(ChangeSet) ? TraceKind.Commit
            : _timerSegment ? TraceKind.Timer
            : TraceKind.Input;
        var trace = NewTrace(correlation, kind);
        trace.Source = source;
        trace.InputType = TypeName(messageType);
        _current = trace;
        if (_correlations.Contains(correlation))
        {
            _interesting = true;
        }
    }

    void IDispatchObserver.InputUnhandled(SourceId source, Type messageType)
    {
        if (!_active || !(_watchAll || KeepFailures))
        {
            return;
        }

        var trace = Take();
        trace.Id = _nextId++;
        trace.Kind = TraceKind.Input;
        trace.Status = TraceStatus.Unhandled;
        trace.Source = source;
        trace.SourceName = Names.Source(_sources, source);
        trace.InputType = TypeName(messageType);
        Keep(trace);
    }

    void IDispatchObserver.InputFinished(CorrelationId correlation)
    {
        if (!_segmentActive)
        {
            return;
        }

        bool keep = _interesting || _failure && KeepFailures || _watchAll && (!_timerSegment || IncludeTimers);
        foreach (var trace in _segment)
        {
            if (keep)
            {
                trace.SourceName = Names.Source(_sources, trace.Source);
                Keep(trace);
            }
            else
            {
                Recycle(trace);
            }
        }

        _segment.Clear();
        _open.Clear();
        _current = null;
        _segmentActive = false;
    }

    void IDispatchObserver.InputAborted(CorrelationId correlation, SourceId source, string module, string reason)
    {
        if (!_segmentActive)
        {
            return;
        }

        var trace = Find(correlation);
        if (trace is not null)
        {
            trace.Status = TraceStatus.Aborted;
            trace.Detail = module + ": " + reason;
        }

        _failure = true;
    }

    void IDispatchObserver.EventPublished(CorrelationId correlation, CorrelationId causedBy, SourceId source, EventTypeId type, int depth)
    {
        if (!_segmentActive)
        {
            return;
        }

        var trace = Find(correlation) ?? NewTrace(correlation, TraceKind.Event);
        trace.CausedBy = causedBy;
        trace.Source = source;
        trace.Depth = depth;
        trace.InputType = EventName(type);
        if (_correlations.Contains(correlation))
        {
            _interesting = true;
        }
    }

    void IDispatchObserver.Committed(in CommitSummary summary)
    {
        if (!_segmentActive)
        {
            return;
        }

        var trace = Find(summary.Correlation)
            ?? NewTrace(summary.Correlation, summary.CausedBy.IsNone ? TraceKind.Commit : TraceKind.Cascade);

        trace.CausedBy = summary.CausedBy;
        trace.Source = summary.Source;
        trace.Committed = true;
        trace.EntityCount = summary.EntityCount;
        trace.WallTime = summary.WallTime;
        trace.SimTime = summary.SimTime;
        if (_correlations.Contains(summary.Correlation))
        {
            _interesting = true;
        }
    }

    void IDispatchObserver.CommitRejected(CorrelationId correlation, SourceId source, string reason)
    {
        if (!_segmentActive)
        {
            return;
        }

        var trace = Find(correlation) ?? NewTrace(correlation, TraceKind.Commit);
        trace.Status = TraceStatus.Rejected;
        trace.Detail = reason;
        trace.Source = source;
        _failure = true;
    }

    void IDispatchObserver.TriggerRaised(DispatchTriggerKind kind, EntityId entity, EventTypeId eventType, CorrelationId correlation, int depth)
    {
        if (!_segmentActive)
        {
            return;
        }

        var trace = Find(correlation) ?? NewTrace(correlation, TraceKind.Cascade);
        if (trace.StepList.Count == 0)
        {
            trace.Depth = depth;
        }

        trace.StepList.Add(new TraceStep(TraceStepKind.Trigger, kind, entity, eventType, depth, string.Empty, 0, default, string.Empty));
        if (!entity.IsNone && _entities.Contains(entity))
        {
            _interesting = true;
        }
    }

    void IDispatchObserver.RuleEvaluated(RuleInfo rule, RuleOutcome outcome, CorrelationId correlation)
    {
        if (!_segmentActive)
        {
            return;
        }

        var trace = Find(correlation) ?? NewTrace(correlation, TraceKind.Cascade);
        trace.StepList.Add(new TraceStep(TraceStepKind.Rule, default, default, default, 0, rule.FullName, rule.RegistrationIndex,
            outcome, string.Empty));
    }

    void IDispatchObserver.SessionCommandApplied(SessionCommand command, SourceId source, CorrelationId correlation)
    {
        if (!_segmentActive)
        {
            return;
        }

        var trace = Find(correlation) ?? NewTrace(correlation, TraceKind.Cascade);
        trace.StepList.Add(new TraceStep(TraceStepKind.SessionApplied, default, default, default, 0, string.Empty, 0, default,
            Describe(command)));
    }

    void IDispatchObserver.SessionCommandRejected(SessionCommand command, SourceId source, CorrelationId correlation, string reason)
    {
        if (!_segmentActive)
        {
            return;
        }

        var trace = Find(correlation) ?? NewTrace(correlation, TraceKind.Cascade);
        trace.StepList.Add(new TraceStep(TraceStepKind.SessionRejected, default, default, default, 0, string.Empty, 0, default,
            Describe(command) + ": " + reason));
        _failure = true;
    }

    // ---- internals --------------------------------------------------------------------------------------------

    private static string Describe(SessionCommand command) =>
        command.Scenario is null ? command.Kind.ToString() : command.Kind + " " + command.Scenario;

    private string EventName(EventTypeId type)
    {
        if (_eventTypes is not null)
        {
            try
            {
                return _eventTypes.NameOf(type);
            }
            catch (KeyNotFoundException)
            {
            }
        }

        return "event#" + type.Value;
    }

    // Type.Name is not free in steady state: the runtime keeps a type's name cache behind a weak handle that a collection
    // clears, so reading the name again after a GC allocates (a few hundred bytes, once per type and GC). Names are kept here.
    private string TypeName(Type type)
    {
        if (!_typeNames.TryGetValue(type, out string? name))
        {
            name = type.Name;
            _typeNames[type] = name;
        }

        return name;
    }

    private DispatchTrace? Find(CorrelationId correlation)
    {
        if (correlation.IsNone)
        {
            return _current;
        }

        return _open.TryGetValue(correlation, out var trace) ? trace : null;
    }

    private DispatchTrace NewTrace(CorrelationId correlation, TraceKind kind)
    {
        var trace = Take();
        trace.Id = _nextId++;
        trace.Correlation = correlation;
        trace.Kind = kind;
        _segment.Add(trace);
        if (!correlation.IsNone)
        {
            _open[correlation] = trace;
        }

        if (_current is null)
        {
            _current = trace;
        }

        return trace;
    }

    private DispatchTrace Take() => _pool.Count > 0 ? _pool.Pop() : new DispatchTrace();

    private void Recycle(DispatchTrace trace)
    {
        trace.Reset();
        _pool.Push(trace);
    }

    private void Keep(DispatchTrace trace)
    {
        if (_ring.Count == Capacity)
        {
            _ring.RemoveAt(0); // evicted traces are dropped, not recycled: a reader may still hold them
        }

        _ring.Add(trace);
    }
}
