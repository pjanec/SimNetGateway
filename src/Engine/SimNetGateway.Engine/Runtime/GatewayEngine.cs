namespace SimNetGateway.Engine;

/// <summary>
/// The engine: owns the registries, the state repository, the session controller, the rule indexes, the
/// ingress router and the work queue, and is driven by <see cref="Tick"/> (DD-001 6). It has no protocol or
/// network knowledge. Single-threaded.
/// </summary>
public sealed class GatewayEngine : IGatewayContext, ICommitObserver
{
    private const string CascadeOverflowReason = "cascade overflow";

    private readonly IClock _clock;
    private readonly GatewayOptions _options;
    private readonly SessionController _session;
    private readonly IngressRouter _router = new();
    private readonly IngressContext _ingress;
    private readonly List<IInboundChannel> _channels = new();
    private readonly EngineSink _sink;
    private readonly WorkQueue _queue = new();

    private readonly List<RuleInfo> _ruleInfos = new();
    private readonly Dictionary<string, RuleInfo> _rulesByName = new(StringComparer.Ordinal);
    private readonly List<ModuleProduction> _productions = new();
    private readonly List<ModuleConsumption> _consumptions = new();
    private readonly List<IEntityTableInternal> _tables = new();
    private readonly List<EntityId> _pendingRelease = new();   // destroyed in this drain, released at its end
    private readonly Dictionary<string, IEntityTableInternal> _tablesByName = new(StringComparer.Ordinal);

    // Rule indexes (arrays / lists built at registration; each list is in registration order).
    private readonly List<Rule<EntityTrigger>> _createdRules = new();
    private readonly List<Rule<EntityTrigger>> _removedRules = new();
    private readonly List<Rule<EntityTrigger>> _anyChangedRules = new();
    private readonly List<Rule<EntityTrigger>> _anyWrittenRules = new();
    private List<Rule<EntityTrigger>>?[] _changedByAttr = new List<Rule<EntityTrigger>>?[8];
    private List<Rule<EntityTrigger>>?[] _writtenByAttr = new List<Rule<EntityTrigger>>?[8];
    private List<Rule<EventTrigger>>?[] _eventByType = new List<Rule<EventTrigger>>?[8];
    private readonly List<Rule<EventTrigger>> _anyEventRules = new();
    private readonly List<Rule<SessionTrigger>> _sessionRules = new();
    private readonly List<Rule<SessionRequestTrigger>> _requestRules = new();
    private readonly List<Rule<ChangeSetTrigger>> _changeSetEndRules = new();
    private readonly List<TimerEntry> _timers = new();
    private readonly List<Rule<TimerTrigger>> _tickEndRules = new();

    // Reused trigger objects and scratch buffers.
    private readonly EntityTrigger _entityTrigger = new();
    private readonly EventTrigger _eventTrigger = new();
    private readonly SessionTrigger _sessionTrigger = new();
    private readonly SessionRequestTrigger _requestTrigger = new();
    private readonly ChangeSetTrigger _changeSetTrigger = new();
    private readonly TimerTrigger _timerTrigger = new();
    private Rule<EntityTrigger>[] _entityMatches = new Rule<EntityTrigger>[16];
    private Rule<EventTrigger>[] _eventMatches = new Rule<EventTrigger>[16];
    private long _stamp;

    private double _wall;
    private bool _inTick;
    private bool _busy;                  // a handler runs, a drain is in progress or a top-level rule runs
    private int _currentDepth = -1;      // depth of the item being dispatched; -1 outside any dispatch
    private CorrelationId _currentCorrelation;
    private IDispatchObserver? _observer;

    public GatewayEngine(IClock clock, GatewayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _options = options ?? new GatewayOptions();
        _observer = _options.DispatchObserver;
        _wall = clock.Now;
        Attributes = new AttributeRegistry();
        EventTypes = new EventTypeRegistry();
        Sources = new SourceRegistry();
        State = new StateRepository(Attributes, _options.WriteAuthority, _options.JournalCapacity, _options.EventLogCapacity);
        State.Observer = this;
        Identities = new IdentityRegistry(State);
        Diagnostics = new Diagnostics(_options.DiagnosticsCapacity, () => _wall);
        _session = new SessionController(_wall);
        SyncTime();
        _ingress = new IngressContext(this);
        _sink = new EngineSink(this);
    }

    public AttributeRegistry Attributes { get; }
    public EventTypeRegistry EventTypes { get; }
    public SourceRegistry Sources { get; }
    public StateRepository State { get; }
    public IdentityRegistry Identities { get; }
    public Diagnostics Diagnostics { get; }
    public ISessionView Session => _session;
    public GatewayOptions Options => _options;

    IStateView IGatewayContext.State => State;

    public double WallTime => _wall;
    public double SimTime => _session.SimTime;
    public long TickCount { get; private set; }

    /// <summary>
    /// The correlation of the item being dispatched (the record, event or command whose rules run now); none outside any
    /// dispatch, in ingress handlers and in timer rules (R-078). Outbound recorders stamp it on what they send.
    /// </summary>
    public CorrelationId CurrentCorrelation => _currentCorrelation;

