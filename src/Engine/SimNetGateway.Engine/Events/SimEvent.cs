namespace SimNetGateway.Engine;

/// <summary>
/// A transient simulation event (DD-001 5.2): type, payload and the usual metadata. Pooled; valid while it is
/// in the event log (<see cref="ChangeJournal.RecentEvents"/>).
/// </summary>
public sealed class SimEvent
{
    internal SimEvent()
    {
    }

    public EventTypeId Type { get; internal set; }
    public AttributeBag Payload { get; internal set; } = null!;
    public SourceId Source { get; internal set; }
    public CorrelationId Correlation { get; internal set; }
    public CorrelationId CausedBy { get; internal set; }
    public double WallTime { get; internal set; }
    public double SimTime { get; internal set; }

    internal void Reset()
    {
        Type = default;
        Payload = null!;
        Source = default;
        Correlation = default;
        CausedBy = default;
        WallTime = 0;
        SimTime = 0;
    }
}
