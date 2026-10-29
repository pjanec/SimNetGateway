using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Showcase;

namespace SimNetGateway.Engine.Monitoring.Tests;

/// <summary>
/// Compares the tracer, tick by tick, with what the engine itself counted: the outcomes of every rule in the traces equal the
/// changes of its counters, and (when a probe is given) the records and events dispatched equal the probe's list.
/// </summary>
internal sealed class TracerAudit
{
    private readonly GatewayEngine _engine;
    private readonly DispatchTracer _tracer;
    private long[] _fires;
    private long[] _rejects;
    private long[] _failures;
    private int _probeSeen;

    public long Disabled;
    public long Cascades;
    public long Commits;
    public long Traces;

    public TracerAudit(GatewayEngine engine, DispatchTracer tracer)
    {
        _engine = engine;
        _tracer = tracer;
        (_fires, _rejects, _failures) = Snapshot();
    }

    private (long[], long[], long[]) Snapshot() =>
        (_engine.Rules.Select(r => r.FireCount).ToArray(), _engine.Rules.Select(r => r.ConditionRejects).ToArray(),
         _engine.Rules.Select(r => r.Failures).ToArray());

    public void Check(string context, ProbeModule? probe)
    {
        var rules = _engine.Rules;
        if (rules.Count != _fires.Length)
        {
            (_fires, _rejects, _failures) = Snapshot(); // rules were added (the mirror module): start over
        }

        var fired = new long[rules.Count];
        var rejected = new long[rules.Count];
        var failed = new long[rules.Count];
        var dispatched = new List<(bool, long)>();
        foreach (var trace in _tracer.Traces)
        {
            Traces++;
            Cascades += trace.Kind == TraceKind.Cascade ? 1 : 0;
            Commits += trace.Kind == TraceKind.Commit && trace.Committed ? 1 : 0;
            bool end = false;
            foreach (var step in trace.Steps)
            {
                if (step.Kind == TraceStepKind.Trigger)
                {
                    if (step.Trigger == DispatchTriggerKind.ChangeSetEnd) dispatched.Add((false, trace.Correlation.Value));
                    if (step.Trigger == DispatchTriggerKind.Event) dispatched.Add((true, trace.Correlation.Value));
                }
                else if (step.Kind == TraceStepKind.Rule)
                {
                    switch (step.Outcome)
                    {
                        case RuleOutcome.Fired: fired[step.RuleIndex]++; break;
                        case RuleOutcome.ConditionRejected: rejected[step.RuleIndex]++; break;
                        case RuleOutcome.Failed: failed[step.RuleIndex]++; break;
                        default: Disabled++; break;
                    }
                }
            }

            Assert.False(end);
        }

        for (int i = 0; i < rules.Count; i++)
        {
            Assert.True(rules[i].FireCount - _fires[i] == fired[i] && rules[i].ConditionRejects - _rejects[i] == rejected[i]
                && rules[i].Failures - _failures[i] == failed[i],
                $"{context}: rule {rules[i].FullName} counters changed by {rules[i].FireCount - _fires[i]}/{rules[i].ConditionRejects - _rejects[i]}/" +
                $"{rules[i].Failures - _failures[i]}, the traces show {fired[i]}/{rejected[i]}/{failed[i]}");
        }

        if (probe is not null)
        {
            var seen = probe.Dispatches.Skip(_probeSeen).Select(d => (d.IsEvent, d.Correlation)).ToList();
            _probeSeen = probe.Dispatches.Count;
            Assert.True(seen.OrderBy(x => x).SequenceEqual(dispatched.OrderBy(x => x)),
                $"{context}: the probe saw {seen.Count} records and events, the traces {dispatched.Count}");
        }

        (_fires, _rejects, _failures) = Snapshot();
        _tracer.Clear();
    }
}

