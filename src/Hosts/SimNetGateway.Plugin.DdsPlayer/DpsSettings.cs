namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The "dps" section (DD-017): the DDS domain and partition of this station, and the thresholds that decide when a changed value is worth a sample.
/// <see cref="Partition"/> empty = a name made from the node's site and application. <see cref="LeaseSeconds"/> is the liveliness lease the player
/// topics expect of a client; <see cref="HeartbeatSeconds"/> the longest simulation time a state instance goes without a sample.
/// </summary>
public sealed record DpsSettings
{
    public bool Enabled { get; init; } = true;

    public int Domain { get; init; }

    public string Partition { get; init; } = string.Empty;

    public double LeaseSeconds { get; init; } = 2.0;

    public double HeartbeatSeconds { get; init; } = 5.0;

    public double PositionThresholdM { get; init; } = 0.05;

    public double AngleThresholdDeg { get; init; } = 1.0;

    public double VelocityThreshold { get; init; } = 0.1;

    /// <summary>The most seats a gateway offers (DD-023 D-11: the seat number is the high 16 bits of the station, far below 32768).</summary>
    public const int MaxSeats = 1024;

    /// <summary>
    /// How many players this gateway serves (DD-023): seat n is the station <c>node | n &lt;&lt; 16</c> on the partition <c>&lt;base&gt;-n</c>; with 1 the partition is <c>&lt;base&gt;</c> itself (D-6).
    /// Valid 1 .. <see cref="MaxSeats"/>; <see cref="Validate"/> says why a value is not.
    /// </summary>
    public int Seats { get; init; } = 1;

    /// <summary>Throws <see cref="ArgumentException"/> with a clear message when <see cref="Seats"/> is outside 1 .. <see cref="MaxSeats"/>.</summary>
    public void Validate()
    {
        if (Seats < 1 || Seats > MaxSeats)
        {
            throw new ArgumentException($"dps.seats must be between 1 and {MaxSeats}, not {Seats}.");
        }
    }
}
