using System.Diagnostics;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Engine.Monitoring;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Showcase;

namespace SimNetGateway.Viewer;

/// <summary>Where the session is in a frame: operator actions are applied only while it is <see cref="Idle"/> (R-074).</summary>
public enum ViewerPhase
{
    /// <summary>Between ticks.</summary>
    Idle,

    /// <summary>Inside <c>Engine.Tick()</c> and the scenario's own work for that tick.</summary>
    Ticking,
}

/// <summary>Settings of a <see cref="ViewerSession"/>.</summary>
public sealed record ViewerSessionOptions
{
    public ViewerScenario Scenario { get; init; } = ViewerScenario.Convoy;

    /// <summary>Population of the traffic generator.</summary>
    public int Entities { get; init; } = 1000;

    /// <summary>Playback speed, clamped to <see cref="ViewerSession.MinSpeed"/> .. <see cref="ViewerSession.MaxSpeed"/>.</summary>
    public double Speed { get; init; } = 1.0;

    public bool Playing { get; init; } = true;

    /// <summary>Wall time a frame may spend ticking (DD-005 section 4.1); at least one owed tick is always run.</summary>
    public double TickBudgetMs { get; init; } = 50.0;

    /// <summary>Hard cap of ticks per frame; 0 = none.</summary>
    public int MaxTicksPerFrame { get; init; }

    /// <summary>Owed ticks beyond this many are dropped (and counted) instead of piling up when the gateway cannot keep up.</summary>
    public int MaxBacklogTicks { get; init; } = 600;

    /// <summary>The tables and the inspector refresh at most this often (R-075: 4 Hz).</summary>
    public double TableRefreshSeconds { get; init; } = 0.25;

    public int TraceCapacity { get; init; } = 512;

    /// <summary>Another observer that sees every dispatch call after the tracer (tests probe the engine with it).</summary>
    public IDispatchObserver? ExtraObserver { get; init; }

    /// <summary>The time source of the tick budget and the tick time samples; null = <see cref="Stopwatch"/>.</summary>
    public Func<long>? Timestamp { get; init; }

    /// <summary>Ticks per second of <see cref="Timestamp"/>.</summary>
    public long TimestampFrequency { get; init; } = Stopwatch.Frequency;
}

/// <summary>How often each group of models has been refreshed (R-075).</summary>
public sealed class RefreshCounts
{
    /// <summary>Dashboard model: every frame.</summary>
    public int Dashboard { get; internal set; }

    /// <summary>Map model: every frame.</summary>
    public int Map { get; internal set; }

    /// <summary>Entity table, inspector, rules, journal, identities, diagnostics, vocabulary: at most 4 Hz.</summary>
    public int Tables { get; internal set; }

    internal void Reset() => Dashboard = Map = Tables = 0;
}

/// <summary>
/// The viewer without a window (DD-005 sections 2 and 4.1): it hosts one scenario in process, steps its gateway at a fixed 60 Hz
/// on a <see cref="ManualClock"/> from <see cref="Frame"/> with play / pause / single step / speed and a per-frame tick budget,
/// feeds the <see cref="MetricsSampler"/>, owns all view models and the <see cref="DispatchTracer"/>, refreshes them by the policy
/// of R-075 and applies the operator's actions between ticks (R-074). It knows neither raylib nor ImGui: the frontend reads it
/// and enqueues <see cref="ViewerAction"/>s; the headless mode and the tests drive <see cref="Frame"/> themselves.
/// Single-threaded, like the engine.
/// </summary>
public sealed class ViewerSession
{
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 8.0;

    /// <summary>Engine ticks per second of simulated time at speed x1.</summary>
    public const double TickRate = 60.0;

    private const double Epsilon = 1e-9;
    private const int MaxLogEntries = 200;
    private const int SamplerCapacity = 600;

    private readonly ViewerSessionOptions _options;
    private readonly Func<long> _timestamp;
    private readonly double _msPerTimestampTick;
    private readonly Queue<ViewerAction> _queue = new();
    private readonly List<ActionOutcome> _log = new();
    private ScenarioInstance _scenario = null!;
    private SourceId? _operatorSource;
    private double _owed;
    private int _pendingSteps;
    private double _sinceTables;
    private bool _wasBehind;
    private ScenarioReport? _report;
    private IngressCounter _ingress = null!;

