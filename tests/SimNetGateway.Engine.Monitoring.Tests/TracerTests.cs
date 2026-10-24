using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Monitoring.Tests;

/// <summary>Forwards every observer call to several observers (a watching tracer and a reference tracer that keeps everything).</summary>
internal sealed class MultiObserver : IDispatchObserver
{
    private readonly IDispatchObserver[] _targets;

    public MultiObserver(params IDispatchObserver[] targets) => _targets = targets;

    public void InputStarted(CorrelationId correlation, SourceId source, Type messageType) { foreach (var t in _targets) t.InputStarted(correlation, source, messageType); }
    public void InputUnhandled(SourceId source, Type messageType) { foreach (var t in _targets) t.InputUnhandled(source, messageType); }
    public void InputFinished(CorrelationId correlation) { foreach (var t in _targets) t.InputFinished(correlation); }
    public void InputAborted(CorrelationId correlation, SourceId source, string module, string reason) { foreach (var t in _targets) t.InputAborted(correlation, source, module, reason); }
    public void EventPublished(CorrelationId correlation, CorrelationId causedBy, SourceId source, EventTypeId type, int depth) { foreach (var t in _targets) t.EventPublished(correlation, causedBy, source, type, depth); }
    public void Committed(in CommitSummary summary) { foreach (var t in _targets) t.Committed(in summary); }
    public void CommitRejected(CorrelationId correlation, SourceId source, string reason) { foreach (var t in _targets) t.CommitRejected(correlation, source, reason); }
    public void TriggerRaised(DispatchTriggerKind kind, EntityId entity, EventTypeId eventType, CorrelationId correlation, int depth) { foreach (var t in _targets) t.TriggerRaised(kind, entity, eventType, correlation, depth); }
    public void RuleEvaluated(RuleInfo rule, RuleOutcome outcome, CorrelationId correlation) { foreach (var t in _targets) t.RuleEvaluated(rule, outcome, correlation); }
    public void SessionCommandApplied(SessionCommand command, SourceId source, CorrelationId correlation) { foreach (var t in _targets) t.SessionCommandApplied(command, source, correlation); }
    public void SessionCommandRejected(SessionCommand command, SourceId source, CorrelationId correlation, string reason) { foreach (var t in _targets) t.SessionCommandRejected(command, source, correlation, reason); }
}

/// <summary>
/// The dispatch tracer against a reference probe module (DD-005 section 8): for ingress, processor cascades, events, session
/// commands, aborted, rejected and unhandled inputs the trace of a correlation equals the sequence of triggers the probe saw, every
/// matching rule appears with the outcome its static behaviour implies (fired, condition rejected, failed, disabled), outbound
/// messages are attributed to the dispatching correlation, cascades carry the correlation that caused them.
/// </summary>
public class TracerTests
{
    private static readonly string[] RuleOrder =
    [
        "Probe/Created", "Probe/Removed", "Probe/Written", "Probe/AnyEvent", "Probe/End", "Probe/Session", "Probe/Request",
        "W/Table", "W/Bind", "W/Fire", "W/Reject", "W/Fail", "W/Off", "W/ProcPos", "W/EvFire", "W/EvReject", "W/EvFail", "W/EvOff",
        "W/OtherSub", "W/SessionOk", "W/SessionReject", "W/Veto", "W/Timer",
    ];

