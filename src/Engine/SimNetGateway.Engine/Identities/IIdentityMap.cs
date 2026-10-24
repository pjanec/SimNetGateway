namespace SimNetGateway.Engine;

/// <summary>The key-type independent face of an <see cref="IdentityMap{TKey}"/>; all the engine ever sees of it.</summary>
public interface IIdentityMap
{
    /// <summary>The side whose wire identity this map translates.</summary>
    SourceId Side { get; }

    string Name { get; }

    Type KeyType { get; }

    /// <summary>Number of committed bindings.</summary>
    int Count { get; }

    /// <summary>Whether the entity has a (committed) key in this map.</summary>
    bool Contains(EntityId id);

    /// <summary>
    /// The entity's committed key as text (the key's <c>ToString()</c>; allocates only here); false when it has none. The
    /// default (for an implementation that cannot describe its keys) knows no key.
    /// </summary>
    bool TryDescribeKey(EntityId id, out string text)
    {
        text = string.Empty;
        return false;
    }
}

/// <summary>What the engine and the query engine need of a map beyond <see cref="IIdentityMap"/>.</summary>
internal interface IIdentityMapInternal : IIdentityMap
{
    /// <summary>Removes the entity's binding and releases an allocated key to its allocator.</summary>
    void ReleaseEntity(EntityId id);

    /// <summary>Entity at a position of the dense list of bound entities (0 .. Count - 1); valid until the next change.</summary>
    EntityId BoundAt(int index);
}
