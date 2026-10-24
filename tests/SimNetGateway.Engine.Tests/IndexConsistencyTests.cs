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

    private static void Verify(QueryFixture f, Random rng, int maxId, string context)
    {
        var repo = f.Repo;
        var live = Live(repo);
        var liveIds = Ids(live);

        // recency lists: complete, no duplicates, ordered by time
        foreach (var (list, time, name) in new (RecencyEnumerable, Func<EntityRecord, double>, string)[]
        {
            (repo.ByLastWrite, e => e.LastWriteWallTime, "write"),
            (repo.ByLastChange, e => e.LastChangeWallTime, "change"),
        })
        {
            var walked = Walk(list);
            Assert.True(liveIds.SequenceEqual(Ids(walked)), $"{context}: {name} list is not the live set");
            for (int i = 1; i < walked.Count; i++)
            {
                Assert.True(time(walked[i - 1]) <= time(walked[i]), $"{context}: {name} list is not ordered by time");
            }
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
        double time = 1;
        int operations = 0;
        int batches = 0;

        while (operations < 3000)
        {
            time += rng.Next(0, 4) == 0 ? 0 : rng.NextDouble() * 3; // non-decreasing, sometimes equal
            var destroyedNow = new HashSet<EntityId>();
            var cs = f.Repo.Begin(rng.Next(2) == 0 ? f.A : f.B, time, time);
            var createdNow = new List<EntityId>();
            int n = rng.Next(1, 9);
            for (int i = 0; i < n; i++)
            {
                operations++;
                var pool = live.Where(x => !destroyedNow.Contains(x)).Concat(createdNow.Where(x => !destroyedNow.Contains(x))).ToList();
                int op = pool.Count < 5 ? 0 : rng.Next(0, 100);
                if (op < 12)
                {
                    var id = cs.CreateEntity();
                    createdNow.Add(id);
                    all.Add(id);
                    if (rng.Next(2) == 0)
                    {
                        cs.Set(id, f.Pos, RandomPos(rng));
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
                    cs.Set(target, f.Pos, RandomPos(rng));
                }
                else if (op < 48)
                {
                    cs.Set(target, f.Target, RandomPos(rng));
                }
                else if (op < 56)
                {
                    cs.Clear(target, rng.Next(2) == 0 ? f.Pos : f.Target);
                }
                else if (op < 72)
                {
                    // re-parent: any entity ever created, alive or not
                    cs.Set(target, f.Parent, QueryFixture.R(all[rng.Next(all.Count)]));
                }
                else if (op < 78)
                {
                    cs.Clear(target, f.Parent);
                }
                else if (op < 90)
                {
                    cs.Set(target, f.Count, QueryFixture.I(rng.Next(0, 3))); // often the same value: written, not changed
                }
                else if (op < 95)
                {
                    cs.Set(target, f.Speed, QueryFixture.D(rng.Next(0, 3)));
                }
                else
                {
                    cs.Clear(target, f.Speed);
                }
            }

            var result = cs.Commit();
            Assert.True(result.Succeeded, result.Reason);
            live.RemoveAll(destroyedNow.Contains);
            live.AddRange(createdNow.Where(x => !destroyedNow.Contains(x)));
            batches++;

            Verify(f, rng, all.Count + 1, $"seed {seed}, batch {batches}");
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
