using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>The percentile helper of the benchmark report: exact values on known inputs and the edge cases.</summary>
public class PercentilesTests
{
    private static double[] OneToHundred() => Enumerable.Range(1, 100).Select(i => (double)i).ToArray();

    [Theory]
    [InlineData(50, 50)]
    [InlineData(95, 95)]
    [InlineData(99, 99)]
    [InlineData(100, 100)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    public void Nearest_rank_percentiles_of_1_to_100_are_exact(double percent, double expected)
    {
        Assert.Equal(expected, Percentiles.Of(OneToHundred(), percent));
    }

    [Fact]
    public void Ranks_round_up()
    {
        // n = 10: p50 -> rank 5, p51 -> rank 6, p90 -> rank 9, p91 -> rank 10
        var samples = Enumerable.Range(1, 10).Select(i => i * 10.0).ToArray();
        Assert.Equal(50.0, Percentiles.Of(samples, 50));
        Assert.Equal(60.0, Percentiles.Of(samples, 51));
        Assert.Equal(90.0, Percentiles.Of(samples, 90));
        Assert.Equal(100.0, Percentiles.Of(samples, 91));
    }

    [Fact]
    public void Unsorted_input_gives_the_same_result_and_is_not_modified()
    {
        var sorted = OneToHundred();
        var shuffled = sorted.Reverse().ToArray();
        for (int i = 0; i + 1 < shuffled.Length; i += 2)
        {
            (shuffled[i], shuffled[i + 1]) = (shuffled[i + 1], shuffled[i]);
        }

        var copy = (double[])shuffled.Clone();
        Assert.Equal(95.0, Percentiles.Of(shuffled, 95));
        Assert.Equal(50.0, Percentiles.Of(shuffled, 50));
        Assert.Equal(copy, shuffled);
        Assert.Equal(Percentiles.Summarize(sorted), Percentiles.Summarize(shuffled));
    }

    [Fact]
    public void One_sample_is_every_percentile()
    {
        var one = new[] { 3.5 };
        Assert.Equal(3.5, Percentiles.Of(one, 0));
        Assert.Equal(3.5, Percentiles.Of(one, 50));
        Assert.Equal(3.5, Percentiles.Of(one, 99));
        Assert.Equal(3.5, Percentiles.Of(one, 100));
        var summary = Percentiles.Summarize(one);
        Assert.Equal(new SampleSummary(1, 3.5, 3.5, 3.5, 3.5, 3.5), summary);
    }

    [Fact]
    public void Equal_samples_give_that_value()
    {
        var samples = Enumerable.Repeat(2.25, 17).ToArray();
        var summary = Percentiles.Summarize(samples);
        Assert.Equal(new SampleSummary(17, 2.25, 2.25, 2.25, 2.25, 2.25), summary);
    }

    [Fact]
    public void Summary_of_1_to_100()
    {
        var summary = Percentiles.Summarize(OneToHundred());
        Assert.Equal(100, summary.Count);
        Assert.Equal(50.5, summary.Mean, 12);
        Assert.Equal(50.0, summary.P50);
        Assert.Equal(95.0, summary.P95);
        Assert.Equal(99.0, summary.P99);
        Assert.Equal(100.0, summary.Max);
    }

    [Fact]
    public void Summary_orders_the_percentiles_and_the_maximum_is_the_largest_sample()
    {
        var random = new DeterministicRandom(5);
        var samples = Enumerable.Range(0, 257).Select(_ => random.NextDouble(0, 10)).ToArray();
        var summary = Percentiles.Summarize(samples);
        Assert.True(summary.P50 <= summary.P95 && summary.P95 <= summary.P99 && summary.P99 <= summary.Max);
        Assert.Equal(samples.Max(), summary.Max);
        Assert.Equal(samples.Average(), summary.Mean, 9);
    }

    [Fact]
    public void Two_samples()
    {
        var samples = new[] { 9.0, 1.0 };
        Assert.Equal(1.0, Percentiles.Of(samples, 50));
        Assert.Equal(9.0, Percentiles.Of(samples, 51));
        Assert.Equal(9.0, Percentiles.Of(samples, 99));
    }

    [Fact]
    public void Bad_input_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Percentiles.Of(Array.Empty<double>(), 50));
        Assert.Throws<ArgumentException>(() => Percentiles.Summarize(Array.Empty<double>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Percentiles.Of(new[] { 1.0 }, 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => Percentiles.Of(new[] { 1.0 }, -1));
    }
}
