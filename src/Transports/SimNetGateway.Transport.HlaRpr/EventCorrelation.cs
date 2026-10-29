namespace SimNetGateway.Transport.HlaRpr;

/// <summary>An HLA EventIdentifier: the issuing federate's event count and the issuing object's RTI name.</summary>
internal readonly record struct EventKey(ushort Count, string IssuingObject);

/// <summary>
/// The bounded correlation between the gateway's event <see cref="Guid"/> and the HLA EventIdentifier (DD-008 section 3.4): the
/// same event seen as a WeaponFire and later as a MunitionDetonation gets the same identity on both sides. An entry lives
/// <c>window</c> seconds from its creation (an entry of age >= window is gone), and at most <c>capacity</c> entries are kept (the
/// oldest go first). Outbound counts are allocated per issuing object.
/// </summary>
internal sealed class EventCorrelation
{
    private readonly double _window;
    private readonly int _capacity;
    private readonly Func<Guid> _newGuid;
    private readonly Dictionary<Guid, EventKey> _byGuid = new();
    private readonly Dictionary<EventKey, Guid> _byKey = new();
    private readonly Queue<(double Expires, Guid Id)> _order = new();
    private readonly Dictionary<string, ushort> _nextCount = new(StringComparer.Ordinal);

    public EventCorrelation(double windowSeconds, int capacity, Func<Guid> newGuid)
    {
        _window = windowSeconds;
        _capacity = Math.Max(1, capacity);
        _newGuid = newGuid;
    }

    public int Count => _byGuid.Count;

    /// <summary>Drops the entries whose time is up.</summary>
    public void Expire(double now)
    {
        while (_order.Count > 0 && _order.Peek().Expires <= now)
        {
            Remove(_order.Dequeue().Id);
        }
    }

    /// <summary>The identifier of an outbound event: the existing one for a known Guid, else a new count of the issuing object.</summary>
    public EventKey ForOutbound(Guid id, string issuingObject, double now)
    {
        Expire(now);
        if (_byGuid.TryGetValue(id, out var known))
        {
            return known;
        }

        _nextCount.TryGetValue(issuingObject, out var count);
        EventKey key;
        int guard = 0;
        do
        {
            key = new EventKey(count, issuingObject);
            count++; // wraps at 65536
        }
        while (_byKey.ContainsKey(key) && ++guard <= ushort.MaxValue);
        _nextCount[issuingObject] = count;
        Add(id, key, now);
        return key;
    }

    /// <summary>The Guid of an inbound event: the existing one for a known identifier, else a new Guid.</summary>
    public Guid ForInbound(EventKey key, double now)
    {
        Expire(now);
        if (_byKey.TryGetValue(key, out var known))
        {
            return known;
        }

        var id = _newGuid();
        Add(id, key, now);
        return id;
    }

    private void Add(Guid id, EventKey key, double now)
    {
        while (_byGuid.Count >= _capacity && _order.Count > 0)
        {
            Remove(_order.Dequeue().Id);
        }

        _byGuid[id] = key;
        _byKey[key] = id;
        _order.Enqueue((now + _window, id));
    }

    private void Remove(Guid id)
    {
        if (_byGuid.Remove(id, out var key))
        {
            _byKey.Remove(key);
        }
    }
}
