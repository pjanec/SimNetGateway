using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class WriteAuthorityTests
{
    private static Fixture Single(params string[] trusted)
    {
        // Sources are registered by the fixture; trusted names are resolved afterwards.
        var holder = new SourceRegistry();
        var ids = trusted.Select(n => holder.Register(n)).ToArray();
        return new Fixture(new SingleWriterPolicy(ids));
    }

    [Fact]
    public void Permissive_allows_everything()
    {
        var f = new Fixture();
        var id = f.Create(f.A);
        var cs = f.Begin(f.B);
        cs.Set(id, f.Count, AttrValue.From(1L));
        cs.TransferOwner(id, f.B);
        Assert.True(cs.Commit().Succeeded);
        cs = f.Begin(f.A);
        cs.DestroyEntity(id);
        Assert.True(cs.Commit().Succeeded);
        Assert.Same(PermissivePolicy.Instance, PermissivePolicy.Instance);
    }

    [Fact]
    public void Single_writer_rejects_non_owner_set_clear_destroy_transfer()
    {
        var f = new Fixture(new SingleWriterPolicy());
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(1L));
        cs.Commit();

        var cs2 = f.Begin(f.B);
        cs2.Set(id, f.Count, AttrValue.From(2L));
        var r = cs2.Commit();
        Assert.Equal(CommitStatus.Rejected, r.Status);
        Assert.Contains("source " + f.B.Value, r.Reason);
        Assert.Contains("entity " + id.Value, r.Reason);
        Assert.Contains("owned by source " + f.A.Value, r.Reason);
        Assert.Equal(1L, f.Repo.Find(id)!.Get(f.Count).AsInt());

        cs2 = f.Begin(f.B);
        cs2.Clear(id, f.Count);
        Assert.Equal(CommitStatus.Rejected, cs2.Commit().Status);
        Assert.True(f.Repo.Find(id)!.Has(f.Count));

        cs2 = f.Begin(f.B);
        cs2.DestroyEntity(id);
        Assert.Equal(CommitStatus.Rejected, cs2.Commit().Status);
        Assert.Equal(1, f.Repo.Count);

        cs2 = f.Begin(f.B);
        cs2.TransferOwner(id, f.B);
        Assert.Equal(CommitStatus.Rejected, cs2.Commit().Status);
        Assert.Equal(f.A, f.Repo.Find(id)!.Owner);
    }

    [Fact]
    public void Denied_command_rejects_whole_transaction()
    {
        var f = new Fixture(new SingleWriterPolicy());
        var mine = f.Create(f.B);
        var theirs = f.Create(f.A);
        var cs = f.Begin(f.B);
        var created = cs.CreateEntity();
        cs.Set(mine, f.Count, AttrValue.From(1L));
        cs.Set(theirs, f.Count, AttrValue.From(1L));
        var r = cs.Commit();
        Assert.Equal(CommitStatus.Rejected, r.Status);
        Assert.False(f.Repo.Find(mine)!.Has(f.Count));
        Assert.Null(f.Repo.Find(created));
        Assert.Equal(2, f.Repo.Count);
    }

    [Fact]
    public void Single_writer_allows_owner_trusted_and_engine()
    {
        var sources = new SourceRegistry();
        var a = sources.Register("a");
        var b = sources.Register("b");
        var proc = sources.Register("proc");
        var attrs = new AttributeRegistry();
        var count = attrs.Register("count", AttrKind.Int);
        var repo = new StateRepository(attrs, new SingleWriterPolicy(new List<SourceId> { proc }));

        var cs = repo.Begin(a, 1, 1);
        var id = cs.CreateEntity();
        cs.Commit();

        cs = repo.Begin(a, 1, 1);
        cs.Set(id, count, AttrValue.From(1L));
        Assert.True(cs.Commit().Succeeded);

        cs = repo.Begin(proc, 1, 1);
        cs.Set(id, count, AttrValue.From(2L));
        Assert.True(cs.Commit().Succeeded);

        cs = repo.Begin(b, 1, 1);
        cs.Set(id, count, AttrValue.From(3L));
        Assert.False(cs.Commit().Succeeded);

        cs = repo.Begin(SourceRegistry.Engine, 1, 1);
        cs.DestroyEntity(id);
        Assert.True(cs.Commit().Succeeded);
    }

    [Fact]
    public void Entity_created_in_same_transaction_is_not_checked()
    {
        // A relayed entity: writer A sets origin (and owner) to B in the same transaction, then writes it.
        var f = new Fixture(new SingleWriterPolicy());
        var cs = f.Begin(f.A);
        var id = cs.CreateEntity(f.B);
        cs.Set(id, f.Count, AttrValue.From(1L));
        cs.TransferOwner(id, f.A);
        Assert.True(cs.Commit().Succeeded);
        Assert.Equal(f.B, f.Repo.Find(id)!.Origin);
        Assert.Equal(f.A, f.Repo.Find(id)!.Owner);

        // Afterwards A is not the owner of an entity owned by B.
        var other = f.Begin(f.A);
        var rel = other.CreateEntity(f.B);
        other.Commit();
        cs = f.Begin(f.A);
        cs.Set(rel, f.Count, AttrValue.From(1L));
        Assert.Equal(CommitStatus.Rejected, cs.Commit().Status);
    }

    [Fact]
    public void Transfer_owner_hands_write_access_over()
    {
        var f = new Fixture(new SingleWriterPolicy());
        var id = f.Create(f.A);
        var cs = f.Begin(f.A);
        cs.TransferOwner(id, f.B);
        var r = cs.Commit();
        Assert.True(r.Succeeded);
        Assert.True(r.Record!.Entities[0].OwnerChanged);
        Assert.Equal(f.A, r.Record.Entities[0].PreviousOwner);
        Assert.Equal(f.B, f.Repo.Find(id)!.Owner);

        cs = f.Begin(f.B);
        cs.Set(id, f.Count, AttrValue.From(1L));
        Assert.True(cs.Commit().Succeeded);

        cs = f.Begin(f.A);
        cs.Set(id, f.Count, AttrValue.From(2L));
        Assert.Equal(CommitStatus.Rejected, cs.Commit().Status);
    }

    [Fact]
    public void Default_interface_methods_are_permissive()
    {
        IWriteAuthorityPolicy p = new CustomPolicy();
        var f = new Fixture();
        var id = f.Create(f.A);
        var e = f.Repo.Find(id)!;
        Assert.False(p.CanWrite(e, f.A, default));
        Assert.True(p.CanDestroy(e, f.A));
        Assert.True(p.CanTransferOwner(e, f.A, f.B));
    }

    private sealed class CustomPolicy : IWriteAuthorityPolicy
    {
        public bool CanWrite(EntityRecord entity, SourceId writer, AttrId attr) => false;
    }
}
