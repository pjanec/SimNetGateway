namespace SimNetGateway.Engine;

/// <summary>The engine's view of an <see cref="EntityTable{T}"/>: entries are dropped when their entity is destroyed.</summary>
internal interface IEntityTable
{
    string Name { get; }

    bool Remove(EntityId id);
}

/// <summary>
/// Engine-managed per-module state keyed by entity (DD-004 P-35, R-058): dense storage (an index dictionary plus
/// parallel arrays with swap-remove), allocation-free in steady state. Entries of destroyed entities are removed by
/// the engine after the removal dispatch, so removal rules can still read them. Create tables with
/// <see cref="ModuleSetup.CreateEntityTable{T}"/>.
/// </summary>
public sealed class EntityTable<T> : IEntityTable
    where T : struct
{
    private readonly Dictionary<EntityId, int> _index = new();
    private EntityId[] _ids = new EntityId[16];
    private T[] _values = new T[16];
    private int _count;

    internal EntityTable(string name) => Name = name;

    public string Name { get; }

    public int Count => _count;

    public bool Contains(EntityId id) => _index.ContainsKey(id);

    /// <summary>
    /// The entry of the entity, added (default value) if missing. The reference is valid only until the next
    /// <c>GetOrAdd</c> or <c>Remove</c> on this table (storage may grow, entries move on removal).
    /// </summary>
    public ref T GetOrAdd(EntityId id) => ref GetOrAdd(id, out _);

    /// <summary>Like <see cref="GetOrAdd(EntityId)"/>; <paramref name="added"/> says whether the entry is new.</summary>
    public ref T GetOrAdd(EntityId id, out bool added)
    {
        if (_index.TryGetValue(id, out int slot))
        {
            added = false;
            return ref _values[slot];
        }

        if (_count == _ids.Length)
        {
            Array.Resize(ref _ids, _count * 2);
            Array.Resize(ref _values, _count * 2);
        }

        slot = _count++;
        _ids[slot] = id;
        _values[slot] = default;
        _index.Add(id, slot);
        added = true;
        return ref _values[slot];
    }

    public bool TryGet(EntityId id, out T value)
    {
        if (_index.TryGetValue(id, out int slot))
        {
            value = _values[slot];
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Removes the entry (the last entry takes its place); false when there was none.</summary>
    public bool Remove(EntityId id)
    {
        if (!_index.Remove(id, out int slot))
        {
            return false;
        }

        int last = --_count;
        if (slot != last)
        {
            var movedId = _ids[last];
            _ids[slot] = movedId;
            _values[slot] = _values[last];
            _index[movedId] = slot;
        }

        _ids[last] = default;
        _values[last] = default;
        return true;
    }

    /// <summary>The entity of the entry at a dense position (0 .. Count - 1); positions change on <see cref="Remove"/>.</summary>
    public EntityId IdAt(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _ids[index];
    }

    /// <summary>The value at a dense position, by reference (valid until the next <c>GetOrAdd</c> or <c>Remove</c>).</summary>
    public ref T ValueAt(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return ref _values[index];
    }

    public override string ToString() => Name + " (" + _count + " entries)";
}
