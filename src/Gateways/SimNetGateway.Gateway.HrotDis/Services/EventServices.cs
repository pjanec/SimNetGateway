using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>A DIS event identifier: the gateway's site and application and a counter.</summary>
public readonly record struct DisEventId(ushort Site, ushort Application, ushort Event);

/// <summary>Produces the DIS event identifiers of the events the gateway originates (DD-006 section 3.2): a counter 1 .. 65535 that wraps.</summary>
public sealed class DisEventIds
{
    private readonly ushort _site;
    private readonly ushort _application;
    private ushort _last;

    public DisEventIds(ushort site, ushort application)
    {
        _site = site;
        _application = application;
    }

    public DisEventId Next()
    {
        _last = _last == ushort.MaxValue ? (ushort)1 : (ushort)(_last + 1);
        return new DisEventId(_site, _application, _last);
    }
}

/// <summary>
/// Links a HROT detonation to its fire (DD-006 section 6.3): HROT carries no event id, so the id of a fire is reused for the next
/// detonation of the same shooter and munition within <see cref="Window"/> seconds (first in, first out); a detonation without
/// such a fire gets a new id. Times are the engine's wall time in seconds.
/// </summary>
public sealed class FireCorrelator
{
    /// <summary>The longest time between a fire and its detonation.</summary>
    public const double Window = 30.0;

    private readonly List<Entry> _open = new();

    private readonly record struct Entry(long Shooter, long Munition, DisEventId Id, double Time);

    /// <summary>Fires that wait for their detonation.</summary>
    public int Pending => _open.Count;

    /// <summary>Remembers a fire (<paramref name="shooter"/> is the HROT id, <paramref name="munition"/> the TKB type or the packed DIS type).</summary>
    public void OnFire(long shooter, long munition, DisEventId id, double time)
    {
        Prune(time);
        _open.Add(new Entry(shooter, munition, id, time));
    }

    /// <summary>The id of the oldest fire of the shooter and munition that is at most <see cref="Window"/> old (and forgets it); null when there is none.</summary>
    public DisEventId? MatchDetonation(long shooter, long munition, double time)
    {
        Prune(time);
        for (int i = 0; i < _open.Count; i++)
        {
            var entry = _open[i];
            if (entry.Shooter == shooter && entry.Munition == munition)
            {
                _open.RemoveAt(i);
                return entry.Id;
            }
        }

        return null;
    }

    public void Clear() => _open.Clear();

    private void Prune(double now) => _open.RemoveAll(e => now - e.Time > Window);
}

/// <summary>
/// The HROT entity id allocator port as a range of ids (<see cref="RangeAllocator"/>): the default of the profile and what the
/// tests inject. The HROT pack replaces it by an allocator over <c>HrotNodeContext.IdAllocator</c> (DD-007 section 3 item 3).
/// </summary>
public sealed class HrotIdRangeAllocator : IIdAllocator<long>
{
    private readonly RangeAllocator _range;

    public HrotIdRangeAllocator(long first, long last)
    {
        if (first < 0 || last < first)
        {
            throw new ArgumentException("The range must be non-negative and not empty.");
        }

        _range = new RangeAllocator((ulong)first, (ulong)last);
    }

    public ulong InUse => _range.InUse;

    public ulong Available => _range.Available;

    public bool TryAllocate(out long key)
    {
        bool ok = _range.TryAllocate(out ulong value);
        key = (long)value;
        return ok;
    }

    public void Release(in long key)
    {
        if (key >= 0)
        {
            _range.Release((ulong)key);
        }
    }
}
