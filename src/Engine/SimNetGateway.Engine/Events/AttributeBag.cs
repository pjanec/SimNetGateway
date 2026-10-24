namespace SimNetGateway.Engine;

/// <summary>
/// The payload of a simulation event: a small set of (attribute, value) pairs. Pooled: rent one with
/// <c>RentPayload()</c>; a published bag is owned by the engine and recycled when its event leaves the event log.
/// Values are not kind-checked against the registry.
/// </summary>
public sealed class AttributeBag
{
    private readonly List<(AttrId Attr, AttrValue Value)> _items = new();

    internal AttributeBag()
    {
    }

    public int Count => _items.Count;

    /// <summary>True once the bag was handed to the engine with Publish (so it is not returned as unused).</summary>
    internal bool Published { get; set; }

    /// <summary>Sets the value of the attribute, overwriting an existing entry. Returns this for chaining.</summary>
    public AttributeBag Set(AttrId attr, AttrValue value)
    {
        var items = _items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Attr == attr)
            {
                items[i] = (attr, value);
                return this;
            }
        }

        items.Add((attr, value));
        return this;
    }

    /// <summary>The value of the attribute, or <see cref="AttrValue.None"/> when absent.</summary>
    public AttrValue Get(AttrId attr) => TryGet(attr, out var v) ? v : default;

    public bool TryGet(AttrId attr, out AttrValue value)
    {
        var items = _items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Attr == attr)
            {
                value = items[i].Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public Enumerator GetEnumerator() => new(_items);

    internal void Clear()
    {
        _items.Clear();
        Published = false;
    }

    public struct Enumerator
    {
        private readonly List<(AttrId Attr, AttrValue Value)> _list;
        private int _index;

        internal Enumerator(List<(AttrId Attr, AttrValue Value)> list)
        {
            _list = list;
            _index = -1;
        }

        public bool MoveNext() => ++_index < _list.Count;

        public (AttrId Attr, AttrValue Value) Current => _list[_index];
    }
}
