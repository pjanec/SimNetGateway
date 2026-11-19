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
}
