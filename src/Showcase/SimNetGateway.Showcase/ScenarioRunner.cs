using System.Globalization;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

/// <summary>
/// Runs a <see cref="ScenarioScript"/> against the gateway on a <see cref="ManualClock"/> at 60 Hz (DD-002 section 8.1). The clock
/// is the runner clock: tick k is at k / 60 seconds, steps are scheduled on it (not on sim time, which resets at Load and
/// freezes in a pause). Per tick: advance the clock, begin the act that starts now, run the steps that are due (inject, operator,
/// action) in script order, let the simulated RTI answer, <c>Tick()</c> the engine, run the <see cref="InvariantChecker"/>,
/// evaluate the checks that are due. Everything is recorded in the <see cref="ScenarioReport"/>.
/// </summary>
public sealed class ScenarioRunner
{
    /// <summary>One tick: 60 Hz.</summary>
    public const double Dt = NedRprHost.Dt;

    private readonly GatewayConfiguration _configuration;
    private readonly GatewayOptions _engineOptions;

    public ScenarioRunner(GatewayConfiguration? configuration = null, GatewayOptions? engineOptions = null)
    {
        _configuration = configuration ?? NedRprConfigurations.Showcase;
        _engineOptions = engineOptions ?? new GatewayOptions { JournalCapacity = 32768, DiagnosticsCapacity = 8192, EventLogCapacity = 8192 };
    }

    /// <summary>
    /// Called after every engine tick of a run, once the runner's own bookkeeping and the invariant checker ran (the viewer
    /// models and their tests refresh here); null = nothing.
    /// </summary>
    public Action<ScenarioContext>? OnTick { get; init; }

    /// <summary>Called with the context before the first tick (observers, taps and monitors are installed here); null = nothing.</summary>
    public Action<ScenarioContext>? OnStart { get; init; }

    /// <summary>The context of the last run (the host, recorders and checker after the script ended).</summary>
    public ScenarioContext? Context { get; private set; }

    /// <summary>Runs the script from start to end and returns the report.</summary>
    public ScenarioReport Run(ScenarioScript script)
    {
        var run = Start(script);
        while (run.Step())
        {
        }

        return run.Finish();
    }

    /// <summary>
    /// Builds the gateway for the script and returns the run before its first tick (<see cref="OnStart"/> has been called):
    /// the caller drives it tick by tick with <see cref="ScenarioRun.Step"/> (the viewer's frame loop does) and collects the
    /// report with <see cref="ScenarioRun.Finish"/>. <see cref="Run"/> is this plus a loop.
    /// </summary>
    public ScenarioRun Start(ScenarioScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var clock = new ManualClock();
        ProbeModule? probe = null;
        var faulty = new FaultyModule();
        var rogue = new RogueProcessor();
        var host = new NedRprHost(_configuration, clock, new NedRprHostOptions { EngineOptions = _engineOptions }, builder =>
        {
            probe = new ProbeModule(new PivotSchema(builder.Attributes, builder.EventTypes));
            builder.AddModule(probe);
            builder.AddModule(faulty);
            builder.AddModule(rogue);
        });
        var checker = new InvariantChecker(host) { Probe = probe };
        var ctx = new ScenarioContext(host, clock, checker, probe!, faulty, rogue);
        Context = ctx;
        OnStart?.Invoke(ctx);
        var run = new ScenarioRun(script, ctx, OnTick);
        run.Begin();
        return run;
    }
}

/// <summary>
/// One scenario run that is advanced tick by tick (see <see cref="ScenarioRunner.Start"/>): per tick the same sequence
/// <see cref="ScenarioRunner.Run"/> always had. The live results (<see cref="Conditions"/>, <see cref="Violations"/>) can be
/// read between ticks.
/// </summary>
public sealed class ScenarioRun
{
    private readonly ScenarioScript _script;
    private readonly ScenarioContext _ctx;
    private readonly NedRprHost _host;
    private readonly List<TimelineEntry> _timeline = new();
    private readonly List<GViolation> _violations = new();
    private readonly Dictionary<string, ConditionResult> _results = new(StringComparer.Ordinal);
    private readonly List<ConditionResult> _ordered = new();
    private readonly List<InputRecord> _pendingStamps = new();
    private readonly List<TimelineEntry> _pendingTimeline = new();
    private readonly List<string> _stepFailures = new();
    private int _diagnosticsSeen;
    private bool _quiet;
    private string? _description;

