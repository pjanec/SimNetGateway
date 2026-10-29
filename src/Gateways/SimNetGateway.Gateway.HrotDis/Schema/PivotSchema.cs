using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// The pivot schema of the HROT DIS gateway (DD-006 section 3.1, R-080): RPR FOM 2.0 aligned names, ECEF / geocentric Euler /
/// body-axis values, DIS enumerations, plus HROT mirrors. Registration is idempotent by name. Names are <c>Domain.Name</c>;
/// units and frames are in the descriptions.
/// </summary>
public sealed class PivotSchema
{
    /// <summary>Edge of a cell of the spatial index on <see cref="Position"/>, metres (R-056).</summary>
    public const double PositionCellSize = 1000.0;

    public PivotSchema(AttributeRegistry attributes, EventTypeRegistry events)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(events);

        EntityType = attributes.Register("Identity.EntityType", AttrKind.Int,
            "DIS entity type packed in 64 bits (kind 8, domain 8, country 16, category 8, subcategory 8, specific 8, extra 8; kind on top)");
        Marking = attributes.Register("Identity.Marking", AttrKind.String, "Marking / name (the DIS egress truncates to 11 ASCII characters)");
        Force = attributes.Register("Identity.Force", AttrKind.Int, "DIS force id (0 other, 1 friendly, 2 opposing, 3 neutral)");

        Position = attributes.Register("Spatial.Position", AttrKind.Vec3, "Position, ECEF, metres", AttrOptions.SpatialIndex, PositionCellSize);
        Orientation = attributes.Register("Spatial.Orientation", AttrKind.Vec3,
            "Geocentric Euler angles (psi, theta, phi), radians; DIS body axes x forward, y right, z down");
        Velocity = attributes.Register("Spatial.Velocity", AttrKind.Vec3, "Velocity, ECEF, m/s (body axes for body-axis DR algorithms)");
        Acceleration = attributes.Register("Spatial.Acceleration", AttrKind.Vec3, "Acceleration, ECEF, m/s^2 (HROT: 0)");
        AngularVelocity = attributes.Register("Spatial.AngularVelocity", AttrKind.Vec3, "Angular velocity, body axes (DIS FRD), rad/s");
        DrAlgorithm = attributes.Register("Spatial.DrAlgorithm", AttrKind.Int, "Dead-reckoning algorithm, DIS numbering (HROT entities: 2, FPW)");
        StateTime = attributes.Register("Spatial.StateTime", AttrKind.Double, "Time at which the spatial values were valid, seconds");

        Damage = attributes.Register("Status.Damage", AttrKind.Int, "DIS damage state: 0 none, 1 slight, 2 moderate, 3 destroyed");
        Deactivated = attributes.Register("Status.Deactivated", AttrKind.Bool, "The entity announced that it is leaving");

        ArticulationParts = attributes.Register("Articulation.Parts", AttrKind.Object, "Articulated parts (immutable ArticulationParts)");
        Parent = attributes.Register("Structure.Parent", AttrKind.EntityRef, "ORBAT superior; reverse-indexed", AttrOptions.IndexReferences);

        TkbType = attributes.Register("Hrot.TkbType", AttrKind.Int, "HROT template id (mirror)");
        Health = attributes.Register("Hrot.Health", AttrKind.Vec3, "HROT health (current, max, 0) (mirror)");
        OwnerNode = attributes.Register("Hrot.OwnerNode", AttrKind.Int, "Node id of the HROT owner (diagnostics)");
        MasterFlags = attributes.Register("Hrot.MasterFlags", AttrKind.Int, "HROT EntityMasterFlags (mirror)");

        EventId = attributes.Register("Event.Id", AttrKind.Object, "DIS event identifier (boxed DisEventId)");
        EventFiring = attributes.Register("Event.Firing", AttrKind.EntityRef, "Firing entity");
        EventTarget = attributes.Register("Event.Target", AttrKind.EntityRef, "Target entity (optional)");
        EventMunitionEntity = attributes.Register("Event.MunitionEntity", AttrKind.EntityRef, "Munition entity (optional)");
        EventMunitionType = attributes.Register("Event.MunitionType", AttrKind.Int, "Munition, packed DIS entity type; 0 = none");
        EventMunitionTkb = attributes.Register("Event.MunitionTkb", AttrKind.Int, "Munition, TKB numbering (mirror); 0 = none");
        EventWarhead = attributes.Register("Event.Warhead", AttrKind.Int, "DIS warhead; 0 = other");
        EventFuse = attributes.Register("Event.Fuse", AttrKind.Int, "DIS fuse; 0 = other");
        EventQuantity = attributes.Register("Event.Quantity", AttrKind.Int, "Number of rounds");
        EventRate = attributes.Register("Event.Rate", AttrKind.Int, "Rate of fire, rounds per minute; 0 = single");
        EventLocation = attributes.Register("Event.Location", AttrKind.Vec3, "Event location, ECEF, metres");
        EventVelocity = attributes.Register("Event.Velocity", AttrKind.Vec3, "Munition velocity, ECEF, m/s");
        EventRange = attributes.Register("Event.Range", AttrKind.Double, "Range to the target, metres");
        EventRelativeLocation = attributes.Register("Event.RelativeLocation", AttrKind.Vec3, "Detonation location in the target's body coordinates (FRD), metres");
        EventResult = attributes.Register("Event.Result", AttrKind.Int, "DIS detonation result (1 entity impact, 3 ground impact)");

