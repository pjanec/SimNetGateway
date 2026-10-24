using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Engine.Tests;

/// <summary>Writes every observer call as one line, so a test can compare the whole sequence.</summary>
internal sealed class LogObserver : IDispatchObserver
{
    public readonly List<string> Lines = new();

    public void InputStarted(CorrelationId correlation, SourceId source, Type messageType) =>
        Lines.Add($"start c{correlation.Value} s{source.Value} {messageType.Name}");

    public void InputUnhandled(SourceId source, Type messageType) => Lines.Add($"unhandled s{source.Value} {messageType.Name}");

    public void InputFinished(CorrelationId correlation) => Lines.Add($"finish c{correlation.Value}");

    public void InputAborted(CorrelationId correlation, SourceId source, string module, string reason) =>
        Lines.Add($"abort c{correlation.Value} {module}: {reason}");

    public void EventPublished(CorrelationId correlation, CorrelationId causedBy, SourceId source, EventTypeId type, int depth) =>
        Lines.Add($"event c{correlation.Value} by c{causedBy.Value} s{source.Value} t{type.Value} d{depth}");

    public void Committed(in CommitSummary summary) =>
        Lines.Add($"commit c{summary.Correlation.Value} by c{summary.CausedBy.Value} s{summary.Source.Value} n{summary.EntityCount}");

    public void CommitRejected(CorrelationId correlation, SourceId source, string reason) =>
        Lines.Add($"reject c{correlation.Value} s{source.Value} {reason}");

    public void TriggerRaised(DispatchTriggerKind kind, EntityId entity, EventTypeId eventType, CorrelationId correlation, int depth) =>
        Lines.Add($"trigger {kind} e{entity.Value} t{eventType.Value} c{correlation.Value} d{depth}");

    public void RuleEvaluated(RuleInfo rule, RuleOutcome outcome, CorrelationId correlation) =>
        Lines.Add($"rule {rule.FullName} {outcome} c{correlation.Value}");

    public void SessionCommandApplied(SessionCommand command, SourceId source, CorrelationId correlation) =>
        Lines.Add($"session+ {command.Kind} s{source.Value} c{correlation.Value}");

    public void SessionCommandRejected(SessionCommand command, SourceId source, CorrelationId correlation, string reason) =>
        Lines.Add($"session- {command.Kind} s{source.Value} c{correlation.Value} {reason}");
}

internal readonly record struct CmdMsg(SessionCommandKind Kind);

internal readonly record struct EvMsg(long N);

public class DispatchObserverTests
{
    private static (EngineFixture F, LogObserver Log) Fixture(GatewayOptions? options = null)
    {
        var f = new EngineFixture(options);
        return (f, new LogObserver());
    }