    /// <summary>The rules a trigger reaches and what each does, derived from the rules' static behaviour and the probe's masks.</summary>
    private static List<(string Rule, RuleOutcome Outcome)> ExpectedRules(World w, Trig t)
    {
        var rules = new List<(string, RuleOutcome)>();
        void Add(string name, RuleOutcome outcome = RuleOutcome.Fired) => rules.Add((name, outcome));
        void Changed(int changed)
        {
            if ((changed & World.CountBit) != 0) Add("W/Fire");
            if ((changed & World.PosBit) != 0) Add("W/Reject", RuleOutcome.ConditionRejected);
            if ((changed & World.LabelBit) != 0) Add("W/Fail", RuleOutcome.Failed);
            if ((changed & (World.CountBit | World.PosBit)) != 0) Add("W/Off", w.OffEnabled ? RuleOutcome.Fired : RuleOutcome.Disabled);
            if ((changed & World.PosBit) != 0) Add("W/ProcPos");
        }

        switch (t.Kind)
        {
            case DispatchTriggerKind.EntityCreated:
            {
                var (changed, written) = w.Masks.TryGetValue((t.Corr, t.Entity.Value), out var m) ? m : (0, 0);
                Add("Probe/Created");
                if (written != 0) Add("Probe/Written");
                Add("W/Table");
                Add("W/Bind");
                Changed(changed);
                break;
            }

            case DispatchTriggerKind.EntityUpdated:
            {
                var (changed, _) = w.Masks[(t.Corr, t.Entity.Value)];
                Add("Probe/Written");
                Changed(changed);
                break;
            }

            case DispatchTriggerKind.EntityRemoved:
                Add("Probe/Removed");
                break;
            case DispatchTriggerKind.Event:
                Add("Probe/AnyEvent");
                if (t.EventType == w.Fired)
                {
                    Add("W/EvFire");
                    Add("W/EvReject", RuleOutcome.ConditionRejected);
                    Add("W/EvFail", RuleOutcome.Failed);
                    Add("W/EvOff", RuleOutcome.Disabled);
                }
                else
                {
                    Add("W/OtherSub");
                }

                break;
            case DispatchTriggerKind.ChangeSetEnd:
                Add("Probe/End");
                break;
            case DispatchTriggerKind.SessionChanged:
                Add("Probe/Session");
                Add("W/SessionOk");
                Add("W/SessionReject", RuleOutcome.ConditionRejected);
                break;
            case DispatchTriggerKind.SessionRequest:
                Add("Probe/Request");
                Add("W/Veto");
                break;
        }

        return rules;
    }

    private static string Show(IEnumerable<(string Rule, RuleOutcome Outcome)> rules) =>
        string.Join(", ", rules.Select(r => r.Rule + "=" + r.Outcome));

    private static void CompareTrigger(World w, DispatchTrace trace, TraceTrigger actual, Trig expected, string context)
    {
        Assert.True(actual.Kind == expected.Kind && actual.Entity == expected.Entity && actual.EventType == expected.EventType,
            $"{context}: trace {trace.Correlation.Value} has trigger {actual.Kind}/{actual.Entity.Value}/{actual.EventType.Value}, " +
            $"expected {expected.Kind}/{expected.Entity.Value}/{expected.EventType.Value}");
        var rules = ExpectedRules(w, expected);
        var actualRules = actual.Rules.Select(r => (r.Rule, r.Outcome)).ToList();
        Assert.True(rules.SequenceEqual(actualRules),
            $"{context}: trigger {expected.Kind} of correlation {expected.Corr} has rules [{Show(actualRules)}], expected [{Show(rules)}]");
    }

