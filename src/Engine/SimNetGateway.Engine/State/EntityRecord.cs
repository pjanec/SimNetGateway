using System.Runtime.InteropServices;

namespace SimNetGateway.Engine;

/// <summary>One entity: identity, ownership, timestamps and its attribute slots. Mutated only by the repository.</summary>
public sealed class EntityRecord
{
    private readonly Dictionary<AttrId, AttrSlot> _slots = new();

    internal EntityRecord(EntityId id, SourceId origin, SourceId owner, CorrelationId createdBy, double createdWallTime,
        double createdSimTime)
    {
        Id = id;
        Origin = origin;
        Owner = owner;
        IsAlive = true;
        CreatedBy = createdBy;
        CreatedWallTime = createdWallTime;
        LastWriteWallTime = createdWallTime;
        LastChangeWallTime = createdWallTime;
        LastWriteSimTime = createdSimTime;
        LastChangeSimTime = createdSimTime;
        LastOriginWriteWallTime = createdWallTime;
        LastOriginWriteSimTime = createdSimTime;
    }

    public EntityId Id { get; }
    public SourceId Origin { get; }
    public SourceId Owner { get; internal set; }
    public bool IsAlive { get; internal set; }
    public CorrelationId CreatedBy { get; }
    public double CreatedWallTime { get; }
    public double LastWriteWallTime { get; internal set; }
    public double LastChangeWallTime { get; internal set; }

    /// <summary>Sim time of the last write; the sim clock restarts at 0 on Load, so this is not monotonic.</summary>
    public double LastWriteSimTime { get; internal set; }

    /// <summary>Sim time of the last change of any attribute value, owner or lifetime.</summary>
    public double LastChangeSimTime { get; internal set; }

    /// <summary>
    /// Wall time of the last committed change set that wrote the entity (create, set, clear, transfer) and whose source
    /// was the entity's <see cref="Owner"/> at that commit (the owner before the transaction; the owner defaults to the
    /// origin and changes only via TransferOwner, R-057). Writes by any other source (processors, the engine) do not
    /// move it, so it measures whether the side itself still sends the entity (R-068). A new entity starts with the
    /// times of its creation commit even when the creating source is not its owner, so that it does not look stale at
    /// birth. Known limitation: relayed traffic (R-061) written by a source other than the owner counts as a non-origin
    /// write; the WAN design (out of PoC scope, R-062) must write as the owner or adjust this rule.
    /// </summary>
    public double LastOriginWriteWallTime { get; internal set; }

    /// <summary>Sim time of the last origin write (see <see cref="LastOriginWriteWallTime"/>); not monotonic across Load.</summary>
    public double LastOriginWriteSimTime { get; internal set; }

    // Intrusive links of the repository's recency lists (by last write, by last change, by last origin write).
    internal EntityRecord? WritePrev;
    internal EntityRecord? WriteNext;
    internal EntityRecord? ChangePrev;
    internal EntityRecord? ChangeNext;
    internal EntityRecord? OriginPrev;
    internal EntityRecord? OriginNext;

    /// <summary>Number of attributes currently present.</summary>
    public int Count => _slots.Count;

    public bool Has(AttrId attr) => _slots.ContainsKey(attr);

    /// <summary>The value of the attribute, or <see cref="AttrValue.None"/> when absent.</summary>
    public AttrValue Get(AttrId attr) => _slots.TryGetValue(attr, out var slot) ? slot.Value : default;

    public bool TryGet(AttrId attr, out AttrValue value)
    {
        if (_slots.TryGetValue(attr, out var slot))
        {
            value = slot.Value;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetSlot(AttrId attr, out AttrSlot slot) => _slots.TryGetValue(attr, out slot);

    public AttributeEnumerable Attributes => new(_slots);

    internal ref AttrSlot SlotRef(AttrId attr) => ref CollectionsMarshal.GetValueRefOrNullRef(_slots, attr);

    internal void AddSlot(AttrId attr, in AttrSlot slot) => _slots.Add(attr, slot);

    internal bool RemoveSlot(AttrId attr) => _slots.Remove(attr);

    public override string ToString() => "Entity#" + Id.Value + (IsAlive ? "" : " (dead)");

    /// <summary>Allocation-free enumerable over (attribute, slot) pairs.</summary>
    public readonly struct AttributeEnumerable
    {
        private readonly Dictionary<AttrId, AttrSlot> _slots;

        internal AttributeEnumerable(Dictionary<AttrId, AttrSlot> slots) => _slots = slots;

        public AttributeEnumerator GetEnumerator() => new(_slots);
    }

    public struct AttributeEnumerator
    {
        private Dictionary<AttrId, AttrSlot>.Enumerator _inner;

        internal AttributeEnumerator(Dictionary<AttrId, AttrSlot> slots) => _inner = slots.GetEnumerator();

        public bool MoveNext() => _inner.MoveNext();

        public (AttrId Attr, AttrSlot Slot) Current
        {
            get
            {
                var kv = _inner.Current;
                return (kv.Key, kv.Value);
            }
        }
    }
}
