using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class RepositoryTests
{
    [Fact]
    public void Create_set_and_read_back_with_metadata()
    {
        var f = new Fixture();
        f.Wall = 5; f.Sim = 1;
        var cs = f.Begin(f.A);
        var id = cs.CreateEntity();
        cs.Set(id, f.Pos, AttrValue.From(new Vec3(1, 2, 3)));
        cs.Set(id, f.Count, AttrValue.From(7L));
        var corr = cs.Correlation;
        var r = cs.Commit();

        Assert.Equal(CommitStatus.Committed, r.Status);
        Assert.True(r.Succeeded);
        Assert.Equal(1, f.Repo.Count);
        var e = f.Repo.Find(id)!;
        Assert.True(f.Repo.TryGet(id, out var e2));
        Assert.Same(e, e2);
        Assert.Equal(id, e.Id);
        Assert.Equal(f.A, e.Origin);
        Assert.Equal(f.A, e.Owner);
        Assert.True(e.IsAlive);
        Assert.Equal(corr, e.CreatedBy);
        Assert.Equal(5, e.CreatedWallTime);
        Assert.Equal(5, e.LastWriteWallTime);
        Assert.Equal(5, e.LastChangeWallTime);
        Assert.Equal(2, e.Count);
        Assert.True(e.Has(f.Pos));
        Assert.False(e.Has(f.Speed));
        Assert.Equal(new Vec3(1, 2, 3), e.Get(f.Pos).AsVec3());
        Assert.True(e.Get(f.Speed).IsNone);
        Assert.True(e.TryGet(f.Count, out var v));
        Assert.Equal(7L, v.AsInt());
        Assert.False(e.TryGet(f.Speed, out _));
        Assert.True(e.TryGetSlot(f.Pos, out var slot));
        Assert.Equal(new AttrMeta(f.A, corr, 5, 1, 5, 1, 1), slot.Meta);
    }

    [Fact]
    public void Origin_can_be_overridden_and_owner_follows_origin()
    {
        var f = new Fixture();
        var cs = f.Begin(f.A);
        var id = cs.CreateEntity(f.B);
        cs.Commit();
        var e = f.Repo.Find(id)!;
        Assert.Equal(f.B, e.Origin);
        Assert.Equal(f.B, e.Owner);
    }

    [Fact]
    public void Written_versus_changed()
    {
        var f = new Fixture();
        f.Wall = 1; f.Sim = 1;
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(1L));
        cs.Commit();

        f.Wall = 2; f.Sim = 2;
        cs = f.Begin(f.B);
        cs.Set(id, f.Count, AttrValue.From(1L)); // same value
        var corr2 = cs.Correlation;
        var r = cs.Commit();

        var e = f.Repo.Find(id)!;
        e.TryGetSlot(f.Count, out var slot);
        Assert.Equal(1u, slot.Meta.Version);
        Assert.Equal(f.B, slot.Meta.Source);
        Assert.Equal(corr2, slot.Meta.Correlation);
        Assert.Equal(2, slot.Meta.WallTime);
        Assert.Equal(2, slot.Meta.SimTime);
        Assert.Equal(1, slot.Meta.ChangedWallTime);
        Assert.Equal(1, slot.Meta.ChangedSimTime);
        Assert.Equal(2, e.LastWriteWallTime);
        Assert.Equal(1, e.LastChangeWallTime);

        var delta = r.Record!.Entities[0];
        Assert.True(delta.WasWritten(f.Count));
        Assert.False(delta.WasChanged(f.Count));
        Assert.True(delta.TryGetChange(f.Count, out var ch));
        Assert.False(ch.Changed);

        f.Wall = 3; f.Sim = 3;
        cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(2L));
        r = cs.Commit();
        e.TryGetSlot(f.Count, out slot);
        Assert.Equal(2u, slot.Meta.Version);
        Assert.Equal(3, slot.Meta.ChangedWallTime);
        Assert.Equal(3, e.LastChangeWallTime);
        Assert.True(r.Record!.Entities[0].WasChanged(f.Count));
        Assert.False(r.Record.Entities[0].WasChanged(f.Pos));
        Assert.False(r.Record.Entities[0].WasWritten(f.Pos));
    }

    [Fact]
    public void Clear_removes_slot_and_counts_as_change_absent_clear_is_noop()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(3L));
        cs.Commit();

        cs = f.Begin(f.A);
        cs.Clear(id, f.Count);
        var r = cs.Commit();
        Assert.Equal(CommitStatus.Committed, r.Status);
        Assert.False(f.Repo.Find(id)!.Has(f.Count));
        var ch = r.Record!.Entities[0].Changes[0];
        Assert.Equal(3L, ch.Old.AsInt());
        Assert.True(ch.New.IsNone);
        Assert.True(ch.Changed);

        cs = f.Begin(f.A);
        cs.Clear(id, f.Count); // already absent
        r = cs.Commit();
        Assert.Equal(CommitStatus.Empty, r.Status);
        Assert.Null(r.Record);
    }

    [Fact]
    public void Reads_during_open_transaction_see_committed_state_pending_sees_buffer()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(1L));
        cs.Commit();

        cs = f.Begin(f.A);
        Assert.True(f.Repo.IsChangeOpen);
        cs.Set(id, f.Count, AttrValue.From(2L));
        Assert.Equal(1L, f.Repo.Find(id)!.Get(f.Count).AsInt());
        Assert.True(cs.TryGetPending(id, f.Count, out var p));
        Assert.Equal(2L, p.AsInt());
        cs.Set(id, f.Count, AttrValue.From(3L));
        Assert.True(cs.TryGetPending(id, f.Count, out p));
        Assert.Equal(3L, p.AsInt());
        Assert.False(cs.TryGetPending(id, f.Pos, out _));
        Assert.False(cs.TryGetPending(new EntityId(99), f.Count, out _));

        cs.Clear(id, f.Count);
        Assert.False(cs.TryGetPending(id, f.Count, out _)); // pending clear => false
        cs.Set(id, f.Count, AttrValue.From(4L));
        Assert.True(cs.TryGetPending(id, f.Count, out p));
        Assert.Equal(4L, p.AsInt());
        cs.DestroyEntity(id);
        Assert.False(cs.TryGetPending(id, f.Count, out _)); // pending destroy => false
        cs.Discard();
        Assert.False(f.Repo.IsChangeOpen);
    }

    [Fact]
    public void Reserved_ids_are_usable_within_the_transaction()
    {
        var f = new Fixture();
        var cs = f.Begin(f.A);
        var a = cs.CreateEntity();
        var b = cs.CreateEntity();
        Assert.NotEqual(a, b);
        Assert.False(a.IsNone);
        Assert.Equal(0, f.Repo.Count); // not visible yet
        cs.Set(a, f.Count, AttrValue.From(1L));
        cs.Set(b, f.Name, AttrValue.From("x"));
        cs.Set(a, f.Pos, AttrValue.From(new Vec3(1, 1, 1)));
        Assert.True(cs.TryGetPending(a, f.Count, out _));
        var r = cs.Commit();
        Assert.Equal(2, f.Repo.Count);
        Assert.Equal(2, r.Record!.Count);
        Assert.Equal(a, r.Record.Entities[0].Id);
        Assert.Equal(b, r.Record.Entities[1].Id);
        Assert.True(r.Record.Entities[0].Created);
        Assert.Equal(2, r.Record.Entities[0].Changes.Count);
    }

    [Fact]
    public void Discard_applies_nothing_and_burns_ids()
    {
        var f = new Fixture();
        var cs = f.Begin(f.A);
        var burned = cs.CreateEntity();
        cs.Set(burned, f.Count, AttrValue.From(1L));
        Assert.Equal(2, cs.CommandCount);
        cs.Discard();
        Assert.False(cs.IsOpen);
        Assert.Equal(0, f.Repo.Count);
        Assert.Equal(0, f.Repo.Journal.Count);
        Assert.Null(f.Repo.Find(burned));

        var next = f.Create(f.A);
        Assert.True(next.Value > burned.Value);
    }

    [Fact]
    public void Destroy_removes_entity_and_keeps_dead_record_readable()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(5L));
        cs.Commit();

        cs = f.Begin(f.A);
        cs.DestroyEntity(id);
        var r = cs.Commit();
        Assert.Equal(CommitStatus.Committed, r.Status);
        Assert.Equal(0, f.Repo.Count);
        Assert.Null(f.Repo.Find(id));
        Assert.False(f.Repo.TryGet(id, out _));
        var d = r.Record!.Entities[0];
        Assert.True(d.Destroyed);
        Assert.False(d.Created);
        Assert.False(d.Entity.IsAlive);
        Assert.Equal(5L, d.Entity.Get(f.Count).AsInt());
        Assert.Equal(1, d.Entity.Count);
    }

    [Fact]
    public void Create_and_destroy_in_one_transaction_is_empty()
    {
        var f = new Fixture();
        var cs = f.Begin(f.A);
        var id = cs.CreateEntity();
        cs.Set(id, f.Count, AttrValue.From(1L));
        cs.DestroyEntity(id);
        var r = cs.Commit();
        Assert.Equal(CommitStatus.Empty, r.Status);
        Assert.True(r.Succeeded);
        Assert.Null(r.Record);
        Assert.Equal(0, f.Repo.Count);
        Assert.Equal(0, f.Repo.Journal.Count);
        Assert.Null(f.Repo.Find(id));
    }

    [Fact]
    public void Empty_transaction_is_empty_and_not_journaled()
    {
        var f = new Fixture();
        var r = f.Begin(f.A).Commit();
        Assert.Equal(CommitStatus.Empty, r.Status);
        Assert.Equal(0, f.Repo.Journal.Count);
    }

    [Fact]
    public void Create_and_destroy_does_not_disturb_other_entities_of_the_transaction()
    {
        var f = new Fixture();
        var cs = f.Begin(f.A);
        var keep = cs.CreateEntity();
        var gone = cs.CreateEntity();
        cs.Set(keep, f.Count, AttrValue.From(1L));
        cs.Set(gone, f.Count, AttrValue.From(2L));
        cs.DestroyEntity(gone);
        var r = cs.Commit();
        Assert.Equal(1, r.Record!.Count);
        Assert.Equal(keep, r.Record.Entities[0].Id);
        Assert.Equal(1, f.Repo.Count);
    }

    [Fact]
    public void Entities_enumerates_live_records()
    {
        var f = new Fixture();
        var a = f.Create(f.A);
        var b = f.Create(f.A);
        var seen = new HashSet<EntityId>();
        foreach (var e in f.Repo.Entities)
        {
            seen.Add(e.Id);
        }

        Assert.Equal(new HashSet<EntityId> { a, b }, seen);
        var cs = f.Begin(f.A);
        cs.DestroyEntity(a);
        cs.Commit();
        int n = 0;
        foreach (var _ in f.Repo.Entities) { n++; }
        Assert.Equal(1, n);
    }

    [Fact]
    public void Attributes_enumerator_lists_slots()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(1L));
        cs.Set(id, f.Name, AttrValue.From("n"));
        cs.Commit();
        var found = new Dictionary<AttrId, AttrValue>();
        foreach (var (attr, slot) in f.Repo.Find(id)!.Attributes)
        {
            found[attr] = slot.Value;
        }

        Assert.Equal(2, found.Count);
        Assert.Equal("n", found[f.Name].AsString());
    }

    [Fact]
    public void Merged_multiple_sets_of_one_attribute()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(1L));
        cs.Commit();

        cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(2L));
        cs.Set(id, f.Count, AttrValue.From(3L));
        cs.Set(id, f.Count, AttrValue.From(4L));
        var r = cs.Commit();
        var d = r.Record!.Entities[0];
        Assert.Single(d.Changes);
        var ch = d.Changes[0];
        Assert.Equal(1L, ch.Old.AsInt());
        Assert.Equal(4L, ch.New.AsInt());
        Assert.True(ch.Changed);

        cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(9L));
        cs.Set(id, f.Count, AttrValue.From(4L)); // back to the original
        r = cs.Commit();
        ch = r.Record!.Entities[0].Changes[0];
        Assert.Equal(4L, ch.Old.AsInt());
        Assert.Equal(4L, ch.New.AsInt());
        Assert.False(ch.Changed);
    }

    [Fact]
    public void Value_restored_within_one_transaction_is_written_but_not_changed()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(4L));
        cs.Commit();
        Assert.True(f.Repo.Find(id)!.TryGetSlot(f.Count, out var before));

        f.Wall += 5;
        cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(9L));
        cs.Clear(id, f.Count);
        cs.Set(id, f.Count, AttrValue.From(4L)); // back to the value before the transaction
        var correlation = cs.Correlation;
        cs.Commit();

        Assert.True(f.Repo.Find(id)!.TryGetSlot(f.Count, out var after));
        Assert.Equal(before.Meta.Version, after.Meta.Version);
        Assert.Equal(before.Meta.ChangedWallTime, after.Meta.ChangedWallTime);
        Assert.Equal(f.Wall, after.Meta.WallTime);
        Assert.Equal(correlation, after.Meta.Correlation);
        Assert.Equal(before.Meta.ChangedWallTime, f.Repo.Find(id)!.LastChangeWallTime);
    }

    [Fact]
    public void Set_then_clear_of_an_absent_attribute_changes_nothing()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(4L));
        cs.Clear(id, f.Count);
        var r = cs.Commit();

        Assert.Equal(CommitStatus.Empty, r.Status);
        Assert.False(f.Repo.Find(id)!.Has(f.Count));
    }

    [Fact]
    public void Inverse_is_recorded_for_every_command()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(1L));
        cs.Set(id, f.Name, AttrValue.From("old"));
        cs.Commit();

        cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(2L));
        cs.Clear(id, f.Name);
        cs.Set(id, f.Pos, AttrValue.From(new Vec3(1, 2, 3)));
        cs.TransferOwner(id, f.B);
        var r = cs.Commit();
        var d = r.Record!.Entities[0];
        Assert.True(d.OwnerChanged);
        Assert.Equal(f.A, d.PreviousOwner);
        Assert.Equal(f.B, f.Repo.Find(id)!.Owner);
        Assert.Equal(3, d.Changes.Count);
        Assert.True(d.TryGetChange(f.Count, out var c1));
        Assert.Equal(1L, c1.Old.AsInt());
        Assert.Equal(2L, c1.New.AsInt());
        Assert.True(d.TryGetChange(f.Name, out var c2));
        Assert.Equal("old", c2.Old.AsString());
        Assert.True(c2.New.IsNone);
        Assert.True(d.TryGetChange(f.Pos, out var c3));
        Assert.True(c3.Old.IsNone);
    }

    [Fact]
    public void Transfer_to_current_owner_is_noop()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.TransferOwner(id, f.A);
        Assert.Equal(CommitStatus.Empty, cs.Commit().Status);
    }

    [Fact]
    public void Entity_level_times()
    {
        var f = new Fixture();
        f.Wall = 1;
        var id = f.Create(f.A);
        f.Wall = 2;
        var cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(1L));
        cs.Commit();
        var e = f.Repo.Find(id)!;
        Assert.Equal(1, e.CreatedWallTime);
        Assert.Equal(2, e.LastWriteWallTime);
        Assert.Equal(2, e.LastChangeWallTime);
        f.Wall = 3;
        cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(1L));
        cs.Commit();
        Assert.Equal(3, e.LastWriteWallTime);
        Assert.Equal(2, e.LastChangeWallTime);
    }
}
