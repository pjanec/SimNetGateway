using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>An inclusive range of NED entity ids.</summary>
public readonly record struct NedIdRange(ulong First, ulong Last);

/// <summary>Settings of the NED-RPR gateway; the defaults are the ones of the PoC.</summary>
public sealed record NedRprSettings : GatewaySettings
{
    /// <summary>A full RPR update is sent at least this often per entity.</summary>
    public TimeSpan HeartbeatPeriod { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan HeartbeatCheckPeriod { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan DrProcessorPeriod { get; init; } = TimeSpan.FromSeconds(0.1);

    /// <summary>An RPR entity without a write by RPR for this long is removed.</summary>
    public TimeSpan StaleTimeout { get; init; } = TimeSpan.FromSeconds(12);

    public TimeSpan ReaperCheckPeriod { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Ids the gateway allocates for entities it introduces to NED.</summary>
    public NedIdRange NedIdRange { get; init; } = new(1_000_000, 1_999_999);

    public ushort DisSite { get; init; } = 1;

    public ushort DisApplication { get; init; } = 3001;

    /// <summary>Seconds a released DIS entity number rests before it is reused.</summary>
    public double DisQuarantineSeconds { get; init; } = 60;
}
