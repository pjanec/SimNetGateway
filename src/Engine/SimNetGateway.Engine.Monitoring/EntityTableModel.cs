namespace SimNetGateway.Engine.Monitoring;

/// <summary>The sortable columns of the entity table.</summary>
public enum EntityColumn
{
    Id,
    Name,
    Origin,
    Owner,
    Parent,
    Attributes,
    Created,
    LastWrite,
    LastChange,
    LastOriginWrite,

    /// <summary>The key in one identity map (<see cref="EntityTableModel.Sort"/> says which); entities without a key sort first.</summary>
    Key,
}

/// <summary>Which attributes the table treats as the entity's name and its parent; both optional.</summary>
public sealed record EntityTableOptions
{
    /// <summary>A string attribute with the entity's name (shown, searched by <see cref="EntityFilter.NameContains"/>).</summary>
    public AttrId NameAttribute { get; init; }

    /// <summary>An entity-reference attribute pointing at the entity's parent (shown, filtered by <see cref="EntityFilter.Parent"/>).</summary>
    public AttrId ParentAttribute { get; init; }

    /// <summary>Whether rows carry the text of the entity's key in every identity map (costs one string per key).</summary>
    public bool IncludeKeys { get; init; } = true;
}

/// <summary>
/// Which entities the table shows (DD-005 section 3, Entities). Every set criterion must hold. Time windows are in seconds of
/// wall time before the repository's current time: "within" means the time is not older than the window, "not ... within"
/// the opposite (the semantics of the engine's queries).
/// </summary>
public sealed record EntityFilter
{
    public SourceId? Origin { get; init; }
    public SourceId? Owner { get; init; }
    public double? ChangedWithin { get; init; }
    public double? WrittenWithin { get; init; }
    public double? NotWrittenWithin { get; init; }
    public double? OriginWrittenWithin { get; init; }

    /// <summary>Stale candidates (R-068): the owner side has not written the entity within the window.</summary>
    public double? NotOriginWrittenWithin { get; init; }

    public IReadOnlyList<AttrId>? Has { get; init; }
    public IReadOnlyList<AttrId>? Lacks { get; init; }

    /// <summary>Names of identity maps in which the entity has a key.</summary>
    public IReadOnlyList<string>? KnownTo { get; init; }

    /// <summary>Names of identity maps in which the entity has no key.</summary>
    public IReadOnlyList<string>? UnknownTo { get; init; }

    /// <summary>Case-insensitive text the entity's name contains.</summary>
    public string? NameContains { get; init; }

    /// <summary>Only members of this parent (the value of the parent attribute).</summary>
    public EntityId? Parent { get; init; }
}

/// <summary>One entity as the table shows it: a copy, safe to keep.</summary>
public readonly record struct EntityRow(
    EntityId Id,
    string Name,
    SourceId Origin,
    string OriginName,
    SourceId Owner,
    string OwnerName,
    EntityId Parent,
    int AttributeCount,
    double CreatedWallTime,
    double LastWriteWallTime,
    double LastChangeWallTime,
    double LastOriginWriteWallTime,
    string?[]? Keys);

/// <summary>
/// The Entities panel's model: filters, sorts and copies every live entity into rows (DD-005 section 3, R-075). Rows are
/// plain values (strings, ids, numbers, an array of key texts); no entity record is kept. The table is rebuilt by
/// <see cref="Refresh"/> (at most a few times per second); paging reads <see cref="Rows"/> by index.
/// </summary>
public sealed class EntityTableModel
{
    private readonly GatewayEngine _engine;
    private readonly EntityTableOptions _options;
    private readonly List<EntityRow> _rows = new();
    private EntityFilter _filter = new();
    private EntityColumn _column = EntityColumn.Id;
    private bool _descending;
    private int _keyMap;

    public EntityTableModel(GatewayEngine engine, EntityTableOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _options = options ?? new EntityTableOptions();
    }

