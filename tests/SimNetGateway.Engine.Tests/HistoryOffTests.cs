using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

/// <summary>
/// R-070: <c>JournalCapacity = 0</c> / <c>EventLogCapacity = 0</c> switch the history off. Behaviour must be
/// identical, records and events are recycled right after dispatch (never while queued), and queries find nothing.
/// </summary>
public class HistoryOffTests
{
    private static GatewayOptions Options(string config) => config switch
    {
        "journal0" => new GatewayOptions { JournalCapacity = 0 },
        "events0" => new GatewayOptions { EventLogCapacity = 0 },
        "both0" => new GatewayOptions { JournalCapacity = 0, EventLogCapacity = 0 },
        _ => new GatewayOptions(),
    };

    // ---- behaviour is unchanged ---------------------------------------------------------------------

    private static string Describe(ChangeSetRecord r)
    {
        var parts = new List<string>();
        for (int i = 0; i < r.Entities.Count; i++)
        {
            parts.Add(r.Entities[i].Id.Value + "/" + r.Entities[i].Changes.Count);
        }

        return r.Count + "[" + string.Join(",", parts) + "]";
    }

    /// <summary>One engine with a rule of every trigger kind, a cascade, an event cascade and a failing rule.</summary>
    internal sealed class World
    {
        public readonly EngineFixture F;
        public readonly List<string> Log = new();
        public readonly List<EntityId> Tracked = new();

