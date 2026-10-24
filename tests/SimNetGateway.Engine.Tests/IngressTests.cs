using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class IngressTests
{
    [Fact]
    public void Several_handlers_share_the_correlation_and_see_pending_writes_but_not_state()
    {
        var f = new EngineFixture();
        var id = f.Create();
        var log = new List<string>();
        var correlations = new List<CorrelationId>();
        f.Module("first", s => s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
        {
            ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos));
            correlations.Add(ctx.Correlation);
        }));
        f.Module("second", s => s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
        {
            correlations.Add(ctx.Correlation);
            log.Add("pending=" + ctx.Changes.TryGetPending(m.Entity, f.Pos, out var p) + ":" + p.AsVec3().X);
            log.Add("state=" + ctx.State.Find(m.Entity)!.Has(f.Pos));
        }));

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(4, 0, 0)));

        Assert.Equal(new[] { "pending=True:4", "state=False" }, log);
        Assert.Equal(2, correlations.Count);
        Assert.Equal(correlations[0], correlations[1]);
        Assert.False(correlations[0].IsNone);
        Assert.Equal(4, f.Engine.State.Find(id)!.Get(f.Pos).AsVec3().X);
        Assert.True(f.Engine.State.Journal.TryGet(correlations[0], out var record));
        Assert.Equal(f.Net, record.Source);
    }

    [Fact]
    public void The_context_exposes_source_times_and_state()
    {
        var f = new EngineFixture();
        f.Clock.Now = 12;
        double received = 0, wall = 0, sim = 0;
        SourceId source = default;
        f.Module("m", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) =>
        {
            received = ctx.ReceivedAt;
            wall = ctx.WallTime;
            sim = ctx.SimTime;
            source = ctx.Source;
            Assert.Same(f.Engine.Session, ctx.Session);
            Assert.Same(f.Engine.Diagnostics, ctx.Diagnostics);
        }));

        f.Engine.Ingest(f.Net, new PingMsg(1));
        Assert.Equal((12.0, 12.0, 12.0, f.Net), (received, wall, sim, source));

        f.Engine.Ingest(f.Net, new PingMsg(1), receivedAt: 3.5);
        Assert.Equal(3.5, received);
    }

    [Fact]
    public void Abort_discards_everything_and_names_the_module()
    {
        var f = new EngineFixture();
        var id = f.Create();
        int records = f.Engine.State.Journal.Count;
        bool thirdRan = false;
        int eventRules = 0;
        f.Module("writer", s => s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
        {
            ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos));
            ctx.Publish(f.Fired, ctx.RentPayload().Set(f.Speed, EngineFixture.D(1)));
            ctx.Command(SessionCommand.Load("x"));
        }));
        f.Module("checker", s => s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
        {
            ctx.Abort("out of range");
            ctx.Abort("second reason ignored");
            Assert.True(ctx.IsAborted);
        }));
        f.Module("late", s => s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) => thirdRan = true));
        f.Module("rules", s =>
        {
            s.OnAnyEvent().Do(_ => eventRules++);
            s.OnAttributesWritten().Do(_ => eventRules++);
        });

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(9, 9, 9)));

        Assert.False(thirdRan);
        Assert.Equal(0, eventRules);
        Assert.Equal(records, f.Engine.State.Journal.Count);
        Assert.False(f.Engine.State.Find(id)!.Has(f.Pos));
        Assert.Equal(SessionState.Idle, f.Engine.Session.State);
        Assert.Equal(0, f.Engine.State.Journal.EventCount);
        Assert.Equal(1, f.Engine.Diagnostics.TransactionFailures);
        var entry = EngineFixture.Entries(f.Engine.Diagnostics).Last();
        Assert.Equal("checker", entry.Origin);
        Assert.Contains("out of range", entry.Message);
        Assert.DoesNotContain("second", entry.Message);
    }

    [Fact]
    public void An_exception_discards_everything_names_the_module_and_the_next_message_works()
    {
        var f = new EngineFixture();
        var id = f.Create();
        int records = f.Engine.State.Journal.Count;
        bool fail = true;
        f.Module("buggy", s => s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
        {
            ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos));
            ctx.Publish(f.Fired, ctx.RentPayload());
            if (fail)
            {
                throw new InvalidOperationException("kaboom");
            }
        }));

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(1, 0, 0)));

        Assert.Equal(records, f.Engine.State.Journal.Count);
        Assert.False(f.Engine.State.IsChangeOpen);
        Assert.Equal(1, f.Engine.Diagnostics.TransactionFailures);
        var entry = EngineFixture.Entries(f.Engine.Diagnostics).Last();
        Assert.Equal("buggy", entry.Origin);
        Assert.Contains("kaboom", entry.Message);

        fail = false;
        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(2, 0, 0)));

        Assert.Equal(2, f.Engine.State.Find(id)!.Get(f.Pos).AsVec3().X);
        Assert.Equal(records + 1, f.Engine.State.Journal.Count);
        Assert.Equal(1, f.Engine.Diagnostics.TransactionFailures);
    }

    [Fact]
    public void A_validation_failure_discards_state_events_and_commands()
    {
        var f = new EngineFixture();
        int fired = 0;
        f.Module("m", s =>
        {
            s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
            {
                ctx.Changes.Set(new EntityId(999), f.Pos, AttrValue.From(m.Pos)); // does not exist
                ctx.Publish(f.Fired, ctx.RentPayload());
                ctx.Command(SessionCommand.Load("x"));
            });
            s.OnAnyEvent().Do(_ => fired++);
        });
        int records = f.Engine.State.Journal.Count;

        f.Engine.Ingest(f.Net, new MoveMsg(default, default));

        Assert.Equal(0, fired);
        Assert.Equal(SessionState.Idle, f.Engine.Session.State);
        Assert.Equal(records, f.Engine.State.Journal.Count);
        Assert.Equal(1, f.Engine.Diagnostics.TransactionFailures);
        var entry = EngineFixture.Entries(f.Engine.Diagnostics).Last();
        Assert.Equal("net", entry.Origin);
        Assert.Contains("does not exist", entry.Message);
    }

    [Fact]
    public void An_unhandled_message_type_is_counted_per_type()
    {
        var f = new EngineFixture();
        f.Module("m", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) => { }));

        f.Engine.Ingest(f.Net, new MoveMsg(default, default));
        f.Engine.Ingest(f.Net, new MoveMsg(default, default));
        f.Engine.Ingest(f.Net, new PingMsg(1));

        Assert.Equal(2, f.Engine.Diagnostics.UnhandledMessages);
        Assert.Equal(2, f.Engine.Diagnostics.UnhandledCount(typeof(MoveMsg)));
        Assert.Equal(0, f.Engine.Diagnostics.UnhandledCount(typeof(PingMsg)));
        Assert.Equal(1, f.Engine.Diagnostics.MessagesProcessed);
    }

    [Fact]
    public void Ingest_from_a_handler_or_a_rule_is_a_programming_error()
    {
        var f = new EngineFixture();
        f.Module("m", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) =>
            f.Engine.Ingest(f.Net, new PingMsg(2))));

        f.Engine.Ingest(f.Net, new PingMsg(1)); // the nested call throws inside the handler: transaction failure

        Assert.Equal(1, f.Engine.Diagnostics.TransactionFailures);
        Assert.Contains("Ingest", EngineFixture.Entries(f.Engine.Diagnostics).Last().Message);

        var g = new EngineFixture();
        RuleInfo rule = null!;
        g.Module("r", s =>
        {
            s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) => ctx.Changes.CreateEntity());
            rule = s.OnEntityCreated().Do(_ => g.Engine.Ingest(g.Net, new PingMsg(3)));
        });
        g.Engine.Ingest(g.Net, new PingMsg(1));
        Assert.Equal(1, rule.Failures);
    }

    [Fact]
    public void The_per_tick_budget_leaves_the_rest_for_the_next_tick()
    {
        var f = new EngineFixture(new GatewayOptions { MaxMessagesPerTick = 3 });
        int handled = 0;
        f.Module("m", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) => handled++));
        var channel = new TestChannel<PingMsg>("in", f.Net);
        f.Engine.AddInbound(channel);
        for (int i = 0; i < 8; i++)
        {
            channel.Enqueue(new PingMsg(i));
        }

        f.Engine.Tick();
        Assert.Equal((3, 5), (handled, channel.Pending));
        f.Engine.Tick();
        Assert.Equal((6, 2), (handled, channel.Pending));
        f.Engine.Tick();
        Assert.Equal((8, 0), (handled, channel.Pending));
        f.Engine.Tick();
        Assert.Equal(8, handled);
        Assert.Equal(4, f.Engine.TickCount);
    }

    [Fact]
    public void Channels_are_polled_round_robin_and_the_start_rotates_every_tick()
    {
        var f = new EngineFixture(new GatewayOptions { MaxMessagesPerTick = 4 });
        var sourceB = f.Engine.Sources.Register("b");
        var log = new List<string>();
        f.Module("m", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) =>
            log.Add(f.Engine.Sources.NameOf(ctx.Source))));
        var a = new TestChannel<PingMsg>("A", f.Net);
        var b = new TestChannel<PingMsg>("B", sourceB);
        f.Engine.AddInbound(a);
        f.Engine.AddInbound(b);
        for (int i = 0; i < 6; i++)
        {
            a.Enqueue(new PingMsg(i));
            b.Enqueue(new PingMsg(i));
        }

        f.Engine.Tick();
        Assert.Equal("net,b,net,b", string.Join(",", log));
        log.Clear();
        f.Engine.Tick();
        Assert.Equal("b,net,b,net", string.Join(",", log));
    }

    [Fact]
    public void A_pass_that_delivers_nothing_ends_the_ingress_phase()
    {
        var f = new EngineFixture(new GatewayOptions { MaxMessagesPerTick = 100 });
        int handled = 0;
        f.Module("m", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) => handled++));
        var a = new TestChannel<PingMsg>("A", f.Net);
        var b = new TestChannel<PingMsg>("B", f.Net);
        f.Engine.AddInbound(a);
        f.Engine.AddInbound(b);
        a.Enqueue(new PingMsg(1));
        a.Enqueue(new PingMsg(2));
        b.Enqueue(new PingMsg(3));

        f.Engine.Tick();

        Assert.Equal(3, handled);
    }

    [Fact]
    public void Each_message_is_dispatched_completely_before_the_next()
    {
        var f = new EngineFixture();
        var id = f.Create();
        var log = new List<string>();
        f.Module("m", s =>
        {
            s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
            {
                log.Add("handle:" + m.Pos.X);
                ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos));
            });
            s.OnAttributesChanged(f.Pos).Do(t => log.Add("rule:" + t.Entity.Get(f.Pos).AsVec3().X));
        });
        var channel = new TestChannel<MoveMsg>("in", f.Net);
        f.Engine.AddInbound(channel);
        channel.Enqueue(new MoveMsg(id, new Vec3(1, 0, 0)));
        channel.Enqueue(new MoveMsg(id, new Vec3(2, 0, 0)));

        f.Engine.Tick();

        Assert.Equal(new[] { "handle:1", "rule:1", "handle:2", "rule:2" }, log);
    }
}
