using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Engine.Tests;

/// <summary>A standalone repository (every commit sets the query time) with attributes for the index tests.</summary>
internal sealed class QueryFixture
{
    public readonly AttributeRegistry Attributes = new();
    public readonly SourceRegistry Sources = new();
    public readonly SourceId A;
    public readonly SourceId B;
    public readonly AttrId Count;
    public readonly AttrId Speed;
    public readonly AttrId Pos;
    public readonly AttrId Target;
    public readonly AttrId Parent;
    public readonly StateRepository Repo;
    public readonly IdentityRegistry Identities;

    public QueryFixture()
    {
        A = Sources.Register("a");
        B = Sources.Register("b");
        Count = Attributes.Register("count", AttrKind.Int);
        Speed = Attributes.Register("speed", AttrKind.Double);
        Pos = Attributes.Register("pos", AttrKind.Vec3, null, AttrOptions.SpatialIndex, 10);
        Target = Attributes.Register("target", AttrKind.Vec3, null, AttrOptions.SpatialIndex, 5);
        Parent = Attributes.Register("parent", AttrKind.EntityRef, null, AttrOptions.IndexReferences);
        Repo = new StateRepository(Attributes);
        Identities = new IdentityRegistry(Repo);
    }

    /// <summary>Runs <paramref name="work"/> in a transaction at wall time = sim time = <paramref name="time"/>.</summary>
    public CommitResult At(double time, SourceId source, Action<ChangeSet> work) => At(time, time, source, work);

    public CommitResult At(double wall, double sim, SourceId source, Action<ChangeSet> work)
    {
        var cs = Repo.Begin(source, wall, sim);
        work(cs);
        var result = cs.Commit();
        Assert.True(result.Succeeded, result.Reason);
        return result;
    }

    public EntityId Create(double time, SourceId source, Action<ChangeSet, EntityId>? fill = null)
    {
        EntityId id = default;
        At(time, source, cs =>
        {
            id = cs.CreateEntity();
            fill?.Invoke(cs, id);
        });
        return id;
    }

    public static AttrValue I(long v) => AttrValue.From(v);

    public static AttrValue D(double v) => AttrValue.From(v);

    public static AttrValue P(double x, double y, double z) => AttrValue.From(new Vec3(x, y, z));

    public static AttrValue R(EntityId id) => AttrValue.From(id);

    /// <summary>The ids a query returns, ordered.</summary>
    public static int[] Ids(EntityQuery query)
    {
        var list = new List<EntityRecord>();
        query.CopyTo(list);
        return list.Select(e => e.Id.Value).Order().ToArray();
    }

    public static int[] Ids(params EntityId[] ids) => ids.Select(i => i.Value).Order().ToArray();
}

public class QueryTests
{
    // e1 created at t=10 by a, e2 at t=20 by b (with speed), e3 at t=30 by a (with speed and pos). Now = 30.
    private static (QueryFixture F, EntityId E1, EntityId E2, EntityId E3) Three()
    {
        var f = new QueryFixture();
        var e1 = f.Create(10, f.A, (cs, id) => cs.Set(id, f.Count, QueryFixture.I(1)));
        var e2 = f.Create(20, f.B, (cs, id) =>
        {
            cs.Set(id, f.Count, QueryFixture.I(2));
            cs.Set(id, f.Speed, QueryFixture.D(1.0));
        });
        var e3 = f.Create(30, f.A, (cs, id) =>
        {
            cs.Set(id, f.Count, QueryFixture.I(3));
            cs.Set(id, f.Speed, QueryFixture.D(2.0));
            cs.Set(id, f.Pos, QueryFixture.P(5, 5, 5));
        });
        return (f, e1, e2, e3);
    }

    [Fact]
    public void An_empty_query_returns_every_live_entity()
    {
        var (f, e1, e2, e3) = Three();
        Assert.Equal(QueryFixture.Ids(e1, e2, e3), QueryFixture.Ids(f.Repo.Query()));
        Assert.Equal(3, f.Repo.Query().Count());
        Assert.True(f.Repo.Query().Any());
    }

