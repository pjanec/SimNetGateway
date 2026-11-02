using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Common.Tests;

/// <summary>GroupOwnership and GroupOwnershipPolicy (DD-012 section 4a) against a brute-force model of who owns what.</summary>
public class GroupOwnershipTests
{
    private sealed class World
    {
        public readonly AttributeRegistry Attrs = new();
        public readonly SourceRegistry Sources = new();
        public readonly AttrId O1, O2, O3, A, B, C, D, E, U;
        public readonly GroupOwnership Groups;
        public readonly StateRepository Repo;
        public readonly SourceId[] Writers;
        public readonly SourceId Trusted;

        public World(IWriteAuthorityPolicy? inner = null)
        {
            O1 = Attrs.Register("Own.One", AttrKind.Int);
            O2 = Attrs.Register("Own.Two", AttrKind.Int);
            O3 = Attrs.Register("Own.Three", AttrKind.Int);
            A = Attrs.Register("a", AttrKind.Int);
            B = Attrs.Register("b", AttrKind.Int);
            C = Attrs.Register("c", AttrKind.Int);
            D = Attrs.Register("d", AttrKind.Int);
            E = Attrs.Register("e", AttrKind.Int);
            U = Attrs.Register("u", AttrKind.Int);
            Groups = new GroupOwnership([
                new AttributeGroup("G1", O1, [A, B]),
                new AttributeGroup("G2", O2, [C]),
                new AttributeGroup("G3", O3, [D, E]),
            ]);
            Writers = Enumerable.Range(1, 5).Select(i => Sources.Register("w" + i)).ToArray();
            Trusted = Sources.Register("proc.trusted");
            Repo = new StateRepository(Attrs, new GroupOwnershipPolicy(inner ?? PermissivePolicy.Instance, Groups, [Trusted]));
        }

        public ChangeSet Begin(SourceId s) => Repo.Begin(s, 0, 0);

        public EntityId Create(SourceId owner)
        {
            var cs = Begin(owner);
            var id = cs.CreateEntity();
            Assert.True(cs.Commit().Succeeded);
            return id;
        }
    }

    [Fact]
    public void Definitions_are_validated_and_looked_up()
    {
        var w = new World();
        Assert.True(w.Groups.TryGroupOf(w.B, out var g1));
        Assert.Equal("G1", g1.Name);
        Assert.False(w.Groups.TryGroupOf(w.U, out _));
        Assert.False(w.Groups.TryGroupOf(w.O1, out _));
        Assert.True(w.Groups.IsOwnerAttribute(w.O3));
        Assert.Same(g1, w.Groups.Find("G1"));
        Assert.Null(w.Groups.Find("nope"));
        Assert.Throws<ArgumentException>(() => new GroupOwnership([new AttributeGroup("X", w.O1, [w.A]), new AttributeGroup("Y", w.O2, [w.A])]));
        Assert.Throws<ArgumentException>(() => new GroupOwnership([new AttributeGroup("X", w.O1, [w.A]), new AttributeGroup("X", w.O2, [w.B])]));
        Assert.Throws<ArgumentException>(() => new GroupOwnership([new AttributeGroup("X", w.O1, [w.A]), new AttributeGroup("Y", w.O1, [w.B])]));
        Assert.Throws<ArgumentException>(() => new GroupOwnership([new AttributeGroup("X", w.O1, [w.O2]), new AttributeGroup("Y", w.O2, [w.B])]));
        Assert.Throws<ArgumentException>(() => new AttributeGroup("X", w.O1, []));
    }

