using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Engine.Monitoring.Tests;

/// <summary>
/// DD-005 section 4.3: the observer is opt-in. With the tracer attached but watching nothing (or watching an entity the traffic
/// never touches) the engine's steady-state round trip still allocates 0 bytes (R-033), also through a tick with timers.
/// </summary>
public class AllocationTests
{
    private const int Entities = 100;
    private const int Warmup = 3000;
    private const int Measured = 1000;

    private readonly record struct Move(EntityId Entity, Vec3 Pos);

    private sealed class Setup
    {
        public readonly ManualClock Clock = new();
        public readonly GatewayEngine Engine;
        public readonly SourceId Net;
        public readonly EntityId[] Ids = new EntityId[Entities];

        public Setup(DispatchTracer? tracer)
        {
            Engine = new GatewayEngine(Clock, new GatewayOptions
            {
                JournalCapacity = 16, EventLogCapacity = 16, MaxMessagesPerTick = 100, DispatchObserver = tracer,
            });
            tracer?.UseNamesOf(Engine);
            Net = Engine.Sources.Register("net");
            var pos = Engine.Attributes.Register("pos", AttrKind.Vec3);
            var speed = Engine.Attributes.Register("speed", AttrKind.Double);
            var fired = Engine.EventTypes.Register("Fired");
            Engine.AddModule(new LambdaModule("ingress", s => s.OnMessage<Move>((in Move m, IngressContext ctx) =>
            {
                ctx.Changes.Set(m.Entity, pos, AttrValue.From(m.Pos));
                ctx.Publish(fired, ctx.RentPayload().Set(speed, AttrValue.From(m.Pos.X)));
            })));
            Engine.AddModule(new LambdaModule("egress", s =>
            {
                s.OnAttributesChanged(pos).When(static _ => true).Do(static _ => { });
                s.OnAttributesChanged(speed).When(static _ => false).Do(static _ => { });
                s.OnChangeSetEnd().Do(static _ => { });
                s.OnEvent(fired).Do(static _ => { });
                s.OnAnyEvent().Do(static _ => { });
                s.OnTickEnd().Do(static _ => { });
                s.Every(TimeSpan.FromMilliseconds(1)).Do(static _ => { });
            }));
            var cs = Engine.BeginChange(Net);
            for (int i = 0; i < Entities; i++)
            {
                Ids[i] = cs.CreateEntity();
            }

            Assert.True(cs.Commit().Succeeded);
        }

        public void Round(int i) => Engine.Ingest(Net, new Move(Ids[i % Entities], new Vec3(i, i + 1, i + 2)));
    }

    private static long Measure(Setup s)
    {
        for (int i = 0; i < Warmup; i++)
        {
            s.Round(i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = Warmup; i < Warmup + Measured; i++)
        {
            s.Round(i);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void A_round_trip_without_an_observer_allocates_nothing()
    {
        Assert.Equal(0, Measure(new Setup(null)));
    }

    [Fact]
    public void A_tracer_that_watches_nothing_allocates_nothing()
    {
        var tracer = new DispatchTracer();
        var s = new Setup(tracer);

        Assert.False(tracer.IsActive);
        Assert.Equal(0, Measure(s));
        Assert.Empty(tracer.Traces);
        Assert.Equal(Warmup + Measured, s.Engine.Diagnostics.MessagesProcessed);
    }

    [Fact]
    public void A_tracer_watching_an_entity_the_traffic_does_not_touch_allocates_nothing_in_steady_state()
    {
        var tracer = new DispatchTracer();
        tracer.Watch(new EntityId(123_456));
        tracer.WatchCorrelation(new CorrelationId(987_654_321));
        var s = new Setup(tracer);

        Assert.True(tracer.IsActive);
        Assert.Equal(0, Measure(s));
        Assert.Empty(tracer.Traces);
    }

    [Fact]
    public void The_same_through_Tick_with_timers_allocates_nothing()
    {
        var tracer = new DispatchTracer();
        tracer.Watch(new EntityId(123_456));
        var s = new Setup(tracer);
        var channel = new Channel(s.Net);
        s.Engine.AddInbound(channel);
        for (int t = 0; t < 40; t++)
        {
            for (int i = 0; i < 100; i++)
            {
                channel.Enqueue(new Move(s.Ids[i], new Vec3(i, t, 0)));
            }

            s.Clock.Advance(0.001);
            s.Engine.Tick();
        }

        for (int t = 0; t < 100; t++)
        {
            for (int i = 0; i < 100; i++)
            {
                channel.Enqueue(new Move(s.Ids[i], new Vec3(i, t + 1000, 0)));
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 100; t++)
        {
            s.Clock.Advance(0.001);
            s.Engine.Tick();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Empty(tracer.Traces);
    }

    private sealed class Channel : IInboundChannel
    {
        private readonly Queue<Move> _queue = new(20_000);

        public Channel(SourceId source) => Source = source;

        public SourceId Source { get; }

        public string Name => "in";

        public void Enqueue(Move m) => _queue.Enqueue(m);

        public int Drain(IIngressSink sink, int budget)
        {
            int n = 0;
            while (n < budget && _queue.Count > 0)
            {
                var m = _queue.Dequeue();
                sink.Deliver(in m);
                n++;
            }

            return n;
        }
    }

    [Fact]
    public void A_watching_tracer_that_keeps_nothing_leaves_the_engine_behaviour_unchanged()
    {
        // the same traffic with and without a tracer produces the same state and counters
        var plain = new World(new GatewayOptions { DiagnosticsCapacity = 4096 }, 77);
        var traced = new World(new GatewayOptions { DiagnosticsCapacity = 4096 }, 77, new DispatchTracer(50) { WatchAll = true, IncludeTimers = true });
        plain.Run(250);
        traced.Run(250);

        Assert.Equal(plain.Engine.TickCount, traced.Engine.TickCount);
        Assert.Equal(plain.Engine.State.Count, traced.Engine.State.Count);
        Assert.Equal(plain.Engine.Diagnostics.TransactionFailures, traced.Engine.Diagnostics.TransactionFailures);
        Assert.Equal(plain.Engine.Diagnostics.CascadeOverflows, traced.Engine.Diagnostics.CascadeOverflows);
        Assert.Equal(plain.Out.Count, traced.Out.Count);
        Assert.True(plain.Engine.Rules.Select(r => (r.FireCount, r.ConditionRejects, r.Failures))
            .SequenceEqual(traced.Engine.Rules.Select(r => (r.FireCount, r.ConditionRejects, r.Failures))));
        Assert.Equal(plain.Mirror.Records.Count, traced.Mirror.Records.Count);
        Assert.True(plain.Mirror.Records.Select(r => (r.Corr, r.CausedBy)).SequenceEqual(traced.Mirror.Records.Select(r => (r.Corr, r.CausedBy))));
    }
}
