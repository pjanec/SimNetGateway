namespace SimNetGateway.Engine;

/// <summary>An attribute write of one change set, as returned by <see cref="ChangeJournal.GetWrites"/>.</summary>
public readonly record struct JournalWrite(EntityId Entity, AttrChange Change);

/// <summary>
/// Bounded ring of the most recent change-set records, indexed by correlation id. When the ring is full the
/// oldest record is evicted and recycled: a record is valid only while it is in the journal.
/// </summary>
public sealed class ChangeJournal
{
    private readonly ChangeSetRecord?[] _ring;
    private readonly Dictionary<CorrelationId, ChangeSetRecord> _byCorrelation;
    private readonly RecordPools _pools;
    private int _head;   // index of the oldest record
    private int _count;

    // The bounded event log (what was dispatched, not state; DD-001 5.2).
    private readonly SimEvent?[] _events;
    private int _eventHead;
    private int _eventCount;

    internal ChangeJournal(int capacity, RecordPools pools, int eventCapacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventCapacity);
        _events = new SimEvent?[eventCapacity];
        _ring = new ChangeSetRecord?[capacity];
        _byCorrelation = new Dictionary<CorrelationId, ChangeSetRecord>(capacity);
        _pools = pools;
    }

    public int Capacity => _ring.Length;

    public int Count => _count;

    internal void Add(ChangeSetRecord record)
    {
        if (_count == _ring.Length)
        {
            var oldest = _ring[_head]!;
            _byCorrelation.Remove(oldest.Correlation);
            _ring[_head] = null;
            _head = (_head + 1) % _ring.Length;
            _count--;
            if (oldest.Pinned)
            {
                oldest.EvictedWhilePinned = true; // still queued for dispatch: the engine recycles it later
            }
            else
            {
                _pools.ReturnRecord(oldest);
            }
        }

        _ring[(_head + _count) % _ring.Length] = record;
        _count++;
        _byCorrelation[record.Correlation] = record;
    }

    public int EventCapacity => _events.Length;

    public int EventCount => _eventCount;

    /// <summary>The last dispatched events, oldest first; an event is valid only while it is in the log.</summary>
    public EventEnumerator RecentEvents => new(this);

    /// <summary>Adds a dispatched event; the evicted oldest event and its payload return to the pools.</summary>
    internal void AddEvent(SimEvent ev)
    {
        if (_eventCount == _events.Length)
        {
            var oldest = _events[_eventHead]!;
            _events[_eventHead] = null;
            _eventHead = (_eventHead + 1) % _events.Length;
            _eventCount--;
            _pools.ReturnEvent(oldest);
        }

        _events[(_eventHead + _eventCount) % _events.Length] = ev;
        _eventCount++;
    }

    private SimEvent EventAt(int offsetFromOldest) => _events[(_eventHead + offsetFromOldest) % _events.Length]!;

    public bool TryGet(CorrelationId correlation, out ChangeSetRecord record)
    {
        if (_byCorrelation.TryGetValue(correlation, out var found))
        {
            record = found;
            return true;
        }

        record = null!;
        return false;
    }

    /// <summary>Every (entity, attribute change) written by the unit of work, including overwritten values.</summary>
    public WriteEnumerator GetWrites(CorrelationId correlation) =>
        new(_byCorrelation.TryGetValue(correlation, out var record) ? record : null);

    /// <summary>Retained records with WallTime &gt;= <paramref name="wallTime"/>, oldest first.</summary>
    public RecordEnumerator Since(double wallTime) => new(this, wallTime);

    private ChangeSetRecord RecordAt(int offsetFromOldest) => _ring[(_head + offsetFromOldest) % _ring.Length]!;

    public struct EventEnumerator
    {
        private readonly ChangeJournal _journal;
        private int _offset;

        internal EventEnumerator(ChangeJournal journal)
        {
            _journal = journal;
            _offset = -1;
        }

        public readonly SimEvent Current => _journal.EventAt(_offset);

        public readonly EventEnumerator GetEnumerator() => this;

        public bool MoveNext() => ++_offset < _journal._eventCount;
    }

    public struct RecordEnumerator
    {
        private readonly ChangeJournal _journal;
        private readonly double _wallTime;
        private int _offset;

        internal RecordEnumerator(ChangeJournal journal, double wallTime)
        {
            _journal = journal;
            _wallTime = wallTime;
            _offset = -1;
            Current = null!;
        }

        public ChangeSetRecord Current { get; private set; }

        public readonly RecordEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (++_offset < _journal._count)
            {
                var r = _journal.RecordAt(_offset);
                if (r.WallTime >= _wallTime)
                {
                    Current = r;
                    return true;
                }
            }

            return false;
        }
    }

    public struct WriteEnumerator
    {
        private readonly ChangeSetRecord? _record;
        private int _delta;
        private int _change;

        internal WriteEnumerator(ChangeSetRecord? record)
        {
            _record = record;
            _delta = 0;
            _change = -1;
            Current = default;
        }

        public JournalWrite Current { get; private set; }

        public readonly WriteEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_record is null)
            {
                return false;
            }

            var deltas = _record.EntityList;
            while (_delta < deltas.Count)
            {
                var changes = deltas[_delta].ChangeList;
                if (++_change < changes.Count)
                {
                    Current = new JournalWrite(deltas[_delta].Id, changes[_change]);
                    return true;
                }

                _delta++;
                _change = -1;
            }

            return false;
        }
    }
}
