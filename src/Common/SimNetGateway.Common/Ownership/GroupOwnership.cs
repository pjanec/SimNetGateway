using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// One group of attributes that is owned as a unit (a descriptor, DD-012 section 4a): the attributes, and the attribute that holds
/// the owner (an Int attribute whose value is a <see cref="SourceId"/> value; absent or <see cref="SourceId.None"/> = the entity's
/// master <see cref="EntityRecord.Owner"/>).
/// </summary>
public sealed class AttributeGroup
{
    public AttributeGroup(string name, AttrId ownerAttribute, IEnumerable<AttrId> attributes)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(attributes);
        Name = name;
        OwnerAttribute = ownerAttribute;
        Attributes = attributes.ToArray();
        if (Attributes.Count == 0)
        {
            throw new ArgumentException($"Group '{name}' has no attributes.", nameof(attributes));
        }
    }

    /// <summary>The group's name (for example "Damage").</summary>
    public string Name { get; }

    /// <summary>The attribute that holds the owner of the group.</summary>
    public AttrId OwnerAttribute { get; }

    /// <summary>The attributes owned together.</summary>
    public IReadOnlyList<AttrId> Attributes { get; }

    public override string ToString() => Name;
}

/// <summary>
/// The stateless helper of per-group ownership (DD-012 section 4a item 1): group definitions, attribute -> group, the effective owner of a
/// group of an entity (the owner attribute when present and not none, else the entity's master owner: default-to-master), and the writes
/// of a transfer and of a reset to master. The owners live in the pivot as ordinary attributes, so a transfer is an ordinary
/// all-or-nothing change set.
/// </summary>
public sealed class GroupOwnership
{
    private readonly AttributeGroup[] _groups;
    private readonly Dictionary<AttrId, AttributeGroup> _byAttribute = new();
    private readonly HashSet<AttrId> _ownerAttributes = new();

    public GroupOwnership(IEnumerable<AttributeGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        _groups = groups.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in _groups)
        {
            if (!names.Add(group.Name))
            {
                throw new ArgumentException($"Group '{group.Name}' is defined twice.", nameof(groups));
            }

            if (!_ownerAttributes.Add(group.OwnerAttribute))
            {
                throw new ArgumentException($"Group '{group.Name}' shares its owner attribute with another group.", nameof(groups));
            }
        }

        foreach (var group in _groups)
        {
            foreach (var attr in group.Attributes)
            {
                if (_ownerAttributes.Contains(attr))
                {
                    throw new ArgumentException($"An owner attribute is a member of group '{group.Name}'.", nameof(groups));
                }

                if (!_byAttribute.TryAdd(attr, group))
                {
                    throw new ArgumentException($"An attribute is in groups '{_byAttribute[attr].Name}' and '{group.Name}'.", nameof(groups));
                }
            }
        }
    }

    /// <summary>The groups in definition order.</summary>
    public IReadOnlyList<AttributeGroup> Groups => _groups;

    /// <summary>The group with the given name, or null.</summary>
    public AttributeGroup? Find(string name) => Array.Find(_groups, g => g.Name == name);

    /// <summary>The group an attribute belongs to; false for ungrouped attributes and for owner attributes.</summary>
    public bool TryGroupOf(AttrId attr, out AttributeGroup group) => _byAttribute.TryGetValue(attr, out group!);

    /// <summary>True for the attributes that hold group owners.</summary>
    public bool IsOwnerAttribute(AttrId attr) => _ownerAttributes.Contains(attr);

    /// <summary>The effective owner of a group of an entity: the owner attribute when present and not none, else <see cref="EntityRecord.Owner"/>.</summary>
    public SourceId OwnerOf(EntityRecord entity, AttributeGroup group)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(group);
        if (entity.TryGet(group.OwnerAttribute, out var value) && value.Kind == AttrKind.Int && value.AsInt() != 0)
        {
            return new SourceId((int)value.AsInt());
        }

        return entity.Owner;
    }

    /// <summary>True when the group is owned by a source other than <paramref name="master"/> (the side that owns the entity).</summary>
    public bool IsExternal(EntityRecord entity, AttributeGroup group, SourceId master) => OwnerOf(entity, group) != master;

    /// <summary>
    /// Adds the write that moves a group to <paramref name="newOwner"/> to the change set. A new owner equal to the entity's master owner
    /// (or none) clears the owner attribute, so the representation of "master" is unique.
    /// </summary>
    public void Transfer(ChangeSet changes, EntityRecord entity, AttributeGroup group, SourceId newOwner)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(group);
        if (newOwner.IsNone || newOwner == entity.Owner)
        {
            if (entity.Has(group.OwnerAttribute))
            {
                changes.Clear(entity.Id, group.OwnerAttribute);
            }
        }
        else
        {
            changes.Set(entity.Id, group.OwnerAttribute, AttrValue.From((long)newOwner.Value));
        }
    }

    /// <summary>
    /// Adds the writes that return every group owned by <paramref name="leavingOwner"/> to the master owner (a node left or went
    /// silent); returns the number of groups reset.
    /// </summary>
    public int ResetToMaster(ChangeSet changes, EntityRecord entity, SourceId leavingOwner)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(entity);
        int n = 0;
        if (leavingOwner.IsNone || leavingOwner == entity.Owner)
        {
            return 0;
        }

        foreach (var group in _groups)
        {
            if (OwnerOf(entity, group) == leavingOwner)
            {
                changes.Clear(entity.Id, group.OwnerAttribute);
                n++;
            }
        }

        return n;
    }
}

/// <summary>
/// A write authority policy that adds group ownership to an inner policy (DD-012 section 4a item 3): a write of a grouped attribute is
/// allowed iff the writer is the group's effective owner, the engine, or a trusted source (processors). Ungrouped attributes, the owner
/// attributes, destroy and owner transfer are decided by the inner policy.
/// </summary>
public sealed class GroupOwnershipPolicy : IWriteAuthorityPolicy
{
    private readonly IWriteAuthorityPolicy _inner;
    private readonly GroupOwnership _groups;
    private readonly HashSet<SourceId> _trusted;

    public GroupOwnershipPolicy(IWriteAuthorityPolicy inner, GroupOwnership groups, IEnumerable<SourceId> trusted)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(trusted);
        _inner = inner;
        _groups = groups;
        _trusted = new HashSet<SourceId>(trusted);
    }

    public bool CanWrite(EntityRecord entity, SourceId writer, AttrId attr)
    {
        if (_groups.TryGroupOf(attr, out var group))
        {
            return writer == SourceRegistry.Engine || _trusted.Contains(writer) || writer == _groups.OwnerOf(entity, group);
        }

        return _inner.CanWrite(entity, writer, attr);
    }

    public bool CanDestroy(EntityRecord entity, SourceId writer) => _inner.CanDestroy(entity, writer);

    public bool CanTransferOwner(EntityRecord entity, SourceId writer, SourceId newOwner) => _inner.CanTransferOwner(entity, writer, newOwner);
}