    public EntityFilter Filter
    {
        get => _filter;
        set => _filter = value ?? new EntityFilter();
    }

    public EntityColumn SortColumn => _column;

    public bool SortDescending => _descending;

    /// <summary>For <see cref="EntityColumn.Key"/>: the index into <see cref="MapNames"/>.</summary>
    public int SortKeyMap => _keyMap;

    /// <summary>The rows matching the filter, sorted; valid until the next <see cref="Refresh"/> (the list is reused).</summary>
    public IReadOnlyList<EntityRow> Rows => _rows;

    public int Count => _rows.Count;

    /// <summary>Live entities in the repository at the last refresh (before filtering).</summary>
    public int TotalEntities { get; private set; }

    /// <summary>The identity maps whose keys the rows carry, in <see cref="EntityRow.Keys"/> order.</summary>
    public IReadOnlyList<string> MapNames { get; private set; } = Array.Empty<string>();

    public void Sort(EntityColumn column, bool descending = false, int keyMap = 0)
    {
        _column = column;
        _descending = descending;
        _keyMap = keyMap;
    }

    /// <summary>Copies the page [offset, offset + buffer.Length) of the rows; returns how many were written.</summary>
    public int CopyPage(int offset, Span<EntityRow> buffer)
    {
        int n = Math.Min(buffer.Length, Math.Max(0, _rows.Count - offset));
        for (int i = 0; i < n; i++)
        {
            buffer[i] = _rows[offset + i];
        }

        return n;
    }

    public void Refresh()
    {
        var engine = _engine;
        var state = engine.State;
        var maps = engine.Identities.All;
        var names = new string[maps.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = maps[i].Name;
        }

        MapNames = names;
        TotalEntities = state.Count;
        double now = state.Now;
        var filter = _filter;
        var known = Resolve(filter.KnownTo, maps);
        var unknown = Resolve(filter.UnknownTo, maps);
        string? text = string.IsNullOrEmpty(filter.NameContains) ? null : filter.NameContains;
        var nameAttr = _options.NameAttribute;
        var parentAttr = _options.ParentAttribute;

        _rows.Clear();
        foreach (var entity in state.Entities)
        {
            if (!Matches(entity, filter, now, known, unknown, text, nameAttr, parentAttr))
            {
                continue;
            }

            string name = !nameAttr.IsNone && entity.TryGet(nameAttr, out var nv) && nv.Kind == AttrKind.String ? nv.AsString() : string.Empty;
            var parent = !parentAttr.IsNone && entity.TryGet(parentAttr, out var pv) && pv.Kind == AttrKind.EntityRef ? pv.AsEntity() : default;
            string?[]? keys = null;
            if (_options.IncludeKeys)
            {
                keys = new string?[maps.Count];
                for (int m = 0; m < keys.Length; m++)
                {
                    keys[m] = maps[m].TryDescribeKey(entity.Id, out var key) ? key : null;
                }
            }

            _rows.Add(new EntityRow(entity.Id, name, entity.Origin, Names.Source(engine, entity.Origin), entity.Owner,
                Names.Source(engine, entity.Owner), parent, entity.Count, entity.CreatedWallTime, entity.LastWriteWallTime,
                entity.LastChangeWallTime, entity.LastOriginWriteWallTime, keys));
        }

        _rows.Sort(MakeComparison(_column, _descending, _keyMap));
    }

    private static IIdentityMap[] Resolve(IReadOnlyList<string>? names, IReadOnlyList<IIdentityMap> maps)
    {
        if (names is null || names.Count == 0)
        {
            return Array.Empty<IIdentityMap>();
        }

        var result = new List<IIdentityMap>();
        foreach (string name in names)
        {
            foreach (var map in maps)
            {
                if (string.Equals(map.Name, name, StringComparison.Ordinal))
                {
                    result.Add(map);
                }
            }
        }

        return result.ToArray();
    }

