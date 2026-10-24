using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

/// <summary>A struct message that moves the entity known under a (site, app, number) key.</summary>
internal readonly record struct DisMoveMsg(DisId Key, Vec3 Pos);

/// <summary>A struct message that re-parents an entity and toggles one of its attributes.</summary>
internal readonly record struct ReparentMsg(EntityId Entity, EntityId Parent, bool HasSpeed);

/// <summary>
/// DD-001 6.6 exit criterion of stage 3: queries and the index maintenance behind them allocate nothing in steady
/// state (<see cref="GC.GetAllocatedBytesForCurrentThread"/>).
/// </summary>
public class IndexAllocationTests
{
    private const int Warmup = 3000;
    private const int Measured = 1000;

    private struct CountVisitor : IEntityVisitor
    {
        public int Seen;

        public void Visit(EntityRecord entity) => Seen++;
    }

    private static long Measure(Action<int> round, int warmup = Warmup, int measured = Measured)
    {
        for (int i = 0; i < warmup; i++)
        {
            round(i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = warmup; i < warmup + measured; i++)
        {
            round(i);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Steady_state_queries_with_CopyTo_ForEach_Count_and_Any_allocate_nothing()
    {
        var f = new QueryFixture();
        var known = f.Identities.Create<DisId>(f.A, "rpr.entityId");
        var units = new List<EntityId>();
        f.At(1, f.A, cs =>
        {
            for (int u = 0; u < 20; u++)
            {
                units.Add(cs.CreateEntity());
            }
        });
        var all = new List<EntityId>();
        f.At(2, f.B, cs =>
        {
            for (int i = 0; i < 2000; i++)
            {
                var id = cs.CreateEntity();
                all.Add(id);
                cs.Set(id, f.Count, QueryFixture.I(i));
                cs.Set(id, f.Pos, QueryFixture.P(i % 50 * 4, i / 50 * 4, 0));
                cs.Set(id, f.Parent, QueryFixture.R(units[i % 20]));
                if (i % 3 == 0)
                {
                    cs.Set(id, f.Speed, QueryFixture.D(i));
                }
            }
        });
        for (int i = 0; i < 200; i++)
        {
            Assert.True(known.Bind(all[i], new DisId(1, 3001, (ushort)i)));
        }

        var list = new List<EntityRecord>(4096); // pre-sized and reused, as a module would
        var visitor = new CountVisitor();
        var unit = units[3];
        var window = TimeSpan.FromSeconds(3);
        long sink = 0;
        long total = 0;
        double t = 10;

        void Round(int i)
        {
            f.Repo.SetTime(t, t);

            // writes happen outside the measured query calls only through SetTime; the queries themselves:
            f.Repo.Query().ChangedWithin(window).FromOrigin(f.B).Has(f.Speed).CopyTo(list);
            sink += list.Count;
            f.Repo.Query().NotWrittenWithin(window).Count();
            sink += f.Repo.Query().WrittenWithin(window).Lacks(f.Speed).Count();
            f.Repo.Query().ChangedSince(1).AttributeChangedWithin(f.Count, window).CopyTo(list);
            f.Repo.Query().InBox(f.Pos, new Vec3(20, 20, -1), new Vec3(80, 60, 1)).CopyTo(list);
            sink += list.Count;
            f.Repo.Query().WithinRadius(f.Pos, new Vec3(100, 40, 0), 25).Has(f.Count).ForEach(ref visitor);
            f.Repo.Query().InCell(f.Pos, new CellKey(2, 1, 0)).ForEach(ref visitor);
            sink += f.Repo.Query().InCell(f.Pos, new CellKey(2, 1, 0)).Any() ? 1 : 0;
            f.Repo.Query().ReferencedBy(f.Parent, unit).CopyTo(list);
            sink += list.Count;
            f.Repo.Query().KnownTo(known).Has(f.Speed).CopyTo(list);
            sink += list.Count;
            f.Repo.Query().Has(f.Speed).Has(f.Pos).Lacks(f.Target).FromOrigin(f.B).CopyTo(list);
            f.Repo.Query().NotFromOrigin(f.A).Has(f.Pos).Where(f.Count, static (e, c) => e.Get(c).AsInt() % 10 == 0).CopyTo(list);
            sink += list.Count;
            sink += f.Repo.Query().InSimTime().ChangedSince(1).InBox(f.Pos, new Vec3(0, 0, -1), new Vec3(40, 40, 1)).Count();
            sink += f.Repo.Query().ReferencedBy(f.Parent, unit).InBox(f.Pos, new Vec3(0, 0, -1), new Vec3(200, 200, 1))
                .Where(f.Count, static (e, c) => e.Has(c)).Count();
            total += visitor.Seen;
            t += 0.001;
        }

        long allocated = Measure(Round);

        Assert.Equal(0, allocated);
        Assert.True(sink > 0);
        Assert.True(total > 0);
    }

    [Fact]
    public void Resolving_struct_keys_allocates_nothing()
    {
        var f = new QueryFixture();
        var dis = f.Identities.Create<DisId>(f.A, "rpr.entityId");
        var composite = f.Identities.Create<MemberKey>(f.A, "ned.member");
        var ids = new List<EntityId>();
        f.At(1, f.A, cs =>
        {
            for (int i = 0; i < 100; i++)
            {
                var id = cs.CreateEntity();
                ids.Add(id);
                dis.Bind(cs, id, new DisId(1, 3001, (ushort)i));
                composite.Bind(cs, id, new MemberKey(7, i));
            }
        });

        var pending = f.Repo.Begin(f.A, 5, 5);
        long hits = 0;

        long allocated = Measure(i =>
        {
            var key = new DisId(1, 3001, (ushort)(i % 100));
            if (dis.TryResolve(key, out var id) && dis.TryGetKey(id, out var back) && back == key)
            {
                hits++;
            }

            if (composite.TryResolve(pending, new MemberKey(7, i % 100), out var viaPending) && viaPending == id)
            {
                hits++;
            }

            if (composite.TryGetKey(id, out var member) && member.Index == i % 100 && composite.Contains(id))
            {
                hits++;
            }

            if (dis.TryGetOrAllocate(id, out var existing) && existing == key)
            {
                hits++;
            }
        });

        pending.Discard();
        Assert.Equal(0, allocated);
        Assert.Equal(4L * (Warmup + Measured), hits);
    }

    private sealed class Setup
    {
        public readonly EngineFixture F;
        public readonly AttrId Spatial;
        public readonly AttrId Parent;
        public readonly IdentityMap<DisId> Dis;
        public readonly EntityId[] Ids = new EntityId[100];
        public readonly EntityId[] Units = new EntityId[5];

        public Setup()
        {
            F = new EngineFixture(new GatewayOptions { JournalCapacity = 16, EventLogCapacity = 16 });
            var f = F;
            Spatial = f.Engine.Attributes.Register("Spatial.Position", AttrKind.Vec3, null, AttrOptions.SpatialIndex, 10);
            Parent = f.Engine.Attributes.Register("Structure.Parent", AttrKind.EntityRef, null, AttrOptions.IndexReferences);
            var spatial = Spatial;
            var parent = Parent;
            IdentityMap<DisId> dis = null!;
            f.Module("ingress", s =>
            {
                dis = s.Identities.Create<DisId>(f.Net, "rpr.entityId");
                s.OnMessage<DisMoveMsg>((in DisMoveMsg m, IngressContext ctx) =>
                {
                    if (dis.TryResolve(ctx.Changes, m.Key, out var id))
                    {
                        ctx.Changes.Set(id, spatial, AttrValue.From(m.Pos));
                    }
                });
                s.OnMessage<ReparentMsg>((in ReparentMsg m, IngressContext ctx) =>
                {
                    ctx.Changes.Set(m.Entity, parent, AttrValue.From(m.Parent));
                    if (m.HasSpeed)
                    {
                        ctx.Changes.Set(m.Entity, f.Speed, EngineFixture.D(1));
                    }
                    else
                    {
                        ctx.Changes.Clear(m.Entity, f.Speed);
                    }
                });
            });
            f.Module("egress", s =>
            {
                s.OnAttributesChanged(spatial).When(static _ => true).Do(static _ => { });
                s.OnAttributesChanged(parent).Do(static _ => { });
                s.OnChangeSetEnd().Do(static _ => { });
            });
            Dis = dis;

            var cs = f.Engine.BeginChange(f.Net);
            for (int u = 0; u < Units.Length; u++)
            {
                Units[u] = cs.CreateEntity();
            }

            for (int i = 0; i < Ids.Length; i++)
            {
                Ids[i] = cs.CreateEntity();
                dis.Bind(cs, Ids[i], new DisId(1, 3001, (ushort)i));
                cs.Set(Ids[i], spatial, AttrValue.From(new Vec3(i % 4 * 10 + 5, 0, 0))); // four cells, 25 entities each
                cs.Set(Ids[i], parent, AttrValue.From(Units[i % Units.Length]));
            }

            Assert.True(cs.Commit().Succeeded);
        }
    }

    [Fact]
    public void A_round_trip_that_moves_a_spatially_indexed_position_between_used_cells_allocates_nothing()
    {
        var s = new Setup();

        long allocated = Measure(i =>
        {
            int e = i % 100;
            // the entity hops to another of the four occupied cells every time
            double x = (e + i / 100 % 3 + 1) % 4 * 10 + 5;
            s.F.Engine.Ingest(s.F.Net, new DisMoveMsg(new DisId(1, 3001, (ushort)e), new Vec3(x, 1, 2)));
        });

        Assert.Equal(0, allocated);
        Assert.Equal(Warmup + Measured, s.F.Engine.Diagnostics.MessagesProcessed);
        Assert.Equal(100, s.F.Engine.State.Query().Has(s.Spatial).Count());
        Assert.Equal(4, s.F.Engine.State.OccupiedCellCount(s.Spatial));
        // the index still agrees with the data after all those moves
        int inCells = 0;
        for (int c = 0; c < 4; c++)
        {
            inCells += s.F.Engine.State.CellEntityCount(s.Spatial, new CellKey(c, 0, 0));
        }

        Assert.Equal(100, inCells);
    }

    [Fact]
    public void Re_parenting_and_toggling_an_attribute_allocate_nothing_in_steady_state()
    {
        var s = new Setup();

        long allocated = Measure(i =>
        {
            int e = i % 100;
            s.F.Engine.Ingest(s.F.Net, new ReparentMsg(s.Ids[e], s.Units[(e + i / 100 + 1) % s.Units.Length], i % 2 == 0));
        });

        Assert.Equal(0, allocated);
        int referrers = 0;
        foreach (var unit in s.Units)
        {
            referrers += s.F.Engine.State.ReferrerCount(s.Parent, unit);
        }

        Assert.Equal(100, referrers);
    }

    [Fact]
    public void Forming_and_dissolving_cells_and_reference_sets_reuse_pooled_sets_without_allocating()
    {
        var f = new QueryFixture();
        var units = new List<EntityId>();
        var movers = new List<EntityId>();
        f.At(1, f.A, cs =>
        {
            for (int u = 0; u < 50; u++)
            {
                units.Add(cs.CreateEntity());
            }

            for (int m = 0; m < 20; m++)
            {
                var id = cs.CreateEntity();
                movers.Add(id);
                cs.Set(id, f.Pos, QueryFixture.P(m * 10 + 5, 0, 0));      // every mover alone in its own cell
                cs.Set(id, f.Parent, QueryFixture.R(units[m]));           // and alone behind its own target
            }
        });

        long allocated = Measure(i =>
        {
            var cs = f.Repo.Begin(f.A, 10 + i, 10 + i);
            int shift = i % 50;
            for (int m = 0; m < movers.Count; m++)
            {
                // every set is emptied and a new one formed on each step
                cs.Set(movers[m], f.Pos, QueryFixture.P((m + shift) % 50 * 10 + 5, 0, 0));
                cs.Set(movers[m], f.Parent, QueryFixture.R(units[(m + shift) % 50]));
            }

            cs.Commit();
        });

        Assert.Equal(0, allocated);
        Assert.Equal(20, f.Repo.OccupiedCellCount(f.Pos));
        Assert.Equal(20, f.Repo.Query().Has(f.Parent).Count());
    }
}
