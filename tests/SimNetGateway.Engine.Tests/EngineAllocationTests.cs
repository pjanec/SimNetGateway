using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

/// <summary>
/// DD-001 6.6 exit criterion of stage 2: a full message round trip (ingest, commit, dispatch, no-op egress rules)
/// allocates nothing in steady state.
/// </summary>
public class EngineAllocationTests
{
    private const int Entities = 100;
    private const int Warmup = 3000;
    private const int Measured = 1000;

    private sealed class Setup
    {
        public readonly EngineFixture F;
        public readonly EntityId[] Ids = new EntityId[Entities];

        public Setup(bool withEvents)
        {
            F = new EngineFixture(new GatewayOptions { JournalCapacity = 16, EventLogCapacity = 16, MaxMessagesPerTick = 100 });
            var f = F;
            f.Module("ingress", s =>
            {
                s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
                {
                    ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos));
                    if (withEvents)
                    {
                        ctx.Publish(f.Fired, ctx.RentPayload().Set(f.Speed, AttrValue.From(m.Pos.X)).Set(f.Count, AttrValue.From(1L)));
                    }
                });
            });
            f.Module("egress", s =>
            {
                s.OnAttributesChanged(f.Pos).When(static _ => true).Do(static _ => { });
                s.OnChangeSetEnd().Do(static _ => { });
                s.OnEvent(f.Fired).Do(static _ => { });
                s.OnAnyEvent().Do(static _ => { });
                s.OnTickEnd().Do(static _ => { });
                s.Every(TimeSpan.FromMilliseconds(1)).Do(static _ => { });
            });
            var cs = f.Engine.BeginChange(f.Net);
            for (int i = 0; i < Entities; i++)
            {
                Ids[i] = cs.CreateEntity();
            }

            Assert.True(cs.Commit().Succeeded);
        }
    }

    private static void Round(Setup s, int i) =>
        s.F.Engine.Ingest(s.F.Net, new MoveMsg(s.Ids[i % Entities], new Vec3(i, i + 1, i + 2)));

    [Fact]
    public void A_message_round_trip_allocates_nothing_in_steady_state()
    {
        var s = new Setup(withEvents: false);
        for (int i = 0; i < Warmup; i++)
        {
            Round(s, i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = Warmup; i < Warmup + Measured; i++)
        {
            Round(s, i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(Warmup + Measured, s.F.Engine.Diagnostics.MessagesProcessed);
    }

    [Fact]
    public void A_message_with_an_event_and_a_rented_payload_allocates_nothing_in_steady_state()
    {
        var s = new Setup(withEvents: true);
        for (int i = 0; i < Warmup; i++)
        {
            Round(s, i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = Warmup; i < Warmup + Measured; i++)
        {
            Round(s, i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(16, s.F.Engine.State.Journal.EventCount);
    }

    [Fact]
    public void The_same_through_Tick_with_an_inbound_channel_allocates_nothing()
    {
        var s = new Setup(withEvents: true);
        var f = s.F;
        var channel = new TestChannel<MoveMsg>("in", f.Net, capacity: 4096);
        f.Engine.AddInbound(channel);
        for (int i = 0; i < Warmup + Measured; i++)
        {
            channel.Enqueue(new MoveMsg(s.Ids[i % Entities], new Vec3(i, i + 1, i + 2)));
        }

        // 100 messages per tick; the clock moves 1 ms per tick so the 1 ms timer fires on every tick
        int warmupTicks = Warmup / 100;
        for (int t = 0; t < warmupTicks; t++)
        {
            f.Clock.Advance(0.001);
            f.Engine.Tick();
        }

        Assert.Equal(Measured, channel.Pending);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < Measured / 100; t++)
        {
            f.Clock.Advance(0.001);
            f.Engine.Tick();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, channel.Pending);
    }
}
