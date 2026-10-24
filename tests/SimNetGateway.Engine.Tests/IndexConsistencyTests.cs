using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

/// <summary>
/// Randomized consistency of the indexes (invariant G-3 style): after every commit, recency lists, presence sets,
/// the reverse reference index and the spatial grid must equal a brute-force scan of the entities.
/// </summary>
public class IndexConsistencyTests
{
    private static List<EntityRecord> Live(StateRepository repo)
    {
        var list = new List<EntityRecord>();
        foreach (var e in repo.Entities)
        {
            list.Add(e);
        }

        return list;
    }

    private static int[] Ids(IEnumerable<EntityRecord> entities) => entities.Select(e => e.Id.Value).Order().ToArray();

    private static List<EntityRecord> Walk(RecencyEnumerable list)
    {
        var result = new List<EntityRecord>();
        foreach (var e in list)
        {
            result.Add(e);
        }

        return result;
    }

    /// <summary>Brute-force reference of the origin-write fields (R-068), maintained by the test from its own operations.</summary>
    private sealed class OriginModel
    {
        public double Wall;
        public double Sim;
        public SourceId Owner;
        public readonly HashSet<AttrId> Present = new();
    }

    private static void Verify(QueryFixture f, Random rng, int maxId, string context,
        Dictionary<EntityId, OriginModel> model)
    {
        var repo = f.Repo;
        var live = Live(repo);
        var liveIds = Ids(live);

        // recency lists: complete, no duplicates, ordered by time
        foreach (var (list, time, name) in new (RecencyEnumerable, Func<EntityRecord, double>, string)[]
        {
            (repo.ByLastWrite, e => e.LastWriteWallTime, "write"),
            (repo.ByLastChange, e => e.LastChangeWallTime, "change"),
            (repo.ByLastOriginWrite, e => e.LastOriginWriteWallTime, "origin write"),
        })
        {
            var walked = Walk(list);
            Assert.True(liveIds.SequenceEqual(Ids(walked)), $"{context}: {name} list is not the live set");
            for (int i = 1; i < walked.Count; i++)
            {
                Assert.True(time(walked[i - 1]) <= time(walked[i]), $"{context}: {name} list is not ordered by time");
            }
        }

        // origin-write fields and owners equal the reference model, which has exactly the live entities
        Assert.Equal(live.Count, model.Count);
        foreach (var e in live)
        {
            var m = model[e.Id];
            Assert.True(m.Wall == e.LastOriginWriteWallTime, $"{context}: origin wall time of {e.Id.Value}: {e.LastOriginWriteWallTime} vs model {m.Wall}");
            Assert.True(m.Sim == e.LastOriginWriteSimTime, $"{context}: origin sim time of {e.Id.Value}: {e.LastOriginWriteSimTime} vs model {m.Sim}");
            Assert.True(m.Owner == e.Owner, $"{context}: owner of {e.Id.Value}");
        }

        // presence sets
        foreach (var attr in new[] { f.Count, f.Speed, f.Pos, f.Target, f.Parent })
        {
            var expected = Ids(live.Where(e => e.Has(attr)));
            Assert.True(expected.SequenceEqual(QueryFixture.Ids(repo.Query().Has(attr))), $"{context}: presence of {attr.Value}");
            Assert.Equal(expected.Length, repo.Query().Has(attr).Count());
            // the queries re-check every candidate, so stale entries of the set itself are caught by its size
            Assert.True(expected.Length == repo.PresenceCount(attr), $"{context}: presence set size of {attr.Value}");
        }

        // reverse reference index, also for targets that are already dead
        int referrersTotal = 0;
        for (int target = 1; target <= maxId; target++)
        {
            var id = new EntityId(target);
            var expected = Ids(live.Where(e => e.TryGet(f.Parent, out var v) && v.AsEntity() == id));
            Assert.True(expected.SequenceEqual(QueryFixture.Ids(repo.Query().ReferencedBy(f.Parent, id))), $"{context}: referrers of {target}");
            Assert.True(expected.Length == repo.ReferrerCount(f.Parent, id), $"{context}: reverse index size for {target}");
            referrersTotal += expected.Length;
        }

        Assert.Equal(live.Count(e => e.Has(f.Parent)), referrersTotal);

        // spatial grid: every cell of a window around the data, plus random boxes and spheres
        foreach (var (attr, cell) in new[] { (f.Pos, 10.0), (f.Target, 5.0) })
        {
            for (int x = -3; x <= 3; x++)
            {
                for (int y = -3; y <= 3; y++)
                {
                    var key = new CellKey(x, y, 0);
                    var expected = Ids(live.Where(e => e.TryGet(attr, out var v) && CellKey.Of(v.AsVec3(), cell) == key));
                    Assert.True(expected.SequenceEqual(QueryFixture.Ids(repo.Query().InCell(attr, key))), $"{context}: cell {key} of {attr.Value}");
                    Assert.True(expected.Length == repo.CellEntityCount(attr, key), $"{context}: cell size {key} of {attr.Value}");
                }
            }

            // no stale or empty cells anywhere: as many cells as distinct cells of the live positions
            var distinct = live.Where(e => e.Has(attr)).Select(e => CellKey.Of(e.Get(attr).AsVec3(), cell)).Distinct().Count();
            Assert.True(distinct == repo.OccupiedCellCount(attr), $"{context}: occupied cells of {attr.Value}");

            for (int k = 0; k < 3; k++)
            {
                var min = new Vec3(rng.Next(-40, 20), rng.Next(-40, 20), rng.Next(-5, 5));
                var max = new Vec3(min.X + rng.Next(0, 60), min.Y + rng.Next(0, 60), min.Z + rng.Next(0, 10));
                var expectedBox = Ids(live.Where(e => e.TryGet(attr, out var v) &&
                    v.AsVec3().X >= min.X && v.AsVec3().X <= max.X && v.AsVec3().Y >= min.Y && v.AsVec3().Y <= max.Y &&
                    v.AsVec3().Z >= min.Z && v.AsVec3().Z <= max.Z));
                Assert.True(expectedBox.SequenceEqual(QueryFixture.Ids(repo.Query().InBox(attr, min, max))), $"{context}: box {min}-{max}");

                var center = new Vec3(rng.Next(-30, 30), rng.Next(-30, 30), 0);
                double radius = rng.Next(0, 40);
                var expectedSphere = Ids(live.Where(e => e.TryGet(attr, out var v) &&
                    (v.AsVec3() - center) is var d && d.X * d.X + d.Y * d.Y + d.Z * d.Z <= radius * radius));
                Assert.True(expectedSphere.SequenceEqual(QueryFixture.Ids(repo.Query().WithinRadius(attr, center, radius))), $"{context}: sphere");
            }
        }

        // time windows against the fields of the records
        double now = repo.Now;
        double window = rng.Next(0, 30);
        var span = TimeSpan.FromSeconds(window);
        Assert.True(Ids(live.Where(e => e.LastChangeWallTime >= now - window)).SequenceEqual(QueryFixture.Ids(repo.Query().ChangedWithin(span))), $"{context}: ChangedWithin");
        Assert.True(Ids(live.Where(e => e.LastWriteWallTime >= now - window)).SequenceEqual(QueryFixture.Ids(repo.Query().WrittenWithin(span))), $"{context}: WrittenWithin");
        Assert.True(Ids(live.Where(e => e.LastWriteWallTime < now - window)).SequenceEqual(QueryFixture.Ids(repo.Query().NotWrittenWithin(span))), $"{context}: NotWrittenWithin");
        Assert.True(Ids(live.Where(e => model[e.Id].Wall >= now - window)).SequenceEqual(QueryFixture.Ids(repo.Query().OriginWrittenWithin(span))), $"{context}: OriginWrittenWithin");
        Assert.True(Ids(live.Where(e => model[e.Id].Wall < now - window)).SequenceEqual(QueryFixture.Ids(repo.Query().NotOriginWrittenWithin(span))), $"{context}: NotOriginWrittenWithin");
        var side = rng.Next(2) == 0 ? f.A : f.B;
        Assert.True(Ids(live.Where(e => e.Origin == side && model[e.Id].Wall < now - window)).SequenceEqual(
            QueryFixture.Ids(repo.Query().FromOrigin(side).NotOriginWrittenWithin(span))), $"{context}: FromOrigin + NotOriginWrittenWithin");
        double simNow = repo.SimNow;
        Assert.True(Ids(live.Where(e => model[e.Id].Sim >= simNow - window)).SequenceEqual(QueryFixture.Ids(repo.Query().InSimTime().OriginWrittenWithin(span))), $"{context}: sim OriginWrittenWithin");
        Assert.True(Ids(live.Where(e => model[e.Id].Sim < simNow - window)).SequenceEqual(QueryFixture.Ids(repo.Query().InSimTime().NotOriginWrittenWithin(span))), $"{context}: sim NotOriginWrittenWithin");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(20260424)]
    public void Indexes_equal_a_brute_force_scan_after_random_operations(int seed)
    {
        var rng = new Random(seed);
        var f = new QueryFixture();
        var live = new List<EntityId>();
        var all = new List<EntityId>();
        var model = new Dictionary<EntityId, OriginModel>();
        var processor = f.Sources.Register("processor"); // writes derived values, owns nothing
        var sources = new[] { f.A, f.B, processor };
        double time = 1;
        int operations = 0;
        int batches = 0;

        while (operations < 3000)
        {
            time += rng.Next(0, 4) == 0 ? 0 : rng.NextDouble() * 3; // non-decreasing, sometimes equal
            var destroyedNow = new HashSet<EntityId>();
            var source = sources[rng.Next(4) == 0 ? 2 : rng.Next(2)];
            double sim = time * 0.5; // distinct from wall time, so a mix-up of the two is caught
            var cs = f.Repo.Begin(source, time, sim);
            var createdNow = new List<EntityId>();
            var ownerBefore = new Dictionary<EntityId, SourceId>();           // owner before the transaction, per transferred entity
            var curOwner = new Dictionary<EntityId, SourceId>();              // owner now, per transferred entity
            var lastOp = new Dictionary<(EntityId, AttrId), bool>();          // last command per slot: true = Set
            var originWritten = new HashSet<EntityId>();                      // entities with a write by the owner (transfers)

            void Set(EntityId id, AttrId attr, AttrValue value)
            {
                cs.Set(id, attr, value);
                lastOp[(id, attr)] = true;
            }

            void Clear(EntityId id, AttrId attr)
            {
                cs.Clear(id, attr);
                lastOp[(id, attr)] = false;
            }

            int n = rng.Next(1, 9);
            for (int i = 0; i < n; i++)
            {
                operations++;
                var pool = live.Where(x => !destroyedNow.Contains(x)).Concat(createdNow.Where(x => !destroyedNow.Contains(x))).ToList();
                int op = pool.Count < 5 ? 0 : rng.Next(0, 106);
                if (op < 12)
                {
                    var id = cs.CreateEntity();
                    createdNow.Add(id);
                    all.Add(id);
                    if (rng.Next(2) == 0)
                    {
                        Set(id, f.Pos, RandomPos(rng));
                    }

                    continue;
                }

                var target = pool[rng.Next(pool.Count)];
                if (op < 20)
                {
                    cs.DestroyEntity(target);
                    destroyedNow.Add(target);
                }
                else if (op < 40)
                {
                    Set(target, f.Pos, RandomPos(rng));
                }
                else if (op < 48)
                {
                    Set(target, f.Target, RandomPos(rng));
                }
                else if (op < 56)
                {
                    Clear(target, rng.Next(2) == 0 ? f.Pos : f.Target);
                }
                else if (op < 72)
                {
                    // re-parent: any entity ever created, alive or not
                    Set(target, f.Parent, QueryFixture.R(all[rng.Next(all.Count)]));
                }
                else if (op < 78)
                {
                    Clear(target, f.Parent);
                }
                else if (op < 90)
                {
                    Set(target, f.Count, QueryFixture.I(rng.Next(0, 3))); // often the same value: written, not changed
                }
                else if (op < 95)
                {
                    Set(target, f.Speed, QueryFixture.D(rng.Next(0, 3)));
                }
                else if (op < 100)
                {
                    Clear(target, f.Speed);
                }
                else if (!createdNow.Contains(target))
                {
                    // ownership moves around: to another side, the processor or (a no-op) the current owner
                    var newOwner = sources[rng.Next(3)];
                    var current = curOwner.TryGetValue(target, out var c) ? c : model[target].Owner;
                    ownerBefore.TryAdd(target, current);
                    cs.TransferOwner(target, newOwner);
                    curOwner[target] = newOwner;
                    if (newOwner != current && source == ownerBefore[target])
                    {
                        originWritten.Add(target);
                    }
                }
            }

            var result = cs.Commit();
            Assert.True(result.Succeeded, result.Reason);
            live.RemoveAll(destroyedNow.Contains);
            live.AddRange(createdNow.Where(x => !destroyedNow.Contains(x)));
            batches++;

            // reference model of the origin-write fields: an owner's write (by the owner before the transaction)
            // counts when it applied; a Set always applies (only the last command per slot), a Clear only on a
            // present slot
            foreach (var ((id, attr), isSet) in lastOp)
            {
                if (createdNow.Contains(id) || destroyedNow.Contains(id))
                {
                    continue;
                }

                var m = model[id];
                var before = ownerBefore.TryGetValue(id, out var ob) ? ob : m.Owner;
                bool applied = isSet || m.Present.Contains(attr);
                if (applied && before == source)
                {
                    originWritten.Add(id);
                }
            }

            foreach (var ((id, attr), isSet) in lastOp)
            {
                if (createdNow.Contains(id) || destroyedNow.Contains(id))
                {
                    continue;
                }

                if (isSet)
                {
                    model[id].Present.Add(attr);
                }
                else
                {
                    model[id].Present.Remove(attr);
                }
            }

            foreach (var id in originWritten)
            {
                if (!createdNow.Contains(id) && !destroyedNow.Contains(id))
                {
                    model[id].Wall = time;
                    model[id].Sim = sim;
                }
            }

            foreach (var (id, owner) in curOwner)
            {
                if (!destroyedNow.Contains(id))
                {
                    model[id].Owner = owner;
                }
            }

            foreach (var id in destroyedNow)
            {
                model.Remove(id);
            }

            foreach (var id in createdNow.Where(x => !destroyedNow.Contains(x)))
            {
                var m = new OriginModel { Wall = time, Sim = sim, Owner = source };
                foreach (var ((eid, attr), isSet) in lastOp)
                {
                    if (eid == id && isSet)
                    {
                        m.Present.Add(attr);
                    }
                }

                model[id] = m;
            }

            Verify(f, rng, all.Count + 1, $"seed {seed}, batch {batches}", model);
        }

        Assert.True(f.Repo.Count > 10);
        Assert.Equal(live.Count, f.Repo.Count);
    }

    private static AttrValue RandomPos(Random rng)
    {
        // a small area, so that cells fill and empty again; sometimes exactly on a cell border
        double Axis() => rng.Next(0, 5) == 0 ? rng.Next(-5, 6) * 10 : rng.NextDouble() * 60 - 30;
        return QueryFixture.P(Axis(), Axis(), rng.Next(-1, 2));
    }
}
