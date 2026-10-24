namespace SimNetGateway.Engine;

/// <summary>Emptied entity sets go back here instead of being collected (indexes form and dissolve sets constantly).</summary>
internal sealed class EntitySetPool
{
    private readonly Stack<HashSet<EntityRecord>> _free = new();

    public HashSet<EntityRecord> Rent() => _free.Count > 0 ? _free.Pop() : new HashSet<EntityRecord>();

    public void Return(HashSet<EntityRecord> set)
    {
        set.Clear();
        _free.Push(set);
    }
}