    private static bool Matches(EntityRecord e, EntityFilter f, double now, IIdentityMap[] known, IIdentityMap[] unknown,
        string? text, AttrId nameAttr, AttrId parentAttr)
    {
        if (f.Origin is { } origin && e.Origin != origin)
        {
            return false;
        }

        if (f.Owner is { } owner && e.Owner != owner)
        {
            return false;
        }

        if (f.ChangedWithin is { } c && e.LastChangeWallTime < now - c)
        {
            return false;
        }

        if (f.WrittenWithin is { } w && e.LastWriteWallTime < now - w)
        {
            return false;
        }

        if (f.NotWrittenWithin is { } nw && e.LastWriteWallTime >= now - nw)
        {
            return false;
        }

        if (f.OriginWrittenWithin is { } ow && e.LastOriginWriteWallTime < now - ow)
        {
            return false;
        }

        if (f.NotOriginWrittenWithin is { } now2 && e.LastOriginWriteWallTime >= now - now2)
        {
            return false;
        }

        if (f.Has is { } has)
        {
            foreach (var attr in has)
            {
                if (!e.Has(attr))
                {
                    return false;
                }
            }
        }

        if (f.Lacks is { } lacks)
        {
            foreach (var attr in lacks)
            {
                if (e.Has(attr))
                {
                    return false;
                }
            }
        }

        foreach (var map in known)
        {
            if (!map.Contains(e.Id))
            {
                return false;
            }
        }

        foreach (var map in unknown)
        {
            if (map.Contains(e.Id))
            {
                return false;
            }
        }

        if (text is not null)
        {
            if (nameAttr.IsNone || !e.TryGet(nameAttr, out var nv) || nv.Kind != AttrKind.String
                || nv.AsString().IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        if (f.Parent is { } parent)
        {
            if (parentAttr.IsNone || !e.TryGet(parentAttr, out var pv) || pv.Kind != AttrKind.EntityRef || pv.AsEntity() != parent)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The order of the table: by the column (strings ordinal, missing keys first), ties by entity id ascending.</summary>
    private static Comparison<EntityRow> MakeComparison(EntityColumn column, bool descending, int keyMap)
    {
        int sign = descending ? -1 : 1;
        return (a, b) =>
        {
            int c = column switch
            {
                EntityColumn.Id => a.Id.Value.CompareTo(b.Id.Value),
                EntityColumn.Name => string.CompareOrdinal(a.Name, b.Name),
                EntityColumn.Origin => string.CompareOrdinal(a.OriginName, b.OriginName),
                EntityColumn.Owner => string.CompareOrdinal(a.OwnerName, b.OwnerName),
                EntityColumn.Parent => a.Parent.Value.CompareTo(b.Parent.Value),
                EntityColumn.Attributes => a.AttributeCount.CompareTo(b.AttributeCount),
                EntityColumn.Created => a.CreatedWallTime.CompareTo(b.CreatedWallTime),
                EntityColumn.LastWrite => a.LastWriteWallTime.CompareTo(b.LastWriteWallTime),
                EntityColumn.LastChange => a.LastChangeWallTime.CompareTo(b.LastChangeWallTime),
                EntityColumn.LastOriginWrite => a.LastOriginWriteWallTime.CompareTo(b.LastOriginWriteWallTime),
                EntityColumn.Key => CompareKeys(a, b, keyMap),
                _ => 0,
            };
            return c != 0 ? c * sign : a.Id.Value.CompareTo(b.Id.Value);
        };
    }

    private static int CompareKeys(in EntityRow a, in EntityRow b, int keyMap)
    {
        string? ka = a.Keys is { } x && keyMap < x.Length ? x[keyMap] : null;
        string? kb = b.Keys is { } y && keyMap < y.Length ? y[keyMap] : null;
        if (ka is null || kb is null)
        {
            return (ka is null ? 0 : 1).CompareTo(kb is null ? 0 : 1);
        }

        return string.CompareOrdinal(ka, kb);
    }
}