/// <summary>
/// The monitoring library on the real gateway (DD-005 section 8): the models equal the brute-force oracle after every tick of the
/// Convoy Ambush showcase and of benchmark-style traffic with a few hundred entities, the tracer agrees with the rule counters and
/// the showcase probe, and none of it changes what the gateway does.
/// </summary>
public class IntegrationTests
{
    [Fact]
    public void The_models_and_the_tracer_agree_with_the_engine_after_every_tick_of_Convoy_Ambush()
    {
        var mirror = new JournalMirror();
        var tracer = new DispatchTracer(100_000) { WatchAll = true, IncludeTimers = true };
        AllModels? models = null;
        TracerAudit? audit = null;
        var sampler = new MetricsSampler(512, ["ned", "rpr"]);
        long ticks = 0, maxEntities = 0;
        long totalIn = 0;
        var runner = new ScenarioRunner(null, new GatewayOptions { JournalCapacity = 1024, EventLogCapacity = 512, DiagnosticsCapacity = 8192 })
        {
            OnStart = ctx =>
            {
                ctx.Engine.AddModule(mirror.AsModule());
                tracer.Attach(ctx.Engine);
                var range = ctx.NedRange;
                var pool = ctx.DisPool;
                Func<IIdentityMap, AllocatorInfo?> hook = m => m.Name switch
                {
                    "ned.entity" => new AllocatorInfo("range", (long)range.InUse, (long)range.Available, 0, (long)range.Capacity),
                    "rpr.entityId" => new AllocatorInfo("pool", pool.InUse, pool.Available, pool.InQuarantine, pool.Capacity),
                    _ => null,
                };
                models = new AllModels(ctx.Engine, new EntityTableOptions { NameAttribute = ctx.Schema.Name, ParentAttribute = ctx.Schema.Parent },
                    mirror, 5, hook);
                audit = new TracerAudit(ctx.Engine, tracer);
            },
            OnTick = ctx =>
            {
                ticks++;
                string context = "Convoy Ambush tick " + ctx.Tick;
                models!.RefreshAndCheck(context);
                audit!.Check(context, ctx.Probe);
                maxEntities = Math.Max(maxEntities, ctx.Engine.State.Count);
                totalIn = ctx.Engine.Diagnostics.MessagesProcessed;
                sampler.Record(ctx.Engine, 0.5, [totalIn, 0], [ctx.Host.NedSentCount, ctx.Host.RprSentCount]);
                Assert.Equal(ctx.Engine.TickCount, sampler.Latest.Counters.Tick);
            },
        };

        var report = runner.Run(ConvoyAmbushScript.Create());

        // with a journal of 1024 records S-10.3 ("the history is still there") cannot hold, by design; everything else does
        Assert.Equal(["S-10.3"], report.Conditions.Where(c => !c.Passed).Select(c => c.Id).ToArray());
        Assert.Empty(report.Violations);
        Assert.Equal(8400, ticks);
        Assert.True(maxEntities >= 10, "the showcase builds a convoy");
        Assert.True(audit!.Disabled > 0, "the showcase disables rules (act 6): the tracer saw skipped rules");
        Assert.True(audit.Cascades > 0, "cascades of rules were traced: " + audit.Cascades);
        Assert.True(audit.Commits > 100, "commits of processors in timer rules were traced: " + audit.Commits);

        // observing changed nothing: the report is the same text as without it
        // (the only difference is S-0.1 counting the two rules of the test's own mirror module)
        string observed = report.Render().Replace("46 rules", "44 rules");
        string plain = new ScenarioRunner(null, new GatewayOptions { JournalCapacity = 1024, EventLogCapacity = 512, DiagnosticsCapacity = 8192 })
            .Run(ConvoyAmbushScript.Create()).Render();
        Assert.Equal(plain, observed);
    }

    [Fact]
    public void A_tracer_that_records_everything_does_not_change_what_the_showcase_does()
    {
        var tracer = new DispatchTracer(256) { WatchAll = true, IncludeTimers = true };
        var runner = new ScenarioRunner { OnStart = ctx => tracer.Attach(ctx.Engine) };

        var report = runner.Run(ConvoyAmbushScript.Create());

        Assert.True(report.Passed, report.RenderSummary());
        Assert.Equal(new ScenarioRunner().Run(ConvoyAmbushScript.Create()).Render(), report.Render());
        Assert.Equal(256, tracer.Traces.Count);
    }

    [Fact]
    public void The_models_equal_the_oracle_after_every_tick_of_benchmark_traffic_with_a_few_hundred_entities()
    {
        var mirror = new JournalMirror();
        var tracer = new DispatchTracer(100_000) { WatchAll = true, IncludeTimers = true };
        AllModels? models = null;
        TracerAudit? audit = null;
        int ticks = 0;
        int maxEntities = 0;
        var result = BenchmarkRunner.Run(new BenchmarkOptions
        {
            Entities = 300,
            Seconds = 1.5,
            WarmupSeconds = 0.5,
            Record = false,
            OnHostCreated = host =>
            {
                host.Engine.AddModule(mirror.AsModule());
                tracer.Attach(host.Engine);
                models = new AllModels(host.Engine, new EntityTableOptions { NameAttribute = host.Schema.Name, ParentAttribute = host.Schema.Parent },
                    mirror, 6, null, maxRecords: 60, maxWrites: 24);
                audit = new TracerAudit(host.Engine, tracer);
            },
            AfterTick = host =>
            {
                ticks++;
                string context = "benchmark tick " + host.Engine.TickCount;
                models!.RefreshAndCheck(context);
                audit!.Check(context, null);
                maxEntities = Math.Max(maxEntities, host.Engine.State.Count);
            },
        });

        Assert.True(ticks > 120);
        Assert.True(maxEntities >= 290, "the population was built: " + maxEntities);
        Assert.True(audit!.Traces > 1000);
        Assert.True(result.Entities == 300);
    }
}
