namespace SimNetGateway.Engine;

/// <summary>Read access to the committed state.</summary>
public interface IStateView
{
    /// <summary>Number of live entities.</summary>
    int Count { get; }

    EntityRecord? Find(EntityId id);

    bool TryGet(EntityId id, out EntityRecord entity);

    /// <summary>Allocation-free enumeration of the live entities (do not modify state while enumerating).</summary>
    EntityEnumerable Entities { get; }

    ChangeJournal Journal { get; }
}

/// <summary>Struct enumerable over live entity records.</summary>
public readonly struct EntityEnumerable
{
    private readonly Dictionary<EntityId, EntityRecord>? _entities;

    internal EntityEnumerable(Dictionary<EntityId, EntityRecord> entities) => _entities = entities;

    public EntityEnumerator GetEnumerator() => new(_entities);
}

public struct EntityEnumerator
{
    private Dictionary<EntityId, EntityRecord>.ValueCollection.Enumerator _inner;
    private readonly bool _valid;

    internal EntityEnumerator(Dictionary<EntityId, EntityRecord>? entities)
    {
        if (entities is null)
        {
            _inner = default;
            _valid = false;
        }
        else
        {
            _inner = entities.Values.GetEnumerator();
            _valid = true;
        }
    }

    public bool MoveNext() => _valid && _inner.MoveNext();

    public EntityRecord Current => _inner.Current;
}
