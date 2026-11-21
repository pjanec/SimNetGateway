using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The client's reading of <c>SimClock</c> (DD-022 3b), the formula of HROT's slave clock. Wall ticks are about 6.4e17: a <c>double</c> resolves only 128 ticks (12.8 microseconds) there, so
/// elapsed time is always the difference of the two <c>long</c> values first, then converted - never the difference of two doubles.
/// </summary>
public static class DpsClockFormula
{
    /// <summary>Wall ticks per second (100 ns ticks).</summary>
    public const double TicksPerSecond = 10_000_000.0;

    /// <summary>Seconds from <paramref name="anchorTicks"/> to <paramref name="nowTicks"/>: the integer difference first.</summary>
    public static double SecondsSince(long anchorTicks, long nowTicks) => (double)(nowTicks - anchorTicks) / TicksPerSecond;

    /// <summary>
    /// The simulation time at the station's wall tick <paramref name="stationNowTicks"/>: Continuous = <c>AnchorSimTime + elapsed x TimeScale</c>; Stepped = <c>AnchorSimTime</c>, held.
    /// </summary>
    public static double SimTime(in D.SimClock clock, long stationNowTicks) =>
        clock.Mode == D.ClockMode.ClockModeContinuous
            ? clock.AnchorSimTime + SecondsSince(clock.AnchorWallTicks, stationNowTicks) * clock.TimeScale
            : clock.AnchorSimTime;
}
