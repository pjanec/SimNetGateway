using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class CascadeTests
{
    [Fact]
    public void Queued_records_survive_journal_eviction_until_dispatched()
    {
        // One record with 10 created entities makes a rule commit once per entity: 10 records queued at
        // depth 1 against a journal of 2, so 8 fall out of the journal before they are dispatched.
        var f = new EngineFixture(new GatewayOptions { JournalCapacity = 2 });
        var seen = new List<(EntityId Id, long Count)>();
        f.Module("proc", s =>
        {
            s.OnEntityCreated().Named("number").Do(t =>
            {
                var cs = f.Engine.BeginChange(f.Proc);
                cs.Set(t.Id, f.Count, AttrValue.From((long)t.Id.Value * 10));
                cs.Commit();
            });
            s.OnAttributesChanged(f.Count).Named("observe").Do(t =>
            {
                Assert.True(t.Delta.TryGetChange(f.Count, out var change));
                seen.Add((t.Id, change.New.AsInt()));
            });
        });

        var create = f.Engine.BeginChange(f.Net);
        var ids = new List<EntityId>();
        for (int i = 0; i < 10; i++)
        {
            ids.Add(create.CreateEntity());
        }
        create.Commit();

        Assert.Equal(10, seen.Count);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(ids[i], seen[i].Id);
            Assert.Equal(ids[i].Value * 10L, seen[i].Count);
        }
    }

    [Fact]
    public void Cascades_dispatch_in_the_same_tick_breadth_first_with_CausedBy()
    {
        var f = new EngineFixture();
        var id = f.Create();
        var log = new List<string>();
        CorrelationId firstRecord = default, secondRecord = default;
        f.Module("proc", s =>
        {
            s.OnAttributesChanged(f.Pos).Named("derive").Do(t =>
            {
                log.Add("derive");
                firstRecord = t.Correlation;
                var cs = f.Engine.BeginChange(f.Proc);
                cs.Set(t.Id, f.Speed, EngineFixture.D(t.Entity.Get(f.Pos).AsVec3().X * 2));
                cs.Commit();
                log.Add("derive-committed"); // the cascade is queued, not dispatched inside Commit
            });
            s.OnAttributesChanged(f.Pos).Named("egress").Do(_ => log.Add("egress"));
            s.OnAttributesChanged(f.Speed).Named("speed").Do(t =>
            {
                log.Add("speed:" + t.Entity.Get(f.Speed).AsDouble());
                secondRecord = t.Correlation;
                Assert.Equal(firstRecord, t.ChangeSet.CausedBy);
            });
            s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) => ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos)));
        });
        var channel = new TestChannel<MoveMsg>("in", f.Net);
        f.Engine.AddInbound(channel);
        channel.Enqueue(new MoveMsg(id, new Vec3(3, 0, 0)));

        f.Engine.Tick();

        Assert.Equal(new[] { "derive", "derive-committed", "egress", "speed:6" }, log);
        Assert.NotEqual(firstRecord, secondRecord);
        Assert.True(f.Engine.State.Journal.TryGet(secondRecord, out var record));
        Assert.Equal(firstRecord, record.CausedBy);
        Assert.Equal(f.Proc, record.Source);
        Assert.True(f.Engine.State.Journal.TryGet(firstRecord, out var first));
        Assert.True(first.CausedBy.IsNone);
    }

    [Fact]
    public void A_self_retriggering_rule_hits_the_cascade_limit_without_corrupting_state()
    {
        var f = new EngineFixture(); // MaxCascadeDepth = 8
        var id = f.Create();
        var results = new List<CommitStatus>();
        int observer = 0;
        f.Module("loop", s =>
        {
            s.OnAttributesChanged(f.Count).Named("inc").Do(t =>
            {
                var cs = f.Engine.BeginChange(f.Proc);
                cs.Set(t.Id, f.Count, EngineFixture.I(t.Entity.Get(f.Count).AsInt() + 1));
                results.Add(cs.Commit().Status);
            });
            s.OnAttributesChanged(f.Count).Named("watch").Do(_ => observer++);
        });

        var first = f.Write(id, f.Count, EngineFixture.I(1));

        Assert.Equal(CommitStatus.Committed, first.Status);
        // depth 0 (host) + depths 1..8 were applied; the commit for depth 9 was refused before it was applied
        Assert.Equal(9, f.Engine.State.Find(id)!.Get(f.Count).AsInt());
        Assert.Equal(9, observer);
        Assert.Equal(8, results.Count(r => r == CommitStatus.Committed));
        Assert.Equal(new[] { CommitStatus.Rejected }, results.Where(r => r == CommitStatus.Rejected).ToArray());
        Assert.Equal(1, f.Engine.Diagnostics.CascadeOverflows);
        Assert.Equal(0, f.Engine.Diagnostics.TransactionFailures); // diagnosed once, as an overflow
        var entry = EngineFixture.Entries(f.Engine.Diagnostics).Single();
        Assert.Equal("Cascade", entry.Category);
        Assert.Equal("proc", entry.Origin);
        Assert.False(f.Engine.State.IsChangeOpen);

        // the engine still works afterwards
        f.Write(id, f.Count, EngineFixture.I(100));
        Assert.Equal(100 + 8, f.Engine.State.Find(id)!.Get(f.Count).AsInt());
    }

    [Fact]
    public void Ingress_work_is_depth_zero_and_timer_work_is_depth_one()
    {
        var f = new EngineFixture(new GatewayOptions { MaxCascadeDepth = 1 });
        var id = f.Create();
        var results = new List<string>();
        f.Module("m", s =>
        {
            s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) => ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos)));
            s.OnAttributesChanged(f.Pos).Named("level1").Do(t =>
            {
                var cs = f.Engine.BeginChange(f.Proc);
                cs.Set(t.Id, f.Speed, EngineFixture.D(1));
                results.Add("pos->speed:" + cs.Commit().Status);
            });
            s.OnAttributesChanged(f.Speed).Named("level2").Do(t =>
            {
                var cs = f.Engine.BeginChange(f.Proc);
                cs.Set(t.Id, f.Count, EngineFixture.I(1));
                results.Add("speed->count:" + cs.Commit().Status);
            });
            s.Every(TimeSpan.FromSeconds(1)).Do(_ =>
            {
                var cs = f.Engine.BeginChange(f.Proc);
                cs.Set(id, f.Label, AttrValue.From("t"));
                results.Add("timer->label:" + cs.Commit().Status);
            });
            s.OnAttributesChanged(f.Label).Do(t =>
            {
                var cs = f.Engine.BeginChange(f.Proc);
                cs.Set(t.Id, f.Count, EngineFixture.I(2));
                results.Add("label->count:" + cs.Commit().Status);
            });
        });

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(1, 0, 0)));
        Assert.Equal(new[] { "pos->speed:Committed", "speed->count:Rejected" }, results);

        results.Clear();
        f.Clock.Now = 1;
        f.Engine.Tick();
        // the timer firing is depth 0, its write depth 1 (allowed with a limit of 1), the write caused by that depth 2
        Assert.Equal(new[] { "timer->label:Committed", "label->count:Rejected" }, results);
    }

    [Fact]
    public void A_host_commit_is_depth_zero_and_drains_inside_the_call()
    {
        var f = new EngineFixture();
        var id = f.Create();
        int fired = 0;
        f.Module("m", s => s.OnAttributesChanged(f.Pos).Do(_ => fired++));

        var cs = f.Engine.BeginChange(f.Net);
        cs.Set(id, f.Pos, EngineFixture.V(1));
        cs.Commit();

        Assert.Equal(1, fired);
    }

    [Fact]
    public void A_rejected_commit_in_a_rule_is_diagnosed_and_raises_no_trigger()
    {
        var f = new EngineFixture();
        var id = f.Create();
        int fired = 0;
        f.Module("m", s =>
        {
            s.OnAttributesChanged(f.Pos).Do(t =>
            {
                var cs = f.Engine.BeginChange(f.Proc);
                cs.Set(new EntityId(555), f.Speed, EngineFixture.D(1));
                Assert.Equal(CommitStatus.Rejected, cs.Commit().Status);
            });
            s.OnAttributesChanged(f.Speed).Do(_ => fired++);
        });

        f.Write(id, f.Pos, EngineFixture.V(1));

        Assert.Equal(0, fired);
        Assert.Equal(1, f.Engine.Diagnostics.TransactionFailures);
        Assert.Equal("proc", EngineFixture.Entries(f.Engine.Diagnostics).Last().Origin);
    }

    [Fact]
    public void A_rule_that_stays_idle_never_overflows_with_empty_commits()
    {
        var f = new EngineFixture(new GatewayOptions { MaxCascadeDepth = 0 });
        var id = f.Create();
        f.Module("m", s => s.OnAttributesChanged(f.Pos).Do(t =>
        {
            var cs = f.Engine.BeginChange(f.Proc);
            Assert.Equal(CommitStatus.Empty, cs.Commit().Status);
        }));

        f.Write(id, f.Pos, EngineFixture.V(1));

        Assert.Equal(0, f.Engine.Diagnostics.CascadeOverflows);
    }
}
