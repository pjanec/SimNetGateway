using System.Collections;

namespace SimNetGateway.Protocol.Rpr.Hla;

/// <summary>
/// A set of handles (1516e <c>AttributeHandleSet</c>); enumerates in ascending handle order so that behaviour built on it
/// is deterministic.
/// </summary>
public class HandleSet<THandle> : IEnumerable<THandle> where THandle : struct, IHandle
{
    private readonly HashSet<THandle> _items = new();

    public HandleSet()
    {
    }

    public HandleSet(IEnumerable<THandle> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items)
        {
            _items.Add(item);
        }
    }

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    /// <summary>Adds a handle; returns false when it was already present.</summary>
    public bool Add(THandle handle) => _items.Add(handle);

    public bool Remove(THandle handle) => _items.Remove(handle);

    public bool Contains(THandle handle) => _items.Contains(handle);

    public void Clear() => _items.Clear();

    public IEnumerator<THandle> GetEnumerator() => _items.OrderBy(h => h.Value).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A set of attribute handles (1516e <c>AttributeHandleSet</c>).</summary>
public sealed class AttributeHandleSet : HandleSet<AttributeHandle>
{
    public AttributeHandleSet()
    {
    }

    public AttributeHandleSet(IEnumerable<AttributeHandle> items) : base(items)
    {
    }

    /// <summary>An independent copy.</summary>
    public AttributeHandleSet Clone() => new(this);
}

/// <summary>
/// A map from handles to opaque values (1516e <c>AttributeHandleValueMap</c> / <c>ParameterHandleValueMap</c>); enumerates
/// in ascending handle order. Values are byte arrays the RTI never interprets.
/// </summary>
public class HandleValueMap<THandle> : IEnumerable<KeyValuePair<THandle, byte[]>> where THandle : struct, IHandle
{
    private readonly Dictionary<THandle, byte[]> _values = new();

    public int Count => _values.Count;

    public bool IsEmpty => _values.Count == 0;

    public IEnumerable<THandle> Keys => _values.Keys.OrderBy(h => h.Value);

    /// <summary>Sets (adds or replaces) the value of a handle.</summary>
    public void Set(THandle handle, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _values[handle] = value;
    }

    public bool ContainsKey(THandle handle) => _values.ContainsKey(handle);

    public bool TryGetValue(THandle handle, out byte[] value)
    {
        if (_values.TryGetValue(handle, out var found))
        {
            value = found;
            return true;
        }
        value = Array.Empty<byte>();
        return false;
    }

    public byte[] this[THandle handle] => _values[handle];

    public IEnumerator<KeyValuePair<THandle, byte[]>> GetEnumerator() =>
        _values.OrderBy(p => p.Key.Value).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Attribute values of one object instance (1516e <c>AttributeHandleValueMap</c>).</summary>
public sealed class AttributeHandleValueMap : HandleValueMap<AttributeHandle>
{
}

/// <summary>Parameter values of one interaction (1516e <c>ParameterHandleValueMap</c>).</summary>
public sealed class ParameterHandleValueMap : HandleValueMap<ParameterHandle>
{
}