    /// <summary>Compares the traces recorded in one tick with the probe's reference of that tick.</summary>
    private static void CompareTick(World w, DispatchTracer tracer, long lastId, int unknownSent, long failuresBefore, long overflowBefore,
        long sessionRejectionsBefore, string context)
    {
        var fresh = tracer.Traces.Where(t => t.Id > lastId).ToList();
        Assert.True(fresh.Select(t => t.Id).SequenceEqual(fresh.Select(t => t.Id).OrderBy(i => i)), $"{context}: traces out of order");

        // the sequence of ingress inputs equals the handlers' log, unhandled ones aside
        var inputs = fresh.Where(t => t.Kind == TraceKind.Input && t.Status != TraceStatus.Unhandled).ToList();
        Assert.True(w.Arrivals.Select(a => (a.Type, a.Corr)).SequenceEqual(inputs.Select(t => (t.InputType, t.Correlation.Value))),
            $"{context}: ingress inputs [{string.Join(", ", inputs.Select(t => t.InputType + "#" + t.Correlation.Value))}] != " +
            $"[{string.Join(", ", w.Arrivals.Select(a => a.Type + "#" + a.Corr))}]");
        Assert.Equal(unknownSent, fresh.Count(t => t.Status == TraceStatus.Unhandled && t.InputType == nameof(UnknownMsg)));

        // triggers and rule outcomes per correlation
        var byCorrelation = fresh.Where(t => !t.Correlation.IsNone).GroupBy(t => t.Correlation.Value).ToList();
        Assert.True(byCorrelation.All(g => g.Count() == 1), $"{context}: a correlation has two traces");
        foreach (var trace in fresh.Where(t => !t.Correlation.IsNone))
        {
            var expected = w.TriggerLog.Where(t => t.Corr == trace.Correlation.Value).ToList();
            var actual = trace.Triggers();
            Assert.True(expected.Count == actual.Count, $"{context}: correlation {trace.Correlation.Value} ({trace.Kind}, {trace.InputType}) has " +
                $"{actual.Count} triggers [{string.Join(", ", actual.Select(a => a.Kind))}], the probe saw {expected.Count} [{string.Join(", ", expected.Select(a => a.Kind))}]");
            for (int i = 0; i < expected.Count; i++)
            {
                CompareTrigger(w, trace, actual[i], expected[i], context);
            }
        }

        foreach (long corr in w.TriggerLog.Where(t => t.Corr != 0).Select(t => t.Corr).Distinct())
        {
            Assert.True(byCorrelation.Any(g => g.Key == corr), $"{context}: the probe saw correlation {corr}, the tracer has no trace of it");
        }

        // correlation-less units (timers, host session commands): the concatenated triggers equal the probe's
        var noneExpected = w.TriggerLog.Where(t => t.Corr == 0).ToList();
        foreach (var timer in fresh.Where(t => t.Kind == TraceKind.Timer))
        {
            var triggers = timer.Triggers();
            Assert.Single(triggers);
            Assert.Equal(DispatchTriggerKind.Timer, triggers[0].Kind);
            Assert.Equal([("W/Timer", RuleOutcome.Fired)], triggers[0].Rules.Select(r => (r.Rule, r.Outcome)).ToArray());
        }

        var noneActual = fresh.Where(t => t.Correlation.IsNone && t.Kind != TraceKind.Timer).SelectMany(t => t.Triggers().Select(x => (Trace: t, Trigger: x))).ToList();
        Assert.True(noneExpected.Count == noneActual.Count, $"{context}: {noneActual.Count} triggers without correlation, the probe saw {noneExpected.Count}");
        for (int i = 0; i < noneExpected.Count; i++)
        {
            CompareTrigger(w, noneActual[i].Trace, noneActual[i].Trigger, noneExpected[i], context);
        }

        // statuses against the diagnostics
        var failed = fresh.Where(t => t.Status is TraceStatus.Rejected or TraceStatus.Aborted).ToList();
        int overflows = failed.Count(t => t.Detail == "cascade overflow");
        Assert.Equal(w.Engine.Diagnostics.TransactionFailures - failuresBefore, failed.Count - overflows);
        Assert.True(overflows <= w.Engine.Diagnostics.CascadeOverflows - overflowBefore);
        var messages = new List<string>();
        foreach (var d in w.Engine.Diagnostics.Recent)
        {
            if (d.Category == "Transaction")
            {
                messages.Add(d.Message);
            }
        }

        foreach (var t in failed.Where(t => t.Detail != "cascade overflow"))
        {
            Assert.True(messages.Any(m => m.Contains("correlation " + t.Correlation.Value + " ") && m.Contains(t.Detail!.Split(": ").Last())),
                $"{context}: no diagnostic for the {t.Status} trace of correlation {t.Correlation.Value} ({t.Detail})");
        }

        // session commands
        int rejectedSteps = fresh.Sum(t => t.Steps.Count(s => s.Kind == TraceStepKind.SessionRejected));
        Assert.Equal(w.Engine.Diagnostics.SessionRejections - sessionRejectionsBefore, rejectedSteps);
        int appliedSteps = fresh.Sum(t => t.Steps.Count(s => s.Kind == TraceStepKind.SessionApplied));
        Assert.Equal(w.TriggerLog.Count(t => t.Kind == DispatchTriggerKind.SessionChanged), appliedSteps);

        // outbound messages: attributed to the dispatching correlation, in order
        foreach (var trace in fresh.Where(t => !t.Correlation.IsNone))
        {
            var expected = w.Sent.Where(s => s.Corr == trace.Correlation.Value).Select(s => "out: " + s.Text).ToList();
            Assert.True(expected.SequenceEqual(trace.Outbound()), $"{context}: outbound messages of correlation {trace.Correlation.Value} differ");
        }

        Assert.Equal(w.Sent.Count, fresh.Sum(t => t.Outbound().Count));

        // causes: every cascade names the correlation that caused it, and the reverse
        foreach (var (child, parent) in w.CausedByLog)
        {
            var trace = fresh.Single(t => t.Correlation.Value == child);
            Assert.Equal(parent, trace.CausedBy.Value);
            Assert.Contains(trace, tracer.Cascades(new CorrelationId(parent)));
        }

        foreach (var trace in fresh.Where(t => !t.CausedBy.IsNone))
        {
            Assert.Contains(w.CausedByLog, e => e.Child == trace.Correlation.Value && e.CausedBy == trace.CausedBy.Value);
        }

        foreach (var (parent, child) in w.CommitCascades.Where(c => c.Parent != 0))
        {
            var trace = fresh.Single(t => t.Correlation.Value == child);
            Assert.Equal(parent, trace.CausedBy.Value);
            Assert.True(trace.Kind == TraceKind.Cascade && trace.Committed);
        }
    }

    [Theory]
    [InlineData(1, 256, 64, 8)]
    [InlineData(2, 0, 0, 8)]
    [InlineData(3, 4, 2, 8)]
    [InlineData(4, 256, 64, 0)]   // every cascade is refused: rejected commits
    [InlineData(5, 256, 64, 1)]
    public void The_trace_of_every_correlation_equals_the_reference_probe(int seed, int journal, int events, int maxDepth)
    {
        var tracer = new DispatchTracer(100_000) { WatchAll = true, IncludeTimers = true };
        var world = new World(new GatewayOptions { JournalCapacity = journal, EventLogCapacity = events, DiagnosticsCapacity = 4096, MaxCascadeDepth = maxDepth }, seed, tracer);
        long lastId = 0;
        int cascades = 0, rejected = 0, aborted = 0, unhandled = 0, sessions = 0, outbound = 0;
        for (int tick = 1; tick <= 300; tick++)
        {
            int unknownBefore = 0;
            long failures = world.Engine.Diagnostics.TransactionFailures, overflow = world.Engine.Diagnostics.CascadeOverflows;
            long rejections = world.Engine.Diagnostics.SessionRejections;
            long unhandledBefore = world.Engine.Diagnostics.UnhandledMessages;
            world.RandomTick();
            int unknownSent = (int)(world.Engine.Diagnostics.UnhandledMessages - unhandledBefore) + unknownBefore;
            CompareTick(world, tracer, lastId, unknownSent, failures, overflow, rejections, $"seed {seed} depth {maxDepth} tick {tick}");
            var fresh = tracer.Traces.Where(t => t.Id > lastId).ToList();
            cascades += fresh.Count(t => t.Kind == TraceKind.Cascade);
            rejected += fresh.Count(t => t.Status == TraceStatus.Rejected);
            aborted += fresh.Count(t => t.Status == TraceStatus.Aborted);
            unhandled += fresh.Count(t => t.Status == TraceStatus.Unhandled);
            sessions += fresh.Sum(t => t.Steps.Count(s => s.Kind is TraceStepKind.SessionApplied or TraceStepKind.SessionRejected));
            outbound += fresh.Sum(t => t.Outbound().Count);
            lastId = tracer.Traces.Count == 0 ? lastId : tracer.Traces[^1].Id;
        }

        Assert.True(rejected > 0 && aborted > 0 && unhandled > 0 && sessions > 0 && outbound > 0, $"{rejected}/{aborted}/{unhandled}/{sessions}/{outbound}");
        Assert.True(maxDepth == 0 || cascades > 0, "cascades are traced");
        Assert.True(world.Engine.Diagnostics.RuleFailures > 0);
    }

    [Fact]
    public void The_recorder_stamps_the_correlation_that_was_being_dispatched()
    {
        var world = new World(new GatewayOptions { JournalCapacity = 64, EventLogCapacity = 16 }, 7);
        int checkedMessages = 0;
        for (int tick = 1; tick <= 300; tick++)
        {
            int before = world.Out.Count;
            world.RandomTick();
            Assert.Equal(world.Sent.Count, world.Out.Count - before);
            for (int i = 0; i < world.Sent.Count; i++)
            {
                var recorded = world.Out.Messages[before + i];
                Assert.Equal(world.Sent[i].Text, recorded.Message);
                Assert.Equal(world.Sent[i].Corr, recorded.Correlation.Value);
                Assert.False(recorded.Correlation.IsNone);
                checkedMessages++;
            }
        }

        Assert.True(checkedMessages > 20);
        Assert.True(world.Engine.CurrentCorrelation.IsNone);
    }

    // ---- watching entities and correlations ----------------------------------------------------------------------

    private static DispatchTrace RootOf(DispatchTrace trace, DispatchTracer all)
    {
        var current = trace;
        while (!current.CausedBy.IsNone && all.TraceOf(current.CausedBy) is { } parent)
        {
            current = parent;
        }

        return current;
    }

    [Fact]
    public void A_unit_that_touched_an_entity_watched_from_the_start_is_kept_whole_with_its_cascades()
    {
        // pass 1 finds entities with cascades; pass 2 (same seed, so the same run) watches them from the start
        var all = new DispatchTracer(1_000_000) { WatchAll = true, IncludeTimers = true };
        var first = new World(new GatewayOptions { DiagnosticsCapacity = 4096 }, 21, all);
        first.Run(150);
        var entities = all.Traces.Where(t => t.Kind == TraceKind.Cascade && t.Committed)
            .SelectMany(t => t.Steps.Where(s => s.Kind == TraceStepKind.Trigger && !s.Entity.IsNone).Select(s => s.Entity))
            .Distinct().Take(3).ToList();
        Assert.Equal(3, entities.Count);

        var watching = new DispatchTracer(1_000_000);
        foreach (var entity in entities)
        {
            watching.Watch(entity);
        }

        var second = new World(new GatewayOptions { DiagnosticsCapacity = 4096 }, 21, watching);
        second.Run(150);

        var expected = all.Traces.Where(t => !t.Correlation.IsNone && RootAndTree(t, all).Any(x => entities.Any(x.Touches)))
            .Select(t => t.Correlation.Value).ToList();
        var actual = watching.Traces.Where(t => !t.Correlation.IsNone).Select(t => t.Correlation.Value).ToList();
        Assert.True(expected.Count > 3);
        var extra = watching.Traces.Where(t => !expected.Contains(t.Correlation.Value) && !t.Correlation.IsNone).Select(t =>
            $"{t.Correlation.Value}:{t.Kind}:{t.InputType}:causedBy {t.CausedBy.Value}:{string.Join(",", t.Steps.Where(x => x.Kind == TraceStepKind.Trigger).Select(x => x.Trigger + "/" + x.Entity.Value))}").ToList();
        Assert.True(extra.Count == 0, "unexpected: " + string.Join(" | ", extra));
        Assert.Equal(expected, actual);
        Assert.Contains(watching.Traces, t => t.Kind == TraceKind.Cascade);
        Assert.True(all.Traces.Count(t => !t.Correlation.IsNone) > 3 * actual.Count / 2, "most units touched nothing watched and were dropped");
    }

    private static List<DispatchTrace> RootAndTree(DispatchTrace trace, DispatchTracer all)
    {
        var root = RootOf(trace, all);
        var tree = new List<DispatchTrace> { root };
        tree.AddRange(all.Descendants(root.Correlation));
        return tree;
    }

    [Fact]
    public void A_watched_correlation_keeps_its_unit_including_a_failed_one()
    {
        var all = new DispatchTracer(1_000_000) { WatchAll = true, IncludeTimers = true };
        var first = new World(new GatewayOptions { DiagnosticsCapacity = 4096 }, 31, all);
        first.Run(200);
        var rejected = all.Traces.First(t => t.Status == TraceStatus.Rejected && t.Detail != "cascade overflow");
        var aborted = all.Traces.First(t => t.Status == TraceStatus.Aborted);

        var watching = new DispatchTracer(1_000_000);
        watching.WatchCorrelation(rejected.Correlation);
        watching.WatchCorrelation(aborted.Correlation);
        var second = new World(new GatewayOptions { DiagnosticsCapacity = 4096 }, 31, watching);
        second.Run(200);

        var kept = watching.Traces.ToList();
        Assert.Equal(2, kept.Count(t => !t.Correlation.IsNone && (t.Correlation == rejected.Correlation || t.Correlation == aborted.Correlation)));
        var r = watching.TraceOf(rejected.Correlation)!;
        Assert.Equal(TraceStatus.Rejected, r.Status);
        Assert.Equal(rejected.Detail, r.Detail);
        Assert.Equal(TraceStatus.Aborted, watching.TraceOf(aborted.Correlation)!.Status);
        Assert.Equal(aborted.Detail, watching.TraceOf(aborted.Correlation)!.Detail);
    }

    [Fact]
    public void The_ring_is_bounded_and_keeps_the_newest_traces()
    {
        var tracer = new DispatchTracer(10) { WatchAll = true };
        var world = new World(new GatewayOptions { DiagnosticsCapacity = 64 }, 41, tracer);
        world.Run(120);
        Assert.Equal(10, tracer.Traces.Count);
        Assert.True(tracer.Traces.Select(t => t.Id).SequenceEqual(tracer.Traces.Select(t => t.Id).OrderBy(i => i)));
        var held = tracer.Traces[0];
        int steps = held.Steps.Count;
        world.Run(50); // the ring moved on; a trace a reader still holds does not change
        Assert.Equal(steps, held.Steps.Count);
    }
}
