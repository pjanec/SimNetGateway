#nullable enable
using System;

namespace SimNetGateway.Protocol.Dis7;

/// <summary>
/// The 32-bit DIS PDU timestamp (IEEE 1278.1-2012 section 6.2.88 / 5.2.5): the upper 31 bits count units of
/// <c>3600 s / 2^31</c> past the hour, the lowest bit says how the value was produced: 1 = absolute (a clock synchronised
/// with the other applications), 0 = relative (the sender's own clock). The count wraps every hour.
/// </summary>
public static class DisTimestamp
{
    /// <summary>Number of units in an hour: 2^31.</summary>
    public const long UnitsPerHour = 1L << 31;

    /// <summary>One unit in seconds: 3600 / 2^31 (about 1.68 microseconds).</summary>
    public const double UnitSeconds = 3600.0 / UnitsPerHour;

    /// <summary>
    /// Encodes seconds past the hour. Values outside [0, 3600) wrap modulo one hour (3600 -> 0); the result is rounded to
    /// the nearest unit.
    /// </summary>
    public static uint Encode(double secondsPastHour, bool absolute)
    {
        if (double.IsNaN(secondsPastHour) || double.IsInfinity(secondsPastHour))
        {
            throw new ArgumentOutOfRangeException(nameof(secondsPastHour), "must be finite");
        }

        double wrapped = secondsPastHour % 3600.0;
        if (wrapped < 0.0)
        {
            wrapped += 3600.0;
        }

        long units = (long)Math.Round(wrapped / UnitSeconds);
        units %= UnitsPerHour; // rounding can reach 2^31 exactly
        return ((uint)units << 1) | (absolute ? 1u : 0u);
    }

    /// <summary>True for an absolute timestamp (lowest bit set), false for a relative one.</summary>
    public static bool IsAbsolute(uint timestamp) => (timestamp & 1u) != 0;

    /// <summary>The 31-bit unit count.</summary>
    public static uint Units(uint timestamp) => timestamp >> 1;

    /// <summary>Seconds past the hour in [0, 3600).</summary>
    public static double ToSecondsPastHour(uint timestamp) => Units(timestamp) * UnitSeconds;

    public static uint EncodeAbsolute(double secondsPastHour) => Encode(secondsPastHour, true);

    public static uint EncodeRelative(double secondsPastHour) => Encode(secondsPastHour, false);
}
