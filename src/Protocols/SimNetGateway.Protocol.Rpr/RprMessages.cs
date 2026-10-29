namespace SimNetGateway.Protocol.Rpr;

/// <summary>Marker of every message on the RPR (HLA RPR-FOM / DIS) side.</summary>
public interface IRprMessage
{
}

/// <summary>RTI object instance handle.</summary>
public readonly record struct RprObjectHandle(uint Value);

public readonly record struct DisEntityId(ushort Site, ushort Application, ushort Entity);

public readonly record struct DisEntityType(
    byte Kind,
    byte Domain,
    ushort Country,
    byte Category,
    byte Subcategory,
    byte Specific,
    byte Extra);

/// <summary>ECEF position in metres.</summary>
public readonly record struct WorldLocation(double X, double Y, double Z);

/// <summary>Geocentric Euler angles in radians (psi, theta, phi).</summary>
public readonly record struct RprOrientation(double Psi, double Theta, double Phi);

/// <summary>A 3-vector (velocity m/s, acceleration m/s^2, angular velocity rad/s).</summary>
public readonly record struct RprVector(double X, double Y, double Z);

public enum RprDamageState : byte
{
    NoDamage = 0,
    SlightDamage = 1,
    ModerateDamage = 2,
    Destroyed = 3,
}

public enum RprForceId : byte
{
    Other = 0,
    Friendly = 1,
    Opposing = 2,
    Neutral = 3,
}

/// <summary>
/// The detonation result in the numbering of the RPR FOM 2.0 DetonationResultCodeEnum8 (DD-008 section 3.4, R-105). The gateway's
/// pivot numbering is a different one (Other, EntityImpact, GroundImpact, Detonation = 0..3); the NED-RPR gateway maps through
/// <c>NedRprConversions</c>. FOM codes above <see cref="None"/> are not represented (the HLA transport delivers them as
/// <see cref="Other"/>).
/// </summary>
public enum RprDetonationResult : byte
{
    Other = 0,
    EntityImpact = 1,
    EntityProximateDetonation = 2,
    GroundImpact = 3,
    GroundProximateDetonation = 4,
    Detonation = 5,
    None = 6,
}

public enum RprControlKind : byte
{
    StartResume,
    StopFreeze,
}

/// <summary>Attribute update of an object; only the attributes that are present are reflected.</summary>
public sealed record RprObjectUpdate(RprObjectHandle Handle) : IRprMessage
{
    public string? Marking { get; init; }
    public DisEntityType? EntityType { get; init; }
    public RprForceId? ForceId { get; init; }
    public DisEntityId? EntityIdentifier { get; init; }
    public WorldLocation? WorldLocation { get; init; }
    public RprOrientation? Orientation { get; init; }
    public RprVector? VelocityVector { get; init; }
    public RprVector? AccelerationVector { get; init; }
    public RprVector? AngularVelocity { get; init; }

    /// <summary>Dead-reckoning algorithm, DIS numbering.</summary>
    public byte? DeadReckoningAlgorithm { get; init; }

    public RprDamageState? DamageState { get; init; }

    /// <summary>The damage in percent (0..100), as a customer federation that does not use <see cref="DamageState"/> reports it.</summary>
    public byte? DamagePercent { get; init; }
}

/// <summary>
/// Inbound: an aggregate (unit) published by the RPR side. It lists its members by their DIS entity identifier, which
/// may name platforms that are not known (yet).
/// </summary>
public sealed record RprAggregateEntity(
    RprObjectHandle Handle,
    string Marking,
    DisEntityType EntityType,
    RprForceId ForceId,
    DisEntityId[] MemberIds) : IRprMessage;

public readonly record struct RprObjectRemoved(RprObjectHandle Handle) : IRprMessage;

/// <summary>
/// A null handle means "no object". The optional fields carry the RPR FOM WeaponFire parameters the gateway may not know
/// (DD-008 section 3.4, R-105); null = not known (an HLA transport sends the FOM default, and delivers null for a parameter the
/// peer did not send or that was undecodable). <see cref="FuseType"/> and <see cref="WarheadType"/> are the SISO-REF-010 codes.
/// </summary>
public readonly record struct RprWeaponFire(
    Guid EventId,
    RprObjectHandle? FiringObject,
    RprObjectHandle? TargetObject,
    WorldLocation Location,
    DisEntityType Munition,
    int Quantity) : IRprMessage
{
    public ushort? FuseType { get; init; }

    public ushort? WarheadType { get; init; }

    /// <summary>Rounds per minute.</summary>
    public ushort? RateOfFire { get; init; }

    /// <summary>Initial velocity of the munition, m/s.</summary>
    public RprVector? InitialVelocity { get; init; }

    /// <summary>Range to the fire control solution, metres.</summary>
    public float? FireControlSolutionRange { get; init; }

    /// <summary>The munition as an object, when the munition is tracked as one.</summary>
    public RprObjectHandle? MunitionObject { get; init; }
}

/// <summary>A null handle means "no object"; the optional fields are as for <see cref="RprWeaponFire"/>.</summary>
public readonly record struct RprMunitionDetonation(
    Guid EventId,
    RprObjectHandle? FiringObject,
    RprObjectHandle? TargetObject,
    WorldLocation Location,
    DisEntityType Munition,
    RprDetonationResult Result) : IRprMessage
{
    public ushort? FuseType { get; init; }

    public ushort? WarheadType { get; init; }

    public ushort? RateOfFire { get; init; }

    /// <summary>Final velocity of the munition, m/s.</summary>
    public RprVector? FinalVelocity { get; init; }

    /// <summary>Detonation location relative to the target entity, body frame, metres.</summary>
    public RprVector? RelativeDetonationLocation { get; init; }

    public RprObjectHandle? MunitionObject { get; init; }
}

public readonly record struct RprSimulationControl(RprControlKind Kind) : IRprMessage;

/// <summary>
/// Outbound: the gateway asks the RTI to register an object; the token is opaque to the RTI. The entity type decides the
/// object class an HLA transport registers (DD-008 section 3.5, R-105).
/// </summary>
public readonly record struct RprRegisterObject(long Token, DisEntityType EntityType) : IRprMessage;

/// <summary>
/// Inbound: another federate asked for the attributes of one of our objects to be provided (HLA provideAttributeValueUpdate);
/// the gateway answers with a full update of that object (DD-008 section 3.4, R-105).
/// </summary>
public readonly record struct RprUpdateRequested(RprObjectHandle Handle) : IRprMessage;

/// <summary>Inbound: the RTI's answer to <see cref="RprRegisterObject"/> with the same token.</summary>
public readonly record struct RprObjectRegistered(long Token, RprObjectHandle Handle) : IRprMessage;
