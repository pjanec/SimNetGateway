namespace SimNetGateway.Engine;

// The indexes of the repository, all maintained by Apply() in O(1) per touched entity and allocation-free in steady
// state (DD-001 3.4, DD-004 P-33):
//   - three intrusive doubly linked recency lists (by last write, by last change, by last origin write), oldest at
//     the head;
//   - a presence set per attribute;
//   - a reverse reference index per IndexReferences attribute;
//   - a uniform spatial grid per SpatialIndex attribute.
public sealed partial class StateRepository
{
    private readonly EntitySetPool _setPool = new();
    private HashSet<EntityRecord>?[] _presence = new HashSet<EntityRecord>?[8];
    private ReferenceIndex?[] _references = new ReferenceIndex?[8];
    private SpatialGrid?[] _grids = new SpatialGrid?[8];

    // Recency lists. Order is only meaningful if commit wall times never decrease (the engine's wall clock is
    // monotonic); a window query that walks a list relies on it.
    private EntityRecord? _writeHead;
    private EntityRecord? _writeTail;
    private EntityRecord? _changeHead;
    private EntityRecord? _changeTail;
    private EntityRecord? _originHead;
    private EntityRecord? _originTail;

    /// <summary>The live entities by time of last write (even with an unchanged value), oldest first.</summary>
    public RecencyEnumerable ByLastWrite => new(_writeHead, RecencyKind.Write);

    /// <summary>The live entities by time of last change (a value, an owner or the lifetime), oldest first.</summary>
    public RecencyEnumerable ByLastChange => new(_changeHead, RecencyKind.Change);

    /// <summary>
    /// The live entities by time of last origin write (a write by the entity's owner, R-068), oldest first. Writes by
    /// processors and the engine do not move an entity in this list.
    /// </summary>
    public RecencyEnumerable ByLastOriginWrite => new(_originHead, RecencyKind.OriginWrite);

    internal EntityRecord? OriginHead => _originHead;
    internal EntityRecord? OriginTail => _originTail;
    internal EntityRecord? WriteHead => _writeHead;
    internal EntityRecord? WriteTail => _writeTail;
    internal EntityRecord? ChangeTail => _changeTail;

    // ---- recency ----------------------------------------------------------------------------------------

    private void AppendRecency(EntityRecord e)
    {
        LinkWriteTail(e);
        LinkChangeTail(e);
        LinkOriginTail(e);
    }

    private void MarkWritten(EntityRecord e, double wall, double sim)
    {
        e.LastWriteWallTime = wall;
        e.LastWriteSimTime = sim;
        if (!ReferenceEquals(_writeTail, e))
        {
            UnlinkWrite(e);
            LinkWriteTail(e);
        }
    }

    private void MarkChanged(EntityRecord e, double wall, double sim)
    {
        e.LastChangeWallTime = wall;
        e.LastChangeSimTime = sim;
        if (!ReferenceEquals(_changeTail, e))
        {
            UnlinkChange(e);
            LinkChangeTail(e);
        }
    }

    /// <summary>
    /// Records a write by <paramref name="source"/> as an origin write when it is the entity's owner before the
    /// transaction (<paramref name="ownerBefore"/>; R-068).
    /// </summary>
    private void MarkOriginWritten(EntityRecord e, SourceId ownerBefore, SourceId source, double wall, double sim)
    {
        if (source != ownerBefore)
        {
            return;
        }

        e.LastOriginWriteWallTime = wall;
        e.LastOriginWriteSimTime = sim;
        if (!ReferenceEquals(_originTail, e))
        {
            UnlinkOrigin(e);
            LinkOriginTail(e);
        }
    }

    private void LinkOriginTail(EntityRecord e)
    {
        e.OriginPrev = _originTail;
        e.OriginNext = null;
        if (_originTail is null)
        {
            _originHead = e;
        }
        else
        {
            _originTail.OriginNext = e;
        }

        _originTail = e;
    }

    private void UnlinkOrigin(EntityRecord e)
    {
        var prev = e.OriginPrev;
        var next = e.OriginNext;
        if (prev is null)
        {
            _originHead = next;
        }
        else
        {
            prev.OriginNext = next;
        }

        if (next is null)
        {
            _originTail = prev;
        }
        else
        {
            next.OriginPrev = prev;
        }

        e.OriginPrev = null;
        e.OriginNext = null;
    }

    private void LinkWriteTail(EntityRecord e)
    {
        e.WritePrev = _writeTail;
        e.WriteNext = null;
        if (_writeTail is null)
        {
            _writeHead = e;
        }
        else
        {
            _writeTail.WriteNext = e;
        }

        _writeTail = e;
    }

    private void UnlinkWrite(EntityRecord e)
    {
        var prev = e.WritePrev;
        var next = e.WriteNext;
        if (prev is null)
        {
            _writeHead = next;
        }
        else
        {
            prev.WriteNext = next;
        }

        if (next is null)
        {
            _writeTail = prev;
        }
        else
        {
            next.WritePrev = prev;
        }

        e.WritePrev = null;
        e.WriteNext = null;
    }

    private void LinkChangeTail(EntityRecord e)
    {
        e.ChangePrev = _changeTail;
        e.ChangeNext = null;
        if (_changeTail is null)
        {
            _changeHead = e;
        }
        else
        {
            _changeTail.ChangeNext = e;
        }

        _changeTail = e;
    }