    [Fact]
    public void ChangedWithin_is_inclusive_at_the_edge()
    {
        var (f, e1, e2, e3) = Three();
        Assert.Equal(30, f.Repo.Now);
        Assert.Equal(QueryFixture.Ids(e2, e3), QueryFixture.Ids(f.Repo.Query().ChangedWithin(TimeSpan.FromSeconds(10))));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().ChangedWithin(TimeSpan.FromSeconds(9.999))));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().ChangedWithin(TimeSpan.Zero)));
        Assert.Equal(QueryFixture.Ids(e1, e2, e3), QueryFixture.Ids(f.Repo.Query().ChangedWithin(TimeSpan.FromSeconds(20))));
    }

    [Fact]
    public void ChangedSince_is_inclusive_at_the_edge()
    {
        var (f, e1, e2, e3) = Three();
        Assert.Equal(QueryFixture.Ids(e2, e3), QueryFixture.Ids(f.Repo.Query().ChangedSince(20)));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().ChangedSince(20.0001)));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().ChangedSince(31)));
    }

    [Fact]
    public void WrittenWithin_and_NotWrittenWithin_split_the_entities_at_the_edge()
    {
        var (f, e1, e2, e3) = Three();
        Assert.Equal(QueryFixture.Ids(e2, e3), QueryFixture.Ids(f.Repo.Query().WrittenWithin(TimeSpan.FromSeconds(10))));
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().NotWrittenWithin(TimeSpan.FromSeconds(10))));
        Assert.Equal(QueryFixture.Ids(e1, e2), QueryFixture.Ids(f.Repo.Query().NotWrittenWithin(TimeSpan.FromSeconds(9))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().NotWrittenWithin(TimeSpan.FromSeconds(50))));
    }

    [Fact]
    public void A_written_but_unchanged_entity_appears_only_in_written_windows()
    {
        var (f, e1, e2, e3) = Three();
        f.At(40, f.A, cs => cs.Set(e1, f.Count, QueryFixture.I(1))); // same value

        Assert.Equal(40, f.Repo.Now);
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().WrittenWithin(TimeSpan.FromSeconds(5))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().ChangedWithin(TimeSpan.FromSeconds(5))));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().ChangedWithin(TimeSpan.FromSeconds(10))));
        Assert.Equal(QueryFixture.Ids(e2, e3), QueryFixture.Ids(f.Repo.Query().NotWrittenWithin(TimeSpan.FromSeconds(5))));
        Assert.Equal(10, f.Repo.Find(e1)!.LastChangeWallTime);
        Assert.Equal(40, f.Repo.Find(e1)!.LastWriteWallTime);
    }

    [Fact]
    public void AttributeChangedWithin_uses_the_change_time_of_the_slot()
    {
        var (f, e1, e2, e3) = Three();
        f.At(35, f.A, cs => cs.Set(e2, f.Speed, QueryFixture.D(9)));          // changed at 35
        f.At(38, f.A, cs => cs.Set(e3, f.Speed, QueryFixture.D(2.0)));        // written, unchanged
        f.At(40, f.A, cs => cs.Set(e1, f.Count, QueryFixture.I(1)));          // written, unchanged

        var five = TimeSpan.FromSeconds(5);
        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().AttributeChangedWithin(f.Speed, five)));
        Assert.Equal(QueryFixture.Ids(e2, e3), QueryFixture.Ids(f.Repo.Query().AttributeChangedWithin(f.Speed, TimeSpan.FromSeconds(10))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().AttributeChangedWithin(f.Count, five)));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().AttributeChangedWithin(f.Pos, TimeSpan.FromSeconds(1000)).Lacks(f.Pos)));
        // the entity without the attribute is not matched at all
        Assert.DoesNotContain(e1.Value, QueryFixture.Ids(f.Repo.Query().AttributeChangedWithin(f.Speed, TimeSpan.FromSeconds(1000))));
    }

    [Fact]
    public void Has_and_Lacks_filter_by_presence_and_up_to_four_attributes()
    {
        var (f, e1, e2, e3) = Three();
        Assert.Equal(QueryFixture.Ids(e2, e3), QueryFixture.Ids(f.Repo.Query().Has(f.Speed)));
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().Lacks(f.Speed)));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().Has(f.Speed).Has(f.Pos)));
        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().Has(f.Speed).Lacks(f.Pos)));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().Has(f.Parent)));
        Assert.Equal(QueryFixture.Ids(e1, e2, e3), QueryFixture.Ids(f.Repo.Query().Has(f.Count).Has(f.Count).Has(f.Count).Has(f.Count)));
        Assert.Throws<InvalidOperationException>(() => f.Repo.Query().Has(f.Count).Has(f.Count).Has(f.Count).Has(f.Count).Has(f.Count));
        Assert.Equal(QueryFixture.Ids(e1, e2, e3), QueryFixture.Ids(f.Repo.Query().Lacks(f.Parent).Lacks(f.Parent).Lacks(f.Parent).Lacks(f.Target)));
        Assert.Throws<InvalidOperationException>(() => f.Repo.Query().Lacks(f.Parent).Lacks(f.Parent).Lacks(f.Parent).Lacks(f.Parent).Lacks(f.Parent));
    }

    [Fact]
    public void Presence_follows_Clear_and_a_later_Set()
    {
        var (f, e1, e2, e3) = Three();
        f.At(40, f.A, cs => cs.Clear(e2, f.Speed));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().Has(f.Speed)));
        Assert.Equal(QueryFixture.Ids(e1, e2), QueryFixture.Ids(f.Repo.Query().Lacks(f.Speed)));

        f.At(41, f.A, cs => cs.Set(e2, f.Speed, QueryFixture.D(3)));
        Assert.Equal(QueryFixture.Ids(e2, e3), QueryFixture.Ids(f.Repo.Query().Has(f.Speed)));
    }

    [Fact]
    public void FromOrigin_and_NotFromOrigin_filter_by_the_introducing_side()
    {
        var (f, e1, e2, e3) = Three();
        Assert.Equal(QueryFixture.Ids(e1, e3), QueryFixture.Ids(f.Repo.Query().FromOrigin(f.A)));
        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().FromOrigin(f.B)));
        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().NotFromOrigin(f.A)));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().FromOrigin(f.A).NotFromOrigin(f.A)));
    }

    [Fact]
    public void KnownTo_follows_binding_and_ignores_destroyed_entities()
    {
        var (f, e1, e2, e3) = Three();
        var map = f.Identities.Create<ulong>(f.A, "ned.entity");
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().KnownTo(map)));

        Assert.True(map.Bind(e1, 1));
        Assert.True(map.Bind(e3, 3));
        Assert.Equal(QueryFixture.Ids(e1, e3), QueryFixture.Ids(f.Repo.Query().KnownTo(map)));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().KnownTo(map).Has(f.Speed)));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().Has(f.Speed).KnownTo(map)));

        f.At(40, f.A, cs => cs.DestroyEntity(e3)); // standalone: nobody releases the key, the query must not return the dead
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().KnownTo(map)));

        Assert.Throws<ArgumentException>(() => f.Repo.Query().KnownTo(new ForeignMap()));
        Assert.Throws<InvalidOperationException>(() => f.Repo.Query().KnownTo(map).KnownTo(map));
    }

    private sealed class ForeignMap : IIdentityMap
    {
        public SourceId Side => default;
        public string Name => "foreign";
        public Type KeyType => typeof(int);
        public int Count => 0;
        public bool Contains(EntityId id) => false;
    }

    [Fact]
    public void ReferencedBy_follows_set_reparent_clear_and_destroy_of_the_referrer()
    {
        var (f, e1, e2, e3) = Three();
        f.At(40, f.A, cs =>
        {
            cs.Set(e2, f.Parent, QueryFixture.R(e1));
            cs.Set(e3, f.Parent, QueryFixture.R(e1));
        });
        Assert.Equal(QueryFixture.Ids(e2, e3), QueryFixture.Ids(f.Repo.Query().ReferencedBy(f.Parent, e1)));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().ReferencedBy(f.Parent, e2)));

        f.At(41, f.A, cs => cs.Set(e3, f.Parent, QueryFixture.R(e2)));
        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().ReferencedBy(f.Parent, e1)));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().ReferencedBy(f.Parent, e2)));

        f.At(42, f.A, cs => cs.Clear(e2, f.Parent));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().ReferencedBy(f.Parent, e1)));

        f.At(43, f.A, cs => cs.DestroyEntity(e3));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().ReferencedBy(f.Parent, e2)));
        Assert.Throws<InvalidOperationException>(() => f.Repo.Query().ReferencedBy(f.Count, e1)); // not indexed
    }

    [Fact]
    public void A_destroyed_target_keeps_its_referrers_listed()
    {
        var (f, e1, e2, e3) = Three();
        f.At(40, f.A, cs => cs.Set(e2, f.Parent, QueryFixture.R(e1)));
        f.At(41, f.A, cs => cs.DestroyEntity(e1));
        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().ReferencedBy(f.Parent, e1)));
    }

    [Fact]
    public void Where_filters_with_a_predicate_and_with_explicit_state()
    {
        var (f, e1, e2, e3) = Three();
        var count = f.Count;
        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().Where(e => e.Get(count).AsInt() == 2)));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().Has(f.Speed).Where(e => e.Get(count).AsInt() > 2)));

        var query = f.Repo.Query().Has(f.Speed).Where(count, static (e, attr) => e.Get(attr).AsInt() == 3);
        var list = new List<EntityRecord>();
        query.CopyTo(list);
        Assert.Equal(new[] { e3.Value }, list.Select(e => e.Id.Value).ToArray());
        Assert.Equal(1, query.Count());
        Assert.True(query.Any());
        var visitor = new CollectVisitor();
        query.ForEach(ref visitor);
        Assert.Equal(1, visitor.Seen);

        Assert.Throws<InvalidOperationException>(() => f.Repo.Query().Where(e => true).Where(e => true));
    }

    private struct CollectVisitor : IEntityVisitor
    {
        public int Seen;
        public long Sum;

        public void Visit(EntityRecord entity)
        {
            Seen++;
            Sum += entity.Id.Value;
        }
    }

    [Fact]
    public void ForEach_visits_each_match_once_and_returns_the_visitor_state()
    {
        var (f, e1, e2, e3) = Three();
        var visitor = new CollectVisitor();
        f.Repo.Query().Has(f.Speed).ForEach(ref visitor);
        Assert.Equal(2, visitor.Seen);
        Assert.Equal(e2.Value + e3.Value, visitor.Sum);
    }

    [Fact]
    public void Combined_filters_apply_together()
    {
        var (f, e1, e2, e3) = Three();
        var ten = TimeSpan.FromSeconds(10);
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(f.Repo.Query().ChangedWithin(ten).FromOrigin(f.A).Has(f.Speed)));
        Assert.Equal(QueryFixture.Ids(e2), QueryFixture.Ids(f.Repo.Query().ChangedWithin(ten).NotFromOrigin(f.A).Has(f.Speed)));
        var stateful = new List<EntityRecord>();
        f.Repo.Query().WrittenWithin(ten).Has(f.Speed).Where(f.Count, static (e, c) => e.Get(c).AsInt() == 3).CopyTo(stateful);
        Assert.Equal(new[] { e3.Value }, stateful.Select(e => e.Id.Value).ToArray());
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().NotWrittenWithin(ten).Has(f.Speed)));
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().NotWrittenWithin(ten).FromOrigin(f.A).Lacks(f.Speed)));
        Assert.Equal(QueryFixture.Ids(e3), QueryFixture.Ids(
            f.Repo.Query().ChangedSince(25).InBox(f.Pos, new Vec3(0, 0, 0), new Vec3(10, 10, 10)).Has(f.Speed)));
    }

    [Fact]
    public void A_filter_may_be_given_only_once()
    {
        var f = new QueryFixture();
        var q = f.Repo.Query();
        var s = TimeSpan.FromSeconds(1);
        Assert.Throws<InvalidOperationException>(() => q.ChangedWithin(s).ChangedWithin(s));
        Assert.Throws<InvalidOperationException>(() => q.WrittenWithin(s).WrittenWithin(s));
        Assert.Throws<InvalidOperationException>(() => q.NotWrittenWithin(s).NotWrittenWithin(s));
        Assert.Throws<InvalidOperationException>(() => q.FromOrigin(f.A).FromOrigin(f.B));
        Assert.Throws<InvalidOperationException>(() => q.InCell(f.Pos, new CellKey(0, 0, 0)).InCell(f.Pos, new CellKey(0, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => q.ChangedWithin(TimeSpan.FromSeconds(-1)));
        // the builder is a value: using q again is unaffected by the failed calls
        Assert.Empty(QueryFixture.Ids(q));
    }

    [Fact]
    public void The_builder_is_a_value_and_each_filter_returns_a_new_query()
    {
        var (f, e1, e2, e3) = Three();
        var all = f.Repo.Query();
        var withSpeed = all.Has(f.Speed);
        Assert.Equal(3, all.Count());
        Assert.Equal(2, withSpeed.Count());
    }

    [Fact]
    public void Destroyed_entities_disappear_from_every_index()
    {
        var (f, e1, e2, e3) = Three();
        f.At(40, f.A, cs => cs.Set(e2, f.Parent, QueryFixture.R(e3)));
        f.At(41, f.A, cs => cs.Set(e2, f.Target, QueryFixture.P(1, 1, 1)));
        f.At(42, f.A, cs =>
        {
            cs.DestroyEntity(e2);
            cs.DestroyEntity(e3);
        });
        f.Repo.SetTime(42, 42);

        var dead = new[] { e2.Value, e3.Value };
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query()));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().Has(f.Speed)));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().Has(f.Pos)));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().InBox(f.Pos, new Vec3(-100, -100, -100), new Vec3(100, 100, 100))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().InBox(f.Target, new Vec3(-100, -100, -100), new Vec3(100, 100, 100))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().ReferencedBy(f.Parent, e3)));
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().ChangedWithin(TimeSpan.FromSeconds(1000))));
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().WrittenWithin(TimeSpan.FromSeconds(1000))));
        Assert.Equal(QueryFixture.Ids(e1), QueryFixture.Ids(f.Repo.Query().NotWrittenWithin(TimeSpan.Zero)));
        Assert.Equal(new[] { e1.Value }, Order(f.Repo.ByLastWrite));
        Assert.Equal(new[] { e1.Value }, Order(f.Repo.ByLastChange));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().Has(f.Count).Where(e => dead.Contains(e.Id.Value))));
    }

    [Fact]
    public void Entities_move_in_the_recency_order_when_written_and_when_changed()
    {
        var (f, e1, e2, e3) = Three();
        Assert.Equal(new[] { e1.Value, e2.Value, e3.Value }, Order(f.Repo.ByLastWrite));
        Assert.Equal(new[] { e1.Value, e2.Value, e3.Value }, Order(f.Repo.ByLastChange));

        f.At(40, f.A, cs => cs.Set(e1, f.Count, QueryFixture.I(1))); // written, not changed
        Assert.Equal(new[] { e2.Value, e3.Value, e1.Value }, Order(f.Repo.ByLastWrite));
        Assert.Equal(new[] { e1.Value, e2.Value, e3.Value }, Order(f.Repo.ByLastChange));

        f.At(50, f.A, cs => cs.Set(e2, f.Count, QueryFixture.I(22))); // changed
        Assert.Equal(new[] { e3.Value, e1.Value, e2.Value }, Order(f.Repo.ByLastWrite));
        Assert.Equal(new[] { e1.Value, e3.Value, e2.Value }, Order(f.Repo.ByLastChange));

        f.At(60, f.A, cs => cs.TransferOwner(e3, f.B)); // an owner change is a write and a change
        Assert.Equal(new[] { e1.Value, e2.Value, e3.Value }, Order(f.Repo.ByLastWrite));
        Assert.Equal(new[] { e1.Value, e2.Value, e3.Value }, Order(f.Repo.ByLastChange));
    }

    private static int[] Order(RecencyEnumerable list)
    {
        var ids = new List<int>();
        foreach (var e in list)
        {
            ids.Add(e.Id.Value);
        }

        return ids.ToArray();
    }

    [Fact]
    public void A_clear_counts_as_write_and_change_and_a_noop_clear_as_nothing()
    {
        var (f, e1, e2, e3) = Three();
        f.At(40, f.A, cs => cs.Clear(e1, f.Speed)); // absent: no-op
        Assert.Equal(10, f.Repo.Find(e1)!.LastWriteWallTime);
        f.At(41, f.A, cs => cs.Clear(e2, f.Speed));
        Assert.Equal(41, f.Repo.Find(e2)!.LastChangeWallTime);
        Assert.Equal(e2.Value, Order(f.Repo.ByLastChange).Last());
    }

    [Fact]
    public void A_snapshot_stays_valid_while_the_caller_modifies_state()
    {
        var (f, e1, e2, e3) = Three();
        var snapshot = new List<EntityRecord>();
        f.Repo.Query().Has(f.Count).CopyTo(snapshot);
        Assert.Equal(3, snapshot.Count);

        int t = 100;
        foreach (var e in snapshot)
        {
            if (!e.IsAlive)
            {
                continue; // destroyed by an earlier iteration: the snapshot still holds its (dead) record
            }

            // writes and a destroy while walking the result
            f.At(t++, f.A, cs =>
            {
                cs.Set(e.Id, f.Speed, QueryFixture.D(t));
                if (e.Id == e2)
                {
                    cs.DestroyEntity(e3);
                }
            });
        }

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(new[] { e1.Value, e2.Value, e3.Value }, snapshot.Select(e => e.Id.Value).Order().ToArray());
        Assert.False(snapshot.Single(e => e.Id == e3).IsAlive);
        Assert.Equal(2, f.Repo.Query().Count());
    }

    [Fact]
    public void CopyTo_clears_the_target_first()
    {
        var (f, e1, e2, e3) = Three();
        var target = new List<EntityRecord> { f.Repo.Find(e1)!, f.Repo.Find(e1)! };
        f.Repo.Query().Has(f.Pos).CopyTo(target);
        Assert.Single(target);
        Assert.Equal(e3, target[0].Id);
    }

    [Fact]
    public void A_window_query_over_ten_thousand_entities_touches_only_the_matching_ones()
    {
        var f = new QueryFixture();
        f.At(1, f.A, cs =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                var id = cs.CreateEntity();
                cs.Set(id, f.Count, QueryFixture.I(i));
            }
        });

        // 20 entities are updated in a later transaction at t = 100
        var list = new List<EntityRecord>();
        f.Repo.Query().Where(f.Count, static (e, c) => e.Get(c).AsInt() % 500 == 0).CopyTo(list);
        Assert.Equal(20, list.Count);
        f.At(100, f.A, cs =>
        {
            foreach (var e in list)
            {
                cs.Set(e.Id, f.Speed, QueryFixture.D(1));
            }
        });

        var touched = new Counter();
        var recent = f.Repo.Query().ChangedWithin(TimeSpan.FromSeconds(5)).Where(touched, static (e, c) =>
        {
            c.Calls++;
            return true;
        });
        Assert.Equal(20, recent.Count());
        Assert.Equal(20, touched.Calls); // the predicate ran for the 20 candidates, not for 10 000 entities

        touched.Calls = 0;
        var stale = f.Repo.Query().NotWrittenWithin(TimeSpan.FromSeconds(5)).Where(touched, static (e, c) =>
        {
            c.Calls++;
            return true;
        });
        Assert.Equal(9_980, stale.Count());
        Assert.Equal(9_980, touched.Calls);

        // a presence query starts from the small set
        touched.Calls = 0;
        var speedy = f.Repo.Query().Has(f.Speed).Where(touched, static (e, c) =>
        {
            c.Calls++;
            return true;
        });
        Assert.Equal(20, speedy.Count());
        Assert.Equal(20, touched.Calls);
    }

    private sealed class Counter
    {
        public int Calls;
    }

    [Fact]
    public void Any_stops_at_the_first_match()
    {
        var f = new QueryFixture();
        f.At(1, f.A, cs =>
        {
            for (int i = 0; i < 1000; i++)
            {
                cs.Set(cs.CreateEntity(), f.Count, QueryFixture.I(i));
            }
        });
        var touched = new Counter();
        Assert.True(f.Repo.Query().Where(touched, static (e, c) =>
        {
            c.Calls++;
            return true;
        }).Any());
        Assert.Equal(1, touched.Calls);
    }

    // ---- sim time (engine) ------------------------------------------------------------------------------

    [Fact]
    public void Sim_time_filters_follow_the_session_clock_while_wall_time_filters_do_not()
    {
        var f = new EngineFixture();
        var load = new SessionCommand(SessionCommandKind.Load, "s", SessionMode.Live);
        var start = new SessionCommand(SessionCommandKind.Start, null, SessionMode.Live);
        var pause = new SessionCommand(SessionCommandKind.Pause, null, SessionMode.Live);

        f.Clock.Now = 10;
        f.Engine.Tick();                                   // Idle: sim time follows wall time
        var idle = f.Create();                             // wall 10, sim 10
        Assert.Equal(10, f.Engine.State.Find(idle)!.LastChangeSimTime);

        Assert.True(f.Engine.Execute(load, f.Net));        // sim time restarts at 0
        Assert.Equal(0, f.Engine.State.SimNow);
        Assert.Equal(10, f.Engine.State.Now);
        Assert.True(f.Engine.Execute(start, f.Net));
        f.Clock.Advance(2);
        f.Engine.Tick();                                   // wall 12, sim 2
        var running = f.Create();
        Assert.Equal(2, f.Engine.State.Find(running)!.LastChangeSimTime);
        Assert.Equal(12, f.Engine.State.Find(running)!.LastChangeWallTime);
        Assert.Equal(2, f.Engine.State.SimNow);

        var one = TimeSpan.FromSeconds(1);
        // wall time is monotonic across the reset: the first entity is two seconds old
        Assert.Equal(new[] { running.Value }, QueryFixture.Ids(f.Engine.State.Query().ChangedWithin(one)));
        Assert.Equal(new[] { idle.Value }, QueryFixture.Ids(f.Engine.State.Query().NotWrittenWithin(one)));
        // sim time is not: the first entity's sim stamp (10) lies in the "future" of sim time 2, so it is not stale
        Assert.Empty(QueryFixture.Ids(f.Engine.State.Query().InSimTime().NotWrittenWithin(one)));
        Assert.Equal(new[] { idle.Value, running.Value }.Order().ToArray(), QueryFixture.Ids(f.Engine.State.Query().InSimTime().ChangedSince(2)));
        Assert.Equal(new[] { idle.Value }, QueryFixture.Ids(f.Engine.State.Query().InSimTime().ChangedSince(2.0001)));

        // while paused, wall time runs on and sim time stands still
        Assert.True(f.Engine.Execute(pause, f.Net));
        f.Clock.Advance(100);
        f.Engine.Tick();
        Assert.Equal(2, f.Engine.State.SimNow);
        Assert.Equal(112, f.Engine.State.Now);
        Assert.Equal(new[] { running.Value, idle.Value }.Order().ToArray(), QueryFixture.Ids(f.Engine.State.Query().NotWrittenWithin(TimeSpan.FromSeconds(50))));
        Assert.Empty(QueryFixture.Ids(f.Engine.State.Query().ChangedWithin(TimeSpan.FromSeconds(50))));
        Assert.Equal(new[] { running.Value }, QueryFixture.Ids(f.Engine.State.Query().InSimTime().WrittenWithin(TimeSpan.FromSeconds(50)).Where(e => e.Id == running)));
        Assert.Equal(new[] { idle.Value }, QueryFixture.Ids(f.Engine.State.Query().InSimTime().ChangedSince(5)));
    }

    [Fact]
    public void The_engine_supplies_the_query_time_to_processors_in_a_tick()
    {
        var f = new EngineFixture();
        var seen = new List<int>();
        var e = f.Create();
        f.Module("proc", s => s.Every(TimeSpan.FromSeconds(1)).Do(_ =>
        {
            var list = new List<EntityRecord>();
            f.Engine.State.Query().ChangedWithin(TimeSpan.FromSeconds(1)).CopyTo(list);
            seen.Add(list.Count);
        }));
        f.Clock.Advance(1);
        f.Engine.Tick();    // created at wall 0, now 1: exactly at the edge
        f.Clock.Advance(1);
        f.Engine.Tick();    // now 2: outside
        Assert.Equal(new[] { 1, 0 }, seen.ToArray());
        Assert.NotEqual(0, e.Value);
    }
}
