using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class JournalTests
{
    [Fact]
    public void Lookup_by_correlation_and_get_writes()
    {
        var f = new Fixture();
        var a = f.Create(f.A);
        var b = f.Create(f.A);
        var cs = f.Begin(f.A);
        var corr = cs.Correlation;
        cs.Set(a, f.Count, AttrValue.From(1L));
        cs.Set(b, f.Count, AttrValue.From(2L));
        cs.Set(b, f.Name, AttrValue.From("n"));
        cs.Commit();

        // overwritten afterwards, the journal still knows the original write
        cs = f.Begin(f.A);
        cs.Set(a, f.Count, AttrValue.From(50L));
        cs.Commit();

        Assert.True(f.Repo.Journal.TryGet(corr, out var rec));
        Assert.Equal(corr, rec.Correlation);
        var writes = new List<JournalWrite>();
        foreach (var w in f.Repo.Journal.GetWrites(corr))
        {
            writes.Add(w);
        }

        Assert.Equal(3, writes.Count);
        Assert.Equal(a, writes[0].Entity);
        Assert.Equal(1L, writes[0].Change.New.AsInt());
        Assert.Equal(b, writes[1].Entity);
        Assert.Equal("n", writes[2].Change.New.AsString());

        Assert.False(f.Repo.Journal.TryGet(new CorrelationId(9999), out _));
        foreach (var _ in f.Repo.Journal.GetWrites(new CorrelationId(9999)))
        {
            Assert.Fail("no writes expected");
        }
    }

    [Fact]
    public void Since_returns_retained_records_oldest_first()
    {
        var f = new Fixture();
        var times = new[] { 1.0, 2.0, 3.0, 4.0 };
        var corrs = new List<CorrelationId>();
        foreach (var t in times)
        {
            f.Wall = t;
            var cs = f.Begin(f.A);
            corrs.Add(cs.Correlation);
            cs.CreateEntity();
            cs.Commit();
        }

        var got = new List<CorrelationId>();
        foreach (var r in f.Repo.Journal.Since(3.0))
        {
            got.Add(r.Correlation);
        }

        Assert.Equal(new[] { corrs[2], corrs[3] }, got);

        got.Clear();
        foreach (var r in f.Repo.Journal.Since(0))
        {
            got.Add(r.Correlation);
        }

        Assert.Equal(corrs, got);
        Assert.Equal(4, f.Repo.Journal.Count);
        Assert.Equal(1024, f.Repo.Journal.Capacity);
    }

    [Fact]
    public void Eviction_at_capacity_drops_oldest_and_recycles_it()
    {
        var f = new Fixture(journalCapacity: 3);
        var corrs = new List<CorrelationId>();
        for (int i = 0; i < 5; i++)
        {
            f.Wall = i;
            var cs = f.Begin(f.A);
            corrs.Add(cs.Correlation);
            cs.CreateEntity();
            Assert.True(cs.Commit().Succeeded);
        }

        var j = f.Repo.Journal;
        Assert.Equal(3, j.Count);
        Assert.Equal(3, j.Capacity);
        Assert.False(j.TryGet(corrs[0], out _));
        Assert.False(j.TryGet(corrs[1], out _));
        Assert.True(j.TryGet(corrs[2], out _));
        Assert.True(j.TryGet(corrs[4], out _));
        foreach (var _ in j.GetWrites(corrs[0])) { Assert.Fail("evicted"); }

        var got = new List<CorrelationId>();
        foreach (var r in j.Since(0)) { got.Add(r.Correlation); }
        Assert.Equal(new[] { corrs[2], corrs[3], corrs[4] }, got);
    }

    [Fact]
    public void Rejected_and_discarded_and_empty_are_not_journaled()
    {
        var f = new Fixture();
        var cs = f.Begin(f.A);
        cs.Set(new EntityId(77), f.Count, AttrValue.From(1L));
        Assert.False(cs.Commit().Succeeded);
        cs = f.Begin(f.A);
        cs.CreateEntity();
        cs.Discard();
        Assert.True(f.Begin(f.A).Commit().Succeeded);
        Assert.Equal(0, f.Repo.Journal.Count);
    }

    [Fact]
    public void Zero_capacity_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateRepository(new AttributeRegistry(), null, 0));
    }
}