    private readonly Action<ScenarioContext>? _onTick;
    private readonly List<(long Tick, int Order, int ActIndex, Step Step)> _scheduled = new();
    private readonly List<(long Tick, int Order, CheckStep Step)> _checks = new();
    private long[] _actStartTicks = Array.Empty<long>();
    private long _lastTick;
    private long _tick;
    private int _nextStep;
    private int _nextAct;
    private int _nextCheck;

    internal ScenarioRun(ScenarioScript script, ScenarioContext ctx, Action<ScenarioContext>? onTick)
    {
        _onTick = onTick;
        _script = script;
        _ctx = ctx;
        _host = ctx.Host;
        foreach (var condition in script.Conditions)
        {
            var result = new ConditionResult(condition.Id, condition.Text, ActOf(condition.Id));
            _results[condition.Id] = result;
            _ordered.Add(result);
        }
    }

    private static int ActOf(string id)
    {
        // "S-7.3" -> 7
        int dash = id.IndexOf('-');
        int dot = id.IndexOf('.');
        return dash >= 0 && dot > dash && int.TryParse(id.AsSpan(dash + 1, dot - dash - 1), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int act) ? act : -1;
    }

    /// <summary>The conditions in script order, with the results so far (a condition is not <see cref="ConditionResult.Evaluated"/> before its check ran).</summary>
    public IReadOnlyList<ConditionResult> Conditions => _ordered;

    /// <summary>The violations of the global invariants so far.</summary>
    public IReadOnlyList<GViolation> Violations => _violations;

    /// <summary>The steps that threw so far.</summary>
    public IReadOnlyList<string> StepFailures => _stepFailures;

    public ScenarioContext Context => _ctx;

    public ScenarioScript Script => _script;

    /// <summary>The tick the script ends at (60 Hz).</summary>
    public long TotalTicks => _lastTick;

    /// <summary>Ticks done so far.</summary>
    public long Ticks => _tick;

    public bool Finished => _tick >= _lastTick;

    internal void Begin()
    {
        WireTaps();

        // flatten the steps: (tick, act index, order)
        int order = 0;
        for (int a = 0; a < _script.Acts.Count; a++)
        {
            foreach (var step in _script.Acts[a].Steps)
            {
                _scheduled.Add((ScenarioContext.TickOf(step.Time), order++, a, step));
            }
        }

        _scheduled.Sort((x, y) => x.Tick != y.Tick ? x.Tick.CompareTo(y.Tick) : x.Order.CompareTo(y.Order));
        _actStartTicks = new long[_script.Acts.Count];
        for (int a = 0; a < _actStartTicks.Length; a++)
        {
            _actStartTicks[a] = ScenarioContext.TickOf(_script.ActStart(a));
        }

        _lastTick = ScenarioContext.TickOf(_script.EndTime);
        foreach (var s in _scheduled)
        {
            if (s.Step is CheckStep check)
            {
                _checks.Add((s.Tick, s.Order, check));
            }
        }
    }

    /// <summary>Runs the next tick; false (and nothing done) when the script has ended.</summary>
    public bool Step()
    {
        if (_tick >= _lastTick)
        {
            return false;
        }

        var host = _host;
        var ctx = _ctx;
        long tick = ++_tick;
        ctx.Clock.Now = tick / 60.0;
        while (_nextAct < _actStartTicks.Length && _actStartTicks[_nextAct] <= tick)
        {
            BeginAct(_nextAct, tick);
            _nextAct++;
        }

        while (_nextStep < _scheduled.Count && _scheduled[_nextStep].Tick <= tick)
        {
            RunStep(_scheduled[_nextStep].Step, tick);
            _nextStep++;
        }

        if (host.DeliverRti)
        {
            host.Rti.DeliverPending(registered =>
            {
                AddTimeline(tick, "in", "RPR", MessageText.Format(registered));
                host.RprIn.Enqueue(registered);
            });
        }

        host.Engine.Tick();
        AfterTick(tick);
        _onTick?.Invoke(ctx);
        while (_nextCheck < _checks.Count && _checks[_nextCheck].Tick <= tick)
        {
            RunCheck(_checks[_nextCheck].Step, tick);
            _nextCheck++;
        }

        return true;
    }

