using SimNetGateway.Engine;

namespace SimNetGateway.Viewer;

/// <summary>
/// Counts the ingress messages per input side from the engine's dispatch observer (the dashboard's "messages in per side"): every
/// unit of work that starts with the NED or the RPR source is one message. The channels keep no counters, and the scenarios
/// enqueue on them directly, so the observer is the one place that sees all of them.
/// </summary>
internal sealed class IngressCounter : IDispatchObserver
{
    private readonly SourceId _ned;
    private readonly SourceId _rpr;

    public IngressCounter(SourceId ned, SourceId rpr)
    {
        _ned = ned;
        _rpr = rpr;
    }

    public long Ned { get; private set; }

    public long Rpr { get; private set; }

    public void InputStarted(CorrelationId correlation, SourceId source, Type messageType)
    {
        if (source == _ned)
        {
            Ned++;
        }
        else if (source == _rpr)
        {
            Rpr++;
        }
    }

    // messages nobody handles still arrived
    public void InputUnhandled(SourceId source, Type messageType)
    {
        if (source == _ned)
        {
            Ned++;
        }
        else if (source == _rpr)
        {
            Rpr++;
        }
    }

    public void InputFinished(CorrelationId correlation)
    {
    }

    public void InputAborted(CorrelationId correlation, SourceId source, string module, string reason)
    {
    }

    public void EventPublished(CorrelationId correlation, CorrelationId causedBy, SourceId source, EventTypeId type, int depth)
    {
    }

    public void Committed(in CommitSummary summary)
    {
    }

    public void CommitRejected(CorrelationId correlation, SourceId source, string reason)
    {
    }

    public void TriggerRaised(DispatchTriggerKind kind, EntityId entity, EventTypeId eventType, CorrelationId correlation, int depth)
    {
    }

    public void RuleEvaluated(RuleInfo rule, RuleOutcome outcome, CorrelationId correlation)
    {
    }

    public void SessionCommandApplied(SessionCommand command, SourceId source, CorrelationId correlation)
    {
    }

    public void SessionCommandRejected(SessionCommand command, SourceId source, CorrelationId correlation, string reason)
    {
    }
}
