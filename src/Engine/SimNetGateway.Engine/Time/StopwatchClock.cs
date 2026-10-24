namespace SimNetGateway.Engine;

/// <summary>Real monotonic clock: seconds since construction, from <see cref="System.Diagnostics.Stopwatch"/>.</summary>
public sealed class StopwatchClock : IClock
{
    private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();

    public double Now => (System.Diagnostics.Stopwatch.GetTimestamp() - _start) / (double)System.Diagnostics.Stopwatch.Frequency;
}
