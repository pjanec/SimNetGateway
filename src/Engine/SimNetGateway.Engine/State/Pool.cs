namespace SimNetGateway.Engine;

internal sealed class Pool<T> where T : class
{
    private readonly Stack<T> _items = new();
    private readonly Func<T> _factory;

    public Pool(Func<T> factory) => _factory = factory;

    public T Rent() => _items.Count > 0 ? _items.Pop() : _factory();

    public void Return(T item) => _items.Push(item);
}

/// <summary>The pools of the records produced by commits.</summary>
internal sealed class RecordPools
{
    public readonly Pool<ChangeSetRecord> Records = new(static () => new ChangeSetRecord());
    public readonly Pool<EntityDelta> Deltas = new(static () => new EntityDelta());

    public void ReturnRecord(ChangeSetRecord record)
    {
        var list = record.EntityList;
        for (int i = 0; i < list.Count; i++)
        {
            ReturnDelta(list[i]);
        }

        list.Clear();
        record.Correlation = default;
        record.CausedBy = default;
        record.Source = default;
        Records.Return(record);
    }

    public void ReturnDelta(EntityDelta delta)
    {
        delta.Reset();
        Deltas.Return(delta);
    }
}