    private void UnlinkChange(EntityRecord e)
    {
        var prev = e.ChangePrev;
        var next = e.ChangeNext;
        if (prev is null)
        {
            _changeHead = next;
        }
        else
        {
            prev.ChangeNext = next;
        }

        if (next is null)
        {
            _changeTail = prev;
        }
        else
        {
            next.ChangePrev = prev;
        }

        e.ChangePrev = null;
        e.ChangeNext = null;
    }

    // ---- presence, references, spatial ------------------------------------------------------------------

    private void IndexSlotAdded(EntityRecord e, AttrId attr, AttrValue value)
    {
        Grow(ref _presence, attr.Value);
        (_presence[attr.Value] ??= new HashSet<EntityRecord>()).Add(e);
        var def = _attributes[attr];
        if ((def.Options & AttrOptions.IndexReferences) != 0)
        {
            Grow(ref _references, attr.Value);
            (_references[attr.Value] ??= new ReferenceIndex(_setPool)).Add(value.AsEntity(), e);
        }

        if ((def.Options & AttrOptions.SpatialIndex) != 0)
        {
            Grow(ref _grids, attr.Value);
            (_grids[attr.Value] ??= new SpatialGrid(def.CellSize, _setPool)).Add(value.AsVec3(), e);
        }
    }

    /// <summary>The slot existed before and its value changed from <paramref name="old"/> to <paramref name="now"/>.</summary>
    private void IndexSlotChanged(EntityRecord e, AttrId attr, AttrValue old, AttrValue now)
    {
        var options = _attributes[attr].Options;
        if (options == AttrOptions.None)
        {
            return;
        }

        if ((options & AttrOptions.IndexReferences) != 0)
        {
            var from = old.AsEntity();
            var to = now.AsEntity();
            if (from != to)
            {
                var index = _references[attr.Value]!;
                index.Remove(from, e);
                index.Add(to, e);
            }
        }

        if ((options & AttrOptions.SpatialIndex) != 0)
        {
            _grids[attr.Value]!.Move(old.AsVec3(), now.AsVec3(), e);
        }
    }

    private void IndexSlotRemoved(EntityRecord e, AttrId attr, AttrValue old)
    {
        _presence[attr.Value]?.Remove(e);
        var options = _attributes[attr].Options;
        if ((options & AttrOptions.IndexReferences) != 0)
        {
            _references[attr.Value]?.Remove(old.AsEntity(), e);
        }

        if ((options & AttrOptions.SpatialIndex) != 0)
        {
            _grids[attr.Value]?.Remove(old.AsVec3(), e);
        }
    }

    /// <summary>A destroyed entity leaves every index.</summary>
    private void UnindexEntity(EntityRecord e)
    {
        foreach (var (attr, slot) in e.Attributes)
        {
            IndexSlotRemoved(e, attr, slot.Value);
        }

        UnlinkWrite(e);
        UnlinkChange(e);
        UnlinkOrigin(e);
    }

    private static void Grow<T>(ref T[] array, int index)
    {
        if (index >= array.Length)
        {
            Array.Resize(ref array, Math.Max(index + 1, array.Length * 2));
        }
    }

    // ---- index statistics (diagnostics, viewer, tests) ----------------------------------------------------

    /// <summary>Size of the presence set of the attribute: the live entities that have it.</summary>
    public int PresenceCount(AttrId attr) => PresenceOf(attr)?.Count ?? 0;

    /// <summary>Entries of the reverse reference index of an <see cref="AttrOptions.IndexReferences"/> attribute for one target.</summary>
    public int ReferrerCount(AttrId attr, EntityId target) =>
        TryGetReferrers(attr, target, out var referrers) ? referrers.Count : 0;

    /// <summary>Number of occupied cells of the grid of a <see cref="AttrOptions.SpatialIndex"/> attribute.</summary>
    public int OccupiedCellCount(AttrId attr) => GridOf(attr)?.CellCount ?? 0;

    /// <summary>Entries of one cell of the grid of a <see cref="AttrOptions.SpatialIndex"/> attribute.</summary>
    public int CellEntityCount(AttrId attr, CellKey cell) =>
        GridOf(attr) is { } grid && grid.TryGetCell(cell, out var set) ? set.Count : 0;

    // ---- access for the query executor ------------------------------------------------------------------

    internal Dictionary<EntityId, EntityRecord> EntityMap => _entities;

    /// <summary>The live entities that have the attribute; null when there are none yet.</summary>
    internal HashSet<EntityRecord>? PresenceOf(AttrId attr) =>
        (uint)attr.Value < (uint)_presence.Length ? _presence[attr.Value] : null;

    internal bool TryGetReferrers(AttrId attr, EntityId target, out HashSet<EntityRecord> referrers)
    {
        if ((uint)attr.Value < (uint)_references.Length && _references[attr.Value] is { } index)
        {
            return index.TryGet(target, out referrers);
        }

        referrers = null!;
        return false;
    }

    internal SpatialGrid? GridOf(AttrId attr) =>
        (uint)attr.Value < (uint)_grids.Length ? _grids[attr.Value] : null;
}
