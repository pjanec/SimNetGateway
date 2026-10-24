using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Common.Tests;

/// <summary>Time-sliced dead-reckoning extrapolation (R-069, DD-001 section 14 P-42).</summary>
public class SlicedDrTests
{
    private const double Period = 4.2;   // 42,000,000 ticks: divisible by every tested slice count
    private const double Eps = 1e-6;     // ticks run just after the due time, immune to floating-point drift of the timer

    private enum Kind { Moving, Static, Unselected }

    private sealed class Info
    {
        public Kind Kind;
        public Vec3 Pos;
        public Vec3 Vel;
        public double StateTime;
        public int Created;                   // first firing index at which the entity exists
        public int Destroyed = int.MaxValue;  // first firing index at which it no longer exists

        public bool VisibleAt(int firing) => Created <= firing && firing < Destroyed;
    }

    private sealed class Run
    {
        public Rig Rig = null!;
        public Dictionary<EntityId, Info> Model = new();
        public int Slices;
        public int Firings;
        public double SliceSeconds;
        public List<(int Firing, EntityId Id, Vec3 Pos, double At)> Writes = new();
        public List<(SourceId Source, int Entities, List<AttrId> Attrs)> DrSets = new();
    }

    private static Run Simulate(int slices, int seed, int cycles, int initial, bool explicitSlices = true)
    {
        var run = new Run { Slices = slices, Rig = new Rig(), Firings = cycles * slices };
        var r = run.Rig;
        r.AddObservers();
        if (explicitSlices)
        {
            r.AddDr(Period, slices: slices);
        }
        else
        {
            r.AddDr(Period);
        }

        run.SliceSeconds = TimeSpan.FromSeconds(Period).Ticks / slices / 1e7;
        var rng = new Random(seed);
        var alive = new List<EntityId>();

        void Create(int firing)
        {
            double roll = rng.NextDouble();
            var kind = roll < 0.6 ? Kind.Moving : roll < 0.85 ? Kind.Static : Kind.Unselected;
            var info = new Info
            {
                Kind = kind,
                Pos = new Vec3(rng.Next(-1000, 1000), rng.Next(-1000, 1000), rng.Next(0, 100)),
                Vel = kind == Kind.Moving ? new Vec3(rng.Next(1, 50), rng.Next(-20, 20), 0) : Vec3.Zero,
                StateTime = firing * run.SliceSeconds,
                Created = firing,
            };
            EntityId id;
            if (kind == Kind.Unselected)
            {
                var cs = r.Engine.BeginChange(r.Net);
                id = cs.CreateEntity();
                cs.Set(id, r.Pos, AttrValue.From(info.Pos));
                Assert.True(cs.Commit().Succeeded);
            }
            else
            {
                id = r.CreateMover(info.Pos, info.Vel, kind == Kind.Moving ? DisDeadReckoning.Fpw : DisDeadReckoning.Static,
                    info.StateTime);
            }

            run.Model[id] = info;
            alive.Add(id);
        }

        for (int i = 0; i < initial; i++)
        {
            Create(0);
        }

        for (int f = 0; f < run.Firings; f++)
        {
            if (f > 0)
            {
                int creations = rng.NextDouble() < 0.3 ? rng.Next(1, 4) : 0;
                for (int c = 0; c < creations; c++)
                {
                    Create(f);
                }

                if (alive.Count > 0 && rng.NextDouble() < 0.2)
                {
                    int pick = rng.Next(alive.Count);
                    var cs = r.Engine.BeginChange(r.Net);
                    cs.DestroyEntity(alive[pick]);
                    Assert.True(cs.Commit().Succeeded);
                    run.Model[alive[pick]].Destroyed = f;
                    alive.RemoveAt(pick);
                }
            }

            r.TickAt((f + 1) * run.SliceSeconds + Eps);
        }

        foreach (var w in r.PredictionWrites)
        {
            int firing = (int)Math.Round(w.At / run.SliceSeconds) - 1;
            Assert.Equal((firing + 1) * run.SliceSeconds + Eps, w.At, 9);
            run.Writes.Add((firing, w.Entity, w.Pos, w.At));
        }

        run.DrSets.AddRange(r.ChangeSets.Where(c => c.Source == r.Dr));
        return run;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (int n in new[] { 1, 2, 3, 6, 7 })
        {
            foreach (int seed in new[] { 1, 2, 3, 4 })
            {
                yield return new object[] { n, seed, 13 + seed * 11 };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_selected_entity_is_predicted_exactly_once_per_cycle_with_the_value_of_its_firing(
        int slices, int seed, int initial)
    {
        const int cycles = 6;
        var run = Simulate(slices, seed, cycles, initial);
        var byEntity = run.Writes.GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.ToList());

        // each write: entity visible and selected at that firing, value = prediction at that firing's sim time
        foreach (var w in run.Writes)
        {
            var info = run.Model[w.Id];
            Assert.NotEqual(Kind.Unselected, info.Kind);
            Assert.True(info.VisibleAt(w.Firing), "written while not alive");
            Assert.True(w.Firing >= 0 && w.Firing < run.Firings);
            double dt = w.At - info.StateTime;
            Assert.Equal(info.Pos + info.Vel * dt, w.Pos);
        }

        int snapshotsTotal = 0;
        for (int c = 0; c < cycles; c++)
        {
            int first = c * slices, last = first + slices - 1;
            foreach (var (id, info) in run.Model)
            {
                if (info.Kind == Kind.Unselected)
                {
                    continue;
                }

                int count = byEntity.TryGetValue(id, out var ws) ? ws.Count(x => x.Firing >= first && x.Firing <= last) : 0;
                bool inSnapshot = info.VisibleAt(first);
                if (inSnapshot)
                {
                    snapshotsTotal++;
                }

                if (!inSnapshot)
                {
                    Assert.Equal(0, count); // created during the cycle: first predicted in the next one
                }
                else if (info.Kind == Kind.Moving)
                {
                    if (info.VisibleAt(last))
                    {
                        Assert.Equal(1, count);
                    }
                    else
                    {
                        Assert.InRange(count, 0, 1);
                    }
                }
                else
                {
                    // a static entity is written once, in the first cycle in which it is processed, never again
                    int before = ws?.Count(x => x.Firing < first) ?? 0;
                    if (before > 0)
                    {
                        Assert.Equal(0, count);
                    }
                    else if (info.VisibleAt(last))
                    {
                        Assert.Equal(1, count);
                    }
                    else
                    {
                        Assert.InRange(count, 0, 1);
                    }
                }
            }
        }

        Assert.True(snapshotsTotal > 0);
        foreach (var (id, _) in run.Model.Where(m => m.Value.Kind == Kind.Static))
        {
            Assert.True(!byEntity.TryGetValue(id, out var ws) || ws.Count == 1);
        }

        // transactions: none empty, one per firing that wrote, at most one chunk of the snapshot each
        Assert.All(run.DrSets, c => Assert.True(c.Entities > 0));
        Assert.Equal(run.Writes.Select(w => w.Firing).Distinct().Count(), run.DrSets.Count);
        int maxSnapshot = Enumerable.Range(0, cycles)
            .Max(c => run.Model.Values.Count(i => i.Kind != Kind.Unselected && i.VisibleAt(c * slices)));
        int maxChunk = (maxSnapshot + slices - 1) / slices;
        Assert.All(run.DrSets, c => Assert.True(c.Entities <= maxChunk));
    }

    [Fact]
    public void With_one_slice_the_default_and_the_explicit_parameter_give_identical_transactions_and_values()
    {
        for (int seed = 1; seed <= 3; seed++)
        {
            var a = Simulate(1, seed, 8, 17, explicitSlices: false);
            var b = Simulate(1, seed, 8, 17, explicitSlices: true);
            Assert.Equal(a.Writes.Count, b.Writes.Count);
            for (int i = 0; i < a.Writes.Count; i++)
            {
                Assert.Equal(a.Writes[i].Pos, b.Writes[i].Pos);
                Assert.Equal(a.Writes[i].At, b.Writes[i].At);
                Assert.Equal(a.Writes[i].Firing, b.Writes[i].Firing);
            }

            Assert.Equal(a.DrSets.Select(c => c.Entities), b.DrSets.Select(c => c.Entities));

            // unsliced: all entities of a firing are predicted in that firing's single transaction
            var perFiring = a.Writes.GroupBy(w => w.Firing).OrderBy(g => g.Key).Select(g => g.Count()).ToList();
            Assert.Equal(perFiring, a.DrSets.Select(c => c.Entities).ToList());
        }
    }

    [Fact]
    public void The_rule_fires_slices_times_per_period()
    {
        foreach (int n in new[] { 1, 3, 7 })
        {
            var r = new Rig();
            r.AddDr(Period, slices: n);
            double slice = TimeSpan.FromSeconds(Period).Ticks / n / 1e7;
            for (int f = 1; f <= 2 * n; f++)
            {
                r.TickAt(f * slice + Eps);
            }

            Assert.Equal(2 * n, r.Engine.FindRule("dr/Extrapolate")!.FireCount);
        }
    }

    [Fact]
    public void Chunks_are_contiguous_and_cover_a_population_that_does_not_divide_by_the_slice_count()
    {
        // 10 entities, 4 slices: chunks of 3, 3, 3, 1 -> every entity once per cycle
        var r = new Rig();
        r.AddObservers();
        r.AddDr(4.0, slices: 4);
        for (int i = 0; i < 10; i++)
        {
            r.CreateMover(new Vec3(i, 0, 0), new Vec3(1, 0, 0), DisDeadReckoning.Fpw);
        }

        for (int f = 1; f <= 4; f++)
        {
            r.TickAt(f * 1.0);
        }

        var sizes = r.ChangeSets.Where(c => c.Source == r.Dr).Select(c => c.Entities).ToArray();
        Assert.Equal(new[] { 3, 3, 3, 1 }, sizes);
        Assert.Equal(10, r.PredictionWrites.Select(w => w.Entity).Distinct().Count());
        Assert.Equal(10, r.PredictionWrites.Count);
    }

    [Fact]
    public void An_entity_created_mid_cycle_waits_for_the_next_snapshot_and_a_dead_one_is_skipped()
    {
        var r = new Rig();
        r.AddObservers();
        r.AddDr(4.0, slices: 4);
        var a = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);
        var b = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);
        r.TickAt(1.0);   // slice 0: snapshot [a, b]; chunk of 1 -> one of them
        int writtenFirst = r.PredictionWrites.Count;
        Assert.Equal(1, writtenFirst);
        var late = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);
        var cs = r.Engine.BeginChange(r.Net);
        cs.DestroyEntity(b);
        Assert.True(cs.Commit().Succeeded);
        for (int f = 2; f <= 4; f++)
        {
            r.TickAt(f * 1.0);
        }

