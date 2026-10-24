namespace SimNetGateway.Engine.Monitoring;

/// <summary>One attribute touched by a transaction (a copy of <see cref="AttrChange"/> with the attribute's name).</summary>
public readonly record struct ChangeRow(AttrId Attr, string AttrName, AttrValue Old, AttrValue New, bool Changed);

/// <summary>What one transaction did to one entity (a copy of <see cref="EntityDelta"/>).</summary>
public sealed record DeltaRow(
    EntityId Entity, bool Created, bool Destroyed, bool OwnerChanged, SourceId PreviousOwner, IReadOnlyList<ChangeRow> Changes);

/// <summary>One committed change set (a copy of <see cref="ChangeSetRecord"/>, which the engine recycles).</summary>
public sealed record JournalRecordRow(
    CorrelationId Correlation, CorrelationId CausedBy, SourceId Source, string SourceName, double WallTime, double SimTime,
    IReadOnlyList<DeltaRow> Deltas)
{
    public int EntityCount => Deltas.Count;
}

/// <summary>One attribute of an event payload.</summary>
public readonly record struct PayloadRow(AttrId Attr, string AttrName, AttrValue Value);

/// <summary>One dispatched event (a copy of <see cref="SimEvent"/> and its payload, which the engine recycles).</summary>
public sealed record EventRow(
    EventTypeId Type, string TypeName, CorrelationId Correlation, CorrelationId CausedBy, SourceId Source, string SourceName,
    double WallTime, double SimTime, IReadOnlyList<PayloadRow> Payload);

/// <summary>
/// The Journal / Events panel's model (DD-005 section 3): the most recent change sets and events, copied deeply - deltas,
/// changes, payloads - so that the rows stay valid after the engine recycled its records (journal and event log off or tiny,
/// R-070). <see cref="HistoryOff"/> says the journal has capacity 0 (nothing to show), <see cref="EventsOff"/> the same for
/// the event log.
/// </summary>
public sealed class JournalModel
{
    private readonly GatewayEngine _engine;
    private readonly List<JournalRecordRow> _records = new();
    private readonly List<EventRow> _events = new();

    public JournalModel(GatewayEngine engine, int maxRecords = 256, int maxEvents = 256)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRecords);
        ArgumentOutOfRangeException.ThrowIfNegative(maxEvents);
        _engine = engine;
        MaxRecords = maxRecords;
        MaxEvents = maxEvents;
    }

    public int MaxRecords { get; }

    public int MaxEvents { get; }

    public bool HistoryOff { get; private set; }

    public bool EventsOff { get; private set; }

    /// <summary>Retained records at the last refresh (the rows hold at most <see cref="MaxRecords"/> of them).</summary>
    public int RetainedRecords { get; private set; }

    public int RetainedEvents { get; private set; }

    /// <summary>The most recent records, oldest first.</summary>
    public IReadOnlyList<JournalRecordRow> Records => _records;

    /// <summary>The most recent events, oldest first.</summary>
    public IReadOnlyList<EventRow> Events => _events;

    public void Refresh()
    {
        var engine = _engine;
        var journal = engine.State.Journal;
        HistoryOff = journal.Capacity == 0;
        EventsOff = journal.EventCapacity == 0;
        RetainedRecords = journal.Count;
        RetainedEvents = journal.EventCount;

        _records.Clear();
        int skip = Math.Max(0, journal.Count - MaxRecords);
        int index = 0;
        foreach (var record in journal.Since(double.NegativeInfinity))
        {
            if (index++ < skip)
            {
                continue;
            }

            _records.Add(CopyRecord(record));
        }

        _events.Clear();
        int skipEvents = Math.Max(0, journal.EventCount - MaxEvents);
        index = 0;
        foreach (var ev in journal.RecentEvents)
        {
            if (index++ < skipEvents)
            {
                continue;
            }

            _events.Add(CopyEvent(ev));
        }
    }

    /// <summary>The copied record of a correlation, if the last refresh holds it.</summary>
    public bool TryFindRecord(CorrelationId correlation, out JournalRecordRow row)
    {
        foreach (var candidate in _records)
        {
            if (candidate.Correlation == correlation)
            {
                row = candidate;
                return true;
            }
        }

        row = null!;
        return false;
    }

    /// <summary>
    /// The correlations from <paramref name="correlation"/> back along <c>CausedBy</c> while the copied records hold them:
    /// the cause chain of a record, nearest cause first (the correlation itself is not included).
    /// </summary>
    public List<CorrelationId> CausedByChain(CorrelationId correlation)
    {
        var chain = new List<CorrelationId>();
        var current = correlation;
        while (TryFindRecord(current, out var row) && !row.CausedBy.IsNone && chain.Count <= _records.Count)
        {
            chain.Add(row.CausedBy);
            current = row.CausedBy;
        }

        return chain;
    }

    private JournalRecordRow CopyRecord(ChangeSetRecord record)
    {
        var deltas = new DeltaRow[record.Count];
        for (int i = 0; i < deltas.Length; i++)
        {
            var delta = record.Entities[i];
            var changes = new ChangeRow[delta.Changes.Count];
            for (int c = 0; c < changes.Length; c++)
            {
                var change = delta.Changes[c];
                changes[c] = new ChangeRow(change.Attr, Names.Attribute(_engine, change.Attr), change.Old, change.New, change.Changed);
            }

            deltas[i] = new DeltaRow(delta.Id, delta.Created, delta.Destroyed, delta.OwnerChanged, delta.PreviousOwner, changes);
        }

        return new JournalRecordRow(record.Correlation, record.CausedBy, record.Source, Names.Source(_engine, record.Source),
            record.WallTime, record.SimTime, deltas);
    }

    private EventRow CopyEvent(SimEvent ev)
    {
        var payload = new List<PayloadRow>(ev.Payload.Count);
        foreach (var (attr, value) in ev.Payload)
        {
            payload.Add(new PayloadRow(attr, Names.Attribute(_engine, attr), value));
        }

        return new EventRow(ev.Type, Names.EventType(_engine, ev.Type), ev.Correlation, ev.CausedBy, ev.Source,
            Names.Source(_engine, ev.Source), ev.WallTime, ev.SimTime, payload);
    }
}
