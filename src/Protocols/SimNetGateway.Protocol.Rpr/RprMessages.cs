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

public enum RprDetonationResult : byte
{
    Other = 0,
    EntityImpact = 1,
    GroundImpact = 2,
    Detonation = 3,
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

/// <summary>A null handle means "no object".</summary>
public readonly record struct RprWeaponFire(
    Guid EventId,
    RprObjectHandle? FiringObject,
    RprObjectHandle? TargetObject,
    WorldLocation Location,
    DisEntityType Munition,
    int Quantity) : IRprMessage;

/// <summary>A null handle means "no object".</summary>
public readonly record struct RprMunitionDetonation(
    Guid EventId,
    RprObjectHandle? FiringObject,
    RprObjectHandle? TargetObject,
    WorldLocation Location,
    DisEntityType Munition,
    RprDetonationResult Result) : IRprMessage;

public readonly record struct RprSimulationControl(RprControlKind Kind) : IRprMessage;

/// <summary>Outbound: the gateway asks the RTI to register an object; the token is opaque to the RTI.</summary>
public readonly record struct RprRegisterObject(long Token) : IRprMessage;

/// <summary>Inbound: the RTI's answer to <see cref="RprRegisterObject"/> with the same token.</summary>
public readonly record struct RprObjectRegistered(long Token, RprObjectHandle Handle) : IRprMessage;
