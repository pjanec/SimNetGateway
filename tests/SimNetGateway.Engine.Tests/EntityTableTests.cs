using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class EntityTableTests
{
    private struct Published
    {
        public int Version;
        public double Sent;
    }

    private static (EngineFixture F, EntityTable<Published> Table) Setup()
    {
        var f = new EngineFixture();
        EntityTable<Published> table = null!;
        f.Module("egress", s => table = s.CreateEntityTable<Published>("egress.published"));
        return (f, table);
    }

    [Fact]
    public void GetOrAdd_returns_a_reference_to_a_default_entry_that_can_be_modified_in_place()
    {
        var (f, table) = Setup();
        var e = f.Create();

        ref var entry = ref table.GetOrAdd(e, out bool added);
        Assert.True(added);
        Assert.Equal(0, entry.Version);
        entry.Version = 5;
        entry.Sent = 1.5;

        ref var again = ref table.GetOrAdd(e, out bool addedAgain);
        Assert.False(addedAgain);
        Assert.Equal(5, again.Version);
        Assert.Equal(1, table.Count);
        Assert.True(table.Contains(e));
        Assert.True(table.TryGet(e, out var copy));
        Assert.Equal(1.5, copy.Sent);
        Assert.Equal("egress.published", table.Name);

        table.GetOrAdd(e).Version++;
        Assert.Equal(6, table.GetOrAdd(e).Version);
    }

    [Fact]
    public void TryGet_Contains_and_Remove_report_missing_entries()
    {
        var (f, table) = Setup();
        var e = f.Create();
        Assert.False(table.TryGet(e, out _));
        Assert.False(table.Contains(e));
        Assert.False(table.Remove(e));
        table.GetOrAdd(e);
        Assert.True(table.Remove(e));
        Assert.False(table.Remove(e));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Swap_remove_keeps_the_other_entries_intact()
    {
        var (f, table) = Setup();
        var ids = Enumerable.Range(0, 40).Select(_ => f.Create()).ToArray(); // beyond the initial capacity
        for (int i = 0; i < ids.Length; i++)
        {
            table.GetOrAdd(ids[i]).Version = 100 + i;
        }

        Assert.Equal(40, table.Count);
        Assert.True(table.Remove(ids[3]));
        Assert.True(table.Remove(ids[0]));
        Assert.True(table.Remove(ids[39]));

        Assert.Equal(37, table.Count);
        for (int i = 0; i < ids.Length; i++)
        {
            bool removed = i is 3 or 0 or 39;
            Assert.Equal(!removed, table.TryGet(ids[i], out var v));
            if (!removed)
            {
                Assert.Equal(100 + i, v.Version);
            }
        }

        // the dense view lists exactly the survivors, each with its own value
        var seen = new HashSet<int>();
        for (int i = 0; i < table.Count; i++)
        {
            var id = table.IdAt(i);
            Assert.True(seen.Add(id.Value));
            Assert.Equal(100 + Array.IndexOf(ids, id), table.ValueAt(i).Version);
        }

        table.ValueAt(0).Version = -1;
        Assert.Equal(-1, table.GetOrAdd(table.IdAt(0)).Version);
        Assert.Throws<ArgumentOutOfRangeException>(() => table.IdAt(table.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.ValueAt(-1));
    }

    [Fact]
    public void Entries_of_destroyed_entities_are_removed_after_the_removal_dispatch()
    {
        var f = new EngineFixture();
        EntityTable<Published> table = null!;
        int versionSeenInRemoval = -1;
        bool presentInRemoval = false;
        f.Module("egress", s =>
        {
            table = s.CreateEntityTable<Published>("egress.published");
            s.OnEntityRemoved().Do(t =>
            {
                presentInRemoval = table.TryGet(t.Id, out var p);
                versionSeenInRemoval = p.Version;
            });
        });
        var keep = f.Create();
        var gone = f.Create();
        table.GetOrAdd(keep).Version = 1;
        table.GetOrAdd(gone).Version = 2;

        var cs = f.Engine.BeginChange(f.Net);
        cs.DestroyEntity(gone);
        Assert.True(cs.Commit().Succeeded);

        Assert.True(presentInRemoval);           // the removal rule could still read it
        Assert.Equal(2, versionSeenInRemoval);
        Assert.False(table.Contains(gone));      // and it is gone afterwards
        Assert.True(table.Contains(keep));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void Unload_clears_the_tables()
    {
        var (f, table) = Setup();
        for (int i = 0; i < 5; i++)
        {
            table.GetOrAdd(f.Create());
        }

        Assert.True(f.Engine.Execute(new SessionCommand(SessionCommandKind.Load, "s", SessionMode.Live), f.Net));
        Assert.True(f.Engine.Execute(new SessionCommand(SessionCommandKind.Unload, null, SessionMode.Live), f.Net));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void A_table_name_is_unique_per_engine_and_tables_are_independent()
    {
        var f = new EngineFixture();
        EntityTable<Published> a = null!;
        EntityTable<int> b = null!;
        f.Module("m1", s => a = s.CreateEntityTable<Published>("one"));
        f.Module("m2", s => b = s.CreateEntityTable<int>("two"));
        Assert.Throws<InvalidOperationException>(() => f.Module("m3", s => s.CreateEntityTable<int>("one")));
        Assert.Throws<ArgumentException>(() => f.Module("m4", s => s.CreateEntityTable<int>("")));

        var e = f.Create();
        a.GetOrAdd(e).Version = 3;
        b.GetOrAdd(e) = 7;
        var cs = f.Engine.BeginChange(f.Net);
        cs.DestroyEntity(e);
        Assert.True(cs.Commit().Succeeded);
        Assert.Equal(0, a.Count);
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void An_entity_without_an_entry_is_destroyed_without_trouble()
    {
        var (f, table) = Setup();
        var e = f.Create();
        var cs = f.Engine.BeginChange(f.Net);
        cs.DestroyEntity(e);
        Assert.True(cs.Commit().Succeeded);
        Assert.Equal(0, table.Count);
    }
}
