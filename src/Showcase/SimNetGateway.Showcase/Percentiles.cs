namespace SimNetGateway.Showcase;

/// <summary>Summary statistics of a sample set (all in the unit of the samples).</summary>
public readonly record struct SampleSummary(int Count, double Mean, double P50, double P95, double P99, double Max);

/// <summary>Percentiles by the nearest-rank method on a sorted copy of the samples.</summary>
public static class Percentiles
{
    /// <summary>
    /// The nearest-rank percentile: the sample at rank <c>ceil(percent / 100 * n)</c> (at least 1) of the ascending sort.
    /// <paramref name="percent"/> is in (0, 100]; 0 gives the minimum.
    /// </summary>
    public static double Of(IReadOnlyList<double> samples, double percent)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("There are no samples.", nameof(samples));
        }

        if (double.IsNaN(percent) || percent < 0 || percent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), "The percentile must be in [0, 100].");
        }

        var sorted = new double[samples.Count];
        for (int i = 0; i < sorted.Length; i++)
        {
            sorted[i] = samples[i];
        }

        Array.Sort(sorted);
        return AtRank(sorted, percent);
    }

    /// <summary>Mean, p50, p95, p99 and maximum of the samples.</summary>
    public static SampleSummary Summarize(IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("There are no samples.", nameof(samples));
        }

        var sorted = new double[samples.Count];
        double sum = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            sorted[i] = samples[i];
            sum += samples[i];
        }

        Array.Sort(sorted);
        return new SampleSummary(sorted.Length, sum / sorted.Length, AtRank(sorted, 50), AtRank(sorted, 95), AtRank(sorted, 99),
            sorted[^1]);
    }

    private static double AtRank(double[] sorted, double percent)
    {
        int rank = (int)Math.Ceiling(percent * sorted.Length / 100.0);
        rank = Math.Clamp(rank, 1, sorted.Length);
        return sorted[rank - 1];
    }
}
