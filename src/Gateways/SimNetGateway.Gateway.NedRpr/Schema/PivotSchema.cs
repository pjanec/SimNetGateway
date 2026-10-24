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
        EventResult = attributes.Register("Event.Result", AttrKind.Int, "Detonation result (0 other, 1 entity impact, 2 ground impact, 3 detonation); same numbering on both sides");

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

    public EventTypeId WeaponFire { get; }
    public EventTypeId Detonation { get; }
}
