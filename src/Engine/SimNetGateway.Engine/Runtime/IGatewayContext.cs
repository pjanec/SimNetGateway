namespace SimNetGateway.Engine;

/// <summary>What modules and hosts see of the engine (DD-001 10.3).</summary>
public interface IGatewayContext
{
    IStateView State { get; }
    ISessionView Session { get; }
    AttributeRegistry Attributes { get; }
    EventTypeRegistry EventTypes { get; }
    SourceRegistry Sources { get; }
    IdentityRegistry Identities { get; }
    Diagnostics Diagnostics { get; }

    /// <summary>Wall clock of the current tick / unit of work, in seconds.</summary>
    double WallTime { get; }

    double SimTime { get; }

    long TickCount { get; }

    /// <summary>
    /// Opens the (single, reusable) change set for a processor's write; <c>CausedBy</c> is the correlation of the
    /// item being dispatched. Commit it with <see cref="ChangeSet.Commit"/>.
    /// </summary>
    ChangeSet BeginChange(SourceId source);

    /// <summary>A payload from the pool; hand it to <see cref="Publish"/>, which takes ownership.</summary>
    AttributeBag RentPayload();

    /// <summary>Publishes an event with its own correlation id (CausedBy = the item being dispatched).</summary>
    void Publish(SourceId source, EventTypeId type, AttributeBag payload);

    /// <summary>Executes a session command now; false when it was rejected (invalid transition or vetoed).</summary>
    bool Execute(SessionCommand command, SourceId source);
}
