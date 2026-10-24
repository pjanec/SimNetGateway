using System.Runtime.InteropServices;

namespace SimNetGateway.Engine;

/// <summary>
/// Translation between one kind of wire identity (<typeparamref name="TKey"/>) of one side and <see cref="EntityId"/>
/// (DD-001 3.6, R-039). Lookups are O(1) and allocation-free (<see cref="EqualityComparer{T}.Default"/>, no boxing of
/// struct keys). Binding inside a transaction is buffered and validated and applied at commit with it; binding outside
/// a transaction is immediate. Maps are indexes: they raise no triggers.
/// </summary>
public sealed class IdentityMap<TKey> : IIdentityMapInternal, IChangeParticipant
    where TKey : notnull, IEquatable<TKey>
{
    private readonly StateRepository _state;
    private readonly IIdAllocator<TKey>? _allocator;

    // Committed bindings: both directions, plus a dense list of the bound entities (for KnownTo).
    private readonly Dictionary<TKey, EntityId> _byKey = new();
    private readonly Dictionary<EntityId, Binding> _byEntity = new();
    private readonly List<EntityId> _bound = new();

    // Bindings buffered in the open change set (cleared at commit / discard).
    private readonly List<PendingBind> _pending = new();
    private readonly Dictionary<TKey, EntityId> _pendingByKey = new();
    private readonly Dictionary<EntityId, TKey> _pendingByEntity = new();
    private string? _pendingConflict;
    private ChangeSet? _enlistedIn;

    internal IdentityMap(StateRepository state, SourceId side, string name, IIdAllocator<TKey>? allocator)
    {
        _state = state;
        Side = side;
        Name = name;
        _allocator = allocator;
    }

    public SourceId Side { get; }
    public string Name { get; }
    public Type KeyType => typeof(TKey);

    /// <summary>
    /// The allocator that creates keys for entities introduced to this side; null for a map whose keys only come from the
    /// wire. Read-only access for diagnostics (the showcase checks that an unload gives every key back).
    /// </summary>
    public IIdAllocator<TKey>? Allocator => _allocator;

    /// <summary>Number of committed bindings.</summary>
    public int Count => _byEntity.Count;

    public bool Contains(EntityId id) => _byEntity.ContainsKey(id);

    /// <inheritdoc />
    public bool TryDescribeKey(EntityId id, out string text)
    {
        if (_byEntity.TryGetValue(id, out var binding))
        {
            text = binding.Key.ToString() ?? string.Empty;
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>The entity bound to the key (committed bindings only).</summary>
    public bool TryResolve(in TKey key, out EntityId id) => _byKey.TryGetValue(key, out id);

    /// <summary>Like <see cref="TryResolve(in TKey, out EntityId)"/>, and also sees the bindings buffered in <paramref name="pending"/>.</summary>
    public bool TryResolve(ChangeSet pending, in TKey key, out EntityId id)
    {
        if (_byKey.TryGetValue(key, out id))
        {
            return true;
        }

        if (ReferenceEquals(_enlistedIn, pending) && _pendingByKey.TryGetValue(key, out id))
        {
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>The key of the entity; also works while its removal triggers run (keys are released afterwards).</summary>
    public bool TryGetKey(EntityId id, out TKey key)
    {
        if (_byEntity.TryGetValue(id, out var binding))
        {
            key = binding.Key;
            return true;
        }

        key = default!;
        return false;
    }

    /// <summary>
    /// Buffers a binding in the open transaction. At commit the entity must be alive at the end of the transaction,
    /// the key must be free (or already bound to this entity) and the entity must not have another key in this map;
    /// otherwise the whole transaction is rejected. Binding the same pair twice is idempotent.
    /// </summary>
    public void Bind(ChangeSet changes, EntityId id, in TKey key)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (id.IsNone)
        {
            throw new ArgumentException("Cannot bind the none entity.", nameof(id));
        }

        if (!ReferenceEquals(_enlistedIn, changes))
        {
            changes.Enlist(this); // throws when the change set is closed
            _enlistedIn = changes;
        }

        _pending.Add(new PendingBind(id, key));
        if (_pendingByKey.TryGetValue(key, out var other))
        {
            if (other != id)
            {
                _pendingConflict ??= $"identity map '{Name}': key {key} is bound to entities {other.Value} and {id.Value} in one transaction";
            }
        }
        else
        {
            _pendingByKey.Add(key, id);
        }

        if (_pendingByEntity.TryGetValue(id, out var otherKey))
        {
            if (!otherKey.Equals(key))
            {
                _pendingConflict ??= $"identity map '{Name}': entity {id.Value} gets keys {otherKey} and {key} in one transaction";
            }
        }
        else
        {
            _pendingByEntity.Add(id, key);
        }
    }

    /// <summary>
    /// Binds immediately, outside a transaction (an egress publishing an entity during dispatch). Returns false and
    /// binds nothing when the entity is not alive, the key is bound to another entity or the entity already has a
    /// key in this map; binding the same pair again returns true. (A refinement of the design, which shows void.)
    /// </summary>
    public bool Bind(EntityId id, in TKey key)
    {
        if (_state.Find(id) is null)
        {
            return false;
        }

        if (_byKey.TryGetValue(key, out var existing))
        {
            return existing == id;
        }

        if (_byEntity.ContainsKey(id))
        {
            return false;
        }

        AddBinding(id, key, allocated: false);
        return true;
    }

    /// <summary>
    /// The entity's key, or a newly allocated one bound immediately. False when there is no allocator, the allocator
    /// is exhausted or the entity is not alive. Bindings buffered in an open transaction are not considered.
    /// </summary>
    public bool TryGetOrAllocate(EntityId id, out TKey key)
    {
        if (_byEntity.TryGetValue(id, out var binding))
        {
            key = binding.Key;
            return true;
        }

        key = default!;
        if (_allocator is null || _state.Find(id) is null)
        {
            return false;
        }

        if (!_allocator.TryAllocate(out var allocated))
        {
            return false;
        }

        if (_byKey.ContainsKey(allocated))
        {
            _allocator.Release(allocated); // the allocator handed out a key that is already in use
            return false;
        }

        AddBinding(id, allocated, allocated: true);
        key = allocated;
        return true;
    }

    /// <summary>
    /// Removes the entity's committed binding while the entity stays alive (outside a transaction), e.g. when a side stops
    /// knowing the entity under that identity; a key from the allocator goes back to it. False when the entity has no key.
    /// </summary>
    public bool Unbind(EntityId id)
    {
        if (!_byEntity.ContainsKey(id))
        {
            return false;
        }

        ((IIdentityMapInternal)this).ReleaseEntity(id);
        return true;
    }

    private void AddBinding(EntityId id, in TKey key, bool allocated)
    {
        _byKey.Add(key, id);
        _byEntity.Add(id, new Binding(key, allocated, _bound.Count));
        _bound.Add(id);
    }

    EntityId IIdentityMapInternal.BoundAt(int index) => _bound[index];

    void IIdentityMapInternal.ReleaseEntity(EntityId id)
    {
        if (!_byEntity.Remove(id, out var binding))
        {
            return;
        }

        _byKey.Remove(binding.Key);
        int last = _bound.Count - 1;
        if (binding.Slot != last)
        {
            var moved = _bound[last];
            _bound[binding.Slot] = moved;
            CollectionsMarshal.GetValueRefOrNullRef(_byEntity, moved).Slot = binding.Slot;
        }

        _bound.RemoveAt(last);
        if (binding.Allocated)
        {
            _allocator!.Release(binding.Key); // keys received from a wire are not the allocator's
        }
    }

    // ---- IChangeParticipant -----------------------------------------------------------------------------

    string? IChangeParticipant.Validate()
    {
        if (_pendingConflict is not null)
        {
            return _pendingConflict;
        }

        var pending = _pending;
        for (int i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            if (!_state.IsAliveAfterTransaction(p.Id))
            {
                return $"identity map '{Name}': entity {p.Id.Value} is not alive at the end of the transaction";
            }

            if (_byKey.TryGetValue(p.Key, out var owner) && owner != p.Id)
            {
                return $"identity map '{Name}': key {p.Key} is already bound to entity {owner.Value}";
            }

            if (_byEntity.TryGetValue(p.Id, out var binding) && !binding.Key.Equals(p.Key))
            {
                return $"identity map '{Name}': entity {p.Id.Value} already has key {binding.Key}";
            }
        }

        return null;
    }

    void IChangeParticipant.Apply()
    {
        var pending = _pending;
        for (int i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            if (!_byEntity.ContainsKey(p.Id))
            {
                AddBinding(p.Id, p.Key, allocated: false);
            }
        }

        ClearPending();
    }

    void IChangeParticipant.Discard() => ClearPending();

    private void ClearPending()
    {
        _pending.Clear();
        _pendingByKey.Clear();
        _pendingByEntity.Clear();
        _pendingConflict = null;
        _enlistedIn = null;
    }

    public override string ToString() => Name + " (" + typeof(TKey).Name + ", " + Count + " bound)";

    private readonly record struct PendingBind(EntityId Id, TKey Key);

    private struct Binding
    {
        public readonly TKey Key;
        public readonly bool Allocated;
        public int Slot;

        public Binding(TKey key, bool allocated, int slot)
        {
            Key = key;
            Allocated = allocated;
            Slot = slot;
        }
    }
}