        Assert.DoesNotContain(r.PredictionWrites, w => w.Entity == late);
        Assert.DoesNotContain(r.PredictionWrites, w => w.Entity == b && w.At > 1.0);
        Assert.Equal(1, r.PredictionWrites.Count(w => w.Entity == a));
        for (int f = 5; f <= 8; f++)
        {
            r.TickAt(f * 1.0);
        }

        Assert.Equal(1, r.PredictionWrites.Count(w => w.Entity == late));
        Assert.Equal(2, r.PredictionWrites.Count(w => w.Entity == a));
    }

    [Fact]
    public void An_entity_that_lost_a_required_attribute_during_the_cycle_is_skipped_without_error()
    {
        foreach (var lost in new[] { "pos", "alg", "t" })
        {
            var r = new Rig();
            r.AddObservers();
            r.AddDr(4.0, slices: 4);
            var ids = new List<EntityId>();
            for (int i = 0; i < 8; i++)
            {
                ids.Add(r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw));
            }

            r.TickAt(1.0); // snapshot of all 8; chunks of 2
            var cs = r.Engine.BeginChange(r.Net);
            foreach (var id in ids.Skip(2))
            {
                cs.Clear(id, lost == "pos" ? r.Pos : lost == "alg" ? r.Alg : r.StateTime);
            }

            Assert.True(cs.Commit().Succeeded);
            for (int f = 2; f <= 4; f++)
            {
                r.TickAt(f * 1.0);
            }

            Assert.Equal(0, r.Engine.Diagnostics.RuleFailures); // reading a missing attribute would throw in the rule
            Assert.Equal(2, r.PredictionWrites.Count);
            Assert.All(r.PredictionWrites, w => Assert.Contains(w.Entity, ids.Take(2)));
        }
    }

    [Fact]
    public void No_writes_while_the_session_is_paused_with_slices()
    {
        var r = new Rig();
        r.AddObservers();
        r.AddDr(4.2, slices: 6);
        Assert.True(r.Engine.Execute(SessionCommand.Load("s"), r.Host));
        Assert.True(r.Engine.Execute(SessionCommand.Start, r.Host));
        for (int i = 0; i < 20; i++)
        {
            r.CreateMover(new Vec3(i, 0, 0), new Vec3(1, 0, 0), DisDeadReckoning.Fpw);
        }

        double slice = 0.7;
        for (int f = 1; f <= 6; f++)
        {
            r.TickAt(f * slice + Eps);
        }

        int written = r.PredictionWrites.Count;
        Assert.Equal(20, written);
        Assert.True(r.Engine.Execute(SessionCommand.Pause, r.Host));
        double frozen = r.Engine.SimTime;
        r.ChangeSets.Clear();

        // The first cycle after the pause may bring the predictions made earlier in the previous cycle up to the
        // frozen time, once per entity; afterwards nothing is written while the simulation time stands still.
        for (int f = 7; f <= 12; f++)
        {
            r.TickAt(f * slice + Eps);
        }

        Assert.InRange(r.PredictionWrites.Count, written, 2 * written);
        Assert.All(r.PredictionWrites.Skip(written), w => Assert.Equal(frozen, w.At));
        int settled = r.PredictionWrites.Count;
        r.ChangeSets.Clear();
        for (int f = 13; f <= 30; f++)
        {
            r.TickAt(f * slice + Eps);
        }

        Assert.Equal(settled, r.PredictionWrites.Count);
        Assert.DoesNotContain(r.ChangeSets, c => c.Source == r.Dr);
    }

    [Fact]
    public void A_steady_state_sliced_firing_over_1000_moving_entities_allocates_nothing()
    {
        var r = new Rig(new GatewayOptions { JournalCapacity = 16 });
        r.AddDr(periodSeconds: 0.6, slices: 6);
        var cs = r.Engine.BeginChange(r.Net);
        EntityId first = default;
        for (int i = 0; i < 1000; i++)
        {
            var id = cs.CreateEntity();
            if (i == 0)
            {
                first = id;
            }

            cs.Set(id, r.Pos, AttrValue.From(new Vec3(i, 0, 0)));
            cs.Set(id, r.Vel, AttrValue.From(new Vec3(10, 1, 0)));
            cs.Set(id, r.Alg, AttrValue.From((long)DisDeadReckoning.Fpw));
            cs.Set(id, r.StateTime, AttrValue.From(0.0));
        }

        Assert.True(cs.Commit().Succeeded);

        double t = 0;
        for (int i = 0; i < 60; i++)
        {
            t += 0.1;
            r.TickAt(t);
        }

        var rule = r.Engine.FindRule("dr/Extrapolate")!;
        long firings = rule.FireCount;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 12; i++)
        {
            t += 0.1;
            r.TickAt(t);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(firings + 12, rule.FireCount);
        Assert.Equal(0, allocated);
        Assert.True(r.PredictedPosition(first).X > 0);
    }

    [Fact]
    public void Slices_below_one_are_rejected()
    {
        var r = new Rig();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DrExtrapolationProcessor(
            "x", r.Dr, TimeSpan.FromSeconds(1), r.Input, r.PredPos, r.PredAt, r.Predictors, slices: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DrExtrapolationProcessor(
            "x", r.Dr, TimeSpan.FromSeconds(1), r.Input, r.PredPos, r.PredAt, r.Predictors, slices: -2));
    }
}
