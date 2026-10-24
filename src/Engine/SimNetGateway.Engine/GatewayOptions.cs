namespace SimNetGateway.Engine;

/// <summary>Engine configuration (DD-001 10.3).</summary>
public sealed class GatewayOptions
{
    /// <summary>Upper bound of ingress messages processed per <see cref="GatewayEngine.Tick"/>.</summary>
    public int MaxMessagesPerTick { get; init; } = 1000;

    /// <summary>Work beyond this cascade depth is refused before it is applied (DD-001 6.4).</summary>
    public int MaxCascadeDepth { get; init; } = 8;

    /// <summary>Retained change-set records; 0 switches the journal off (records are recycled after dispatch, R-070).</summary>
    public int JournalCapacity { get; init; } = 1024;

    /// <summary>Retained dispatched events; 0 switches the event log off (events are recycled after dispatch).</summary>
    public int EventLogCapacity { get; init; } = 256;

    public int DiagnosticsCapacity { get; init; } = 256;

    /// <summary>Who may write which entity (R-057); null = permissive.</summary>
    public IWriteAuthorityPolicy? WriteAuthority { get; init; }
}
