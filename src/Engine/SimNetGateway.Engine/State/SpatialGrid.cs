namespace SimNetGateway.Engine;

/// <summary>
/// Uniform 3D grid over one <see cref="AttrOptions.SpatialIndex"/> attribute: cell -> live records whose position lies
/// in it. The cell of an entity is derived from its stored position, so the repository removes it with the old value.
/// Emptied cells are dropped and their sets pooled.
/// </summary>
internal sealed class SpatialGrid
{
    private readonly Dictionary<CellKey, HashSet<EntityRecord>> _cells = new();
    private readonly EntitySetPool _pool;

    public SpatialGrid(double cellSize, EntitySetPool pool)
    {
        CellSize = cellSize;
        _pool = pool;
    }

    public double CellSize { get; }

    /// <summary>Number of occupied cells.</summary>
    public int CellCount => _cells.Count;

    public Dictionary<CellKey, HashSet<EntityRecord>> Cells => _cells;

    public void Add(Vec3 position, EntityRecord entity) => AddTo(CellKey.Of(position, CellSize), entity);

    public void Remove(Vec3 position, EntityRecord entity) => RemoveFrom(CellKey.Of(position, CellSize), entity);

    /// <summary>Moves the entity if the new position lies in another cell.</summary>
    public void Move(Vec3 from, Vec3 to, EntityRecord entity)
    {
        var a = CellKey.Of(from, CellSize);
        var b = CellKey.Of(to, CellSize);
        if (a != b)
        {
            RemoveFrom(a, entity);
            AddTo(b, entity);
        }
    }

    public bool TryGetCell(CellKey cell, out HashSet<EntityRecord> entities) => _cells.TryGetValue(cell, out entities!);

    private void AddTo(CellKey cell, EntityRecord entity)
    {
        if (!_cells.TryGetValue(cell, out var set))
        {
            set = _pool.Rent();
            _cells.Add(cell, set);
        }

        set.Add(entity);
    }

    private void RemoveFrom(CellKey cell, EntityRecord entity)
    {
        if (!_cells.TryGetValue(cell, out var set))
        {
            return;
        }

        set.Remove(entity);
        if (set.Count == 0)
        {
            _cells.Remove(cell);
            _pool.Return(set);
        }
    }
}
