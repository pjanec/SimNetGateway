using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

/// <summary>
/// R-068: per entity the time of the last write by its owner, the third recency list and the filters
/// <c>OriginWrittenWithin</c> / <c>NotOriginWrittenWithin</c>.
/// </summary>
public class OriginWriteTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private sealed class Counter
    {
        public int Calls;
    }

    private static int[] WriteOrder(StateRepository repo)
    {
        var order = new List<int>();
        foreach (var e in repo.ByLastWrite)
        {
            order.Add(e.Id.Value);
        }

        return order.ToArray();
    }

    private static List<int> OriginOrder(StateRepository repo)
    {
        var order = new List<int>();
        foreach (var e in repo.ByLastOriginWrite)
        {
            order.Add(e.Id.Value);
        }

        return order;
    }

    [Fact]
    public void A_created_entity_starts_with_its_creation_times_whoever_created_it()
    {
        var f = new QueryFixture();
        var proc = f.Sources.Register("proc");
        var e = f.Create(7, proc); // the creator is the owner (Owner defaults to Origin)
        var rec = f.Repo.Find(e)!;
        Assert.Equal(7, rec.LastOriginWriteWallTime);
        Assert.Equal(7, rec.LastOriginWriteSimTime);

        // created by A, then handed to B inside the creating transaction: still the creation times, not stale at birth
        var moved = default(EntityId);
        f.At(9, 4.5, f.A, cs =>
        {
            moved = cs.CreateEntity();
            cs.TransferOwner(moved, f.B);
        });
        Assert.Equal(9, f.Repo.Find(moved)!.LastOriginWriteWallTime);
        Assert.Equal(4.5, f.Repo.Find(moved)!.LastOriginWriteSimTime);
    }

    [Fact]
    public void Set_and_Clear_by_the_owner_refresh_the_origin_time_with_wall_and_sim_time_kept_apart()
    {
        var f = new QueryFixture();
        var e = f.Create(1, f.A, (cs, id) => cs.Set(id, f.Count, QueryFixture.I(1)));
        f.At(10, 5, f.A, cs => cs.Set(e, f.Count, QueryFixture.I(1))); // unchanged value, still a write
        var rec = f.Repo.Find(e)!;
        Assert.Equal(10, rec.LastOriginWriteWallTime);
        Assert.Equal(5, rec.LastOriginWriteSimTime);

        f.At(20, 8, f.A, cs => cs.Clear(e, f.Count));
        Assert.Equal(20, rec.LastOriginWriteWallTime);
        Assert.Equal(8, rec.LastOriginWriteSimTime);

        // a Clear of an absent attribute writes nothing
        f.At(30, 9, f.A, cs => cs.Clear(e, f.Count));
        Assert.Equal(20, rec.LastOriginWriteWallTime);
    }

    [Fact]
    public void Writes_by_a_source_other_than_the_owner_do_not_refresh_the_origin_time()
    {
        var f = new QueryFixture();
        var proc = f.Sources.Register("proc");
        var e = f.Create(1, f.A, (cs, id) => cs.Set(id, f.Count, QueryFixture.I(1)));
        f.At(50, 25, proc, cs =>
        {
            cs.Set(e, f.Speed, QueryFixture.D(3));
            cs.Set(e, f.Count, QueryFixture.I(2));
            cs.Clear(e, f.Speed);
        });
        f.At(60, f.B, cs => cs.Set(e, f.Count, QueryFixture.I(3))); // another side, not the owner either

        var rec = f.Repo.Find(e)!;
        Assert.Equal(60, rec.LastWriteWallTime);      // the ordinary write time moved
        Assert.Equal(1, rec.LastOriginWriteWallTime); // the origin time did not
        Assert.Equal(1, rec.LastOriginWriteSimTime);
        Assert.Equal(new[] { e.Value }, QueryFixture.Ids(f.Repo.Query().NotOriginWrittenWithin(S(10))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().NotWrittenWithin(S(10))));
    }

    [Fact]
    public void A_transfer_to_the_current_owner_and_an_empty_transaction_write_nothing()
    {
        var f = new QueryFixture();
        var e = f.Create(1, f.A);
        f.At(10, f.A, cs => cs.TransferOwner(e, f.A)); // no-op
        f.At(20, f.A, _ => { });
        Assert.Equal(1, f.Repo.Find(e)!.LastOriginWriteWallTime);
    }

    [Fact]
    public void After_a_transfer_the_new_owner_refreshes_and_the_old_origin_does_not()
    {
        var f = new QueryFixture();
        var e = f.Create(1, f.A, (cs, id) => cs.Set(id, f.Count, QueryFixture.I(1)));
        var rec = f.Repo.Find(e)!;

        f.At(10, f.A, cs => cs.TransferOwner(e, f.B)); // by the owner before the transaction: counts
        Assert.Equal(f.B, rec.Owner);
        Assert.Equal(f.A, rec.Origin);
        Assert.Equal(10, rec.LastOriginWriteWallTime);

        f.At(20, f.A, cs => cs.Set(e, f.Count, QueryFixture.I(2))); // the old origin is no longer the owner
        Assert.Equal(10, rec.LastOriginWriteWallTime);

        f.At(30, f.B, cs => cs.Set(e, f.Count, QueryFixture.I(3))); // the new owner
        Assert.Equal(30, rec.LastOriginWriteWallTime);

        // FromOrigin(A) is the old origin; staleness of its entities is judged by the owner's writes
        Assert.Equal(QueryFixture.Ids(e), QueryFixture.Ids(f.Repo.Query().FromOrigin(f.A).OriginWrittenWithin(S(1))));
    }

    [Fact]
    public void A_transfer_is_judged_by_the_owner_before_the_transaction_not_the_new_owner()
    {
        var f = new QueryFixture();
        var e = f.Create(1, f.A);
        var rec = f.Repo.Find(e)!;

        // B takes the entity: B is not the owner before the transaction, so this is not an origin write
        f.At(10, f.B, cs => cs.TransferOwner(e, f.B));
        Assert.Equal(f.B, rec.Owner);
        Assert.Equal(1, rec.LastOriginWriteWallTime);

        // a Set after a transfer inside one transaction is judged the same way: by B before it, so A's set does not count
        f.At(20, f.B, cs =>
        {
            cs.TransferOwner(e, f.A);
            cs.Set(e, f.Count, QueryFixture.I(1)); // written by B, who owned it before the transaction
        });
        Assert.Equal(20, rec.LastOriginWriteWallTime);
        f.At(30, f.A, cs =>
        {
            cs.TransferOwner(e, f.B);
            cs.Set(e, f.Count, QueryFixture.I(2)); // A owned it before the transaction
        });
        Assert.Equal(30, rec.LastOriginWriteWallTime);

        // B owns it now; A takes it and sets a value in one transaction: A was not the owner before, so nothing counts
        // (the Set is judged by the owner before the transaction, as the write-authority check is)
        f.At(40, f.A, cs =>
        {
            cs.TransferOwner(e, f.A);
            cs.Set(e, f.Count, QueryFixture.I(3));
        });
        Assert.Equal(f.A, rec.Owner);
        Assert.Equal(30, rec.LastOriginWriteWallTime);
        f.At(50, f.B, cs =>
        {
            cs.TransferOwner(e, f.B);
            cs.Clear(e, f.Count);
        });
        Assert.Equal(30, rec.LastOriginWriteWallTime);
    }

    [Fact]
    public void The_origin_list_follows_origin_writes_only_and_forgets_destroyed_entities()
    {
        var f = new QueryFixture();
        var proc = f.Sources.Register("proc");
        var e1 = f.Create(1, f.A);
        var e2 = f.Create(2, f.A);
        var e3 = f.Create(3, f.B);
        Assert.Equal(new[] { e1.Value, e2.Value, e3.Value }, OriginOrder(f.Repo));

        f.At(10, f.A, cs => cs.Set(e1, f.Count, QueryFixture.I(1)));
        Assert.Equal(new[] { e2.Value, e3.Value, e1.Value }, OriginOrder(f.Repo));

        f.At(11, proc, cs => cs.Set(e2, f.Count, QueryFixture.I(1))); // a processor write leaves the position alone
        Assert.Equal(new[] { e2.Value, e3.Value, e1.Value }, OriginOrder(f.Repo));
        Assert.Equal(new[] { e3.Value, e1.Value, e2.Value }, WriteOrder(f.Repo));

        f.At(12, f.B, cs => cs.Set(e3, f.Count, QueryFixture.I(1)));
        Assert.Equal(new[] { e2.Value, e1.Value, e3.Value }, OriginOrder(f.Repo));

        f.At(13, proc, cs => cs.DestroyEntity(e1));
        Assert.Equal(new[] { e2.Value, e3.Value }, OriginOrder(f.Repo));
        f.At(14, proc, cs => cs.DestroyEntity(e2));
        f.At(15, proc, cs => cs.DestroyEntity(e3));
        Assert.Empty(OriginOrder(f.Repo));
        Assert.Equal(0, f.Repo.Query().OriginWrittenWithin(S(1000)).Count());

        // the list works again after it was emptied
        var e4 = f.Create(20, f.A);
        Assert.Equal(new[] { e4.Value }, OriginOrder(f.Repo));
    }

    private static (QueryFixture F, EntityId E1, EntityId E2, EntityId E3) Three()
    {
        var f = new QueryFixture();
        var e1 = f.Create(10, f.A, (cs, id) => cs.Set(id, f.Count, QueryFixture.I(1)));
        var e2 = f.Create(20, f.B, (cs, id) => cs.Set(id, f.Speed, QueryFixture.D(1)));
        var e3 = f.Create(30, f.A, (cs, id) =>
        {
            cs.Set(id, f.Count, QueryFixture.I(3));
            cs.Set(id, f.Speed, QueryFixture.D(2));
        });
        return (f, e1, e2, e3);
    }

    [Fact]
    public void OriginWrittenWithin_and_NotOriginWrittenWithin_split_the_entities_at_the_inclusive_edge()
    {
        var (f, e1, e2, e3) = Three(); // origin times 10, 20, 30; now = 30
        Assert.Equal(QueryFixture.Ids(e2, e3), QueryFixture.Ids(f.Repo.Query().OriginWrittenWithin(S(10))));
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().NotOriginWrittenWithin(S(10))));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().OriginWrittenWithin(S(9.999))));
        Assert.Equal(QueryFixture.Ids(e1, e2), QueryFixture.Ids(f.Repo.Query().NotOriginWrittenWithin(S(9.999))));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().OriginWrittenWithin(TimeSpan.Zero)));
        Assert.Equal(QueryFixture.Ids(e1, e2), QueryFixture.Ids(f.Repo.Query().NotOriginWrittenWithin(TimeSpan.Zero)));
        Assert.Equal(QueryFixture.Ids(e1, e2, e3), QueryFixture.Ids(f.Repo.Query().OriginWrittenWithin(S(20))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().NotOriginWrittenWithin(S(20))));
    }

    [Fact]
    public void The_filters_combine_with_FromOrigin_and_Has()
    {
        var (f, e1, e2, e3) = Three();
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().FromOrigin(f.A).NotOriginWrittenWithin(S(10))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().FromOrigin(f.B).NotOriginWrittenWithin(S(10))));
        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().FromOrigin(f.B).OriginWrittenWithin(S(10))));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().Has(f.Speed).Has(f.Count).OriginWrittenWithin(S(10))));
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().Has(f.Count).NotOriginWrittenWithin(S(10))));
        Assert.Equal(QueryFixture.Ids(e1, e3), QueryFixture.Ids(f.Repo.Query().Has(f.Count).FromOrigin(f.A).OriginWrittenWithin(S(20))));
        // both origin filters together form a band
        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().OriginWrittenWithin(S(10)).NotOriginWrittenWithin(S(5))));
        // together with the ordinary write filters
        f.At(40, f.B, cs => cs.Set(e1, f.Count, QueryFixture.I(9)));                       // a non-owner write at 40
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().WrittenWithin(S(1)).NotOriginWrittenWithin(S(20))));
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().NotOriginWrittenWithin(S(20)).WrittenWithin(S(1))));
    }

    [Fact]
    public void The_sim_time_variants_are_filters_on_the_sim_stamps()
    {
        var f = new QueryFixture();
        var e1 = f.Create(1, f.A);
        f.At(100, 3, f.A, cs => cs.Set(e1, f.Count, QueryFixture.I(1))); // origin write: wall 100, sim 3
        var e2 = default(EntityId);
        f.At(101, 50, f.A, cs => e2 = cs.CreateEntity());                 // wall 101, sim 50
        f.At(102, 52, f.A, cs => cs.Set(e2, f.Count, QueryFixture.I(1))); // wall 102, sim 52
        Assert.Equal(52, f.Repo.SimNow);

        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().InSimTime().OriginWrittenWithin(S(5))));
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().InSimTime().NotOriginWrittenWithin(S(5))));
        // the edge is inclusive in sim time as well: sim stamps 3 and 52, SimNow 52
        Assert.Equal(QueryFixture.Ids(e1, e2), QueryFixture.Ids(f.Repo.Query().InSimTime().OriginWrittenWithin(S(49))));
        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().InSimTime().OriginWrittenWithin(S(48.999))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().InSimTime().NotOriginWrittenWithin(S(49))));
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().InSimTime().NotOriginWrittenWithin(S(48.999))));
        // in wall time both entities are fresh, so the two readings differ
        Assert.Equal(QueryFixture.Ids(e1, e2), QueryFixture.Ids(f.Repo.Query().OriginWrittenWithin(S(5))));
    }

    [Fact]
    public void A_second_origin_window_filter_of_a_kind_throws()
    {
        var f = new QueryFixture();
        var q = f.Repo.Query();
        Assert.Throws<InvalidOperationException>(() => q.OriginWrittenWithin(S(1)).OriginWrittenWithin(S(1)));
        Assert.Throws<InvalidOperationException>(() => q.NotOriginWrittenWithin(S(1)).NotOriginWrittenWithin(S(1)));
        Assert.Throws<InvalidOperationException>(() => q.OriginWrittenWithin(S(1)).InSimTime().OriginWrittenWithin(S(2)));
    }

    [Fact]
    public void Driving_a_window_from_the_origin_list_touches_only_the_matching_candidates()
    {
        var f = new QueryFixture();
        var proc = f.Sources.Register("proc");
        var ids = new List<EntityId>();
        f.At(1, f.A, cs =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                var id = cs.CreateEntity();
                ids.Add(id);
                cs.Set(id, f.Count, QueryFixture.I(i));
            }
        });

        // at t = 100 the owner re-sends 20 entities and the processor rewrites 5 000 others
        f.At(100, f.A, cs =>
        {
            for (int i = 0; i < 20; i++)
            {
                cs.Set(ids[i * 500], f.Speed, QueryFixture.D(1));
            }
        });
        f.At(100, proc, cs =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                if (i % 500 != 0 && i % 2 == 0)
                {
                    cs.Set(ids[i], f.Speed, QueryFixture.D(2));
                }
            }
        });

        var touched = new Counter();
        var fresh = f.Repo.Query().OriginWrittenWithin(S(5)).Where(touched, static (e, c) =>
        {
            c.Calls++;
            return true;
        });
        Assert.Equal(20, fresh.Count());
        Assert.Equal(20, touched.Calls);

        touched.Calls = 0;
        var stale = f.Repo.Query().NotOriginWrittenWithin(S(5)).Where(touched, static (e, c) =>
        {
            c.Calls++;
            return true;
        });
        Assert.Equal(9_980, stale.Count());
        Assert.Equal(9_980, touched.Calls);
        // the processor's 4 980 derived writes keep them "written" but not origin-fresh
        Assert.Equal(4_980, f.Repo.Query().WrittenWithin(S(5)).NotOriginWrittenWithin(S(5)).Count());
    }

    [Fact]
    public void Steady_state_commits_and_the_two_origin_queries_allocate_nothing()
    {
        var f = new QueryFixture();
        var proc = f.Sources.Register("proc");
        var ids = new List<EntityId>();
        f.At(1, f.A, cs =>
        {
            for (int i = 0; i < 500; i++)
            {
                var id = cs.CreateEntity();
                ids.Add(id);
                cs.Set(id, f.Count, QueryFixture.I(i));
            }
        });

        var list = new List<EntityRecord>(1024);
        var visitor = new CountVisitor();
        long sink = 0;
        double t = 10;

        void Round(int i)
        {
            t += 0.01;
            var origin = f.Repo.Begin(f.A, t, t);
            for (int k = 0; k < 20; k++)
            {
                origin.Set(ids[(i * 7 + k) % ids.Count], f.Count, QueryFixture.I(i + k));
            }

            origin.Commit();
            var derived = f.Repo.Begin(proc, t, t);
            for (int k = 0; k < 20; k++)
            {
                derived.Set(ids[(i * 11 + k) % ids.Count], f.Speed, QueryFixture.D(i + k));
            }

            derived.Commit();

            f.Repo.Query().FromOrigin(f.A).NotOriginWrittenWithin(S(3)).CopyTo(list);
            sink += list.Count;
            f.Repo.Query().OriginWrittenWithin(S(3)).Has(f.Count).CopyTo(list);
            sink += list.Count;
            f.Repo.Query().NotOriginWrittenWithin(S(3)).ForEach(ref visitor);
            f.Repo.Query().InSimTime().OriginWrittenWithin(S(3)).ForEach(ref visitor);
            sink += f.Repo.Query().OriginWrittenWithin(S(3)).Count();
        }

        for (int i = 0; i < 2000; i++)
        {
            Round(i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2000; i < 3000; i++)
        {
            Round(i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(sink > 0);
        Assert.True(visitor.Seen > 0);
    }

    private struct CountVisitor : IEntityVisitor
    {
        public int Seen;

        public void Visit(EntityRecord entity) => Seen++;
    }
}