        public World(GatewayOptions options)
        {
            F = new EngineFixture(options);
            var f = F;
            var log = Log;
            f.Module("rich", s =>
            {
                s.OnEntityCreated().Named("created").Do(t => log.Add($"created:{t.Id.Value}:{t.Source.Value}:{t.Correlation.Value}"));
                s.OnEntityRemoved().Named("removed").Do(t => log.Add($"removed:{t.Id.Value}"));
                s.OnAttributesWritten().Named("written").Do(t =>
                    log.Add($"written:{t.Kind}:{t.Id.Value}:{t.Delta.Changes.Count}:{t.Correlation.Value}:{t.ChangeSet.CausedBy.Value}"));
                s.OnAttributesChanged(f.Count).Named("cascade").Do(t =>
                {
                    Assert.True(t.Delta.TryGetChange(f.Count, out var c));
                    log.Add($"count:{t.Id.Value}:{c.New.AsInt()}");
                    long v = c.New.AsInt();
                    if (v == 99)
                    {
                        throw new InvalidOperationException("boom");
                    }

                    if ((v >= 1 && v < 5) || v >= 300)
                    {
                        // 1..4 is a short chain; 300 is unbounded and overflows the cascade depth
                        var cs = f.Engine.BeginChange(f.Proc);
                        cs.Set(t.Id, f.Count, AttrValue.From(v + 1));
                        cs.Commit();
                    }

                    if (v == 200)
                    {
                        var cs = f.Engine.BeginChange(f.Proc);
                        cs.Set(t.Id, f.Count, AttrValue.From(200L)); // no-op write: empty commit
                        Assert.Equal(CommitStatus.Empty, cs.Commit().Status);
                    }
                });
                s.OnAttributesChanged(f.Speed).Named("speed-event").Do(t =>
                {
                    Assert.True(t.Delta.TryGetChange(f.Speed, out var c));
                    f.Engine.Publish(f.Proc, f.Fired, f.Engine.RentPayload().Set(f.Speed, c.New));
                });
                s.OnEvent(f.Fired).Named("fired").Do(t =>
                {
                    log.Add($"fired:{t.Source.Value}:{t.Payload.Count}:{t.Correlation.Value}");
                    if (t.Payload.Count > 0)
                    {
                        f.Engine.Publish(f.Proc, f.Other, f.Engine.RentPayload().Set(f.Count, AttrValue.From(7L)));
                    }
                });
                s.OnAnyEvent().Named("any").Do(t => log.Add($"any:{t.Type.Value}:{t.Payload.Count}"));
                s.OnChangeSetEnd().Named("end").Do(t =>
                    log.Add($"end:{t.ChangeSet.Correlation.Value}:{t.ChangeSet.CausedBy.Value}:{Describe(t.ChangeSet)}"));
                s.OnSessionChanged().Named("session").Do(t => log.Add($"session:{t.Command.Kind}:{t.Previous}->{t.Current}"));
                s.Every(TimeSpan.FromSeconds(1)).Named("timer").Do(t => log.Add($"timer:{t.WallTime}"));
                s.OnTickEnd().Named("tickend").Do(t => log.Add("tickend"));
                s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
                {
                    ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos));
                    ctx.Publish(f.Fired, ctx.RentPayload().Set(f.Count, AttrValue.From(1L)));
                    if (m.Pos.X < 0)
                    {
                        ctx.Abort("negative");
                    }

                    if (m.Pos.Y > 100)
                    {
                        ctx.Command(SessionCommand.Load("m"));
                        ctx.Command(SessionCommand.Start);
                    }
                });
            });
        }

        public EntityId Create()
        {
            var id = F.Create();
            Tracked.Add(id);
            return id;
        }

        public string Dump()
        {
            var sb = new List<string>(Log);
            foreach (var id in Tracked)
            {
                var e = F.Engine.State.Find(id);
                sb.Add(e is null ? $"state:{id.Value}:gone"
                    : $"state:{id.Value}:{(e.Has(F.Pos) ? e.Get(F.Pos).AsVec3().X : -1)}:{(e.Has(F.Speed) ? e.Get(F.Speed).AsDouble() : -1)}:{(e.Has(F.Count) ? e.Get(F.Count).AsInt() : -1)}");
            }

            var d = F.Engine.Diagnostics;
            sb.Add($"diag:{d.MessagesProcessed}:{d.CascadeOverflows}:{d.SessionRejections}:{F.Engine.Session.State}");
            return string.Join("\n", sb);
        }
    }

    private static void Write(World w, EntityId id, AttrId a, AttrValue v)
    {
        var cs = w.F.Engine.BeginChange(w.F.Net);
        cs.Set(id, a, v);
        cs.Commit();
    }

    internal static readonly (string Name, Action<World> Run)[] Scenarios =
    {
        ("create-and-write", w => { var a = w.Create(); Write(w, a, w.F.Pos, EngineFixture.V(3)); }),
        ("multi-attribute-one-trigger", w =>
        {
            var a = w.Create();
            var cs = w.F.Engine.BeginChange(w.F.Net);
            cs.Set(a, w.F.Pos, EngineFixture.V(1));
            cs.Set(a, w.F.Count, EngineFixture.I(50));
            cs.Commit();
        }),
        ("several-entities-one-commit", w =>
        {
            var cs = w.F.Engine.BeginChange(w.F.Net);
            for (int i = 0; i < 5; i++) { w.Tracked.Add(cs.CreateEntity()); }
            cs.Commit();
        }),
        ("cascade-chain", w => { var a = w.Create(); Write(w, a, w.F.Count, EngineFixture.I(1)); }),
        ("cascade-depth-overflow", w => { var a = w.Create(); Write(w, a, w.F.Count, EngineFixture.I(300)); }),
        ("fan-out-queues-many-records", w =>
        {
            var ids = new List<EntityId>();
            for (int i = 0; i < 20; i++) { ids.Add(w.Create()); }
            var cs = w.F.Engine.BeginChange(w.F.Net);
            foreach (var id in ids) { cs.Set(id, w.F.Count, EngineFixture.I(3)); }
            cs.Commit();
        }),
        ("destroy", w =>
        {
            var a = w.Create();
            var b = w.Create();
            Write(w, b, w.F.Pos, EngineFixture.V(2));
            var cs = w.F.Engine.BeginChange(w.F.Net);
            cs.DestroyEntity(a);
            cs.Commit();
        }),
        ("failing-rule", w => { var a = w.Create(); Write(w, a, w.F.Count, EngineFixture.I(99)); }),
        ("empty-commit", w => { var a = w.Create(); Write(w, a, w.F.Count, EngineFixture.I(200)); Write(w, a, w.F.Count, EngineFixture.I(200)); }),
        ("clear-attribute", w =>
        {
            var a = w.Create();
            Write(w, a, w.F.Pos, EngineFixture.V(4));
            var cs = w.F.Engine.BeginChange(w.F.Net);
            cs.Clear(a, w.F.Pos);
            cs.Commit();
        }),
        ("transfer-owner", w =>
        {
            var a = w.Create();
            var cs = w.F.Engine.BeginChange(w.F.Net);
            cs.TransferOwner(a, w.F.Proc);
            cs.Commit();
        }),
        ("event-from-rule-and-event-cascade", w => { var a = w.Create(); Write(w, a, w.F.Speed, EngineFixture.D(9)); }),
        ("direct-publish-and-pool-reuse", w =>
        {
            for (int i = 0; i < 6; i++)
            {
                w.F.Engine.Publish(w.F.Net, w.F.Fired, i % 2 == 0 ? w.F.Engine.RentPayload().Set(w.F.Count, EngineFixture.I(i)) : w.F.Engine.RentPayload());
            }
        }),
        ("ingress-message", w =>
        {
            var a = w.Create();
            w.F.Engine.Ingest(w.F.Net, new MoveMsg(a, new Vec3(1, 2, 3)));
            w.F.Engine.Ingest(w.F.Net, new MoveMsg(a, new Vec3(4, 5, 6)));
        }),
        ("ingress-abort", w =>
        {
            var a = w.Create();
            w.F.Engine.Ingest(w.F.Net, new MoveMsg(a, new Vec3(-1, 0, 0)));
            w.F.Engine.Ingest(w.F.Net, new MoveMsg(a, new Vec3(2, 0, 0)));
        }),
        ("ingress-with-session-commands", w =>
        {
            var a = w.Create();
            w.F.Engine.Ingest(w.F.Net, new MoveMsg(a, new Vec3(1, 200, 0)));
        }),
        ("ingress-unknown-message", w => { w.Create(); w.F.Engine.Ingest(w.F.Net, new PingMsg(1)); }),
        ("session-lifecycle-and-unload-destroys", w =>
        {
            w.Create();
            w.Create();
            var e = w.F.Engine;
            e.Execute(SessionCommand.Load("s"), w.F.Net);
            e.Execute(SessionCommand.Start, w.F.Net);
            e.Execute(SessionCommand.Start, w.F.Net); // rejected
            e.Execute(SessionCommand.Pause, w.F.Net);
            e.Execute(SessionCommand.Resume, w.F.Net);
            e.Execute(SessionCommand.Unload, w.F.Net);
        }),
        ("timers-and-tick-end", w =>
        {
            var a = w.Create();
            for (int i = 1; i <= 4; i++)
            {
                w.F.Clock.Now = i * 0.75;
                w.F.Engine.Tick();
                Write(w, a, w.F.Pos, EngineFixture.V(i));
            }
        }),
        ("channel-and-tick", w =>
        {
            var a = w.Create();
            var ch = new TestChannel<MoveMsg>("in", w.F.Net);
            w.F.Engine.AddInbound(ch);
            for (int i = 0; i < 12; i++) { ch.Enqueue(new MoveMsg(a, new Vec3(i, 0, 0))); }
            w.F.Clock.Now = 2;
            w.F.Engine.Tick();
        }),
    };

    public static IEnumerable<object[]> ScenarioNames()
    {
        foreach (var s in Scenarios)
        {
            yield return new object[] { s.Name };
        }
    }

    [Fact]
    public void There_are_at_least_fifteen_scenarios() => Assert.True(Scenarios.Length >= 15);

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Every_scenario_gives_identical_triggers_and_state_with_the_history_off(string name)
    {
        var run = Scenarios.Single(s => s.Name == name).Run;
        var baseline = new World(new GatewayOptions());
        run(baseline);
        string expected = baseline.Dump();
        Assert.True(baseline.Log.Count > 0, "scenario produced no triggers");

        foreach (var config in new[] { "journal0", "events0", "both0" })
        {
            var w = new World(Options(config));
            run(w);
            Assert.Equal(expected, w.Dump());
        }
    }

    // ---- recycling ----------------------------------------------------------------------------------

    [Fact]
    public void A_record_is_recycled_after_dispatch_so_the_next_commit_rents_the_same_instance()
    {
        var f = new EngineFixture(new GatewayOptions { JournalCapacity = 0 });
        var id = f.Create();
        var first = f.Write(id, f.Count, EngineFixture.I(1)).Record;
        var second = f.Write(id, f.Count, EngineFixture.I(2)).Record;
        var third = f.Write(id, f.Count, EngineFixture.I(3)).Record;

        Assert.Same(first, second);
        Assert.Same(second, third);
    }

    [Fact]
    public void Records_are_not_leaked_when_the_journal_is_off()
    {
        var f = new EngineFixture(new GatewayOptions { JournalCapacity = 0 });
        var id = f.Create();
        var distinct = new HashSet<ChangeSetRecord>(ReferenceEqualityComparer.Instance);
        for (int i = 1; i <= 500; i++)
        {
            distinct.Add(f.Write(id, f.Count, EngineFixture.I(i)).Record!);
        }

        Assert.True(distinct.Count <= 2, "distinct records: " + distinct.Count);
    }

    [Fact]
    public void With_the_default_journal_records_are_not_reused_while_retained()
    {
        var f = new EngineFixture();
        var id = f.Create();
        var a = f.Write(id, f.Count, EngineFixture.I(1)).Record;
        var b = f.Write(id, f.Count, EngineFixture.I(2)).Record;

        Assert.NotSame(a, b);
        Assert.Equal(1, a!.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Queued_records_survive_until_dispatched(int journalCapacity)
    {
        var f = new EngineFixture(new GatewayOptions { JournalCapacity = journalCapacity });
        var seen = new List<(EntityId Id, long Count, int DeltaChanges)>();
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
                Assert.Equal(1, t.ChangeSet.Count);
                seen.Add((t.Id, change.New.AsInt(), t.Delta.Changes.Count));
            });
        });

        var create = f.Engine.BeginChange(f.Net);
        var ids = new List<EntityId>();
        for (int i = 0; i < 25; i++)
        {
            ids.Add(create.CreateEntity());
        }

        create.Commit();

        Assert.Equal(25, seen.Count);
        for (int i = 0; i < 25; i++)
        {
            Assert.Equal((ids[i], ids[i].Value * 10L, 1), seen[i]);
        }

        // and the pool is intact afterwards: later commits still work
        Assert.True(f.Write(ids[0], f.Pos, EngineFixture.V(1)).Succeeded);
    }

    [Fact]
    public void Event_payloads_return_to_the_pool_right_after_dispatch_when_the_log_is_off()
    {
        var f = new EngineFixture(new GatewayOptions { EventLogCapacity = 0 });
        int dispatched = 0;
        f.Module("m", s => s.OnEvent(f.Fired).Do(t => { dispatched++; Assert.Equal(1, t.Payload.Count); }));
        var bag = f.Engine.RentPayload().Set(f.Count, EngineFixture.I(1));
        f.Engine.Publish(f.Net, f.Fired, bag);

        Assert.Equal(1, dispatched);
        Assert.Equal(0, f.Engine.State.Journal.EventCount);
        Assert.Equal(0, f.Engine.State.Journal.EventCapacity);
        foreach (var _ in f.Engine.State.Journal.RecentEvents) { Assert.Fail("event log must be empty"); }

        var again = f.Engine.RentPayload();
        Assert.Same(bag, again);
        Assert.Equal(0, again.Count);
    }

    [Fact]
    public void Ingress_event_payloads_are_recycled_when_the_log_is_off()
    {
        var f = new EngineFixture(new GatewayOptions { EventLogCapacity = 0 });
        var rented = new List<AttributeBag>();
        f.Module("m", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) =>
        {
            var bag = ctx.RentPayload();
            rented.Add(bag);
            ctx.Publish(f.Fired, bag.Set(f.Count, AttrValue.From((long)m.N)));
        }));
        f.Engine.Ingest(f.Net, new PingMsg(1));
        f.Engine.Ingest(f.Net, new PingMsg(2));
        f.Engine.Ingest(f.Net, new PingMsg(3));

        Assert.All(rented, b => Assert.Same(rented[0], b));
        Assert.Equal(0, f.Engine.State.Journal.EventCount);
    }

    [Fact]
    public void An_event_is_not_recycled_while_it_is_queued()
    {
        // A rule publishes 10 events during one dispatch; with the log off all must still be seen intact.
        var f = new EngineFixture(new GatewayOptions { EventLogCapacity = 0 });
        var seen = new List<long>();
        f.Module("m", s =>
        {
            s.OnEvent(f.Other).Do(_ =>
            {
                for (int i = 0; i < 10; i++)
                {
                    f.Engine.Publish(f.Proc, f.Fired, f.Engine.RentPayload().Set(f.Count, AttrValue.From((long)i)));
                }
            });
            s.OnEvent(f.Fired).Do(t => seen.Add(t.Payload.Get(f.Count).AsInt()));
        });
        f.Engine.Publish(f.Net, f.Other, f.Engine.RentPayload());

        Assert.Equal(Enumerable.Range(0, 10).Select(i => (long)i), seen);
    }

    // ---- queries ------------------------------------------------------------------------------------

    private static int CountWrites(ChangeJournal j, CorrelationId corr)
    {
        int n = 0;
        foreach (var _ in j.GetWrites(corr)) { n++; }
        return n;
    }

    [Fact]
    public void Journal_queries_find_nothing_when_the_journal_is_off()
    {
        var f = new EngineFixture(new GatewayOptions { JournalCapacity = 0 });
        var corr = default(CorrelationId);
        f.Module("m", s => s.OnChangeSetEnd().Do(t => corr = t.ChangeSet.Correlation)); // the record is recycled after dispatch
        var id = f.Create();
        f.Write(id, f.Count, EngineFixture.I(1));
        var j = f.Engine.State.Journal;

        Assert.False(corr.IsNone);
        Assert.Equal(0, j.Count);
        Assert.Equal(0, j.Capacity);
        Assert.False(j.TryGet(corr, out _));
        Assert.Equal(0, CountWrites(j, corr));
        foreach (var _ in j.Since(0)) { Assert.Fail("no records"); }
    }

    [Fact]
    public void Journal_queries_find_the_record_with_the_default_capacity()
    {
        var f = new EngineFixture();
        var id = f.Create();
        var corr = f.Write(id, f.Count, EngineFixture.I(1)).Record!.Correlation;
        var j = f.Engine.State.Journal;

        Assert.True(j.TryGet(corr, out _));
        Assert.Equal(2, j.Count);
        Assert.Equal(1, CountWrites(j, corr));
    }

    [Fact]
    public void A_standalone_record_is_valid_until_the_next_commit_when_the_journal_is_off()
    {
        var f = new Fixture(journalCapacity: 0);
        var a = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(a, f.Count, AttrValue.From(5L));
        var r1 = cs.Commit().Record!;

        // still readable after the commit returned
        Assert.Single(r1.Entities);
        Assert.Equal(a, r1.Entities[0].Id);
        Assert.True(r1.Entities[0].Changes.Count == 1);
        Assert.Equal(0, f.Repo.Journal.Count);
        Assert.False(f.Repo.Journal.TryGet(r1.Correlation, out _));

        cs = f.Begin(f.A);
        cs.Set(a, f.Count, AttrValue.From(6L));
        var r2 = cs.Commit().Record!;
        Assert.NotSame(r1, r2);

        cs = f.Begin(f.A);
        cs.Set(a, f.Count, AttrValue.From(7L));
        var r3 = cs.Commit().Record!;
        Assert.Same(r1, r3); // recycled at the next commit: a bounded pool, no leak
        Assert.Equal(7, f.Repo.Find(a)!.Get(f.Count).AsInt());
    }

    [Fact]
    public void A_standalone_repository_does_not_leak_records_when_the_journal_is_off()
    {
        var f = new Fixture(journalCapacity: 0);
        var a = f.Create(f.A);
        var distinct = new HashSet<ChangeSetRecord>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < 300; i++)
        {
            var cs = f.Begin(f.A);
            cs.Set(a, f.Count, AttrValue.From((long)i));
            distinct.Add(cs.Commit().Record!);
        }

        Assert.True(distinct.Count <= 3, "distinct: " + distinct.Count);
    }

    // ---- allocation ---------------------------------------------------------------------------------

    private const int Entities = 100;
    private const int Warmup = 3000;
    private const int Measured = 1000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_message_round_trip_allocates_nothing_in_steady_state_with_the_history_off(bool withEvents)
    {
        var f = new EngineFixture(new GatewayOptions { JournalCapacity = 0, EventLogCapacity = 0, MaxMessagesPerTick = 100 });
        f.Module("ingress", s => s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
        {
            ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos));
            if (withEvents)
            {
                ctx.Publish(f.Fired, ctx.RentPayload().Set(f.Speed, AttrValue.From(m.Pos.X)).Set(f.Count, AttrValue.From(1L)));
            }
        }));
        f.Module("egress", s =>
        {
            s.OnAttributesChanged(f.Pos).When(static _ => true).Do(static _ => { });
            s.OnChangeSetEnd().Do(static _ => { });
            s.OnEvent(f.Fired).Do(static _ => { });
            s.OnAnyEvent().Do(static _ => { });
            s.OnTickEnd().Do(static _ => { });
        });
        var ids = new EntityId[Entities];
        var cs = f.Engine.BeginChange(f.Net);
        for (int i = 0; i < Entities; i++) { ids[i] = cs.CreateEntity(); }
        Assert.True(cs.Commit().Succeeded);

        for (int i = 0; i < Warmup; i++)
        {
            f.Engine.Ingest(f.Net, new MoveMsg(ids[i % Entities], new Vec3(i, i + 1, i + 2)));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = Warmup; i < Warmup + Measured; i++)
        {
            f.Engine.Ingest(f.Net, new MoveMsg(ids[i % Entities], new Vec3(i, i + 1, i + 2)));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(Warmup + Measured, f.Engine.Diagnostics.MessagesProcessed);
        Assert.Equal(0, f.Engine.State.Journal.Count);
        Assert.Equal(0, f.Engine.State.Journal.EventCount);
    }

    [Fact]
    public void The_defaults_are_unchanged()
    {
        var o = new GatewayOptions();
        Assert.Equal(1024, o.JournalCapacity);
        Assert.Equal(256, o.EventLogCapacity);
    }
}
