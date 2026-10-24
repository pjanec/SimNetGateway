namespace SimNetGateway.Engine;

/// <summary>
/// Immutable-by-convention result of one committed transaction (pooled). It is valid until it is evicted
/// from the <see cref="ChangeJournal"/>; callers must not hold it (or its deltas) beyond that.
/// </summary>
public sealed class ChangeSetRecord
{
    private readonly List<EntityDelta> _entities = new();

    internal ChangeSetRecord()
    {
    }

    public CorrelationId Correlation { get; internal set; }
    public CorrelationId CausedBy { get; internal set; }
    public SourceId Source { get; internal set; }
    public double WallTime { get; internal set; }
    public double SimTime { get; internal set; }

    /// <summary>One delta per touched entity, in order of first touch.</summary>
    public ReadOnlyListView<EntityDelta> Entities => new(_entities);

    public int Count => _entities.Count;

    internal List<EntityDelta> EntityList => _entities;

    /// <summary>
    /// Set while the record waits in (or is being dispatched from) the engine's work queue. A pinned record
    /// that falls out of the journal ring is not recycled until its dispatch has finished, because one
    /// cascade level may queue more commits than the journal holds.
    /// </summary>
    internal bool Pinned { get; set; }

    /// <summary>The journal evicted the record while it was pinned; the engine recycles it after dispatch.</summary>
    internal bool EvictedWhilePinned { get; set; }
}
