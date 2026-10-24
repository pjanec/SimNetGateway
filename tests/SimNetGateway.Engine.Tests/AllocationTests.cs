using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class AllocationTests
{
    private const int Entities = 100;

    private static (Fixture F, EntityId[] Ids, AttrId Vec, AttrId Dbl, AttrId Int) Setup()
    {
        var f = new Fixture(journalCapacity: 16);
        var vec = f.Attributes.Register("alloc.vec", AttrKind.Vec3);
        var dbl = f.Attributes.Register("alloc.dbl", AttrKind.Double);
        var itg = f.Attributes.Register("alloc.int", AttrKind.Int);
        var ids = new EntityId[Entities];
        var cs = f.Begin(f.A);
        for (int i = 0; i < Entities; i++)
        {
            ids[i] = cs.CreateEntity();
            cs.Set(ids[i], vec, AttrValue.From(new Vec3(i, i, i)));
            cs.Set(ids[i], dbl, AttrValue.From((double)i));
            cs.Set(ids[i], itg, AttrValue.From((long)i));
        }

        Assert.True(cs.Commit().Succeeded);
        return (f, ids, vec, dbl, itg);
    }

    private static void Round(Fixture f, EntityId[] ids, AttrId vec, AttrId dbl, AttrId itg, int round, bool sameValues)
    {
        var cs = f.Repo.Begin(f.A, round, round);
        for (int k = 0; k < 10; k++)
        {
            var id = ids[(round * 7 + k * 3) % Entities];
            double x = sameValues ? 1.0 : round + k;
            cs.Set(id, vec, AttrValue.From(new Vec3(x, x + 1, x + 2)));
            cs.Set(id, dbl, AttrValue.From(x * 0.5));
            cs.Set(id, itg, AttrValue.From((long)x));
        }

        var r = cs.Commit();
        if (!r.Succeeded)
        {
            throw new InvalidOperationException("commit failed");
        }
    }

    [Fact]
    public void Steady_state_commits_with_changing_values_allocate_nothing()
    {
        var (f, ids, vec, dbl, itg) = Setup();
        for (int i = 0; i < 400; i++)
        {
            Round(f, ids, vec, dbl, itg, i, false);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 400; i < 1400; i++)
        {
            Round(f, ids, vec, dbl, itg, i, false);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Steady_state_commits_with_unchanged_values_allocate_nothing()
    {
        var (f, ids, vec, dbl, itg) = Setup();
        for (int i = 0; i < 400; i++)
        {
            Round(f, ids, vec, dbl, itg, i, true);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 400; i < 1400; i++)
        {
            Round(f, ids, vec, dbl, itg, i, true);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Journal_queries_and_reads_allocate_nothing()
    {
        var (f, ids, vec, dbl, itg) = Setup();
        for (int i = 0; i < 100; i++)
        {
            Round(f, ids, vec, dbl, itg, i, false);
        }

        var corr = new CorrelationId(f.Repo.Journal.Count > 0 ? 100 : 1);
        long sum = 0;
        long before = 0;
        for (int pass = 0; pass < 2; pass++) // first pass warms up JIT and statics
        {
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                foreach (var w in f.Repo.Journal.GetWrites(corr)) { sum += w.Entity.Value; }
                foreach (var r in f.Repo.Journal.Since(0)) { sum += r.Count; }
                foreach (var e in f.Repo.Entities) { sum += e.Get(vec).IsNone ? 0 : 1; }
                var rec = f.Repo.Find(ids[i]);
                foreach (var (a, s) in rec!.Attributes) { sum += a.Value; }
                if (f.Repo.Journal.TryGet(corr, out var found))
                {
                    var deltas = found.Entities;
                    for (int d = 0; d < deltas.Count; d++) { sum += deltas[d].Changes.Count; }
                }
            }
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(sum > 0);
        Assert.Equal(0, after - before);
    }
}
