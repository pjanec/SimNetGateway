using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class TransactionTests
{
    [Fact]
    public void Set_with_wrong_kind_throws_immediately()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        Assert.Throws<ArgumentException>(() => cs.Set(id, f.Count, AttrValue.From(1.5)));
        Assert.Throws<ArgumentException>(() => cs.Set(id, f.Pos, AttrValue.From(1L)));
        Assert.Throws<ArgumentException>(() => cs.Set(id, f.Count, AttrValue.None));
        Assert.Throws<ArgumentException>(() => cs.Set(id, new AttrId(999), AttrValue.From(1L)));
        Assert.Equal(0, cs.CommandCount);
        cs.Discard();
    }

    [Fact]
    public void Write_to_missing_entity_is_rejected_and_nothing_applied()
    {
        var f = new Fixture();
        var existing = f.Create(f.A);
        int journalBefore = f.Repo.Journal.Count;
        var cs = f.Begin(f.A);
        var created = cs.CreateEntity();
        cs.Set(existing, f.Count, AttrValue.From(1L));
        cs.Set(created, f.Count, AttrValue.From(1L));
        cs.Set(new EntityId(12345), f.Count, AttrValue.From(1L));
        var r = cs.Commit();
        Assert.Equal(CommitStatus.Rejected, r.Status);
        Assert.False(r.Succeeded);
        Assert.NotNull(r.Reason);
        Assert.Contains("12345", r.Reason);
        Assert.Null(r.Record);
        Assert.Equal(1, f.Repo.Count);
        Assert.False(f.Repo.Find(existing)!.Has(f.Count));
        Assert.Null(f.Repo.Find(created));
        Assert.Equal(journalBefore, f.Repo.Journal.Count);
        Assert.False(f.Repo.IsChangeOpen);
    }

    [Fact]
    public void Write_to_entity_destroyed_by_earlier_transaction_is_rejected()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.DestroyEntity(id);
        cs.Commit();

        cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(1L));
        Assert.Equal(CommitStatus.Rejected, cs.Commit().Status);

        cs = f.Begin(f.A);
        cs.DestroyEntity(id);
        Assert.Equal(CommitStatus.Rejected, cs.Commit().Status);
        Assert.Equal(2, f.Repo.Journal.Count); // create + destroy only
    }

    [Fact]
    public void Write_after_destroy_in_same_transaction_is_rejected()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var other = f.Create(f.A);
        int journalBefore = f.Repo.Journal.Count;
        var cs = f.Begin(f.A);
        cs.Set(other, f.Count, AttrValue.From(1L));
        cs.DestroyEntity(id);
        cs.Set(id, f.Count, AttrValue.From(1L));
        var r = cs.Commit();
        Assert.Equal(CommitStatus.Rejected, r.Status);
        Assert.Equal(2, f.Repo.Count);
        Assert.True(f.Repo.Find(id)!.IsAlive);
        Assert.False(f.Repo.Find(other)!.Has(f.Count));
        Assert.Equal(journalBefore, f.Repo.Journal.Count);
    }

    [Fact]
    public void Double_destroy_in_one_transaction_is_rejected()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.DestroyEntity(id);
        cs.DestroyEntity(id);
        Assert.Equal(CommitStatus.Rejected, cs.Commit().Status);
        Assert.Equal(1, f.Repo.Count);
        Assert.True(f.Repo.Find(id)!.IsAlive);
    }

    [Fact]
    public void Begin_while_open_throws_and_commands_while_closed_throw()
    {
        var f = new Fixture();
        var cs = f.Begin(f.A);
        Assert.True(cs.IsOpen);
        Assert.Throws<InvalidOperationException>(() => f.Begin(f.A));
        cs.Discard();
        Assert.False(cs.IsOpen);
        Assert.Throws<InvalidOperationException>(() => cs.CreateEntity());
        Assert.Throws<InvalidOperationException>(() => cs.DestroyEntity(new EntityId(1)));
        Assert.Throws<InvalidOperationException>(() => cs.Set(new EntityId(1), f.Count, AttrValue.From(1L)));
        Assert.Throws<InvalidOperationException>(() => cs.Clear(new EntityId(1), f.Count));
        Assert.Throws<InvalidOperationException>(() => cs.TransferOwner(new EntityId(1), f.B));
        Assert.Throws<InvalidOperationException>(() => cs.Commit());
        cs.Discard(); // no-op

        var r = f.Begin(f.A).Commit();
        Assert.Equal(CommitStatus.Empty, r.Status);
    }

    [Fact]
    public void Same_change_set_instance_is_reused()
    {
        var f = new Fixture();
        var a = f.Begin(f.A);
        a.Discard();
        var b = f.Begin(f.B);
        Assert.Same(a, b);
        Assert.Equal(f.B, b.Source);
        Assert.Equal(0, b.CommandCount);
        b.Discard();
    }

    [Fact]
    public void Correlation_ids_strictly_increase_and_record_metadata()
    {
        var f = new Fixture();
        f.Wall = 7; f.Sim = 3;
        var cs = f.Begin(f.A);
        var c1 = cs.Correlation;
        Assert.Equal(1, c1.Value);
        Assert.Equal(7, cs.WallTime);
        Assert.Equal(3, cs.SimTime);
        cs.Discard();
        cs = f.Begin(f.B, c1);
        var c2 = cs.Correlation;
        Assert.True(c2.Value > c1.Value);
        Assert.Equal(c1, cs.CausedBy);
        cs.CreateEntity();
        var r = cs.Commit();
        Assert.Equal(c2, r.Record!.Correlation);
        Assert.Equal(c1, r.Record.CausedBy);
        Assert.Equal(f.B, r.Record.Source);
        Assert.Equal(7, r.Record.WallTime);
        Assert.Equal(3, r.Record.SimTime);
        cs = f.Begin(f.A);
        Assert.True(cs.Correlation.Value > c2.Value);
        cs.Discard();
    }

    [Fact]
    public void Deltas_are_in_order_of_first_touch()
    {
        var f = new Fixture();
        var a = f.Create(f.A);
        var b = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(b, f.Count, AttrValue.From(1L));
        cs.Set(a, f.Count, AttrValue.From(1L));
        cs.Set(b, f.Name, AttrValue.From("x"));
        var r = cs.Commit();
        Assert.Equal(2, r.Record!.Count);
        Assert.Equal(b, r.Record.Entities[0].Id);
        Assert.Equal(a, r.Record.Entities[1].Id);
        Assert.Equal(2, r.Record.Entities[0].Changes.Count);
        int n = 0;
        foreach (var d in r.Record.Entities) { n += d.Changes.Count; }
        Assert.Equal(3, n);
    }

    [Fact]
    public void Entity_created_here_can_be_written_destroyed_and_transferred()
    {
        var f = new Fixture();
        var cs = f.Begin(f.A);
        var id = cs.CreateEntity();
        cs.TransferOwner(id, f.B);
        var r = cs.Commit();
        Assert.Equal(f.B, f.Repo.Find(id)!.Owner);
        Assert.True(r.Record!.Entities[0].Created);
    }
}