    /// <summary>The report of the run so far (the whole run once <see cref="Finished"/>).</summary>
    public ScenarioReport Finish() =>
        new(_script.Name, _timeline, _ordered, _violations, _tick, _script.EndTime, FinalState(), _stepFailures);

    // ---- wiring ---------------------------------------------------------------------------------------------------

    private void WireTaps()
    {
        var host = _host;
        var ctx = _ctx;
        var checker = ctx.Checker;
        host.OnNedInput = message =>
        {
            checker.OnNedReceived(message);
            RecordInput("NED", message);
        };
        host.OnRprInput = message =>
        {
            checker.OnRprReceived(message);
            RecordInput("RPR", message);
        };
        host.NedOut!.Tap = message =>
        {
            checker.OnNedSent(message);
            AddOutput("NED", message);
            if (ctx.LoopbackNed)
            {
                ctx.LoopedNed++;
                AddTimeline(ctx.Tick, "loop", "NED", "looped back: " + MessageText.Format(message));
                LoopBack(message);
            }
        };
        host.RprOut!.Tap = message =>
        {
            host.Rti.Observe(message);
            checker.OnRprSent(message);
            AddOutput("RPR", message);
        };
        host.DisplayOut!.Tap = message =>
        {
            if (message is DisplayEntityPosition position)
            {
                _timeline.Add(new TimelineEntry(ctx.Tick, ctx.RunnerTime, ctx.SimTime, ctx.Act, "out", "DISPLAY", position.Label,
                    nameof(DisplayEntityPosition)));
            }
            else
            {
                AddOutput("DISPLAY", message);
            }
        };
    }

    private void LoopBack(INedMessage message)
    {
        var inbound = _host.NedIn;
        switch (message)
        {
            case NedEntityMaster m: inbound.Enqueue(in m); break;
            case NedGeoSpatial m: inbound.Enqueue(in m); break;
            case NedEntityDamage m: inbound.Enqueue(in m); break;
            case NedWeaponStatus m: inbound.Enqueue(in m); break;
            case NedEntityDisposed m: inbound.Enqueue(in m); break;
            case NedWeaponFire m: inbound.Enqueue(in m); break;
            case NedDetonation m: inbound.Enqueue(in m); break;
            case NedSimControl m: inbound.Enqueue(in m); break;
            case NedFormation m: inbound.Enqueue(in m); break;
            case NedFireAmmunition m: inbound.Enqueue(in m); break;
            case NedDamageNotification m: inbound.Enqueue(in m); break;
            case NedIffStatus m: inbound.Enqueue(in m); break;
            default: throw new InvalidOperationException("Cannot loop back " + message.GetType().Name);
        }
    }

    private void RecordInput(string side, object message)
    {
        var ctx = _ctx;
        var record = new InputRecord(ctx.Tick + 1, ctx.RunnerTime, ctx.Act, side, message);
        ctx.AddInput(record);
        _pendingStamps.Add(record);
        string text = MessageText.Format(message);
        TimelineEntry entry;
        if (_quiet)
        {
            entry = new TimelineEntry(record.Tick, ctx.RunnerTime, ctx.SimTime, ctx.Act, "in", side, _description ?? text, "input (quiet)");
        }
        else
        {
            entry = new TimelineEntry(record.Tick, ctx.RunnerTime, ctx.SimTime, ctx.Act, "in", side,
                _description is { Length: > 0 } d ? text + "   [" + d + "]" : text);
        }

        _timeline.Add(entry);
        _pendingTimeline.Add(entry);
    }

    private void AddOutput(string side, object message) =>
        AddTimeline(_ctx.Tick, "out", side, MessageText.Format(message));

    private void AddTimeline(long tick, string kind, string side, string text) =>
        _timeline.Add(new TimelineEntry(tick, _ctx.RunnerTime, _ctx.SimTime, _ctx.Act, kind, side, text));

    // ---- the steps ------------------------------------------------------------------------------------------------

    private void BeginAct(int index, long tick)
    {
        var act = _script.Acts[index];
        _ctx.Act = act.Number;
        _ctx.Checker.SetExpected(act.ExpectedDiagnostics);
        _timeline.Add(new TimelineEntry(tick, _ctx.RunnerTime, _ctx.SimTime, act.Number, "act", "GW",
            $"Act {act.Number} - {act.Title}"));
    }

