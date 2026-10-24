using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Monitoring.Tests;

/// <summary>The metrics sampler: percentiles and rates exact on known input, ring wrap-around, empty and short rings.</summary>
public class SamplerTests
{
    private static MetricsSampler Sampler(int capacity, params double[] tickMs)
    {
        var sampler = new MetricsSampler(capacity, ["ned", "rpr"]);
        for (int i = 0; i < tickMs.Length; i++)
        {
            Add(sampler, i, tickMs[i]);
        }

        return sampler;
    }

    // sample i happens at time 0.5 * i; side 0 received 10 * i messages, side 1 received i * i; sent 3 * i and 100 + i
    private static void Add(MetricsSampler sampler, int i, double tickMs) =>
        sampler.Record(new TickSample(0.5 * i, tickMs, new EngineCounters(i, 0.5 * i, 0.5 * i, 7, 5 * i, 0, 2 * i, 0, 0, 0, 0)),
            [10L * i, (long)i * i], [3L * i, 100L + i]);

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(20, 1.0)]
    [InlineData(21, 2.0)]
    [InlineData(40, 2.0)]
    [InlineData(41, 3.0)]
    [InlineData(50, 3.0)]
    [InlineData(80, 4.0)]
    [InlineData(81, 5.0)]
    [InlineData(95, 5.0)]
    [InlineData(100, 5.0)]
    public void Percentiles_are_nearest_rank_over_all_samples(double p, double expected)
    {
        var sampler = Sampler(10, 5, 1, 4, 2, 3);

        Assert.Equal(expected, sampler.Percentile(p));
        Assert.Equal(5.0, sampler.Max());
        Assert.Equal(1.0, sampler.Min());
        Assert.Equal(3.0, sampler.Mean());
    }

    [Fact]
    public void Percentiles_over_a_window_use_only_the_newest_samples()
    {
        var sampler = Sampler(10, 5, 1, 4, 2, 3); // newest three: 4, 2, 3 -> sorted 2, 3, 4

        Assert.Equal(2.0, sampler.Percentile(33, 3));
        Assert.Equal(3.0, sampler.Percentile(50, 3));
        Assert.Equal(4.0, sampler.Percentile(100, 3));
        Assert.Equal(3.0, sampler.Mean(3));
        Assert.Equal(3.0, sampler.Percentile(100, 1)); // only the newest
        Assert.Equal(5.0, sampler.Percentile(100, 99)); // a window larger than the ring is all of it
    }

    [Fact]
    public void The_ring_wraps_around_and_keeps_the_newest_samples_in_order()
    {
        var sampler = Sampler(4, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);

        Assert.Equal(4, sampler.Count);
        Assert.Equal(7.0, sampler[0].TickMilliseconds);
        Assert.Equal(10.0, sampler[3].TickMilliseconds);
        Assert.Equal(10.0, sampler.Latest.TickMilliseconds);
        Assert.Equal(7.0, sampler.Min());
        Assert.Equal(8.0, sampler.Percentile(50)); // 7 8 9 10 -> rank 2
        Assert.Equal(8.5, sampler.Mean());
        Assert.Equal(9, sampler[3].Counters.Tick);
        Assert.Equal(90, sampler.InTotal(3, 0));
        Assert.Equal(81, sampler.InTotal(3, 1));
        Assert.Equal(109, sampler.OutTotal(3, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler[4]);
    }

    [Fact]
    public void Rates_are_the_difference_of_totals_over_the_difference_of_times()
    {
        var sampler = Sampler(100, Enumerable.Repeat(1.0, 21).ToArray()); // samples 0..20, 0.5 s apart

        Assert.Equal(20.0, sampler.InRate(0));            // 10 per sample at 2 samples per second
        Assert.Equal(20.0, sampler.InRate(0, 4));
        Assert.Equal(6.0, sampler.OutRate(0, 4));         // 3 per sample
        Assert.Equal(2.0, sampler.OutRate(1, 10));        // +1 per sample
        Assert.Equal(2.0, sampler.TickRate());
        Assert.Equal(10.0, sampler.CounterRate(c => c.MessagesProcessed));
        Assert.Equal(4.0, sampler.CounterRate(c => c.TransactionFailures, 5));
    }

    [Fact]
    public void A_rate_over_a_window_uses_the_window_plus_one_samples()
    {
        var sampler = Sampler(100, Enumerable.Repeat(1.0, 21).ToArray());

        // side 1 received i*i: window 1 = samples 19 and 20: (400 - 361) / 0.5
        Assert.Equal(78.0, sampler.InRate(1, 1));
        // window 2 = samples 18 and 20: (400 - 324) / 1.0
        Assert.Equal(76.0, sampler.InRate(1, 2));
        // all = samples 0 and 20: 400 / 10
        Assert.Equal(40.0, sampler.InRate(1));
    }

    [Fact]
    public void Rates_follow_the_ring_after_it_wrapped()
    {
        var sampler = Sampler(5, Enumerable.Repeat(1.0, 21).ToArray()); // keeps samples 16..20

        Assert.Equal(5, sampler.Count);
        Assert.Equal(20.0, sampler.InRate(0));
        Assert.Equal((400.0 - 256.0) / 2.0, sampler.InRate(1)); // samples 16 and 20
        Assert.Equal(2.0, sampler.TickRate());
    }

    [Fact]
    public void An_empty_or_single_sample_ring_gives_zero_rates_and_zero_statistics()
    {
        var sampler = new MetricsSampler(8, ["ned"]);

        Assert.Equal(0.0, sampler.Percentile(50));
        Assert.Equal(0.0, sampler.Mean());
        Assert.Equal(0.0, sampler.InRate(0));
        Assert.Equal(0.0, sampler.TickRate());

        sampler.Record(new TickSample(1.0, 2.5, default), [5], [6]);

        Assert.Equal(2.5, sampler.Percentile(50));
        Assert.Equal(0.0, sampler.InRate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.InRate(1));
    }

    [Fact]
    public void A_short_totals_span_counts_the_missing_sides_as_zero()
    {
        var sampler = new MetricsSampler(8, ["a", "b"]);
        sampler.Record(new TickSample(0, 1, default), [4], []);

        Assert.Equal(4, sampler.InTotal(0, 0));
        Assert.Equal(0, sampler.InTotal(0, 1));
        Assert.Equal(0, sampler.OutTotal(0, 0));
    }

    [Fact]
    public void The_sampler_equals_a_brute_force_computation_after_every_record()
    {
        var rnd = new Random(5);
        var sampler = new MetricsSampler(37, ["x", "y", "z"]);
        var all = new List<(double Time, double Ms, long[] In, long[] Out)>();
        double time = 0;
        var totalIn = new long[3];
        var totalOut = new long[3];
        for (int i = 0; i < 400; i++)
        {
            time += rnd.Next(1, 5) * 0.25;
            for (int s = 0; s < 3; s++)
            {
                totalIn[s] += rnd.Next(0, 50);
                totalOut[s] += rnd.Next(0, 9);
            }

            double ms = rnd.Next(0, 1000) / 8.0;
            sampler.Record(new TickSample(time, ms, new EngineCounters(i, time, 0, 0, 0, 0, 0, 0, 0, 0, 0)), totalIn, totalOut);
            all.Add((time, ms, (long[])totalIn.Clone(), (long[])totalOut.Clone()));

            var kept = all.Skip(Math.Max(0, all.Count - 37)).ToList();
            foreach (int window in new[] { 0, 1, 5, 36, 80 })
            {
                var w = window == 0 ? kept : kept.Skip(Math.Max(0, kept.Count - window)).ToList();
                var sorted = w.Select(x => x.Ms).OrderBy(x => x).ToList();
                foreach (double p in new[] { 0, 1, 25, 50, 90, 99, 100 })
                {
                    double expected = sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Count), 1, sorted.Count) - 1];
                    Assert.Equal(expected, sampler.Percentile(p, window));
                }

                Assert.Equal(sorted.Count == 0 ? 0 : sorted.Average(), sampler.Mean(window), 9);
                int intervals = window == 0 ? kept.Count - 1 : Math.Min(window, kept.Count - 1);
                for (int s = 0; s < 3; s++)
                {
                    double expectedRate = 0;
                    if (kept.Count >= 2)
                    {
                        var first = kept[kept.Count - 1 - intervals];
                        var last = kept[^1];
                        expectedRate = (last.In[s] - first.In[s]) / (last.Time - first.Time);
                    }

                    Assert.Equal(expectedRate, sampler.InRate(s, window), 9);
                }
            }
        }
    }

    [Fact]
    public void Recording_from_an_engine_copies_its_counters_and_allocates_nothing()
    {
        var world = new World(seed: 9);
        world.Run(30);
        var sampler = new MetricsSampler(16, ["a", "b"]);
        long[] inTotals = [1, 2];
        long[] outTotals = [3, 4];

        sampler.Record(world.Engine, 1.25, inTotals, outTotals);

        var s = sampler.Latest;
        Assert.Equal(world.Engine.TickCount, s.Counters.Tick);
        Assert.Equal(world.Engine.WallTime, s.Time);
        Assert.Equal(world.Engine.State.Count, s.Counters.Entities);
        Assert.Equal(world.Engine.Diagnostics.MessagesProcessed, s.Counters.MessagesProcessed);
        Assert.Equal(world.Engine.Diagnostics.TransactionFailures, s.Counters.TransactionFailures);
        Assert.Equal(world.Engine.Diagnostics.RuleFailures, s.Counters.RuleFailures);

        for (int i = 0; i < 100; i++)
        {
            sampler.Record(world.Engine, 1.0, inTotals, outTotals);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            sampler.Record(world.Engine, 1.0, inTotals, outTotals);
            sampler.Percentile(95, 8);
            sampler.InRate(0, 8);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
