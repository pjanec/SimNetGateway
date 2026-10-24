using SimNetGateway.Engine;

namespace SimNetGateway.Viewer;

/// <summary>Forwards every <see cref="IDispatchObserver"/> call to two observers, in order (the tracer, then an extra one).</summary>
internal sealed class ObserverChain : IDispatchObserver
{
    private readonly IDispatchObserver _first;
    private readonly IDispatchObserver _second;

    public ObserverChain(IDispatchObserver first, IDispatchObserver second)
    {
        _first = first;
        _second = second;
    }

    public void InputStarted(CorrelationId correlation, SourceId source, Type messageType)
    {
        _first.InputStarted(correlation, source, messageType);
        _second.InputStarted(correlation, source, messageType);
    }

    public void InputUnhandled(SourceId source, Type messageType)
    {
        _first.InputUnhandled(source, messageType);
        _second.InputUnhandled(source, messageType);
    }

    public void InputFinished(CorrelationId correlation)
    {
        _first.InputFinished(correlation);
        _second.InputFinished(correlation);
    }

    public void InputAborted(CorrelationId correlation, SourceId source, string module, string reason)
    {
        _first.InputAborted(correlation, source, module, reason);
        _second.InputAborted(correlation, source, module, reason);
    }

    public void EventPublished(CorrelationId correlation, CorrelationId causedBy, SourceId source, EventTypeId type, int depth)
    {
        _first.EventPublished(correlation, causedBy, source, type, depth);
        _second.EventPublished(correlation, causedBy, source, type, depth);
    }

    public void Committed(in CommitSummary summary)
    {
        _first.Committed(in summary);
        _second.Committed(in summary);
    }

    public void CommitRejected(CorrelationId correlation, SourceId source, string reason)
    {
        _first.CommitRejected(correlation, source, reason);
        _second.CommitRejected(correlation, source, reason);
    }

    public void TriggerRaised(DispatchTriggerKind kind, EntityId entity, EventTypeId eventType, CorrelationId correlation, int depth)
    {
        _first.TriggerRaised(kind, entity, eventType, correlation, depth);
        _second.TriggerRaised(kind, entity, eventType, correlation, depth);
    }

    public void RuleEvaluated(RuleInfo rule, RuleOutcome outcome, CorrelationId correlation)
    {
        _first.RuleEvaluated(rule, outcome, correlation);
        _second.RuleEvaluated(rule, outcome, correlation);
    }

    public void SessionCommandApplied(SessionCommand command, SourceId source, CorrelationId correlation)
    {
        _first.SessionCommandApplied(command, source, correlation);
        _second.SessionCommandApplied(command, source, correlation);
    }

    public void SessionCommandRejected(SessionCommand command, SourceId source, CorrelationId correlation, string reason)
    {
        _first.SessionCommandRejected(command, source, correlation, reason);
        _second.SessionCommandRejected(command, source, correlation, reason);
    }
}