    /// <summary>
    /// The optional dispatch observer (R-078); starts as <see cref="GatewayOptions.DispatchObserver"/> and may be changed
    /// between ticks (setting it while a handler runs or a drain is in progress throws).
    /// </summary>
    public IDispatchObserver? DispatchObserver
    {
        get => _observer;
        set
        {
            if (_busy)
            {
                throw new InvalidOperationException("The dispatch observer cannot be changed while a handler runs or a drain is in progress.");
            }

            _observer = value;
        }
    }

    /// <summary>The engine-managed entity tables of all modules, in creation order (R-077).</summary>
    public IReadOnlyList<IEntityTable> EntityTables => _tables;

    /// <summary>Every registered rule, in registration order.</summary>
    public IReadOnlyList<RuleInfo> Rules => _ruleInfos;

    public RuleInfo? FindRule(string fullName) => _rulesByName.TryGetValue(fullName, out var info) ? info : null;

    internal IngressRouter Router => _router;

    /// <summary>What modules declared with <c>Produces</c>, in declaration order (used by the validation of stage 3).</summary>
    public IReadOnlyList<ModuleProduction> Productions => _productions;

    /// <summary>What modules declared with <c>Produces</c> (for the vocabulary validation of stage 3).</summary>
    internal List<ModuleProduction> ProductionList => _productions;

    /// <summary>What modules declared with <c>Consumes</c> or subscribed to, in declaration order (vocabulary validation).</summary>
    internal List<ModuleConsumption> ConsumptionList => _consumptions;

    // ---- registration -----------------------------------------------------------------------------------

    public void AddModule(IGatewayModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.Register(new ModuleSetup(this, module.Name, _clock.Now));
    }

    public void AddInbound(IInboundChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channels.Add(channel);
    }

