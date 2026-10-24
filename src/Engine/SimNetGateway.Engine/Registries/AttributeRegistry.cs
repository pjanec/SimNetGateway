using System.Collections;

namespace SimNetGateway.Engine;

/// <summary>Index options of an attribute (flags only; the indexes themselves arrive with the queries).</summary>
[Flags]
public enum AttrOptions
{
    None = 0,
    IndexReferences = 1,
    SpatialIndex = 2,
}

/// <summary>Definition of a registered attribute.</summary>
public sealed class AttributeDef
{
    internal AttributeDef(AttrId id, string name, AttrKind kind, string? description, AttrOptions options)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Description = description;
        Options = options;
    }

    public AttrId Id { get; }
    public string Name { get; }
    public AttrKind Kind { get; }
    public string? Description { get; internal set; }
    public AttrOptions Options { get; }

    public override string ToString() => Name + ":" + Kind;
}

/// <summary>
/// Names -> typed attribute ids. Registration is idempotent by name, so independent modules that use the
/// same attribute get the same id. A conflicting re-registration is a programming error at startup.
/// </summary>
public sealed class AttributeRegistry : IEnumerable<AttributeDef>
{
    private readonly List<AttributeDef> _defs = new();
    private readonly Dictionary<string, AttributeDef> _byName = new(StringComparer.Ordinal);

    public int Count => _defs.Count;

    public AttrId Register(string name, AttrKind kind, string? description = null, AttrOptions options = AttrOptions.None)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Attribute name must not be empty.", nameof(name));
        }

        if (kind == AttrKind.None)
        {
            throw new InvalidOperationException($"Attribute '{name}' cannot be registered with kind None.");
        }

        if (_byName.TryGetValue(name, out var existing))
        {
            if (existing.Kind != kind || existing.Options != options)
            {
                throw new InvalidOperationException(
                    $"Attribute '{name}' is already registered as {existing.Kind}/{existing.Options}; " +
                    $"cannot re-register as {kind}/{options}.");
            }

            existing.Description ??= description;
            return existing.Id;
        }

        var id = new AttrId(_defs.Count + 1);
        var def = new AttributeDef(id, name, kind, description, options);
        _defs.Add(def);
        _byName.Add(name, def);
        return id;
    }

    public AttributeDef this[AttrId id]
    {
        get
        {
            if (!TryGet(id, out var def))
            {
                throw new KeyNotFoundException($"Unknown attribute id {id.Value}.");
            }

            return def;
        }
    }

    public bool TryGet(AttrId id, out AttributeDef def)
    {
        int index = id.Value - 1;
        if ((uint)index < (uint)_defs.Count)
        {
            def = _defs[index];
            return true;
        }

        def = null!;
        return false;
    }

    public bool TryGet(string name, out AttributeDef def)
    {
        if (name is not null && _byName.TryGetValue(name, out var found))
        {
            def = found;
            return true;
        }

        def = null!;
        return false;
    }

    public List<AttributeDef>.Enumerator GetEnumerator() => _defs.GetEnumerator();

    IEnumerator<AttributeDef> IEnumerable<AttributeDef>.GetEnumerator() => _defs.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _defs.GetEnumerator();
}
