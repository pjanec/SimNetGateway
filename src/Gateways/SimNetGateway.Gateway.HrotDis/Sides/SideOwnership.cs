using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// What a side may do with the ownership of the groups of entities (DD-012 section 4a items 1 and 6): move a group to a new owner and
/// return the groups of a leaving owner to the master. Both are ordinary writes in the change set the side already has open (an ingress
/// handler's <c>ctx.Changes</c>), so they are all-or-nothing with the rest of that message and journaled.
/// </summary>
public sealed class SideOwnership
{
    private readonly PivotSchema _schema;
    private readonly HrotDisSources _sources;

    internal SideOwnership(PivotSchema schema, HrotDisSources sources)
    {
        _schema = schema;
        _sources = sources;
    }

    /// <summary>The group with the given name ("Spatial", "Info", "Damage", "Articulation"), or null.</summary>
    public AttributeGroup? Group(string name) => _schema.Groups.Find(name);

    /// <summary>The effective owner of a group of an entity (default-to-master).</summary>
    public SourceId OwnerOf(EntityRecord entity, AttributeGroup group) => _schema.Groups.OwnerOf(entity, group);

    /// <summary>
    /// Writes the move of <paramref name="group"/> of <paramref name="entity"/> to <paramref name="newOwner"/> (the master owner or none
    /// moves it back). Refused (nothing written, <paramref name="refusal"/> says why) unless the entity is a HROT entity or an entity of
    /// <paramref name="requestingSide"/>'s own origin: the group of a DIS or HLA entity, or of another side's, is never moved (no relaying, R-116).
    /// </summary>
    public bool TryTransfer(ChangeSet changes, EntityRecord entity, AttributeGroup group, SourceId newOwner, SourceId requestingSide,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(group);
        if (!_schema.Groups.Groups.Contains(group))
        {
            refusal = $"'{group.Name}' is not a group of this gateway";
            return false;
        }

        if (entity.Origin != _sources.Hrot && entity.Origin != requestingSide)
        {
            refusal = $"entity {entity.Id.Value} is not a HROT entity and not of the requesting side's origin: its groups cannot be moved";
            return false;
        }

        _schema.Groups.Transfer(changes, entity, group, newOwner);
        refusal = null;
        return true;
    }

    /// <summary>Writes the return of every group owned by <paramref name="leavingOwner"/> to the master owner (a node left or went silent); returns the number of groups reset.</summary>
    public int ResetToMaster(ChangeSet changes, EntityRecord entity, SourceId leavingOwner) =>
        _schema.Groups.ResetToMaster(changes, entity, leavingOwner);
}