    internal EntityTable<T> CreateEntityTable<T>(string name)
        where T : struct
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Entity table name must not be empty.", nameof(name));
        }

        if (_tablesByName.ContainsKey(name))
        {
            throw new InvalidOperationException($"An entity table named '{name}' already exists.");
        }

        var table = new EntityTable<T>(name);
        _tables.Add(table);
        _tablesByName.Add(name, table);
        return table;
    }

    internal RuleInfo RegisterRule<TTrigger>(string module, RuleSubscription subscription, string name,
        Func<TTrigger, bool>[] conditions, Action<TTrigger> action, double startTime)
        where TTrigger : class
    {
        string fullName = module + "/" + name;
        if (_rulesByName.ContainsKey(fullName))
        {
            throw new InvalidOperationException($"A rule named '{fullName}' is already registered.");
        }

        var info = new RuleInfo(module, name, subscription.Description, _ruleInfos.Count);
        var rule = new Rule<TTrigger>(info, conditions, action);
        object boxed = rule; // a reference: only the static type is lost
        switch (subscription.Kind)
        {
            case RuleKind.EntityCreated:
                _createdRules.Add((Rule<EntityTrigger>)boxed);
                break;
            case RuleKind.EntityRemoved:
                _removedRules.Add((Rule<EntityTrigger>)boxed);
                break;
            case RuleKind.AttributesChanged:
                IndexByAttribute((Rule<EntityTrigger>)boxed, subscription.Attributes, ref _changedByAttr, _anyChangedRules);
                break;
            case RuleKind.AttributesWritten:
                IndexByAttribute((Rule<EntityTrigger>)boxed, subscription.Attributes, ref _writtenByAttr, _anyWrittenRules);
                break;
            case RuleKind.Event:
            {
                int index = subscription.EventType.Value;
                if (index < 0)
                {
                    throw new ArgumentException("Invalid event type id.");
                }

                Grow(ref _eventByType, index);
                (_eventByType[index] ??= new List<Rule<EventTrigger>>()).Add((Rule<EventTrigger>)boxed);
                break;
            }

            case RuleKind.AnyEvent:
                _anyEventRules.Add((Rule<EventTrigger>)boxed);
                break;
            case RuleKind.SessionChanged:
                _sessionRules.Add((Rule<SessionTrigger>)boxed);
                break;
            case RuleKind.SessionRequest:
                _requestRules.Add((Rule<SessionRequestTrigger>)boxed);
                break;
            case RuleKind.ChangeSetEnd:
                _changeSetEndRules.Add((Rule<ChangeSetTrigger>)boxed);
                break;
            case RuleKind.Every:
                _timers.Add(new TimerEntry((Rule<TimerTrigger>)boxed, subscription.Period, startTime));
                break;
            case RuleKind.TickEnd:
                _tickEndRules.Add((Rule<TimerTrigger>)boxed);
                break;
        }

        _ruleInfos.Add(info);
        _rulesByName.Add(fullName, info);
        return info;
    }

    private static void IndexByAttribute(Rule<EntityTrigger> rule, AttrId[] attributes,
        ref List<Rule<EntityTrigger>>?[] byAttr, List<Rule<EntityTrigger>> any)
    {
        if (attributes.Length == 0)
        {
            any.Add(rule);
            return;
        }

        foreach (var attr in attributes)
        {
            if (attr.Value <= 0)
            {
                throw new ArgumentException("Invalid attribute id in a rule subscription.");
            }

            Grow(ref byAttr, attr.Value);
            var list = byAttr[attr.Value] ??= new List<Rule<EntityTrigger>>();
            if (!list.Contains(rule))
            {
                list.Add(rule);
            }
        }
    }

    private static void Grow<T>(ref T[] array, int index)
    {
        if (index >= array.Length)
        {
            Array.Resize(ref array, Math.Max(index + 1, array.Length * 2));
        }
    }

    // ---- IGatewayContext --------------------------------------------------------------------------------

    public ChangeSet BeginChange(SourceId source)
    {
        RefreshWall();
        return State.Begin(source, _wall, _session.SimTime, _currentCorrelation);
    }

    public AttributeBag RentPayload() => State.Pools.Bags.Rent();

    public void Publish(SourceId source, EventTypeId type, AttributeBag payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Published)
        {
            throw new InvalidOperationException("The payload was already published.");
        }

        RefreshWall();
        bool top = !_busy;
        if (top)
        {
            _busy = true;
        }

        var started = default(CorrelationId);
        bool announced = false;
        try
        {
            int depth = _currentDepth + 1;
            if (depth > _options.MaxCascadeDepth)
            {
                Diagnostics.CascadeOverflow(SourceName(source), "event of type " + EventTypeName(type) + " refused at depth " + depth);
                State.Pools.ReturnBag(payload);
                return;
            }

            payload.Published = true;
            var ev = State.Pools.Events.Rent();
            ev.Type = type;
            ev.Payload = payload;
            ev.Source = source;
            ev.Correlation = State.AllocateCorrelation();
            ev.CausedBy = _currentCorrelation;
            ev.WallTime = _wall;
            ev.SimTime = _session.SimTime;
            var observer = _observer;
            if (observer is not null)
            {
                if (top)
                {
                    started = ev.Correlation;
                    announced = true;
                    observer.InputStarted(started, source, typeof(SimEvent));
                }

                observer.EventPublished(ev.Correlation, ev.CausedBy, source, type, depth);
            }

            var item = new WorkItem { Kind = WorkKind.Event, Depth = depth, Correlation = ev.Correlation, Event = ev };
            _queue.Enqueue(item);
            if (top)
            {
                DrainLoop();
            }
        }
        finally
        {
            if (announced)
            {
                _observer?.InputFinished(started);
            }

            if (top)
            {
                ResetDispatchState();
            }
        }
    }

    public bool Execute(SessionCommand command, SourceId source)
    {
        ArgumentNullException.ThrowIfNull(command);
        RefreshWall();
        bool top = !_busy;
        if (top)
        {
            _busy = true;
        }

        bool announced = false;
        try
        {
            int depth = _currentDepth + 1;
            if (depth > _options.MaxCascadeDepth)
            {
                Diagnostics.CascadeOverflow(SourceName(source), "session command " + command.Kind + " refused at depth " + depth);
                return false;
            }

            if (top && _observer is { } observer)
            {
                announced = true;
                observer.InputStarted(default, source, typeof(SessionCommand));
            }

            if (!ApplySession(command, source, depth, out var previous))
            {
                return false;
            }

            _queue.Enqueue(new WorkItem
            {
                Kind = WorkKind.SessionChanged,
                Depth = depth,
                Correlation = _currentCorrelation,
                Command = command,
                Source = source,
                Previous = previous,
                Current = _session.State,
            });
            if (top)
            {
                DrainLoop();
            }

            return true;
        }
        finally
        {
            if (announced)
            {
                _observer?.InputFinished(default);
            }

            if (top)
            {
                ResetDispatchState();
            }
        }
    }

    // ---- ingress ----------------------------------------------------------------------------------------

    /// <summary>
    /// Processes one message completely: handlers -> commit -> dispatch -> cascades. A programming error to call
    /// while a handler runs or a drain is in progress.
    /// </summary>
    public void Ingest<T>(SourceId source, in T message, double? receivedAt = null)
    {
        if (_busy)
        {
            throw new InvalidOperationException("Ingest was called while a handler is running or a drain is in progress.");
        }

        RefreshWall();
        var handlers = _router.Find<T>();
        if (handlers is null)
        {
            Diagnostics.CountUnhandled(typeof(T));
            _observer?.InputUnhandled(source, typeof(T));
            return;
        }

        _busy = true;
        try
        {
            IngestMessage(source, in message, receivedAt ?? _wall, handlers);
        }
        finally
        {
            ResetDispatchState();
        }
    }

    private void IngestMessage<T>(SourceId source, in T message, double receivedAt, HandlerList<T> handlers)
    {
        Diagnostics.MessagesProcessed++;
        double sim = _session.SimTime;
        var changes = State.Begin(source, _wall, sim);
        var observer = _observer;
        if (observer is null)
        {
            IngestHandled(source, in message, receivedAt, handlers, changes, sim);
            return;
        }

        var correlation = changes.Correlation;
        observer.InputStarted(correlation, source, typeof(T));
        try
        {
            IngestHandled(source, in message, receivedAt, handlers, changes, sim);
        }
        finally
        {
            observer.InputFinished(correlation);
        }
    }

    private void IngestHandled<T>(SourceId source, in T message, double receivedAt, HandlerList<T> handlers,
        ChangeSet changes, double sim)
    {
        var ctx = _ingress;
        ctx.Begin(changes, source, receivedAt, _wall, sim);

        string? failure = null;
        string? failedModule = null;
        try
        {
            var list = handlers.Handlers;
            var modules = handlers.Modules;
            for (int i = 0; i < handlers.Count; i++)
            {
                ctx.CurrentModule = modules[i];
                list[i](message, ctx);
                if (ctx.IsAborted)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            failure = "exception: " + ex.Message;
            failedModule = ctx.CurrentModule;
        }

        if (failure is null && ctx.IsAborted)
        {
            failure = ctx.AbortReason ?? "aborted";
            failedModule = ctx.AbortedBy;
        }

        if (failure is not null)
        {
            changes.Discard();
            ReleasePending(ctx);
            _observer?.InputAborted(changes.Correlation, source, failedModule ?? SourceName(source), failure);
            Diagnostics.TransactionFailed(failedModule ?? SourceName(source),
                "correlation " + changes.Correlation.Value + " from " + SourceName(source) + ": " + failure);
            return;
        }

        // The Committed hook enqueues the record at depth 0; the Rejected hook records the diagnostic.
        var result = changes.Commit();
        if (result.Status == CommitStatus.Rejected)
        {
            ReleasePending(ctx);
            return;
        }

        ReleaseUnusedPayloads(ctx);
        var events = ctx.PendingEvents;
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            _queue.Enqueue(new WorkItem { Kind = WorkKind.Event, Depth = 0, Correlation = ev.Correlation, Event = ev });
        }

        events.Clear();
        var commands = ctx.PendingCommands;
        for (int i = 0; i < commands.Count; i++)
        {
            _queue.Enqueue(new WorkItem
            {
                Kind = WorkKind.Command,
                Depth = 0,
                Correlation = changes.Correlation,
                Command = commands[i],
                Source = source,
            });
        }

        commands.Clear();
        DrainLoop();
    }

    /// <summary>Returns the pending events (with their payloads), the unpublished payloads and drops the commands.</summary>
    private void ReleasePending(IngressContext ctx)
    {
        ReleaseUnusedPayloads(ctx);
        var events = ctx.PendingEvents;
        for (int i = 0; i < events.Count; i++)
        {
            State.Pools.ReturnEvent(events[i]);
        }

        events.Clear();
        ctx.PendingCommands.Clear();
    }

    private void ReleaseUnusedPayloads(IngressContext ctx)
    {
        var rented = ctx.Rented;
        for (int i = 0; i < rented.Count; i++)
        {
            if (!rented[i].Published)
            {
                State.Pools.ReturnBag(rented[i]);
            }
        }

        rented.Clear();
    }

    // ---- the tick ---------------------------------------------------------------------------------------

    /// <summary>One engine tick: clock and sim time, ingress, timers, tick-end rules (DD-001 6.1).</summary>
    public void Tick()
    {
        if (_busy)
        {
            throw new InvalidOperationException("Tick was called while a handler is running or a drain is in progress.");
        }

        _inTick = true;
        try
        {
            _wall = _clock.Now;
            _session.Advance(_wall);
            SyncTime();
            TickCount++;
            RunIngress();
            RunTimers();
            RunTickEnd();
        }
        finally
        {
            _inTick = false;
        }
    }

    private void RunIngress()
    {
        int n = _channels.Count;
        if (n == 0)
        {
            return;
        }

        int max = _options.MaxMessagesPerTick;
        int start = (int)((TickCount - 1) % n);
        int processed = 0;
        while (processed < max)
        {
            int delivered = 0;
            for (int k = 0; k < n && processed < max; k++)
            {
                var channel = _channels[(start + k) % n];
                _sink.Current = channel;
                int got = channel.Drain(_sink, 1);
                processed += got;
                delivered += got;
            }

            if (delivered == 0)
            {
                break;
            }
        }
    }

    private void RunTimers()
    {
        var timers = _timers;
        for (int i = 0; i < timers.Count; i++)
        {
            var timer = timers[i];
            if (!timer.Rule.Info.Enabled || _wall < timer.Due)
            {
                continue;
            }

            timer.Due += timer.PeriodSeconds;
            if (timer.Due <= _wall)
            {
                timer.Due = _wall + timer.PeriodSeconds; // no catch-up bursts
            }

            FireTopLevel(timer.Rule, timer.Period);
        }
    }

    private void RunTickEnd()
    {
        var rules = _tickEndRules;
        for (int i = 0; i < rules.Count; i++)
        {
            FireTopLevel(rules[i], TimeSpan.Zero);
        }
    }

    /// <summary>A timer or tick-end firing is a depth-0 origin: the work it produces is depth 1.</summary>
    private void FireTopLevel(Rule<TimerTrigger> rule, TimeSpan period)
    {
        if (!rule.Info.Enabled)
        {
            return;
        }

        var trigger = _timerTrigger;
        trigger.WallTime = _wall;
        trigger.SimTime = _session.SimTime;
        trigger.Period = period;
        _busy = true;
        _currentDepth = 0;
        _currentCorrelation = default;
        var observer = _observer;
        try
        {
            if (observer is not null)
            {
                observer.InputStarted(default, SourceRegistry.Engine, typeof(TimerTrigger));
                observer.TriggerRaised(period == TimeSpan.Zero ? DispatchTriggerKind.TickEnd : DispatchTriggerKind.Timer,
                    default, default, default, 0);
            }

            rule.Invoke(trigger, Diagnostics, observer, default);
            DrainLoop();
        }
        finally
        {
            observer?.InputFinished(default);
            ResetDispatchState();
        }
    }

    private sealed class EngineSink : IIngressSink
    {
        private readonly GatewayEngine _engine;

        public EngineSink(GatewayEngine engine) => _engine = engine;

        public IInboundChannel Current { get; set; } = null!;

        public void Deliver<T>(in T message, double? receivedAt = null) =>
            _engine.Ingest(Current.Source, in message, receivedAt);
    }

    private sealed class TimerEntry
    {
        public TimerEntry(Rule<TimerTrigger> rule, TimeSpan period, double startTime)
        {
            Rule = rule;
            Period = period;
            PeriodSeconds = period.TotalSeconds;
            Due = startTime + PeriodSeconds;
        }

        public Rule<TimerTrigger> Rule { get; }
        public TimeSpan Period { get; }
        public double PeriodSeconds { get; }
        public double Due { get; set; }
    }

    // ---- the work queue and dispatch --------------------------------------------------------------------

    private void RefreshWall()
    {
        if (_inTick || _busy)
        {
            return;
        }

        _wall = _clock.Now;
        _session.Advance(_wall);
        SyncTime();
    }

    /// <summary>Gives the repository the time that relative query filters are measured from.</summary>
    private void SyncTime() => State.SetTime(_wall, _session.SimTime);

    private void ResetDispatchState()
    {
        _busy = false;
        _currentDepth = -1;
        _currentCorrelation = default;
    }

    /// <summary>Processes the queue breadth-first until it is empty (never re-entrant).</summary>
    private void DrainLoop()
    {
        while (_queue.TryDequeue(out var item))
        {
            _currentDepth = item.Depth;
            _currentCorrelation = item.Correlation;
            switch (item.Kind)
            {
                case WorkKind.Record:
                {
                    var record = item.Record!;
                    try
                    {
                        DispatchRecord(record);
                    }
                    finally
                    {
                        CollectDestroyed(record);
                        Unpin(record);
                    }

                    break;
                }
                case WorkKind.Event:
                    DispatchEvent(item.Event!);
                    break;
                case WorkKind.Command:
                    if (ApplySession(item.Command!, item.Source, item.Depth, out var previous))
                    {
                        DispatchSessionChanged(item.Command!, previous, _session.State, item.Source, item.Depth);
                    }

                    break;
                case WorkKind.SessionChanged:
                    DispatchSessionChanged(item.Command!, item.Previous, item.Current, item.Source, item.Depth);
                    break;
            }
        }

        _currentDepth = -1;
        _currentCorrelation = default;
        ReleaseDestroyed();
    }

    private void DrainIfIdle()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            DrainLoop();
        }
        finally
        {
            ResetDispatchState();
        }
    }

    private void DispatchRecord(ChangeSetRecord record)
    {
        var deltas = record.EntityList;
        var trigger = _entityTrigger;
        var observer = _observer;
        var correlation = record.Correlation;
        for (int i = 0; i < deltas.Count; i++)
        {
            var delta = deltas[i];
            var kind = delta.Created ? EntityTriggerKind.Created
                : delta.Destroyed ? EntityTriggerKind.Removed
                : EntityTriggerKind.Updated;
            observer?.TriggerRaised(kind == EntityTriggerKind.Created ? DispatchTriggerKind.EntityCreated
                : kind == EntityTriggerKind.Removed ? DispatchTriggerKind.EntityRemoved
                : DispatchTriggerKind.EntityUpdated, delta.Id, default, correlation, _currentDepth);
            int count = CollectEntityMatches(delta, kind);
            if (count == 0)
            {
                continue;
            }

            trigger.Kind = kind;
            trigger.Entity = delta.Entity;
            trigger.Id = delta.Id;
            trigger.Delta = delta;
            trigger.ChangeSet = record;
            trigger.Source = record.Source;
            trigger.Correlation = record.Correlation;
            var matches = _entityMatches;
            for (int m = 0; m < count; m++)
            {
                matches[m].Invoke(trigger, Diagnostics, observer, correlation);
            }
        }

        var end = _changeSetEndRules;
        if (end.Count > 0)
        {
            _changeSetTrigger.ChangeSet = record;
            observer?.TriggerRaised(DispatchTriggerKind.ChangeSetEnd, default, default, correlation, _currentDepth);
            for (int i = 0; i < end.Count; i++)
            {
                end[i].Invoke(_changeSetTrigger, Diagnostics, observer, correlation);
            }
        }
    }

    /// <summary>
    /// Collects the rules matching one delta into the scratch buffer, de-duplicated by stamp and sorted by
    /// registration index. A removed entity matches only OnEntityRemoved rules; an owner-only delta of an
    /// existing entity matches nothing.
    /// </summary>
    private int CollectEntityMatches(EntityDelta delta, EntityTriggerKind kind)
    {
        int count = 0;
        long stamp = ++_stamp;
        if (kind == EntityTriggerKind.Removed)
        {
            AddMatches(_removedRules, stamp, ref count);
            return count;
        }

        if (kind == EntityTriggerKind.Created)
        {
            AddMatches(_createdRules, stamp, ref count);
        }

        var changes = delta.ChangeList;
        bool anyChanged = false;
        for (int i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            int attr = change.Attr.Value;
            if (change.Changed)
            {
                anyChanged = true;
                if (attr < _changedByAttr.Length && _changedByAttr[attr] is { } changedRules)
                {
                    AddMatches(changedRules, stamp, ref count);
                }
            }

            if (attr < _writtenByAttr.Length && _writtenByAttr[attr] is { } writtenRules)
            {
                AddMatches(writtenRules, stamp, ref count);
            }
        }

        if (changes.Count > 0)
        {
            AddMatches(_anyWrittenRules, stamp, ref count);
            if (anyChanged)
            {
                AddMatches(_anyChangedRules, stamp, ref count);
            }
        }

        // Insertion sort by registration index (no allocation; the lists are short).
        var matches = _entityMatches;
        for (int i = 1; i < count; i++)
        {
            var current = matches[i];
            int j = i - 1;
            while (j >= 0 && matches[j].Index > current.Index)
            {
                matches[j + 1] = matches[j];
                j--;
            }

            matches[j + 1] = current;
        }

        return count;
    }

    private void AddMatches(List<Rule<EntityTrigger>> rules, long stamp, ref int count)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.Stamp == stamp)
            {
                continue;
            }

            rule.Stamp = stamp;
            if (count == _entityMatches.Length)
            {
                Array.Resize(ref _entityMatches, count * 2);
            }

            _entityMatches[count++] = rule;
        }
    }

    private void DispatchEvent(SimEvent ev)
    {
        int count = 0;
        int typeIndex = ev.Type.Value;
        if (typeIndex >= 0 && typeIndex < _eventByType.Length && _eventByType[typeIndex] is { } typed)
        {
            for (int i = 0; i < typed.Count; i++)
            {
                AddEventMatch(typed[i], ref count);
            }
        }

        for (int i = 0; i < _anyEventRules.Count; i++)
        {
            AddEventMatch(_anyEventRules[i], ref count);
        }

        var matches = _eventMatches;
        for (int i = 1; i < count; i++)
        {
            var current = matches[i];
            int j = i - 1;
            while (j >= 0 && matches[j].Index > current.Index)
            {
                matches[j + 1] = matches[j];
                j--;
            }

            matches[j + 1] = current;
        }

        var observer = _observer;
        observer?.TriggerRaised(DispatchTriggerKind.Event, default, ev.Type, ev.Correlation, _currentDepth);
        if (count == 0)
        {
            Diagnostics.CountNoSubscriber(ev.Type);
        }
        else
        {
            var trigger = _eventTrigger;
            trigger.Event = ev;
            trigger.Type = ev.Type;
            trigger.Payload = ev.Payload;
            trigger.Source = ev.Source;
            trigger.Correlation = ev.Correlation;
            for (int i = 0; i < count; i++)
            {
                matches[i].Invoke(trigger, Diagnostics, observer, ev.Correlation);
            }
        }

        State.Journal.AddEvent(ev);
    }

    private void AddEventMatch(Rule<EventTrigger> rule, ref int count)
    {
        if (count == _eventMatches.Length)
        {
            Array.Resize(ref _eventMatches, count * 2);
        }

        _eventMatches[count++] = rule;
    }

    // ---- session ----------------------------------------------------------------------------------------

    /// <summary>Validates, runs the veto rules and applies the command; false (with a diagnostic) when refused.</summary>
    private bool ApplySession(SessionCommand command, SourceId source, int depth, out SessionState previous)
    {
        previous = _session.State;
        var observer = _observer;
        var correlation = _currentCorrelation;
        if (!_session.IsValid(command))
        {
            string invalid = "invalid transition: " + command.Kind + " while " + previous;
            Diagnostics.SessionRejected(SourceName(source), invalid);
            observer?.SessionCommandRejected(command, source, correlation, invalid);
            return false;
        }

        var request = _requestTrigger;
        request.Reset(command, source);
        var rules = _requestRules;
        if (rules.Count > 0)
        {
            observer?.TriggerRaised(DispatchTriggerKind.SessionRequest, default, default, correlation, depth);
        }

        for (int i = 0; i < rules.Count; i++)
        {
            rules[i].Invoke(request, Diagnostics, observer, correlation);
        }

        if (request.IsVetoed)
        {
            string vetoed = command.Kind + " vetoed: " + request.VetoReason;
            Diagnostics.SessionRejected(SourceName(source), vetoed);
            observer?.SessionCommandRejected(command, source, correlation, vetoed);
            return false;
        }

        _session.TryApply(command);
        SyncTime(); // Load and Unload reset sim time
        observer?.SessionCommandApplied(command, source, correlation);
        return true;
    }

    private void DispatchSessionChanged(SessionCommand command, SessionState previous, SessionState current,
        SourceId source, int depth)
    {
        _currentDepth = depth;
        var trigger = _sessionTrigger;
        trigger.Command = command;
        trigger.Previous = previous;
        trigger.Current = current;
        trigger.Source = source;
        var observer = _observer;
        var correlation = _currentCorrelation;
        observer?.TriggerRaised(DispatchTriggerKind.SessionChanged, default, default, correlation, depth);
        var rules = _sessionRules;
        for (int i = 0; i < rules.Count; i++)
        {
            rules[i].Invoke(trigger, Diagnostics, observer, correlation);
        }

        if (command.Kind == SessionCommandKind.Unload)
        {
            // The removal change set belongs to the same level as the session trigger, so it is committed
            // "one level up": the Committed hook enqueues its record at depth.
            _currentDepth = depth - 1;
            DestroyAllEntities();
            _currentDepth = depth;
        }
    }

    /// <summary>Unload: one engine change set removes every live entity (normal removal triggers follow).</summary>
    private void DestroyAllEntities()
    {
        var changes = State.Begin(SourceRegistry.Engine, _wall, _session.SimTime, _currentCorrelation);
        foreach (var entity in State.Entities)
        {
            changes.DestroyEntity(entity.Id);
        }

        changes.Commit();
    }

    // ---- ICommitObserver --------------------------------------------------------------------------------

    string? ICommitObserver.BeforeApply(ChangeSet changeSet)
    {
        int depth = _currentDepth + 1;
        if (depth > _options.MaxCascadeDepth)
        {
            Diagnostics.CascadeOverflow(SourceName(changeSet.Source),
                "change set of correlation " + changeSet.Correlation.Value + " refused at depth " + depth);
            return CascadeOverflowReason;
        }

        return null;
    }

    /// <summary>Remembers the entities a dispatched record destroyed, for release at the end of the drain.</summary>
    private void CollectDestroyed(ChangeSetRecord record)
    {
        var deltas = record.EntityList;
        for (int i = 0; i < deltas.Count; i++)
        {
            if (deltas[i].Destroyed)
            {
                _pendingRelease.Add(deltas[i].Id);
            }
        }
    }

    /// <summary>
    /// At the end of a drain, entities destroyed during it lose their identity keys (released to their
    /// allocators) and their entity-table entries. Until then removal rules, and the events and cascades of
    /// the same unit of work (e.g. an event published by the message that destroyed its target), can still
    /// translate and read them. The next unit of work never sees them.
    /// </summary>
    private void ReleaseDestroyed()
    {
        var ids = _pendingRelease;
        for (int i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            Identities.ReleaseEntity(id);
            for (int t = 0; t < _tables.Count; t++)
            {
                _tables[t].Remove(id);
            }
        }

        ids.Clear();
    }

    /// <summary>Ends the queue's hold on a record; recycles it if the journal evicted it meanwhile.</summary>
    private void Unpin(ChangeSetRecord record)
    {
        record.Pinned = false;
        if (record.EvictedWhilePinned)
        {
            record.EvictedWhilePinned = false;
            State.Pools.ReturnRecord(record);
        }
    }

    void ICommitObserver.Committed(ChangeSet changeSet, ChangeSetRecord record)
    {
        var observer = _observer;
        var correlation = record.Correlation;
        bool announced = false;
        if (observer is not null)
        {
            if (!_busy)
            {
                // a commit made from outside any dispatch (a host writing directly): it is a unit of work of its own
                announced = true;
                observer.InputStarted(correlation, record.Source, typeof(ChangeSet));
            }

            observer.Committed(new CommitSummary(correlation, record.CausedBy, record.Source, record.Count, record.WallTime,
                record.SimTime));
        }

        record.Pinned = true;
        _queue.Enqueue(new WorkItem
        {
            Kind = WorkKind.Record,
            Depth = _currentDepth + 1,
            Correlation = correlation,
            Record = record,
        });
        try
        {
            DrainIfIdle();
        }
        finally
        {
            if (announced)
            {
                observer!.InputFinished(correlation);
            }
        }
    }

    void ICommitObserver.Rejected(ChangeSet changeSet, string reason)
    {
        var observer = _observer;
        if (observer is not null)
        {
            bool announced = !_busy;
            if (announced)
            {
                observer.InputStarted(changeSet.Correlation, changeSet.Source, typeof(ChangeSet));
            }

            observer.CommitRejected(changeSet.Correlation, changeSet.Source, reason);
            if (announced)
            {
                observer.InputFinished(changeSet.Correlation);
            }
        }

        if (ReferenceEquals(reason, CascadeOverflowReason))
        {
            return; // already diagnosed as a cascade overflow
        }

        Diagnostics.TransactionFailed(SourceName(changeSet.Source),
            "correlation " + changeSet.Correlation.Value + " rejected: " + reason);
    }

    // ---- vocabulary validation ----------------------------------------------------------------------------

    /// <summary>
    /// Checks the declared vocabulary (DD-001 3.5): warnings for attributes and event types that are consumed (by a
    /// subscription or <c>Consumes</c>) but never produced (<c>Produces</c>) or produced but never consumed; errors for
    /// ids that are not registered. Kind conflicts cannot occur here - the registry rejects them at registration.
    /// Call it after all modules were added. Entity structure is never checked (R-034).
    /// </summary>
    public ValidationReport Validate()
    {
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();
        var attrProducers = new Dictionary<int, SortedSet<string>>();
        var attrConsumers = new Dictionary<int, SortedSet<string>>();
        var eventProducers = new Dictionary<int, SortedSet<string>>();
        var eventConsumers = new Dictionary<int, SortedSet<string>>();

        foreach (var p in _productions)
        {
            foreach (var attr in p.Attributes)
            {
                Declare(attrProducers, errors, p.Module, attr, "produces");
            }

            if (!p.EventType.IsNone)
            {
                Declare(eventProducers, errors, p.Module, p.EventType, "produces");
            }
        }

        foreach (var c in _consumptions)
        {
            foreach (var attr in c.Attributes)
            {
                Declare(attrConsumers, errors, c.Module, attr, "consumes");
            }

            if (!c.EventType.IsNone)
            {
                Declare(eventConsumers, errors, c.Module, c.EventType, "consumes");
            }
        }

        foreach (var def in Attributes)
        {
            bool produced = attrProducers.TryGetValue(def.Id.Value, out var producers);
            bool consumed = attrConsumers.TryGetValue(def.Id.Value, out var consumers);
            if (consumed && !produced)
            {
                warnings.Add(new ValidationIssue(ValidationSeverity.Warning, ValidationCodes.AttributeNeverProduced, def.Name,
                    $"attribute '{def.Name}' is consumed by {string.Join(", ", consumers!)} but no module produces it"));
            }
            else if (produced && !consumed)
            {
                warnings.Add(new ValidationIssue(ValidationSeverity.Warning, ValidationCodes.AttributeNeverConsumed, def.Name,
                    $"attribute '{def.Name}' is produced by {string.Join(", ", producers!)} but no module consumes it"));
            }
        }

        for (int id = 1; id <= EventTypes.Count; id++)
        {
            string name = EventTypes.NameOf(new EventTypeId(id));
            bool produced = eventProducers.TryGetValue(id, out var producers);
            bool consumed = eventConsumers.TryGetValue(id, out var consumers);
            if (consumed && !produced)
            {
                warnings.Add(new ValidationIssue(ValidationSeverity.Warning, ValidationCodes.EventNeverProduced, name,
                    $"event type '{name}' is consumed by {string.Join(", ", consumers!)} but no module produces it"));
            }
            else if (produced && !consumed)
            {
                warnings.Add(new ValidationIssue(ValidationSeverity.Warning, ValidationCodes.EventNeverConsumed, name,
                    $"event type '{name}' is produced by {string.Join(", ", producers!)} but no module consumes it"));
            }
        }

        string dump = ValidationReport.BuildDump(Attributes, EventTypes, attrProducers, attrConsumers, eventProducers, eventConsumers);
        return new ValidationReport(errors, warnings, dump);
    }

    private void Declare(Dictionary<int, SortedSet<string>> map, List<ValidationIssue> errors, string module, AttrId attr, string verb)
    {
        if (!Attributes.TryGet(attr, out _))
        {
            errors.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCodes.UnknownAttribute, module,
                $"module '{module}' {verb} attribute id {attr.Value}, which is not registered"));
            return;
        }

        if (!map.TryGetValue(attr.Value, out var modules))
        {
            map.Add(attr.Value, modules = new SortedSet<string>(StringComparer.Ordinal));
        }

        modules.Add(module);
    }

    private void Declare(Dictionary<int, SortedSet<string>> map, List<ValidationIssue> errors, string module, EventTypeId type, string verb)
    {
        if (type.Value < 1 || type.Value > EventTypes.Count)
        {
            errors.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCodes.UnknownEventType, module,
                $"module '{module}' {verb} event type id {type.Value}, which is not registered"));
            return;
        }

        if (!map.TryGetValue(type.Value, out var modules))
        {
            map.Add(type.Value, modules = new SortedSet<string>(StringComparer.Ordinal));
        }

        modules.Add(module);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private string SourceName(SourceId source)
    {
        try
        {
            return Sources.NameOf(source);
        }
        catch (KeyNotFoundException)
        {
            return "source#" + source.Value;
        }
    }

    private string EventTypeName(EventTypeId type)
    {
        try
        {
            return EventTypes.NameOf(type);
        }
        catch (KeyNotFoundException)
        {
            return "#" + type.Value;
        }
    }
}
