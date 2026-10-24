namespace SimNetGateway.Engine;

/// <summary>
/// What one transaction did to one entity (pooled; valid while its <see cref="ChangeSetRecord"/> is in the
/// journal). Holds enough to invert the transaction: old values, previous owner, the dead record.
/// </summary>
public sealed class EntityDelta
{
    private readonly List<AttrChange> _changes = new();

    internal EntityDelta()
    {
        Entity = null!;
    }

    public EntityRecord Entity { get; internal set; }
    public EntityId Id => Entity.Id;
    public bool Created { get; internal set; }
    public bool Destroyed { get; internal set; }
    public bool OwnerChanged { get; internal set; }
    public SourceId PreviousOwner { get; internal set; }

    /// <summary>One entry per attribute touched, in order of first touch.</summary>
    public ReadOnlyListView<AttrChange> Changes => new(_changes);

    internal List<AttrChange> ChangeList => _changes;

    public bool WasWritten(AttrId attr) => TryGetChange(attr, out _);

    public bool WasChanged(AttrId attr) => TryGetChange(attr, out var c) && c.Changed;

    public bool TryGetChange(AttrId attr, out AttrChange change)
    {
        var list = _changes;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Attr == attr)
            {
                change = list[i];
                return true;
            }
        }

        change = default;
        return false;
    }

    internal void Reset()
    {
        Entity = null!;
        Created = false;
        Destroyed = false;
        OwnerChanged = false;
        PreviousOwner = default;
        _changes.Clear();
    }
}