    public ViewerSession(ViewerSessionOptions? options = null)
    {
        _options = options ?? new ViewerSessionOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(_options.TickBudgetMs);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxTicksPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxBacklogTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.TableRefreshSeconds);
        _timestamp = _options.Timestamp ?? Stopwatch.GetTimestamp;
        _msPerTimestampTick = 1000.0 / _options.TimestampFrequency;
        Playing = _options.Playing;
        Speed = Math.Clamp(_options.Speed, MinSpeed, MaxSpeed);
        BuildScenario(_options.Scenario, _options.Entities);
    }

    // ---- what is hosted -------------------------------------------------------------------------------------------------

    /// <summary>Counts the gateways built so far: 1 at the start, +1 for every scenario switch or restart.</summary>
    public int Generation { get; private set; }

    public ViewerScenario Scenario => _scenario.Kind;

    public string ScenarioName => _scenario.Name;

    /// <summary>The population of the traffic generator (0 for the other scenarios).</summary>
    public int TrafficEntities => _scenario.Entities;

    public NedRprHost Host => _scenario.Host;

    public GatewayEngine Engine => _scenario.Host.Engine;

    public PivotSchema Schema => _scenario.Host.Schema;

    public ManualClock Clock => _scenario.Clock;

    /// <summary>Convoy Ambush only: the conditions S-* with their results so far; null for the other scenarios.</summary>
    public IReadOnlyList<ConditionResult>? ConvoyConditions => _scenario.Convoy?.Conditions;

    /// <summary>Convoy Ambush only: the violations of the global invariants G-* so far.</summary>
    public IReadOnlyList<GViolation>? ConvoyViolations => _scenario.Convoy?.Violations;

    /// <summary>Convoy Ambush only: the steps that threw so far.</summary>
    public IReadOnlyList<string>? ConvoyStepFailures => _scenario.Convoy?.StepFailures;

    /// <summary>Convoy Ambush only: the act that runs.</summary>
    public int ConvoyAct => _scenario.Convoy?.Context.Act ?? -1;

    /// <summary>Convoy Ambush: the report of the run so far (the whole report once the script ended); null for the others.</summary>
    public ScenarioReport? ConvoyReport
    {
        get
        {
            var convoy = _scenario.Convoy;
            if (convoy is null)
            {
                return null;
            }

            return convoy.Finished ? _report ??= convoy.Finish() : convoy.Finish();
        }
    }

    // ---- time control and accounting --------------------------------------------------------------------------------------

    public bool Playing { get; private set; }

    public double Speed { get; private set; }

    /// <summary>The scenario has run to its end (never for the endless ones).</summary>
    public bool Finished => _scenario.Finished;

    /// <summary>The ticks the scenario has run (the engine's tick count).</summary>
    public long TicksDone => _scenario.Ticks;

    /// <summary>The tick the scenario ends at; 0 for an endless one.</summary>
    public long TotalTicks => _scenario.TotalTicks;

    /// <summary>Frames since the scenario was built.</summary>
    public long Frames { get; private set; }

    /// <summary>Frames since the viewer started (not reset by a restart).</summary>
    public long TotalFrames { get; private set; }

    /// <summary>Seconds of frame time since the scenario was built (the sum of the <see cref="Frame"/> arguments).</summary>
    public double FrameSeconds { get; private set; }

    /// <summary>Ticks run by the last frame.</summary>
    public int TicksLastFrame { get; private set; }

    /// <summary>Ticks the last frame ran for the speed (single steps excluded): what the cap and the budget limit.</summary>
    public int PlayedLastFrame { get; private set; }

    /// <summary>Ticks run for the speed (play), not counting single steps.</summary>
    public long PlayedTicks { get; private set; }

    /// <summary>Ticks run by single steps.</summary>
    public long SteppedTicks { get; private set; }

    /// <summary>Ticks the speed schedule owed so far: the sum of speed x frame time x 60 over the playing frames.</summary>
    public double TicksOwedTotal { get; private set; }

    /// <summary>Ticks given up instead of run: the backlog beyond the cap, what was owed at pause or at the end of the scenario.</summary>
    public double TicksDropped { get; private set; }

    /// <summary>Owed ticks not yet run (fractions included). Invariant: played + backlog + dropped = owed total.</summary>
    public double BacklogExact => _owed;

    /// <summary>Whole ticks owed and not run: the gateway is behind by this many.</summary>
    public int Backlog => (int)Math.Floor(_owed + Epsilon);

    /// <summary>The last frame could not run all the ticks it owed (budget or cap): the dashboard says "behind".</summary>
    public bool Behind { get; private set; }

    public ViewerPhase Phase { get; private set; }

    /// <summary>The scenario's simulated seconds at the manual clock.</summary>
    public double ClockSeconds => _scenario.Clock.Now;

    // ---- models ---------------------------------------------------------------------------------------------------------

    public MetricsSampler Sampler { get; private set; } = null!;

    public DispatchTracer Tracer { get; private set; } = null!;

    public DashboardModel Dashboard { get; private set; } = null!;

    public MapModel Map { get; private set; } = null!;

    public EntityTableModel Entities { get; private set; } = null!;

    public InspectorModel Inspector { get; private set; } = null!;

    public RuleTableModel Rules { get; private set; } = null!;

    public JournalModel Journal { get; private set; } = null!;

    public IdentityModel Identities { get; private set; } = null!;

    public DiagnosticsModel Diagnostics { get; private set; } = null!;

    public VocabularyModel Vocabulary { get; private set; } = null!;

    /// <summary>How often each group of models was refreshed since the scenario was built.</summary>
    public RefreshCounts Refreshes { get; } = new();

    // ---- operator actions -----------------------------------------------------------------------------------------------

    /// <summary>Actions waiting for the next gap between ticks.</summary>
    public int PendingActions => _queue.Count;

    /// <summary>The most recent applied actions, oldest first.</summary>
    public IReadOnlyList<ActionOutcome> ActionLog => _log;

    /// <summary>Called for every applied action, always between ticks.</summary>
    public event Action<ActionOutcome>? ActionApplied;

    /// <summary>Called after every tick, between ticks.</summary>
    public event Action<ViewerSession>? TickCompleted;

    /// <summary>
    /// Queues an action. It is applied at the next gap between ticks - the start of the next <see cref="Frame"/> or before the next
    /// tick of the current one - never during a dispatch, even when it is enqueued from inside one (R-074).
    /// </summary>
    public void Enqueue(ViewerAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _queue.Enqueue(action);
    }

    // ---- the frame ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One frame of <paramref name="seconds"/> of frame time: applies the queued actions, runs the ticks the speed owes (and the
    /// single steps requested) within the budget, then refreshes the models by the policy - dashboard and map every frame, tables
    /// and inspector when at least <see cref="ViewerSessionOptions.TableRefreshSeconds"/> of frame time passed since their last
    /// refresh.
    /// </summary>
    public void Frame(double seconds)
    {
        if (Phase != ViewerPhase.Idle)
        {
            throw new InvalidOperationException("Frame() must not be called from inside a tick.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        Frames++;
        TotalFrames++;
        FrameSeconds += seconds;
        _sinceTables += seconds;
        TicksLastFrame = 0;
        PlayedLastFrame = 0;
        ApplyActions();

        if (Playing && !Finished)
        {
            double owed = Speed * seconds * TickRate;
            _owed += owed;
            TicksOwedTotal += owed;
        }

        long start = _timestamp();
        bool limited = false;
        while (true)
        {
            ApplyActions();
            if (Finished)
            {
                break;
            }

            if (_pendingSteps > 0)
            {
                _pendingSteps--;
                SteppedTicks++;
                RunTick();
                continue;
            }

            if (!Playing || _owed < 1.0 - Epsilon)
            {
                break;
            }

            if (_options.MaxTicksPerFrame > 0 && PlayedLastFrame >= _options.MaxTicksPerFrame)
            {
                limited = true;
                break;
            }

            if (PlayedLastFrame > 0 && (_timestamp() - start) * _msPerTimestampTick >= _options.TickBudgetMs)
            {
                limited = true;
                break;
            }

            _owed -= 1.0;
            PlayedTicks++;
            PlayedLastFrame++;
            RunTick();
        }

        if (Finished && _owed != 0)
        {
            TicksDropped += _owed;
            _owed = 0;
        }

        if (Backlog > _options.MaxBacklogTicks)
        {
            double excess = _owed - _options.MaxBacklogTicks;
            TicksDropped += excess;
            _owed -= excess;
        }

        Behind = limited && Backlog >= 1;
        _wasBehind |= Behind;
        RefreshModels();
    }

    private void RunTick()
    {
        var scenario = _scenario;
        Phase = ViewerPhase.Ticking;
        long before = _timestamp();
        bool ran;
        try
        {
            ran = scenario.Step();
        }
        finally
        {
            Phase = ViewerPhase.Idle;
        }

        long after = _timestamp();
        if (!ran)
        {
            return;
        }

        TicksLastFrame++;
        var host = scenario.Host;
        Span<long> ins = stackalloc long[3];
        Span<long> outs = stackalloc long[3];
        ins[0] = _ingress.Ned;
        ins[1] = _ingress.Rpr;
        outs[0] = host.NedSentCount;
        outs[1] = host.RprSentCount;
        outs[2] = host.DisplaySentCount;
        Sampler.Record(host.Engine, (after - before) * _msPerTimestampTick, ins, outs);
        TickCompleted?.Invoke(this);
    }

    // ---- refresh policy (R-075) -----------------------------------------------------------------------------------------

    private void RefreshModels()
    {
        Dashboard.Refresh();
        Refreshes.Dashboard++;
        Map.Refresh();
        Refreshes.Map++;
        if (_sinceTables >= _options.TableRefreshSeconds - Epsilon)
        {
            RefreshTables();
        }
    }

    private void RefreshTables()
    {
        _sinceTables = 0;
        Entities.Refresh();
        if (!Inspector.Selected.IsNone)
        {
            Inspector.Refresh();
        }

        Rules.Refresh();
        Journal.Refresh();
        Identities.Refresh();
        Diagnostics.Refresh();
        Vocabulary.Refresh();
        Dashboard.UpdateDiagnostics();
        Refreshes.Tables++;
    }

    /// <summary>Selects an entity in the inspector at once (a click is rare; the periodic refresh keeps it current).</summary>
    public void Select(EntityId entity)
    {
        Inspector.Load(entity);
    }

    // ---- applying actions -----------------------------------------------------------------------------------------------

    private void ApplyActions()
    {
        while (_queue.Count > 0)
        {
            Apply(_queue.Dequeue());
        }
    }

    private void Apply(ViewerAction action)
    {
        Debug.Assert(Phase == ViewerPhase.Idle, "operator actions are applied between ticks only");
        bool accepted = true;
        string text;
        switch (action)
        {
            case ViewerAction.Play:
                Playing = true;
                text = "play";
                break;
            case ViewerAction.Pause:
                Playing = false;
                TicksDropped += _owed;
                _owed = 0;
                text = "pause";
                break;
            case ViewerAction.Step:
                if (Playing)
                {
                    accepted = false;
                    text = "single step refused: the viewer is playing";
                }
                else if (Finished)
                {
                    accepted = false;
                    text = "single step refused: the scenario has ended";
                }
                else
                {
                    _pendingSteps++;
                    text = "single step";
                }

                break;
            case ViewerAction.SetSpeed setSpeed:
                Speed = Math.Clamp(setSpeed.Speed, MinSpeed, MaxSpeed);
                text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"speed x{Speed:0.##}");
                break;
            case ViewerAction.SetRuleEnabled rule:
                if (Engine.FindRule(rule.FullName) is { } info)
                {
                    info.Enabled = rule.Enabled;
                    text = (rule.Enabled ? "enabled " : "disabled ") + rule.FullName;
                }
                else
                {
                    accepted = false;
                    text = "no rule " + rule.FullName;
                }

                break;
            case ViewerAction.ExecuteSession execute:
                {
                    _operatorSource ??= Engine.Sources.Register("operator");
                    accepted = Engine.Execute(execute.Command, _operatorSource.Value);
                    text = $"session {execute.Command.Kind}" + (execute.Command.Scenario is { } s ? " " + s : string.Empty)
                        + (accepted ? " accepted" : " refused");
                    break;
                }

            case ViewerAction.SwitchScenario switchTo:
                BuildScenario(switchTo.Scenario, switchTo.Entities > 0 ? switchTo.Entities : _options.Entities);
                text = "scenario " + _scenario.Name;
                break;
            case ViewerAction.Restart:
                BuildScenario(_scenario.Kind, _scenario.Kind == ViewerScenario.Traffic ? _scenario.Entities : _options.Entities);
                text = "restart " + _scenario.Name;
                break;
            case ViewerAction.WatchEntity watch:
                Tracer.Watch(watch.Entity);
                text = "trace entity " + watch.Entity.Value;
                break;
            case ViewerAction.UnwatchEntity unwatch:
                Tracer.Unwatch(unwatch.Entity);
                text = "untrace entity " + unwatch.Entity.Value;
                break;
            case ViewerAction.WatchAll watchAll:
                Tracer.WatchAll = watchAll.On;
                text = watchAll.On ? "trace everything" : "trace selected only";
                break;
            case ViewerAction.WatchCorrelation correlation:
                Tracer.WatchCorrelation(correlation.Correlation);
                text = "trace correlation " + correlation.Correlation.Value;
                break;
            case ViewerAction.ClearTraces:
                Tracer.Clear();
                text = "traces cleared";
                break;
            default:
                accepted = false;
                text = "unknown action " + action.GetType().Name;
                break;
        }

        var outcome = new ActionOutcome(action, accepted, text, TicksDone, Frames);
        _log.Add(outcome);
        if (_log.Count > MaxLogEntries)
        {
            _log.RemoveRange(0, _log.Count - MaxLogEntries);
        }

        ActionApplied?.Invoke(outcome);
    }

    // ---- building a scenario --------------------------------------------------------------------------------------------

    private void BuildScenario(ViewerScenario kind, int entities)
    {
        var scenario = kind switch
        {
            ViewerScenario.Convoy => ScenarioInstance.CreateConvoy(),
            ViewerScenario.Demo => ScenarioInstance.CreateDemo(),
            ViewerScenario.Traffic => ScenarioInstance.CreateTraffic(Math.Clamp(entities, 1, BenchmarkRunner.MaxEntities)),
            ViewerScenario.Empty => ScenarioInstance.CreateEmpty(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        _scenario = scenario;
        Generation++;
        _owed = 0;
        _pendingSteps = 0;
        _operatorSource = null;
        _report = null;
        _wasBehind = false;
        Behind = false;
        Frames = 0;
        FrameSeconds = 0;
        TicksLastFrame = 0;
        PlayedLastFrame = 0;
        PlayedTicks = 0;
        SteppedTicks = 0;
        TicksOwedTotal = 0;
        TicksDropped = 0;
        Refreshes.Reset();

        var host = scenario.Host;
        var engine = host.Engine;
        Tracer = new DispatchTracer(_options.TraceCapacity) { KeepFailures = true };
        Tracer.UseNamesOf(engine);
        _ingress = new IngressCounter(host.NedSource, host.RprSource);
        IDispatchObserver observer = new ObserverChain(Tracer, _ingress);
        engine.DispatchObserver = _options.ExtraObserver is { } extra ? new ObserverChain(observer, extra) : observer;
        HookOutbound(host.NedOut, "ned", engine);
        HookOutbound(host.RprOut, "rpr", engine);
        HookOutbound(host.DisplayOut, "display", engine);

        Sampler = new MetricsSampler(SamplerCapacity, ["ned", "rpr", "display"]);
        var options = new EntityTableOptions { NameAttribute = host.Schema.Name, ParentAttribute = host.Schema.Parent };
        Entities = new EntityTableModel(engine, options);
        Inspector = new InspectorModel(engine);
        Rules = new RuleTableModel(engine);
        Journal = new JournalModel(engine);
        Identities = new IdentityModel(engine, DescribeAllocator);
        Diagnostics = new DiagnosticsModel(engine);
        Vocabulary = new VocabularyModel(engine);
        Map = new MapModel(engine, host.Schema);
        Dashboard = new DashboardModel(engine, Sampler);

        // the first frame shows everything
        _sinceTables = double.PositiveInfinity;
    }

    private void HookOutbound<T>(InMemoryOutbound<T>? outbound, string side, GatewayEngine engine)
    {
        if (outbound is null)
        {
            return;
        }

        var tracer = Tracer;
        var previous = outbound.Tap;
        outbound.Tap = message =>
        {
            previous?.Invoke(message);
            if (tracer.IsActive)
            {
                tracer.OutboundSent(engine.CurrentCorrelation, side, MessageText.Format(message!));
            }
        };
    }

    private AllocatorInfo? DescribeAllocator(IIdentityMap map)
    {
        var host = _scenario.Host;
        if (ReferenceEquals(map, host.NedIds) && host.NedIds.Allocator is RangeAllocator range)
        {
            return new AllocatorInfo("id range", (long)range.InUse, (long)range.Available, 0, (long)range.Capacity);
        }

        if (ReferenceEquals(map, host.RprEntityIds) && host.RprEntityIds.Allocator is MappedAllocator<SimNetGateway.Protocol.Rpr.DisEntityId> mapped && mapped.Numbers is NumberPool pool)
        {
            return new AllocatorInfo("entity numbers", pool.InUse, pool.Available, pool.InQuarantine, pool.Capacity);
        }

        return null;
    }
}