    private void RunStep(Step step, long tick)
    {
        try
        {
            Perform(step, tick);
        }
        catch (Exception ex)
        {
            string text = $"step at r={step.Time:F3} threw {ex.GetType().Name}: {ex.Message}";
            _stepFailures.Add(text);
            AddTimeline(tick, "violation", "GW", text);
        }
    }

    private void Perform(Step step, long tick)
    {
        var ctx = _ctx;
        switch (step)
        {
            case InjectStep inject:
                _quiet = inject.Quiet;
                _description = inject.Description;
                try
                {
                    inject.Enqueue(_host);
                }
                finally
                {
                    _quiet = false;
                    _description = null;
                }

                break;
            case OperatorStep op:
                {
                    var rule = ctx.Rule(op.RuleFullName);
                    rule.Enabled = op.Enable;
                    AddTimeline(tick, "op", "GW", (op.Enable ? "enable " : "disable ") + op.RuleFullName);
                    break;
                }

            case ActionStep action:
                AddTimeline(tick, "action", "GW", action.Description);
                action.Run(ctx);
                break;
        }
    }

    private void RunCheck(CheckStep step, long tick)
    {
        if (!_results.TryGetValue(step.Id, out var result))
        {
            result = new ConditionResult(step.Id, "(added by the script)", ActOf(step.Id));
            _results[step.Id] = result;
            _ordered.Add(result);
        }

        CheckResult verdict;
        try
        {
            verdict = step.Check(_ctx);
        }
        catch (Exception ex)
        {
            verdict = CheckResult.Fail($"the check threw {ex.GetType().Name}: {ex.Message}");
        }

        result.Add(_ctx.RunnerTime, verdict);
        AddTimeline(tick, "check", "GW", step.Id + (verdict.Passed ? " PASS" : " FAIL"));
    }

    // ---- after a tick ---------------------------------------------------------------------------------------------

    private void AfterTick(long tick)
    {
        var ctx = _ctx;
        foreach (var input in _pendingStamps)
        {
            input.SimTime = ctx.SimTime;
        }

        foreach (var entry in _pendingTimeline)
        {
            entry.SimTime = ctx.SimTime;
        }

        _pendingStamps.Clear();
        _pendingTimeline.Clear();

        foreach (var violation in ctx.Checker.Evaluate())
        {
            _violations.Add(new GViolation(violation.Id, violation.Tick, ctx.RunnerTime, violation.SimTime, violation.Detail));
            AddTimeline(tick, "violation", "GW", violation.ToString());
        }

        var diagnostics = ctx.Engine.Diagnostics;
        int index = 0;
        foreach (var entry in diagnostics.Recent)
        {
            if (index++ < _diagnosticsSeen)
            {
                continue;
            }

            AddTimeline(tick, "diag", "GW", $"{entry.Severity} {entry.Category} {entry.Origin}: {entry.Message}");
        }

        _diagnosticsSeen = diagnostics.Count;
    }

    private IReadOnlyList<string> FinalState()
    {
        var ctx = _ctx;
        var engine = ctx.Engine;
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"session {engine.Session.State}, mode {engine.Session.Mode}, sim time {engine.SimTime:F3} s; repository holds {engine.State.Count} entities"),
        };
        var counts = new List<string>();
        foreach (var map in engine.Identities.All)
        {
            counts.Add($"{map.Name}={map.Count}");
        }

        lines.Add("identity maps bound: " + string.Join(", ", counts));
        var range = ctx.NedRange;
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"NED id range: {range.InUse} of {range.Capacity} in use, {range.Available} free"));
        var pool = ctx.DisPool;
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"DIS entity numbers: {pool.InUse} in use, {pool.InQuarantine} in quarantine, {pool.Available} available of {pool.Capacity}"));
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"journal holds {engine.State.Journal.Count} records and {engine.State.Journal.EventCount} events; diagnostics: " +
            $"{engine.Diagnostics.TransactionFailures} transaction failures, {engine.Diagnostics.CascadeOverflows} cascade overflows, " +
            $"{engine.Diagnostics.UnhandledMessages} unhandled messages, {engine.Diagnostics.RuleFailures} rule failures"));
        return lines;
    }
}