    private static void Move(EngineFixture f) =>
        f.Module("ingress", s =>
        {
            s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) => ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos)));
            s.OnMessage<CmdMsg>((in CmdMsg m, IngressContext ctx) =>
                ctx.Command(m.Kind == SessionCommandKind.Load ? SessionCommand.Load("s") : new SessionCommand(m.Kind)));
            s.OnMessage<EvMsg>((in EvMsg m, IngressContext ctx) =>
                ctx.Publish(f.Fired, ctx.RentPayload().Set(f.Count, AttrValue.From(m.N))));
        });

    // ---- null observer ------------------------------------------------------------------------------------------

    [Fact]
    public void Without_an_observer_nothing_is_reported_and_the_correlation_is_none_outside_dispatch()
    {
        var f = new EngineFixture();
        Move(f);
        var id = f.Create();
        CorrelationId inRule = default;
        f.Module("m", s => s.OnAttributesChanged(f.Pos).Do(t => inRule = f.Engine.CurrentCorrelation));

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(1, 2, 3)));

        Assert.Null(f.Engine.DispatchObserver);
        Assert.False(inRule.IsNone);
        Assert.True(f.Engine.CurrentCorrelation.IsNone);
        f.Clock.Advance(1);
        f.Engine.Tick();
        Assert.True(f.Engine.CurrentCorrelation.IsNone);
    }

    [Fact]
    public void The_observer_comes_from_the_options_and_can_be_changed_and_removed_between_ticks()
    {
        var log = new LogObserver();
        var f = new EngineFixture(new GatewayOptions { DispatchObserver = log });
        Move(f);
        var id = f.Create();
        Assert.Same(log, f.Engine.DispatchObserver);
        Assert.NotEmpty(log.Lines);

        var second = new LogObserver();
        f.Engine.DispatchObserver = second;
        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(1, 0, 0)));
        int firstCount = log.Lines.Count;
        Assert.NotEmpty(second.Lines);

        f.Engine.DispatchObserver = null;
        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(2, 0, 0)));
        Assert.Equal(firstCount, log.Lines.Count);
        Assert.Equal(second.Lines.Count, second.Lines.Count);
        int secondCount = second.Lines.Count;
        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(3, 0, 0)));
        Assert.Equal(secondCount, second.Lines.Count);
    }

    [Fact]
    public void The_observer_cannot_be_changed_while_a_rule_runs()
    {
        var (f, log) = Fixture();
        f.Engine.DispatchObserver = log;
        Exception? caught = null;
        f.Module("m", s => s.OnEntityCreated().Do(_ =>
        {
            try
            {
                f.Engine.DispatchObserver = null;
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;
            }
        }));

        f.Create();

        Assert.NotNull(caught);
        Assert.Same(log, f.Engine.DispatchObserver);
    }

    // ---- exact sequences ----------------------------------------------------------------------------------------

    [Fact]
    public void An_ingress_message_is_reported_from_start_through_commit_and_triggers_to_finish()
    {
        var (f, log) = Fixture();
        Move(f);
        f.Module("m", s =>
        {
            s.OnAttributesChanged(f.Pos).Named("R").Do(_ => { });
            s.OnChangeSetEnd().Named("End").Do(_ => { });
        });
        var id = f.Create();
        f.Engine.DispatchObserver = log;

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(1, 2, 3)));

        Assert.Equal(
        [
            $"start c2 s{f.Net.Value} MoveMsg",
            $"commit c2 by c0 s{f.Net.Value} n1",
            $"trigger EntityUpdated e{id.Value} t0 c2 d0",
            "rule m/R Fired c2",
            "trigger ChangeSetEnd e0 t0 c2 d0",
            "rule m/End Fired c2",
            "finish c2",
        ], log.Lines);
    }

    [Fact]
    public void Created_updated_and_removed_entities_raise_their_own_kinds_of_trigger_even_without_rules()
    {
        var (f, log) = Fixture();
        f.Engine.DispatchObserver = log;

        var id = f.Create();                                      // created
        f.Write(id, f.Count, EngineFixture.I(1));                 // updated
        var cs = f.Engine.BeginChange(f.Net);
        cs.DestroyEntity(id);                                     // removed
        cs.Commit();

        Assert.Equal(
        [
            $"trigger EntityCreated e{id.Value} t0 c1 d0",
            $"trigger EntityUpdated e{id.Value} t0 c2 d0",
            $"trigger EntityRemoved e{id.Value} t0 c3 d0",
        ], log.Lines.Where(l => l.StartsWith("trigger ", StringComparison.Ordinal)).ToList());
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("rule ", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_matching_rule_reports_its_outcome_in_registration_order()
    {
        var (f, log) = Fixture();
        Move(f);
        RuleInfo off = null!;
        f.Module("m", s =>
        {
            s.OnAttributesChanged(f.Pos).Named("Fire").Do(_ => { });
            s.OnAttributesChanged(f.Pos).Named("Reject").When(_ => false).Do(_ => { });
            s.OnAttributesChanged(f.Pos).Named("Fail").Do(_ => throw new InvalidOperationException("x"));
            off = s.OnAttributesChanged(f.Pos).Named("Off").Do(_ => { });
            s.OnAttributesChanged(f.Pos).Named("Cond").When(_ => true).When(_ => true).Do(_ => { });
            s.OnAttributesChanged(f.Count).Named("NotMatching").Do(_ => { });
        });
        off.Enabled = false;
        var id = f.Create();
        f.Engine.DispatchObserver = log;

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(1, 0, 0)));

        var rules = log.Lines.Where(l => l.StartsWith("rule ", StringComparison.Ordinal)).ToList();
        Assert.Equal(
        [
            "rule m/Fire Fired c2", "rule m/Reject ConditionRejected c2", "rule m/Fail Failed c2", "rule m/Off Disabled c2",
            "rule m/Cond Fired c2",
        ], rules);
        Assert.Equal(1, f.Engine.FindRule("m/Fail")!.Failures);
    }

    [Fact]
    public void An_aborted_transaction_and_a_throwing_handler_report_the_abort_and_nothing_else()
    {
        var (f, log) = Fixture();
        f.Module("m", s =>
        {
            s.OnMessage<PingMsg>((in PingMsg p, IngressContext ctx) =>
            {
                if (p.N == 0) ctx.Abort("no way");
                else throw new InvalidOperationException("boom");
            });
            s.OnAnyEvent().Do(_ => { });
        });
        f.Engine.DispatchObserver = log;

        f.Engine.Ingest(f.Net, new PingMsg(0));
        f.Engine.Ingest(f.Net, new PingMsg(1));

        Assert.Equal(
        [
            $"start c1 s{f.Net.Value} PingMsg", "abort c1 m: no way", "finish c1",
            $"start c2 s{f.Net.Value} PingMsg", "abort c2 m: exception: boom", "finish c2",
        ], log.Lines);
        Assert.Equal(2, f.Engine.Diagnostics.TransactionFailures);
    }

    [Fact]
    public void A_rejected_commit_and_an_unhandled_message_are_reported()
    {
        var (f, log) = Fixture();
        Move(f);
        f.Engine.DispatchObserver = log;

        f.Engine.Ingest(f.Net, new MoveMsg(new EntityId(999), new Vec3(1, 0, 0)));
        f.Engine.Ingest(f.Net, new UnknownMsg());

        Assert.Equal($"start c1 s{f.Net.Value} MoveMsg", log.Lines[0]);
        Assert.StartsWith("reject c1 ", log.Lines[1]);
        Assert.Equal("finish c1", log.Lines[2]);
        Assert.Equal($"unhandled s{f.Net.Value} UnknownMsg", log.Lines[3]);
        Assert.Equal(4, log.Lines.Count);
        Assert.Equal(1, f.Engine.Diagnostics.UnhandledMessages);
        Assert.Contains(EngineFixture.Entries(f.Engine.Diagnostics), e => e.Message.Contains(log.Lines[1].Substring($"reject c1 s{f.Net.Value} ".Length)));
    }

    private readonly record struct UnknownMsg;

    [Fact]
    public void A_processor_cascade_is_reported_inside_the_rule_that_caused_it_with_the_causing_correlation()
    {
        var (f, log) = Fixture();
        Move(f);
        f.Module("proc", s =>
        {
            s.OnAttributesChanged(f.Pos).Named("Derive").Do(t =>
            {
                var cs = f.Engine.BeginChange(f.Proc);
                cs.Set(t.Id, f.Speed, AttrValue.From(1.5));
                cs.Commit();
            });
            s.OnAttributesChanged(f.Speed).Named("Seen").Do(_ => { });
        });
        var id = f.Create();
        f.Engine.DispatchObserver = log;

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(1, 2, 3)));

        Assert.Equal(
        [
            $"start c2 s{f.Net.Value} MoveMsg",
            $"commit c2 by c0 s{f.Net.Value} n1",
            $"trigger EntityUpdated e{id.Value} t0 c2 d0",
            $"commit c3 by c2 s{f.Proc.Value} n1",
            "rule proc/Derive Fired c2",
            $"trigger EntityUpdated e{id.Value} t0 c3 d1",
            "rule proc/Seen Fired c3",
            "finish c2",
        ], log.Lines);
    }

    [Fact]
    public void An_event_published_by_a_rule_and_a_cascade_overflow_are_reported()
    {
        var (f, log) = Fixture(new GatewayOptions { MaxCascadeDepth = 1 });
        Move(f);
        f.Module("proc", s =>
        {
            s.OnAttributesChanged(f.Pos).Named("Emit").Do(t =>
                f.Engine.Publish(f.Proc, f.Fired, f.Engine.RentPayload().Set(f.Count, AttrValue.From(1L))));
            s.OnEvent(f.Fired).Named("Again").Do(_ =>
            {
                var cs = f.Engine.BeginChange(f.Proc); // depth 2 > 1: refused
                cs.Set(new EntityId(1), f.Speed, AttrValue.From(1.0));
                cs.Commit();
            });
        });
        var id = f.Create();
        f.Engine.DispatchObserver = log;

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(1, 2, 3)));

        Assert.Equal(
        [
            $"start c2 s{f.Net.Value} MoveMsg",
            $"commit c2 by c0 s{f.Net.Value} n1",
            $"trigger EntityUpdated e{id.Value} t0 c2 d0",
            $"event c3 by c2 s{f.Proc.Value} t{f.Fired.Value} d1",
            "rule proc/Emit Fired c2",
            $"trigger Event e0 t{f.Fired.Value} c3 d1",
            $"reject c4 s{f.Proc.Value} cascade overflow",
            "rule proc/Again Fired c3",
            "finish c2",
        ], log.Lines);
        Assert.Equal(1, f.Engine.Diagnostics.CascadeOverflows);
    }

    [Fact]
    public void Units_of_work_that_start_outside_an_ingress_message_are_bracketed()
    {
        var (f, log) = Fixture();
        f.Module("m", s =>
        {
            s.OnAttributesChanged(f.Count).Named("OnCount").Do(_ => { });
            s.OnEvent(f.Fired).Named("OnFired").Do(_ => { });
            s.OnSessionChanged().Named("OnSession").Do(_ => { });
            s.Every(TimeSpan.FromSeconds(1)).Named("Tick").Do(_ => { });
            s.OnTickEnd().Named("End").Do(_ => { });
        });
        var id = f.Create();
        f.Engine.DispatchObserver = log;

        f.Write(id, f.Count, EngineFixture.I(5));                                          // a host commit
        f.Engine.Publish(f.Net, f.Fired, f.Engine.RentPayload().Set(f.Count, AttrValue.From(1L))); // a host event
        f.Engine.Execute(SessionCommand.Load("x"), f.Net);                                 // a host session command
        f.Clock.Advance(1.0);
        f.Engine.Tick();                                                                   // timer and tick-end

        int net = f.Net.Value;
        Assert.Equal(
        [
            $"start c2 s{net} ChangeSet",
            $"commit c2 by c0 s{net} n1",
            $"trigger EntityUpdated e{id.Value} t0 c2 d0",
            "rule m/OnCount Fired c2",
            "finish c2",
            $"start c3 s{net} SimEvent",
            $"event c3 by c0 s{net} t{f.Fired.Value} d0",
            $"trigger Event e0 t{f.Fired.Value} c3 d0",
            "rule m/OnFired Fired c3",
            "finish c3",
            $"start c0 s{net} SessionCommand",
            $"session+ Load s{net} c0",
            "trigger SessionChanged e0 t0 c0 d0",
            "rule m/OnSession Fired c0",
            "finish c0",
            "start c0 s1 TimerTrigger",
            "trigger Timer e0 t0 c0 d0",
            "rule m/Tick Fired c0",
            "finish c0",
            "start c0 s1 TimerTrigger",
            "trigger TickEnd e0 t0 c0 d0",
            "rule m/End Fired c0",
            "finish c0",
        ], log.Lines);
    }

    [Fact]
    public void Session_commands_are_reported_applied_or_refused_with_the_veto_and_the_request_trigger()
    {
        var (f, log) = Fixture();
        Move(f);
        bool veto = false;
        f.Module("m", s =>
        {
            s.OnSessionRequest().Named("Gate").Do(t =>
            {
                if (veto)
                {
                    t.Veto("not now");
                }
            });
            s.OnSessionChanged().Named("Changed").Do(_ => { });
        });
        f.Engine.DispatchObserver = log;
        int net = f.Net.Value;

        f.Engine.Execute(SessionCommand.Start, f.Net);              // invalid while idle
        veto = true;
        f.Engine.Execute(SessionCommand.Load("a"), f.Net);          // vetoed
        veto = false;
        f.Engine.Ingest(f.Net, new CmdMsg(SessionCommandKind.Load)); // from a handler: the message's correlation

        Assert.Equal(
        [
            $"start c0 s{net} SessionCommand",
            $"session- Start s{net} c0 invalid transition: Start while Idle",
            "finish c0",
            $"start c0 s{net} SessionCommand",
            "trigger SessionRequest e0 t0 c0 d0",
            "rule m/Gate Fired c0",
            $"session- Load s{net} c0 Load vetoed: not now",
            "finish c0",
            $"start c1 s{net} CmdMsg",
            "trigger SessionRequest e0 t0 c1 d0",
            "rule m/Gate Fired c1",
            $"session+ Load s{net} c1",
            "trigger SessionChanged e0 t0 c1 d0",
            "rule m/Changed Fired c1",
            "finish c1",
        ], log.Lines);
        Assert.Equal(2, f.Engine.Diagnostics.SessionRejections);
    }

    [Fact]
    public void A_message_that_publishes_an_event_reports_the_event_under_the_message_correlation()
    {
        var (f, log) = Fixture();
        Move(f);
        f.Module("m", s => s.OnEvent(f.Fired).Named("OnFired").Do(_ => { }));
        f.Engine.DispatchObserver = log;

        f.Engine.Ingest(f.Net, new EvMsg(7));

        Assert.Equal(
        [
            $"start c1 s{f.Net.Value} EvMsg",
            $"trigger Event e0 t{f.Fired.Value} c1 d0",
            "rule m/OnFired Fired c1",
            "finish c1",
        ], log.Lines);
    }

    // ---- CurrentCorrelation -----------------------------------------------------------------------------------

    [Fact]
    public void The_current_correlation_is_the_dispatched_items_inside_rules_and_none_elsewhere()
    {
        var f = new EngineFixture();
        Move(f);
        var seen = new List<(string Where, long Current, long Expected)>();
        CorrelationId inHandler = default;
        f.Module("m", s =>
        {
            s.OnMessage<PingMsg>((in PingMsg p, IngressContext ctx) => inHandler = f.Engine.CurrentCorrelation);
            s.OnAttributesChanged(f.Pos).Named("Entity").Do(t => seen.Add(("entity", f.Engine.CurrentCorrelation.Value, t.Correlation.Value)));
            s.OnChangeSetEnd().Named("End").Do(t => seen.Add(("end", f.Engine.CurrentCorrelation.Value, t.ChangeSet.Correlation.Value)));
            s.OnEvent(f.Fired).Named("Event").Do(t => seen.Add(("event", f.Engine.CurrentCorrelation.Value, t.Correlation.Value)));
            s.Every(TimeSpan.FromSeconds(1)).Named("Timer").Do(_ => seen.Add(("timer", f.Engine.CurrentCorrelation.Value, 0)));
            s.OnTickEnd().Named("TickEnd").Do(_ => seen.Add(("tickend", f.Engine.CurrentCorrelation.Value, 0)));
        });
        var id = f.Create();
        seen.Clear();

        f.Engine.Ingest(f.Net, new PingMsg(1));
        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(1, 1, 1)));
        f.Engine.Ingest(f.Net, new EvMsg(1));
        f.Engine.Publish(f.Proc, f.Fired, f.Engine.RentPayload().Set(f.Count, AttrValue.From(1L)));
        f.Clock.Advance(1);
        f.Engine.Tick();

        Assert.True(inHandler.IsNone);
        Assert.NotEmpty(seen);
        Assert.All(seen, x => Assert.Equal(x.Expected, x.Current));
        Assert.Contains(seen, x => x.Where == "timer" && x.Current == 0);
        Assert.Contains(seen, x => x.Where == "event" && x.Current != 0);
        Assert.True(f.Engine.CurrentCorrelation.IsNone);
    }

    // ---- entity tables and identity keys -------------------------------------------------------------------------

    private readonly record struct Fuel(int Litres, string Grade);

    [Fact]
    public void Entity_tables_are_listed_by_name_and_describe_their_entries()
    {
        var f = new EngineFixture();
        EntityTable<Fuel> fuel = null!;
        EntityTable<int> plain = null!;
        f.Module("a", s => fuel = s.CreateEntityTable<Fuel>("a.fuel"));
        f.Module("b", s => plain = s.CreateEntityTable<int>("b.plain"));
        var one = f.Create();
        var two = f.Create();
        fuel.GetOrAdd(one) = new Fuel(40, "diesel");
        plain.GetOrAdd(one) = 7;

        Assert.Equal(["a.fuel", "b.plain"], f.Engine.EntityTables.Select(t => t.Name).ToArray());
        Assert.True(f.Engine.EntityTables[0].TryDescribe(one, out var text));
        Assert.Equal(new Fuel(40, "diesel").ToString(), text);
        Assert.True(f.Engine.EntityTables[1].TryDescribe(one, out text));
        Assert.Equal("7", text);
        Assert.False(f.Engine.EntityTables[0].TryDescribe(two, out text));
        Assert.Equal(string.Empty, text);
        Assert.False(f.Engine.EntityTables[0].TryDescribe(new EntityId(999), out _));

        // the entry goes with the entity
        var cs = f.Engine.BeginChange(f.Net);
        cs.DestroyEntity(one);
        cs.Commit();
        Assert.False(f.Engine.EntityTables[0].TryDescribe(one, out _));
    }

    [Fact]
    public void Describing_an_entry_allocates_only_when_called()
    {
        var f = new EngineFixture();
        EntityTable<Fuel> fuel = null!;
        f.Module("a", s => fuel = s.CreateEntityTable<Fuel>("a.fuel"));
        var one = f.Create();
        fuel.GetOrAdd(one) = new Fuel(40, "diesel");
        var table = f.Engine.EntityTables[0];
        for (int i = 0; i < 1000; i++)
        {
            fuel.GetOrAdd(one).Litres.ToString();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            fuel.GetOrAdd(one) = new Fuel(i, "diesel");
            fuel.TryGet(one, out _);
            fuel.Contains(one);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(table.TryDescribe(one, out _));
    }

    [Fact]
    public void Identity_keys_describe_themselves_as_text()
    {
        var f = new EngineFixture();
        IdentityMap<ulong> ids = null!;
        IdentityMap<string> labels = null!;
        f.Module("a", s =>
        {
            ids = s.Identities.Create<ulong>(f.Net, "net.id");
            labels = s.Identities.Create<string>(f.Net, "net.label");
        });
        var one = f.Create();
        var two = f.Create();
        Assert.True(ids.Bind(one, 12345UL));
        Assert.True(labels.Bind(one, "Hawk"));

        Assert.True(f.Engine.Identities.All[0].TryDescribeKey(one, out var text));
        Assert.Equal("12345", text);
        Assert.True(f.Engine.Identities.All[1].TryDescribeKey(one, out text));
        Assert.Equal("Hawk", text);
        Assert.False(f.Engine.Identities.All[0].TryDescribeKey(two, out text));
        Assert.Equal(string.Empty, text);
    }

    // ---- recorders --------------------------------------------------------------------------------------------

    [Fact]
    public void The_recorders_stamp_the_correlation_that_was_dispatched_when_a_message_was_sent()
    {
        var f = new EngineFixture();
        Move(f);
        var outbound = new InMemoryOutbound<string> { Context = f.Engine };
        var detached = new InMemoryOutbound<string>();
        f.Module("egress", s =>
        {
            s.OnAttributesChanged(f.Pos).Do(t => outbound.Send("pos " + t.Correlation.Value));
            s.OnEvent(f.Fired).Do(t => outbound.Send("event " + t.Correlation.Value));
            s.Every(TimeSpan.FromSeconds(1)).Do(_ => outbound.Send("timer"));
        });
        var id = f.Create();

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(1, 0, 0)));
        f.Engine.Publish(f.Proc, f.Fired, f.Engine.RentPayload().Set(f.Count, AttrValue.From(1L)));
        f.Clock.Advance(1);
        f.Engine.Tick();
        outbound.Send("from the host");
        detached.Send("without an engine");

        Assert.Equal(4, outbound.Count);
        Assert.Equal(("pos 2", 2L), (outbound.Messages[0].Message, outbound.Messages[0].Correlation.Value));
        Assert.Equal(outbound.Messages[1].Message, "event " + outbound.Messages[1].Correlation.Value);
        Assert.False(outbound.Messages[1].Correlation.IsNone);
        Assert.True(outbound.Messages[2].Correlation.IsNone); // a timer rule is not part of any correlation
        Assert.True(outbound.Messages[3].Correlation.IsNone);
        Assert.True(detached.Messages[0].Correlation.IsNone);
    }

    // ---- invariants over random traffic --------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Over_random_traffic_the_observer_calls_keep_their_invariants_and_agree_with_the_counters(int seed)
    {
        var rnd = new Random(seed);
        var f = new EngineFixture(new GatewayOptions { DiagnosticsCapacity = 4096, MaxCascadeDepth = 3 });
        Move(f);
        var log = new LogObserver();
        var current = new List<(long Reported, long Engine)>();
        RuleInfo off = null!;
        f.Module("m", s =>
        {
            s.OnAttributesChanged(f.Pos).Named("Fire").Do(t =>
            {
                current.Add((t.Correlation.Value, f.Engine.CurrentCorrelation.Value));
                if (t.Id.Value % 3 == 0)
                {
                    var cs = f.Engine.BeginChange(f.Proc);
                    cs.Set(t.Id, f.Pos, AttrValue.From(new Vec3(t.Entity.Get(f.Pos).AsVec3().X + 1, 0, 0))); // a chain, cut by the depth limit
                    cs.Commit();
                }
            });
            s.OnAttributesChanged(f.Pos).Named("Reject").When(_ => rnd.Next(2) == 0).Do(_ => { });
            s.OnAttributesChanged(f.Pos).Named("Fail").Do(_ => { if (rnd.Next(3) == 0) throw new InvalidOperationException("x"); });
            off = s.OnAttributesChanged(f.Pos, f.Count).Named("Off").Do(_ => { });
            s.OnEvent(f.Fired).Named("Event").Do(t => current.Add((t.Correlation.Value, f.Engine.CurrentCorrelation.Value)));
            s.OnSessionChanged().Named("Session").Do(_ => { });
            s.OnEntityRemoved().Named("Removed").Do(_ => { });
        });
        var ids = new List<EntityId> { f.Create(), f.Create(), f.Create(), f.Create() };
        f.Engine.DispatchObserver = log;
        var channel = new TestChannel<MoveMsg>("in", f.Net);
        f.Engine.AddInbound(channel);

        var fires = new Dictionary<string, long[]>();
        foreach (var rule in f.Engine.Rules)
        {
            fires[rule.FullName] = [rule.FireCount, rule.ConditionRejects, rule.Failures];
        }

        for (int i = 0; i < 400; i++)
        {
            switch (rnd.Next(8))
            {
                case 0:
                    f.Engine.Ingest(f.Net, new MoveMsg(ids[rnd.Next(ids.Count)], new Vec3(rnd.Next(100), 0, 0)));
                    break;
                case 1:
                    f.Engine.Ingest(f.Net, new EvMsg(rnd.Next(5)));
                    break;
                case 2:
                    f.Engine.Ingest(f.Net, new CmdMsg((SessionCommandKind)rnd.Next(5)));
                    break;
                case 3:
                    f.Engine.Execute(new SessionCommand((SessionCommandKind)rnd.Next(5)), f.Net);
                    break;
                case 4:
                    off.Enabled = !off.Enabled;
                    break;
                case 5:
                    ids.Add(f.Create());
                    break;
                case 6:
                    channel.Enqueue(new MoveMsg(ids[rnd.Next(ids.Count)], new Vec3(rnd.Next(100), 1, 1)));
                    f.Clock.Advance(0.5);
                    f.Engine.Tick();
                    break;
                default:
                    f.Engine.Ingest(f.Net, new MoveMsg(new EntityId(5000), new Vec3(1, 1, 1))); // rejected
                    break;
            }
        }

        // the units of work are bracketed, never nested, and everything happens inside one
        int depth = 0;
        var outcomes = new Dictionary<string, long[]>();
        foreach (string line in log.Lines)
        {
            if (line.StartsWith("start ", StringComparison.Ordinal)) { Assert.Equal(0, depth); depth++; }
            else if (line.StartsWith("finish ", StringComparison.Ordinal)) { Assert.Equal(1, depth); depth--; }
            else if (line.StartsWith("unhandled ", StringComparison.Ordinal)) { Assert.Equal(0, depth); }
            else
            {
                Assert.Equal(1, depth);
                if (line.StartsWith("rule ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    if (!outcomes.TryGetValue(parts[1], out var counts)) outcomes[parts[1]] = counts = new long[4];
                    counts[(int)Enum.Parse<RuleOutcome>(parts[2])]++;
                }
            }
        }

        Assert.Equal(0, depth);
        Assert.True(f.Engine.CurrentCorrelation.IsNone);
        foreach (var rule in f.Engine.Rules)
        {
            var counts = outcomes.TryGetValue(rule.FullName, out var c) ? c : new long[4];
            Assert.Equal(rule.FireCount - fires[rule.FullName][0], counts[(int)RuleOutcome.Fired]);
            Assert.Equal(rule.ConditionRejects - fires[rule.FullName][1], counts[(int)RuleOutcome.ConditionRejected]);
            Assert.Equal(rule.Failures - fires[rule.FullName][2], counts[(int)RuleOutcome.Failed]);
        }

        Assert.True(outcomes["m/Off"][(int)RuleOutcome.Disabled] > 0 && outcomes["m/Off"][(int)RuleOutcome.Fired] > 0);
        Assert.True(outcomes["m/Fail"][(int)RuleOutcome.Failed] > 0 && outcomes["m/Reject"][(int)RuleOutcome.ConditionRejected] > 0);
        Assert.NotEmpty(current);
        Assert.All(current, x => Assert.Equal(x.Reported, x.Engine));
        Assert.Contains(log.Lines, l => l.StartsWith("reject ", StringComparison.Ordinal) && l.EndsWith("cascade overflow", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.StartsWith("session- ", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.StartsWith("session+ ", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.StartsWith("commit ", StringComparison.Ordinal) && !l.Contains(" by c0 "));
    }
}
