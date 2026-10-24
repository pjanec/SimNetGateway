namespace SimNetGateway.Engine;

internal enum WorkKind : byte
{
    Record,
    Event,
    Command,
    SessionChanged,
}

/// <summary>One unit of the breadth-first dispatch queue (DD-001 6.4).</summary>
internal struct WorkItem
{
    public WorkKind Kind;
    public int Depth;
    public CorrelationId Correlation;
    public ChangeSetRecord? Record;
    public SimEvent? Event;
    public SessionCommand? Command;
    public SourceId Source;
    public SessionState Previous;
    public SessionState Current;
}

/// <summary>FIFO ring of work items; grows by doubling, so it does not allocate in steady state.</summary>
internal sealed class WorkQueue
{
    private WorkItem[] _items = new WorkItem[64];
    private int _head;
    private int _count;

    public int Count => _count;

    public void Enqueue(in WorkItem item)
    {
        if (_count == _items.Length)
        {
            var bigger = new WorkItem[_items.Length * 2];
            for (int i = 0; i < _count; i++)
            {
                bigger[i] = _items[(_head + i) % _items.Length];
            }

            _items = bigger;
            _head = 0;
        }

        _items[(_head + _count) % _items.Length] = item;
        _count++;
    }

    public bool TryDequeue(out WorkItem item)
    {
        if (_count == 0)
        {
            item = default;
            return false;
        }

        item = _items[_head];
        _items[_head] = default; // do not keep records, events or commands alive
        _head = (_head + 1) % _items.Length;
        _count--;
        return true;
    }
}
