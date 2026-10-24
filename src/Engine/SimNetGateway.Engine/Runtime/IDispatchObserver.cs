namespace SimNetGateway.Engine;

/// <summary>What happened to one rule when its trigger reached it (DD-005 P-55, R-078).</summary>
public enum RuleOutcome
{
    /// <summary>All conditions held and the action ran to completion.</summary>
    Fired,

    /// <summary>A <c>When</c> condition returned false.</summary>
    ConditionRejected,

    /// <summary>A condition or the action threw.</summary>
    Failed,

    /// <summary>The rule was disabled, so it was skipped.</summary>
    Disabled,
}

/// <summary>The kinds of trigger the dispatcher raises.</summary>
public enum DispatchTriggerKind
{
    EntityCreated,
    EntityUpdated,
    EntityRemoved,
    Event,
    ChangeSetEnd,
    SessionChanged,
    SessionRequest,
    Timer,
    TickEnd,
}

/// <summary>A copy of the interesting header of a committed change-set record (the record itself is recycled).</summary>
public readonly record struct CommitSummary(
    CorrelationId Correlation, CorrelationId CausedBy, SourceId Source, int EntityCount, double WallTime, double SimTime);

/// <summary>
/// Optional listener on the engine's dispatch (DD-005 P-55, R-078): which inputs started, what their commits did, which
/// triggers were raised and what every matching rule did with them. Set with <see cref="GatewayOptions.DispatchObserver"/> or
/// <see cref="GatewayEngine.DispatchObserver"/>; null (the default) costs one branch per call site and allocates nothing.
/// Calls come from the engine thread in dispatch order; an implementation must not call back into the engine and must
/// not keep engine objects. Triggers are identified by value (ids), never by trigger object.
/// </summary>
public interface IDispatchObserver
{
    /// <summary>
    /// A unit of work starts: an ingress message (<paramref name="messageType"/> is its type, the correlation that of its
    /// transaction), a published event (<c>SimEvent</c>), a session command executed from outside a dispatch
    /// (<c>SessionCommand</c>, correlation none), a commit made from outside a dispatch (<c>ChangeSet</c>) or a timer or
    /// tick-end rule firing (<c>TimerTrigger</c>, correlation none). Everything up to <see cref="InputFinished"/>, cascades
    /// included, belongs to it.
    /// </summary>
    void InputStarted(CorrelationId correlation, SourceId source, Type messageType);

    /// <summary>An ingress message nobody handles (no correlation is allocated).</summary>
    void InputUnhandled(SourceId source, Type messageType);

    /// <summary>The unit of work that <see cref="InputStarted"/> announced is complete (also after a failure).</summary>
    void InputFinished(CorrelationId correlation);

    /// <summary>The handlers aborted the transaction or threw; nothing was committed.</summary>
    void InputAborted(CorrelationId correlation, SourceId source, string module, string reason);

    /// <summary>An event was queued for dispatch (with its own correlation when published by a processor or host).</summary>
    void EventPublished(CorrelationId correlation, CorrelationId causedBy, SourceId source, EventTypeId type, int depth);

    /// <summary>A transaction committed and its record was queued for dispatch.</summary>
    void Committed(in CommitSummary summary);

    /// <summary>A transaction was rejected (validation, or a cascade overflow).</summary>
    void CommitRejected(CorrelationId correlation, SourceId source, string reason);

    /// <summary>
    /// A trigger is about to reach its rules: <paramref name="entity"/> for entity triggers, <paramref name="eventType"/>
    /// for events (none otherwise). <paramref name="depth"/> is the cascade depth of the dispatched item.
    /// </summary>
    void TriggerRaised(DispatchTriggerKind kind, EntityId entity, EventTypeId eventType, CorrelationId correlation, int depth);

    /// <summary>What a rule did with the trigger raised last (also for disabled rules).</summary>
    void RuleEvaluated(RuleInfo rule, RuleOutcome outcome, CorrelationId correlation);

    /// <summary>A session command was accepted and applied.</summary>
    void SessionCommandApplied(SessionCommand command, SourceId source, CorrelationId correlation);

    /// <summary>A session command was refused (invalid transition or vetoed); <paramref name="reason"/> is the diagnostic text.</summary>
    void SessionCommandRejected(SessionCommand command, SourceId source, CorrelationId correlation, string reason);
}
