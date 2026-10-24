namespace SimNetGateway.Engine;

/// <summary>Which recency list of the repository.</summary>
internal enum RecencyKind : byte
{
    Write,
    Change,
    OriginWrite,
}

/// <summary>Allocation-free enumeration of a recency list, oldest first (diagnostics, viewer, tests).</summary>
public readonly struct RecencyEnumerable
{
    private readonly EntityRecord? _head;
    private readonly RecencyKind _kind;

    internal RecencyEnumerable(EntityRecord? head, RecencyKind kind)
    {
        _head = head;
        _kind = kind;
    }

    public RecencyEnumerator GetEnumerator() => new(_head, _kind);
}

public struct RecencyEnumerator
{
    private readonly RecencyKind _kind;
    private EntityRecord? _next;

    internal RecencyEnumerator(EntityRecord? head, RecencyKind kind)
    {
        _kind = kind;
        _next = head;
        Current = null!;
    }

    public EntityRecord Current { get; private set; }

    public bool MoveNext()
    {
        var next = _next;
        if (next is null)
        {
            return false;
        }

        Current = next;
        _next = _kind switch
        {
            RecencyKind.Write => next.WriteNext,
            RecencyKind.Change => next.ChangeNext,
            _ => next.OriginNext,
        };
        return true;
    }
}
