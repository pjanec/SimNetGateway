using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// Allocates 64-bit keys from the inclusive range [first, last] (e.g. the range of NED entity ids reserved for the
/// gateway, DD-001 3.6). Next-fit: allocation continues after the last handed-out key and wraps around, so a key that
/// was just released is not handed out again while other keys are free. Exhaustion makes <see cref="TryAllocate"/>
/// return false. Releasing a key outside the range or not currently allocated is ignored.
/// </summary>
public sealed class RangeAllocator : IIdAllocator<ulong>
{
    private readonly ulong _first;
    private readonly ulong _last;
    private readonly HashSet<ulong> _allocated = new();
    private ulong _next;

    public RangeAllocator(ulong first, ulong last)
    {
        if (last < first)
        {
            throw new ArgumentException("The range is empty (last < first).", nameof(last));
        }

        if (last - first == ulong.MaxValue)
        {
            throw new ArgumentException("The range must not span all of ulong.", nameof(last));
        }

        _first = first;
        _last = last;
        _next = first;
        Capacity = last - first + 1;
    }

    /// <summary>Number of keys in the range.</summary>
    public ulong Capacity { get; }

    /// <summary>Keys currently allocated.</summary>
    public ulong InUse => (ulong)_allocated.Count;

    /// <summary>Keys that can still be allocated.</summary>
    public ulong Available => Capacity - InUse;

    public bool TryAllocate(out ulong key)
    {
        if ((ulong)_allocated.Count >= Capacity)
        {
            key = 0;
            return false;
        }

        // At least one key is free, so the scan ends within one lap.
        ulong candidate = _next;
        while (!_allocated.Add(candidate))
        {
            candidate = candidate == _last ? _first : candidate + 1;
        }

        key = candidate;
        _next = candidate == _last ? _first : candidate + 1;
        return true;
    }

    public void Release(in ulong key)
    {
        if (key >= _first && key <= _last)
        {
            _allocated.Remove(key);
        }
    }
}