    [Fact]
    public void A_group_defaults_to_the_master_and_moves_with_a_transfer_and_back()
    {
        var w = new World();
        var master = w.Writers[0];
        var other = w.Writers[1];
        var id = w.Create(master);
        var g = w.Groups.Find("G2")!;
        Assert.Equal(master, w.Groups.OwnerOf(w.Repo.Find(id)!, g));

        var cs = w.Begin(other);
        w.Groups.Transfer(cs, w.Repo.Find(id)!, g, other);
        Assert.True(cs.Commit().Succeeded);
        Assert.Equal(other, w.Groups.OwnerOf(w.Repo.Find(id)!, g));
        Assert.True(w.Groups.IsExternal(w.Repo.Find(id)!, g, master));

        // the master can no longer write the group, the new owner can
        cs = w.Begin(master);
        cs.Set(id, w.C, AttrValue.From(1L));
        Assert.False(cs.Commit().Succeeded);
        cs = w.Begin(other);
        cs.Set(id, w.C, AttrValue.From(2L));
        Assert.True(cs.Commit().Succeeded);

        // transfer to the master clears the attribute (one representation of "master")
        cs = w.Begin(other);
        w.Groups.Transfer(cs, w.Repo.Find(id)!, g, master);
        Assert.True(cs.Commit().Succeeded);
        Assert.False(w.Repo.Find(id)!.Has(g.OwnerAttribute));
        Assert.Equal(master, w.Groups.OwnerOf(w.Repo.Find(id)!, g));
    }

    [Fact]
    public void Reset_to_master_returns_only_the_groups_of_the_leaving_owner()
    {
        var w = new World();
        var master = w.Writers[0];
        var x = w.Writers[1];
        var y = w.Writers[2];
        var id = w.Create(master);
        var cs = w.Begin(master);
        var rec = w.Repo.Find(id)!;
        w.Groups.Transfer(cs, rec, w.Groups.Find("G1")!, x);
        w.Groups.Transfer(cs, rec, w.Groups.Find("G2")!, y);
        w.Groups.Transfer(cs, rec, w.Groups.Find("G3")!, x);
        Assert.True(cs.Commit().Succeeded);

        cs = w.Begin(SourceRegistry.Engine);
        Assert.Equal(2, w.Groups.ResetToMaster(cs, w.Repo.Find(id)!, x));
        Assert.True(cs.Commit().Succeeded);
        rec = w.Repo.Find(id)!;
        Assert.Equal(master, w.Groups.OwnerOf(rec, w.Groups.Find("G1")!));
        Assert.Equal(y, w.Groups.OwnerOf(rec, w.Groups.Find("G2")!));
        Assert.Equal(master, w.Groups.OwnerOf(rec, w.Groups.Find("G3")!));
        cs = w.Begin(SourceRegistry.Engine);
        Assert.Equal(0, w.Groups.ResetToMaster(cs, w.Repo.Find(id)!, x));
        cs.Discard();
    }

    [Fact]
    public void A_change_set_with_an_allowed_and_a_refused_write_commits_nothing()
    {
        var w = new World();
        var master = w.Writers[0];
        var x = w.Writers[1];
        var id = w.Create(master);
        var cs = w.Begin(master);
        w.Groups.Transfer(cs, w.Repo.Find(id)!, w.Groups.Find("G2")!, x);
        Assert.True(cs.Commit().Succeeded);

        cs = w.Begin(master);
        cs.Set(id, w.A, AttrValue.From(5L)); // allowed: master owns G1
        cs.Set(id, w.U, AttrValue.From(6L)); // allowed: ungrouped
        cs.Set(id, w.C, AttrValue.From(7L)); // refused: x owns G2
        Assert.False(cs.Commit().Succeeded);
        var rec = w.Repo.Find(id)!;
        Assert.False(rec.Has(w.A));
        Assert.False(rec.Has(w.U));
        Assert.False(rec.Has(w.C));
    }

    [Fact]
    public void Ungrouped_attributes_destroy_and_owner_transfer_are_decided_by_the_inner_policy()
    {
        var w = new World(new SingleWriterPolicy());
        var master = w.Writers[0];
        var other = w.Writers[1];
        var id = w.Create(master);
        var cs = w.Begin(other);
        cs.Set(id, w.U, AttrValue.From(1L));
        Assert.False(cs.Commit().Succeeded); // inner: only the owner
        cs = w.Begin(other);
        cs.DestroyEntity(id);
        Assert.False(cs.Commit().Succeeded);
        cs = w.Begin(master);
        cs.Set(id, w.U, AttrValue.From(1L));
        Assert.True(cs.Commit().Succeeded);
        // a grouped attribute: the group owner decides, even when it is not the entity owner
        cs = w.Begin(master);
        w.Groups.Transfer(cs, w.Repo.Find(id)!, w.Groups.Find("G1")!, other);
        Assert.True(cs.Commit().Succeeded);
        cs = w.Begin(other);
        cs.Set(id, w.A, AttrValue.From(1L));
        Assert.True(cs.Commit().Succeeded);
    }

