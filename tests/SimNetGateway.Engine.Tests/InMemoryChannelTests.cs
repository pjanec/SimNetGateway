using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Engine.Tests;

public class InMemoryChannelTests
{
    private sealed class Sink : IIngressSink
    {
        public readonly List<string> Log = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => Log.Add(message + "@" + receivedAt);
    }

    [Fact]
    public void Typed_inbound_keeps_order_respects_budget_and_passes_receivedAt()
    {
        var ch = new InMemoryInbound<PingMsg>(new SourceId(2), "p");
        for (int i = 0; i < 40; i++)   // more than the initial ring: growth keeps order
        {
            ch.Enqueue(new PingMsg(i), i % 2 == 0 ? i : null);
        }

        Assert.Equal(40, ch.Count);
        var sink = new Sink();
        Assert.Equal(3, ch.Drain(sink, 3));
        Assert.Equal(new[] { "PingMsg { N = 0 }@0", "PingMsg { N = 1 }@", "PingMsg { N = 2 }@2" }, sink.Log);
        Assert.Equal(37, ch.Count);
        Assert.Equal(37, ch.Drain(sink, 1000));
        Assert.Equal(0, ch.Drain(sink, 1000));
        Assert.Equal("PingMsg { N = 39 }@", sink.Log[^1]);
    }

    [Fact]
    public void Typed_inbound_enqueue_and_Tick_allocate_nothing_in_steady_state()
    {
        var clock = new ManualClock();
        var engine = new GatewayEngine(clock, new GatewayOptions { MaxMessagesPerTick = 100 });
        var source = engine.Sources.Register("net");
        int seen = 0;
        engine.AddModule(new LambdaModule("m", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) => seen += m.N)));
        var ch = new InMemoryInbound<PingMsg>(source, "net");
        engine.AddInbound(ch);

        for (int i = 0; i < 2000; i++)
        {
            ch.Enqueue(new PingMsg(1), 1.0);
            ch.Enqueue(new PingMsg(1));
            engine.Tick();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            ch.Enqueue(new PingMsg(1), 1.0);
            ch.Enqueue(new PingMsg(1));
            engine.Tick();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(6000, seen);
    }

    [Fact]
    public void Multi_type_inbound_preserves_arrival_order_and_routes_by_the_real_type()
    {
        var engine = new GatewayEngine(new ManualClock());
        var source = engine.Sources.Register("net");
        var log = new List<string>();
        engine.AddModule(new LambdaModule("m", s =>
        {
            s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) => log.Add("ping" + m.N));
            s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) => log.Add("move" + m.Pos.X));
        }));
        var ch = new InMemoryInbound(source, "net");
        engine.AddInbound(ch);

        ch.Enqueue(new PingMsg(1));
        ch.Enqueue(new MoveMsg(default, new Vec3(2, 0, 0)));
        ch.Enqueue(new PingMsg(3));
        ch.Enqueue(new MoveMsg(default, new Vec3(4, 0, 0)));
        Assert.Equal(4, ch.Count);
        engine.Tick();

        Assert.Equal(new[] { "ping1", "move2", "ping3", "move4" }, log);
        Assert.Equal(0, ch.Count);
    }

    [Fact]
    public void Outbound_stamps_tick_wall_and_sim_from_the_context_and_keeps_a_global_sequence()
    {
        var clock = new ManualClock(5);
        var engine = new GatewayEngine(clock);
        var rec = new InMemoryOutbound<PingMsg>();
        var other = new InMemoryOutbound<MoveMsg>();

        rec.Send(new PingMsg(0));   // no context: zeros
        Assert.Equal((0L, 0.0, 0.0), (rec.Messages[0].Tick, rec.Messages[0].WallTime, rec.Messages[0].SimTime));

        rec.Context = engine;
        other.Context = engine;
        clock.Now = 10;
        engine.Tick();
        double simAtFirst = engine.SimTime;
        rec.Send(new PingMsg(1));
        clock.Now = 20;
        engine.Tick();
        other.Send(new MoveMsg(default, default));
        rec.Send(new PingMsg(2));

        var r1 = rec.Messages[1];
        Assert.Equal((1L, 10.0), (r1.Tick, r1.WallTime));
        Assert.Equal(simAtFirst, r1.SimTime);
        Assert.Equal(engine.SimTime, other.Messages[0].SimTime);
        Assert.Equal((2L, 20.0), (other.Messages[0].Tick, other.Messages[0].WallTime));
        Assert.True(rec.Messages[0].Sequence < r1.Sequence);
        Assert.True(r1.Sequence < other.Messages[0].Sequence);
        Assert.True(other.Messages[0].Sequence < rec.Messages[2].Sequence);
        Assert.Equal(3, rec.Count);

        rec.Clear();
        Assert.Equal(0, rec.Count);
    }

    [Fact]
    public void Outbound_Between_is_inclusive_and_OfType_filters()
    {
        var clock = new ManualClock();
        var engine = new GatewayEngine(clock);
        var rec = new InMemoryOutbound<object> { Context = engine };
        foreach (var (t, msg) in new (double, object)[] { (1, new PingMsg(1)), (2, "s"), (3, new PingMsg(3)), (4, new PingMsg(4)) })
        {
            clock.Now = t;
            engine.Tick();
            rec.Send(msg);
        }

        Assert.Equal(new[] { 2.0, 3.0 }, rec.Between(2, 3).Select(r => r.WallTime));
        Assert.Equal(new[] { 1, 3, 4 }, rec.OfType<PingMsg>().Select(p => p.N));
        Assert.Equal(new[] { "s" }, rec.OfType<string>());
    }

    [Fact]
    public void Outbound_Tap_loops_back_into_an_inbound_channel()
    {
        var engine = new GatewayEngine(new ManualClock());
        var source = engine.Sources.Register("net");
        var seen = new List<int>();
        engine.AddModule(new LambdaModule("m", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) => seen.Add(m.N))));
        var inbound = new InMemoryInbound<PingMsg>(source, "net");
        engine.AddInbound(inbound);
        var outbound = new InMemoryOutbound<PingMsg> { Tap = m => inbound.Enqueue(m) };

        outbound.Send(new PingMsg(7));
        Assert.Equal(1, outbound.Count);
        Assert.Equal(1, inbound.Count);
        engine.Tick();
        Assert.Equal(new[] { 7 }, seen);
    }

    [Fact]
    public void NullOutbound_counts()
    {
        var n = new NullOutbound<PingMsg>();
        n.Send(new PingMsg(1));
        n.Send(new PingMsg(2));
        Assert.Equal(2, n.Sent);
    }
}
