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

    /// <summary>
    /// The seat the render client (DD-022 3e) sits on (DD-023 D-12): its <c>RenderClient</c>, <c>FrameDone</c> and <c>RenderJobResult</c> are read on that seat's partition, its <c>RenderJob</c> and
    /// <c>FrameRequest</c> written there, and <c>ClockStep.Samples</c> counts the world's topics plus that seat's own. Valid 1 .. <see cref="Seats"/>; default 1.
    /// </summary>
    public int RenderSeat { get; init; } = 1;

    /// <summary>
    /// The folder the render client and the gateway both reach (DD-022 3e, D-5): a job's folder and a frame's file are sent relative to it. Empty = not configured (a film job then fails with
    /// that reason). A folder outside it fails the job.
    /// </summary>
    public string RenderRoot { get; init; } = string.Empty;

    /// <summary>Throws <see cref="ArgumentException"/> with a clear message when <see cref="Seats"/> is outside 1 .. <see cref="MaxSeats"/> or <see cref="RenderSeat"/> outside 1 .. <see cref="Seats"/>.</summary>
    public void Validate()
    {
        if (Seats < 1 || Seats > MaxSeats)
        {
            throw new ArgumentException($"dps.seats must be between 1 and {MaxSeats}, not {Seats}.");
        }

        if (RenderSeat < 1 || RenderSeat > Seats)
        {
            throw new ArgumentException($"dps.renderSeat must be between 1 and dps.seats ({Seats}), not {RenderSeat}.");
        }
    }
}
