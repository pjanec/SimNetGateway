namespace SimNetGateway.Engine;

/// <summary>
/// Reverse index of one <see cref="AttrOptions.IndexReferences"/> attribute: target entity -> the live records whose
/// attribute points at it. A destroyed target keeps its referrers (they still hold the reference); a destroyed
/// referrer is removed by the repository. A reference to <see cref="EntityId.None"/> is not indexed.
/// </summary>
internal sealed class ReferenceIndex
{
    private readonly Dictionary<EntityId, HashSet<EntityRecord>> _byTarget = new();
    private readonly EntitySetPool _pool;

    public ReferenceIndex(EntitySetPool pool) => _pool = pool;

    public void Add(EntityId target, EntityRecord referrer)
    {
        if (target.IsNone)
        {
            return;
        }

        if (!_byTarget.TryGetValue(target, out var set))
        {
            set = _pool.Rent();
            _byTarget.Add(target, set);
        }

        set.Add(referrer);
    }

    public void Remove(EntityId target, EntityRecord referrer)
    {
        if (target.IsNone || !_byTarget.TryGetValue(target, out var set))
        {
            return;
        }

        set.Remove(referrer);
        if (set.Count == 0)
        {
            _byTarget.Remove(target);
            _pool.Return(set);
        }
    }

    public bool TryGet(EntityId target, out HashSet<EntityRecord> referrers) => _byTarget.TryGetValue(target, out referrers!);
}
