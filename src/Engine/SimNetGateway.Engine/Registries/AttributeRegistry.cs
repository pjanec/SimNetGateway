using System.Collections;

namespace SimNetGateway.Engine;

/// <summary>
/// Index options of an attribute. <see cref="IndexReferences"/> needs kind <see cref="AttrKind.EntityRef"/> (reverse
/// reference index, DD-001 3.7); <see cref="SpatialIndex"/> needs kind <see cref="AttrKind.Vec3"/> and a cell size
/// (uniform grid, DD-004 P-33 / R-056).
/// </summary>
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
    internal AttributeDef(AttrId id, string name, AttrKind kind, string? description, AttrOptions options, double cellSize)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Description = description;
        Options = options;
        CellSize = cellSize;
    }

    public AttrId Id { get; }
    public string Name { get; }
    public AttrKind Kind { get; }
    public string? Description { get; internal set; }
    public AttrOptions Options { get; }

    /// <summary>Edge length of a spatial grid cell; 0 unless <see cref="AttrOptions.SpatialIndex"/> is set.</summary>
    public double CellSize { get; }

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

    /// <summary>
    /// Registers (or finds) an attribute. <paramref name="cellSize"/> is the grid cell edge of a
    /// <see cref="AttrOptions.SpatialIndex"/> attribute (required and positive with it, 0 without it).
    /// Index options on a value kind that cannot carry them throw <see cref="ArgumentException"/>.
    /// </summary>
    public AttrId Register(string name, AttrKind kind, string? description = null, AttrOptions options = AttrOptions.None,
        double cellSize = 0)
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
            if (existing.Kind != kind || existing.Options != options || existing.CellSize != cellSize)
            {
                throw new InvalidOperationException(
                    $"Attribute '{name}' is already registered as {existing.Kind}/{existing.Options}/cell {existing.CellSize}; " +
                    $"cannot re-register as {kind}/{options}/cell {cellSize}.");
            }

            existing.Description ??= description;
            return existing.Id;
        }

        // A new registration: the options must fit the value kind (a re-registration is checked against the first one).
        if ((options & AttrOptions.IndexReferences) != 0 && kind != AttrKind.EntityRef)
        {
            throw new ArgumentException($"Attribute '{name}': IndexReferences requires kind EntityRef, not {kind}.", nameof(options));
        }

        if ((options & AttrOptions.SpatialIndex) != 0)
        {
            if (kind != AttrKind.Vec3)
            {
                throw new ArgumentException($"Attribute '{name}': SpatialIndex requires kind Vec3, not {kind}.", nameof(options));
            }

            if (!(cellSize > 0) || double.IsInfinity(cellSize))
            {
                throw new ArgumentException($"Attribute '{name}': SpatialIndex requires a positive finite cell size.", nameof(cellSize));
            }
        }
        else if (cellSize != 0)
        {
            throw new ArgumentException($"Attribute '{name}': a cell size is only valid with SpatialIndex.", nameof(cellSize));
        }

        var id = new AttrId(_defs.Count + 1);
        var def = new AttributeDef(id, name, kind, description, options, cellSize);
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