    [Fact]
    public void Seeded_random_operations_match_a_brute_force_model_of_the_owners()
    {
        for (int seed = 1; seed <= 60; seed++)
        {
            var r = new Random(seed);
            var w = new World();
            var all = w.Writers.Append(w.Trusted).Append(SourceRegistry.Engine).ToArray();
            var entities = new List<(EntityId Id, SourceId Master, SourceId?[] Owners)>();
            var values = new Dictionary<(EntityId, AttrId), long>();
            var attrs = new[] { w.A, w.B, w.C, w.D, w.E, w.U };
            var groupOfAttr = new Dictionary<AttrId, int> { [w.A] = 0, [w.B] = 0, [w.C] = 1, [w.D] = 2, [w.E] = 2 };
            long counter = 0;

            for (int step = 0; step < 250; step++)
            {
                int op = r.Next(10);
                if (entities.Count == 0 || op == 0)
                {
                    var master = w.Writers[r.Next(w.Writers.Length)];
                    entities.Add((w.Create(master), master, new SourceId?[3]));
                    continue;
                }

                var (id, masterOf, owners) = entities[r.Next(entities.Count)];
                if (op <= 2)
                {
                    int gi = r.Next(3);
                    var newOwner = r.Next(4) == 0 ? masterOf : w.Writers[r.Next(w.Writers.Length)];
                    var cs = w.Begin(w.Writers[r.Next(w.Writers.Length)]);
                    w.Groups.Transfer(cs, w.Repo.Find(id)!, w.Groups.Groups[gi], newOwner);
                    Assert.True(cs.Commit().Succeeded);
                    owners[gi] = newOwner == masterOf ? null : newOwner;
                }
                else if (op == 3)
                {
                    var leaving = w.Writers[r.Next(w.Writers.Length)];
                    var cs = w.Begin(SourceRegistry.Engine);
                    int reset = w.Groups.ResetToMaster(cs, w.Repo.Find(id)!, leaving);
                    int expected = leaving == masterOf ? 0 : owners.Count(o => o == leaving);
                    Assert.Equal(expected, reset);
                    Assert.True(cs.Commit().Succeeded);
                    for (int i = 0; i < 3; i++)
                    {
                        if (owners[i] == leaving && leaving != masterOf)
                        {
                            owners[i] = null;
                        }
                    }
                }
                else
                {
                    var writer = all[r.Next(all.Length)];
                    var cs = w.Begin(writer);
                    var picked = attrs.OrderBy(_ => r.Next()).Take(r.Next(1, 4)).ToArray();
                    bool expectCommit = true;
                    foreach (var a in picked)
                    {
                        cs.Set(id, a, AttrValue.From(++counter));
                        if (groupOfAttr.TryGetValue(a, out int gi))
                        {
                            var owner = owners[gi] ?? masterOf;
                            expectCommit &= writer == owner || writer == w.Trusted || writer == SourceRegistry.Engine;
                        }
                    }

                    bool committed = cs.Commit().Succeeded;
                    Assert.True(expectCommit == committed, $"seed {seed} step {step}: expected commit={expectCommit}");
                    if (committed)
                    {
                        // the value written last for an attribute wins; remember what the record now holds
                        foreach (var a in picked)
                        {
                            values[(id, a)] = w.Repo.Find(id)!.Get(a).AsInt();
                        }
                    }
                }

                // invariants after every operation: one effective owner per group, equal to the model; refused writes left no trace
                foreach (var (eid, m, o) in entities)
                {
                    var rec = w.Repo.Find(eid)!;
                    for (int gi = 0; gi < 3; gi++)
                    {
                        Assert.Equal(o[gi] ?? m, w.Groups.OwnerOf(rec, w.Groups.Groups[gi]));
                    }

                    foreach (var a in attrs)
                    {
                        Assert.Equal(values.TryGetValue((eid, a), out long v) ? v : (long?)null, rec.TryGet(a, out var cur) ? cur.AsInt() : (long?)null);
                    }
                }
            }
        }
    }
}