        FrameGeneration = attributes.Register("Hrot.FrameGeneration", AttrKind.Int, "Generation of the HROT origin that was just applied");

        WeaponFire = events.Register("WeaponFire", "A weapon was fired (payload Event.*)");
        MunitionDetonation = events.Register("MunitionDetonation", "A munition detonated (payload Event.*)");
        FrameRebased = events.Register("Hrot.FrameRebased", "The HROT origin became valid or changed (payload Hrot.FrameGeneration); replicas are re-sent");
    }

    public AttrId EntityType { get; }
    public AttrId Marking { get; }
    public AttrId Force { get; }

    public AttrId Position { get; }
    public AttrId Orientation { get; }
    public AttrId Velocity { get; }
    public AttrId Acceleration { get; }
    public AttrId AngularVelocity { get; }
    public AttrId DrAlgorithm { get; }
    public AttrId StateTime { get; }

    public AttrId Damage { get; }
    public AttrId Deactivated { get; }

    public AttrId ArticulationParts { get; }

    /// <summary>The ORBAT superior (reverse-indexed: <c>Query().ReferencedBy(Parent, unit)</c> lists the subordinates).</summary>
    public AttrId Parent { get; }

    public AttrId TkbType { get; }
    public AttrId Health { get; }
    public AttrId OwnerNode { get; }
    public AttrId MasterFlags { get; }

    public AttrId EventId { get; }
    public AttrId EventFiring { get; }
    public AttrId EventTarget { get; }
    public AttrId EventMunitionEntity { get; }
    public AttrId EventMunitionType { get; }
    public AttrId EventMunitionTkb { get; }
    public AttrId EventWarhead { get; }
    public AttrId EventFuse { get; }
    public AttrId EventQuantity { get; }
    public AttrId EventRate { get; }
    public AttrId EventLocation { get; }
    public AttrId EventVelocity { get; }
    public AttrId EventRange { get; }
    public AttrId EventRelativeLocation { get; }
    public AttrId EventResult { get; }

    public AttrId FrameGeneration { get; }

    public EventTypeId WeaponFire { get; }
    public EventTypeId MunitionDetonation { get; }

    /// <summary>Internal event of the gateway: the HROT origin became valid or changed (see <see cref="FrameGeneration"/>).</summary>
    public EventTypeId FrameRebased { get; }
}

/// <summary>DIS detonation results the gateway produces (SISO-REF-010 numbering).</summary>
public static class DetonationResults
{
    public const int Other = 0;
    public const int EntityImpact = 1;
    public const int GroundImpact = 3;
}

/// <summary>DIS dead-reckoning algorithm numbers the gateway needs.</summary>
public static class DrAlgorithms
{
    public const int Static = 1;
    public const int Fpw = 2;

    /// <summary>The algorithms whose velocity and acceleration are in body axes (FPB .. FVB).</summary>
    public static bool IsBodyAxis(int algorithm) => algorithm >= 6 && algorithm <= 9;
}

/// <summary>One articulated part of an entity: type id, azimuth and elevation (radians) and their rates (rad/s).</summary>
public readonly record struct ArticulationPart(int PartId, double Azimuth, double Elevation, double AzimuthRate = 0.0, double ElevationRate = 0.0);

/// <summary>The immutable value of <c>Articulation.Parts</c>: the parts ordered by part id, compared by value.</summary>
public sealed class ArticulationParts : IEquatable<ArticulationParts>
{
    public static readonly ArticulationParts Empty = new([]);

    private readonly ArticulationPart[] _parts;

    private ArticulationParts(ArticulationPart[] parts) => _parts = parts;

    public IReadOnlyList<ArticulationPart> Parts => _parts;

    /// <summary>A copy with the part replaced (same part id) or added; the order stays by part id.</summary>
    public ArticulationParts With(ArticulationPart part)
    {
        var list = new List<ArticulationPart>(_parts);
        int index = list.FindIndex(p => p.PartId == part.PartId);
        if (index >= 0)
        {
            list[index] = part;
        }
        else
        {
            list.Add(part);
            list.Sort((a, b) => a.PartId.CompareTo(b.PartId));
        }

        return new ArticulationParts(list.ToArray());
    }

    public bool Equals(ArticulationParts? other) => other is not null && _parts.AsSpan().SequenceEqual(other._parts);

    public override bool Equals(object? obj) => obj is ArticulationParts other && Equals(other);

    public override int GetHashCode() => _parts.Length;
}
