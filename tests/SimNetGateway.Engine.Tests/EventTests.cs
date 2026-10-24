using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class EventTests
{
    [Fact]
    public void Events_reach_typed_and_any_rules_after_the_entity_triggers_and_see_the_updated_state()
    {
        var f = new EngineFixture();
        var id = f.Create();
        var log = new List<string>();
        CorrelationId entityCorrelation = default, eventCorrelation = default;
        f.Module("m", s =>
        {
            s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
            {
                ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos));
                ctx.Publish(f.Fired, ctx.RentPayload().Set(f.Speed, EngineFixture.D(42)));
                ctx.Publish(f.Other, ctx.RentPayload());
            });
            s.OnAnyEvent().Named("any").Do(t => log.Add("any:" + t.Type.Value));
            s.OnEvent(f.Fired).Named("fired").Do(t =>
            {
                log.Add("fired:" + t.Payload.Get(f.Speed).AsDouble() + ":" + f.Engine.State.Find(id)!.Get(f.Pos).AsVec3().X);
                eventCorrelation = t.Correlation;
                Assert.Equal(f.Net, t.Source);
                Assert.Same(t.Event.Payload, t.Payload);
            });
            s.OnAttributesChanged(f.Pos).Named("entity").Do(t =>
            {
                log.Add("entity");
                entityCorrelation = t.Correlation;
            });
            s.OnEvent(f.Other).Named("other").Do(_ => log.Add("other"));
        });

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(5, 0, 0)));

        // entity first; then events in publish order; typed and any rules merged in registration order
        Assert.Equal(new[] { "entity", "any:1", "fired:42:5", "any:2", "other" }, log);
        Assert.Equal(entityCorrelation, eventCorrelation);
        Assert.False(eventCorrelation.IsNone);
    }

    [Fact]
    public void An_aborted_transaction_raises_no_event()
    {
        var f = new EngineFixture();
        int events = 0;
        f.Module("m", s =>
        {
            s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) =>
            {
                ctx.Publish(f.Fired, ctx.RentPayload());
                if (m.N == 0)
                {
                    ctx.Abort("no");
                }
            });
            s.OnAnyEvent().Do(_ => events++);
        });

        f.Engine.Ingest(f.Net, new PingMsg(0));
        Assert.Equal(0, events);
        f.Engine.Ingest(f.Net, new PingMsg(1));
        Assert.Equal(1, events);
    }

    [Fact]
    public void An_event_of_a_message_without_state_changes_is_still_dispatched()
    {
        var f = new EngineFixture();
        int events = 0;
        f.Module("m", s =>
        {
            s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) => ctx.Publish(f.Fired, ctx.RentPayload()));
            s.OnEvent(f.Fired).Do(_ => events++);
        });

        f.Engine.Ingest(f.Net, new PingMsg(1)); // empty change set

        Assert.Equal(1, events);
        Assert.Equal(0, f.Engine.State.Journal.Count);
    }

    [Fact]
    public void An_event_with_no_subscriber_is_counted_per_type()
    {
        var f = new EngineFixture();
        f.Module("m", s =>
        {
            s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) =>
            {
                ctx.Publish(f.Fired, ctx.RentPayload());
                ctx.Publish(f.Other, ctx.RentPayload());
            });
            s.OnEvent(f.Other).Do(_ => { });
        });

        f.Engine.Ingest(f.Net, new PingMsg(1));
        f.Engine.Ingest(f.Net, new PingMsg(2));

        Assert.Equal(2, f.Engine.Diagnostics.EventsWithoutSubscriber);
        Assert.Equal(2, f.Engine.Diagnostics.EventsWithoutSubscriberCount(f.Fired));
        Assert.Equal(0, f.Engine.Diagnostics.EventsWithoutSubscriberCount(f.Other));
        Assert.Equal(4, f.Engine.State.Journal.EventCount); // still logged
    }

    [Fact]
    public void Processor_published_events_cascade_with_their_own_correlation_and_CausedBy()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        CorrelationId firstCorrelation = default;
        f.Module("m", s =>
        {
            s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) => ctx.Publish(f.Fired, ctx.RentPayload().Set(f.Count, EngineFixture.I(7))));
            s.OnEvent(f.Fired).Named("assemble").Do(t =>
            {
                log.Add("assemble");
                firstCorrelation = t.Correlation;
                var bag = f.Engine.RentPayload().Set(f.Count, EngineFixture.I(t.Payload.Get(f.Count).AsInt() + 1));
                f.Engine.Publish(f.Proc, f.Other, bag);
                log.Add("published");
            });
            s.OnEvent(f.Fired).Named("sibling").Do(_ => log.Add("sibling"));
            s.OnEvent(f.Other).Named("derived").Do(t =>
            {
                log.Add("derived:" + t.Payload.Get(f.Count).AsInt());
                Assert.Equal(f.Proc, t.Source);
                Assert.NotEqual(firstCorrelation, t.Correlation);
                Assert.Equal(firstCorrelation, t.Event.CausedBy);
            });
        });

        f.Engine.Ingest(f.Net, new PingMsg(1));

        Assert.Equal(new[] { "assemble", "published", "sibling", "derived:8" }, log);
    }

    [Fact]
    public void A_host_published_event_gets_its_own_correlation_and_no_cause()
    {
        var f = new EngineFixture();
        SimEvent? seen = null;
        f.Module("m", s => s.OnEvent(f.Fired).Do(t => seen = t.Event));
        f.Clock.Now = 3;
        f.Create(); // consumes a correlation id

        var bag = f.Engine.RentPayload().Set(f.Count, EngineFixture.I(1));
        f.Engine.Publish(f.Net, f.Fired, bag);

        Assert.NotNull(seen);
        Assert.False(seen!.Correlation.IsNone);
        Assert.True(seen.CausedBy.IsNone);
        Assert.Equal((f.Net, f.Fired, 3.0), (seen.Source, seen.Type, seen.WallTime));
    }

    [Fact]
    public void An_event_publishing_itself_hits_the_cascade_limit()
    {
        var f = new EngineFixture();
        int dispatched = 0;
        f.Module("m", s => s.OnEvent(f.Fired).Do(_ =>
        {
            dispatched++;
            f.Engine.Publish(f.Proc, f.Fired, f.Engine.RentPayload());
        }));

        f.Engine.Publish(f.Net, f.Fired, f.Engine.RentPayload());

        Assert.Equal(9, dispatched); // depth 0..8
        Assert.Equal(1, f.Engine.Diagnostics.CascadeOverflows);
    }

    [Fact]
    public void The_event_log_keeps_the_last_N_oldest_first_and_recycles()
    {
        var f = new EngineFixture(new GatewayOptions { EventLogCapacity = 4 });
        var first = f.Engine.RentPayload().Set(f.Count, EngineFixture.I(0));
        f.Engine.Publish(f.Net, f.Fired, first);
        for (int i = 1; i < 4; i++)
        {
            f.Engine.Publish(f.Net, f.Fired, f.Engine.RentPayload().Set(f.Count, EngineFixture.I(i)));
        }

        Assert.Equal(4, f.Engine.State.Journal.EventCount);
        Assert.Equal(4, f.Engine.State.Journal.EventCapacity);
        foreach (var oldest in f.Engine.State.Journal.RecentEvents)
        {
            Assert.Same(first, oldest.Payload);
            break;
        }

        f.Engine.Publish(f.Net, f.Fired, f.Engine.RentPayload().Set(f.Count, EngineFixture.I(4)));
        f.Engine.Publish(f.Net, f.Fired, f.Engine.RentPayload().Set(f.Count, EngineFixture.I(5)));

        var counts = new List<long>();
        foreach (var ev in f.Engine.State.Journal.RecentEvents)
        {
            counts.Add(ev.Payload.Get(f.Count).AsInt());
        }

        Assert.Equal(new long[] { 2, 3, 4, 5 }, counts);
        // the evicted events returned their payloads to the pool (cleared)
        var reused = f.Engine.RentPayload();
        Assert.Equal(0, reused.Count);
    }

    [Fact]
    public void Evicted_payloads_are_handed_out_again()
    {
        var f = new EngineFixture(new GatewayOptions { EventLogCapacity = 1 });
        var a = f.Engine.RentPayload();
        f.Engine.Publish(f.Net, f.Fired, a);
        f.Engine.Publish(f.Net, f.Fired, f.Engine.RentPayload()); // evicts the first event

        var again = f.Engine.RentPayload();

        Assert.Same(a, again);
        Assert.Equal(0, again.Count);
    }

    [Fact]
    public void Unpublished_payloads_return_to_the_pool_when_the_message_finishes()
    {
        var f = new EngineFixture();
        AttributeBag? rented = null;
        f.Module("m", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) =>
        {
            rented = ctx.RentPayload().Set(f.Count, EngineFixture.I(1));
        }));

        f.Engine.Ingest(f.Net, new PingMsg(1));
        var again = f.Engine.RentPayload();

        Assert.Same(rented, again);
        Assert.Equal(0, again.Count);
    }

    [Fact]
    public void Payloads_of_discarded_events_return_to_the_pool()
    {
        var f = new EngineFixture();
        AttributeBag? published = null, unpublished = null;
        f.Module("m", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) =>
        {
            published = ctx.RentPayload();
            ctx.Publish(f.Fired, published);
            unpublished = ctx.RentPayload();
            ctx.Abort("no");
        }));

        f.Engine.Ingest(f.Net, new PingMsg(1));
        var a = f.Engine.RentPayload();
        var b = f.Engine.RentPayload();

        Assert.True((ReferenceEquals(a, published) && ReferenceEquals(b, unpublished)) ||
                    (ReferenceEquals(a, unpublished) && ReferenceEquals(b, published)));
        Assert.False(ReferenceEquals(a, b));
    }

    [Fact]
    public void Publishing_a_bag_twice_is_a_programming_error()
    {
        var f = new EngineFixture();
        var bag = f.Engine.RentPayload();
        f.Engine.Publish(f.Net, f.Fired, bag);

        Assert.Throws<InvalidOperationException>(() => f.Engine.Publish(f.Net, f.Fired, bag));
    }

    [Fact]
    public void AttributeBag_overwrites_reports_absent_as_None_and_enumerates()
    {
        var f = new EngineFixture();
        var bag = f.Engine.RentPayload().Set(f.Count, EngineFixture.I(1)).Set(f.Speed, EngineFixture.D(2)).Set(f.Count, EngineFixture.I(3));

        Assert.Equal(2, bag.Count);
        Assert.Equal(3, bag.Get(f.Count).AsInt());
        Assert.True(bag.Get(f.Pos).IsNone);
        Assert.False(bag.TryGet(f.Pos, out _));
        Assert.True(bag.TryGet(f.Speed, out var v));
        Assert.Equal(2.0, v.AsDouble());
        var attrs = new List<AttrId>();
        foreach (var (attr, _) in bag)
        {
            attrs.Add(attr);
        }

        Assert.Equal(new[] { f.Count, f.Speed }, attrs);
    }
}
