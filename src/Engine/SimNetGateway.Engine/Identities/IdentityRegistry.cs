namespace SimNetGateway.Engine;

/// <summary>
/// The identity maps of a gateway, behind a non-generic interface: the engine never looks at a key type (R-035, R-039).
/// Modules create their maps at registration (<c>setup.Identities.Create&lt;ulong&gt;(ned, "ned.entity", allocator)</c>).
/// </summary>
public sealed class IdentityRegistry
{
    private readonly StateRepository _state;
    private readonly List<IIdentityMap> _maps = new();
    private readonly List<IIdentityMapInternal> _internal = new();
    private readonly Dictionary<string, IIdentityMap> _byName = new(StringComparer.Ordinal);

    public IdentityRegistry(StateRepository state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <summary>Every map, in creation order.</summary>
    public IReadOnlyList<IIdentityMap> All => _maps;

    /// <summary>Creates a map for one kind of wire identity of <paramref name="side"/>; the name must be unique.</summary>
    public IdentityMap<TKey> Create<TKey>(SourceId side, string name, IIdAllocator<TKey>? allocator = null)
        where TKey : notnull, IEquatable<TKey>
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Identity map name must not be empty.", nameof(name));
        }

        if (_byName.ContainsKey(name))
        {
            throw new InvalidOperationException($"An identity map named '{name}' already exists.");
        }

        var map = new IdentityMap<TKey>(_state, side, name, allocator);
        _maps.Add(map);
        _internal.Add(map);
        _byName.Add(name, map);
        return map;
    }

    public IIdentityMap? Find(string name) => name is not null && _byName.TryGetValue(name, out var map) ? map : null;

    /// <summary>Called by the engine after the removal triggers of a destroyed entity ran.</summary>
    internal void ReleaseEntity(EntityId id)
    {
        var maps = _internal;
        for (int i = 0; i < maps.Count; i++)
        {
            maps[i].ReleaseEntity(id);
        }
    }
}
