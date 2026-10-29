using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// The pivot schema of the NED-RPR gateway (R-037): registers every attribute and event type on the engine's registries
/// and exposes the ids. Registration is idempotent by name, so constructing it twice on the same registries yields the
/// same ids. Names are <c>Domain.Name</c>; units and frames are in the descriptions.
/// </summary>
public sealed class PivotSchema
{
    /// <summary>Edge of a cell of the spatial index on <see cref="Position"/>, metres.</summary>
    public const double PositionCellSize = 1000.0;

    public PivotSchema(AttributeRegistry attributes, EventTypeRegistry events)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(events);

        Name = attributes.Register("Identity.Name", AttrKind.String, "Entity name / marking");
        TypeTkb = attributes.Register("Identity.TypeTkb", AttrKind.Int, "Entity type, TKB numbering (the pivot numbering for types)");
        Force = attributes.Register("Identity.Force", AttrKind.Int, "Force, NedForce numbering (0 unknown, 1 friendly, 2 opposing, 3 neutral)");

        Position = attributes.Register("Spatial.Position", AttrKind.Vec3, "Position, ECEF, metres",
            AttrOptions.SpatialIndex, PositionCellSize);
        Velocity = attributes.Register("Spatial.Velocity", AttrKind.Vec3, "Velocity, ECEF, m/s");
        Acceleration = attributes.Register("Spatial.Acceleration", AttrKind.Vec3, "Acceleration, ECEF, m/s^2");
        Orientation = attributes.Register("Spatial.Orientation", AttrKind.Vec3, "Geocentric Euler angles (psi, theta, phi), radians");
        AngularVelocity = attributes.Register("Spatial.AngularVelocity", AttrKind.Vec3, "Angular velocity, body frame, rad/s");
        DrAlgorithm = attributes.Register("Spatial.DrAlgorithm", AttrKind.Int, "Dead-reckoning algorithm, DIS numbering");
        StateTime = attributes.Register("Spatial.StateTime", AttrKind.Double, "Simulation time of the spatial state, seconds");

        Damage = attributes.Register("Status.Damage", AttrKind.Int, "Damage level 0..3 (0 none, 1 light, 2 heavy, 3 destroyed); the NED scale is the pivot scale");

        SelectedMunition = attributes.Register("Weapon.SelectedMunition", AttrKind.Int, "Selected munition, TKB numbering");
        Ammo = attributes.Register("Weapon.Ammo", AttrKind.Int, "Remaining ammunition, rounds");

        PredictedPosition = attributes.Register("Derived.PredictedPosition", AttrKind.Vec3, "Dead-reckoned position at PredictedAt, ECEF, metres");
        PredictedAt = attributes.Register("Derived.PredictedAt", AttrKind.Double, "Simulation time of PredictedPosition, seconds");

        EventId = attributes.Register("Event.Id", AttrKind.Object, "Event id (boxed Guid)");
        EventShooter = attributes.Register("Event.Shooter", AttrKind.EntityRef, "Shooting entity");
        EventTarget = attributes.Register("Event.Target", AttrKind.EntityRef, "Target entity");
        EventMunitionTkb = attributes.Register("Event.MunitionTkb", AttrKind.Int, "Munition, TKB numbering; 0 = not given");
        EventLocation = attributes.Register("Event.Location", AttrKind.Vec3, "Event location, ECEF, metres");
        EventQuantity = attributes.Register("Event.Quantity", AttrKind.Int, "Number of rounds");
        EventResult = attributes.Register("Event.Result", AttrKind.Int, "Detonation result in the pivot numbering (0 other, 1 entity impact, 2 ground impact, 3 detonation); NED uses the same, RPR the FOM numbering (NedRprConversions)");

        Parent = attributes.Register("Structure.Parent", AttrKind.EntityRef,
            "The unit (formation, aggregate) this entity is a member of; reverse-indexed", AttrOptions.IndexReferences);
        MemberIndex = attributes.Register("Structure.MemberIndex", AttrKind.Int, "Position of the entity in its unit's member list");

        AggregateMemberIds = attributes.Register("RprAggregate.MemberIds", AttrKind.Object,
            "The last received member list of an RPR aggregate (immutable DisEntityId[]); private to the aggregate modules");

        FireId = attributes.Register("Fire.Id", AttrKind.Object, "Split-fire assembly: fire id (boxed Guid); private to the assembly modules");
        FireShooter = attributes.Register("Fire.Shooter", AttrKind.EntityRef, "Split-fire assembly: shooting entity");
        FireTarget = attributes.Register("Fire.Target", AttrKind.EntityRef, "Split-fire assembly: target entity");
        FireLocation = attributes.Register("Fire.Location", AttrKind.Vec3, "Split-fire assembly: fire location, ECEF, metres");
        FireMunition = attributes.Register("Fire.Munition", AttrKind.Int, "Split-fire assembly: munition, TKB numbering; 0 = not given");
        FireQuantity = attributes.Register("Fire.Quantity", AttrKind.Int, "Split-fire assembly: number of rounds");
        FirePending = attributes.Register("Fire.Pending", AttrKind.Bool, "Split-fire assembly: the second part has not arrived yet");
        FireStartedAt = attributes.Register("Fire.StartedAt", AttrKind.Double, "Split-fire assembly: wall time of the first part, seconds");

        WeaponFire = events.Register("WeaponFire", "A weapon was fired (payload Event.*)");
        Detonation = events.Register("Detonation", "A munition detonated (payload Event.*)");
    }

    public AttrId Name { get; }
    public AttrId TypeTkb { get; }
    public AttrId Force { get; }

    public AttrId Position { get; }
    public AttrId Velocity { get; }
    public AttrId Acceleration { get; }
    public AttrId Orientation { get; }
    public AttrId AngularVelocity { get; }
    public AttrId DrAlgorithm { get; }
    public AttrId StateTime { get; }

    public AttrId Damage { get; }

    public AttrId SelectedMunition { get; }
    public AttrId Ammo { get; }

    public AttrId PredictedPosition { get; }
    public AttrId PredictedAt { get; }

    public AttrId EventId { get; }
    public AttrId EventShooter { get; }
    public AttrId EventTarget { get; }
    public AttrId EventMunitionTkb { get; }
    public AttrId EventLocation { get; }
    public AttrId EventQuantity { get; }
    public AttrId EventResult { get; }

    /// <summary>The unit an entity is a member of (reverse-indexed: <c>Query().ReferencedBy(Parent, unit)</c> lists the members).</summary>
    public AttrId Parent { get; }

    public AttrId MemberIndex { get; }

    /// <summary>Module-private attribute of the RPR aggregate modules: an immutable <c>DisEntityId[]</c>.</summary>
    public AttrId AggregateMemberIds { get; }

    public AttrId FireId { get; }
    public AttrId FireShooter { get; }
    public AttrId FireTarget { get; }
    public AttrId FireLocation { get; }
    public AttrId FireMunition { get; }
    public AttrId FireQuantity { get; }
    public AttrId FirePending { get; }
    public AttrId FireStartedAt { get; }

    public EventTypeId WeaponFire { get; }
    public EventTypeId Detonation { get; }
}
