namespace SimNetGateway.Protocol.Hrot;

// The gateway <-> pack contract of DD-006 section 0 (R-091): the messages that the HROT DIS gateway pack (an ECS-level
// adapter inside the HROT editor, DD-007) exchanges with the vendored gateway over in-memory channels. Plain immutable
// values, BCL only. Units: local ENU metres (X east, Y north, Z up), HROT body quaternion (x, y, z, w) body-to-ENU with
// x forward, y left, z up, m/s, rad/s (angular velocity in ENU axes), degrees for the geodetic origin, HROT sim seconds.

/// <summary>Marker of every message of the contract.</summary>
public interface IHrotMessage
{
}

/// <summary>Marker of the messages that travel from the pack to the gateway.</summary>
public interface IHrotToGatewayMessage : IHrotMessage
{
}

/// <summary>Marker of the messages that travel from the gateway to the pack.</summary>
public interface IGatewayToHrotMessage : IHrotMessage
{
}

/// <summary>A 3-vector in local ENU axes (metres, m/s, rad/s).</summary>
public readonly record struct HrotVec3(double X, double Y, double Z)
{
    public static HrotVec3 Zero => default;
}

/// <summary>A unit quaternion (x, y, z, w); the default value is NOT a rotation, use <see cref="Identity"/>.</summary>
public readonly record struct HrotQuat(double X, double Y, double Z, double W)
{
    public static HrotQuat Identity => new(0.0, 0.0, 0.0, 1.0);
}

/// <summary>The state of the HROT cluster as the pack reports it (DD-007 section 6).</summary>
public enum ClusterPhase : byte
{
    Idle = 0,
    Edit = 1,
    Preview = 2,
    Live = 3,
    Replay = 4,
    Review = 5,
    Degraded = 6,
}

/// <summary>What the gateway asks the pack to do with HROT's time (only with <c>DisControlsHrot</c>).</summary>
public enum TimeRequestKind : byte
{
    Pause = 0,
    Resume = 1,
}

/// <summary>
/// A dead-reckoning snapshot: the state of a replica as the gateway last received it, valid at <see cref="StateTime"/>.
/// <see cref="DrAlgorithm"/> is the DIS algorithm number (1 static, 2 FPW, 3 RPW, 4 RVW, 5 FVW, 6 FPB, 7 RPB, 8 RVB, 9 FVB).
/// Position and <see cref="Rotation"/> are in the local ENU frame of the origin current when the message was built.
/// <see cref="Velocity"/> and <see cref="Acceleration"/> are ENU components for the world-axis algorithms (1 .. 5) and
/// body axes (DIS FRD, unchanged) for the body-axis ones (6 .. 9). <see cref="AngularVelocity"/> is in ENU axes.
/// </summary>
public readonly record struct HrotDrSnapshot(
    HrotVec3 Position,
    HrotVec3 Velocity,
    HrotVec3 Acceleration,
    HrotQuat Rotation,
    HrotVec3 AngularVelocity,
    int DrAlgorithm,
    double StateTime);

// ---- pack -> gateway ---------------------------------------------------------------------------------------------

/// <summary>The geodetic origin of the local ENU frame (<c>IGeographicTransform.Origin</c>); <see cref="Generation"/> grows with every change.</summary>
public readonly record struct HrotOrigin(double LatitudeDeg, double LongitudeDeg, double AltitudeM, int Generation) : IHrotToGatewayMessage;

/// <summary>The cluster state and the clock: <see cref="Advancing"/> says whether sim time runs.</summary>
public readonly record struct HrotSession(ClusterPhase Phase, bool Advancing, double TimeScale, double SimTime) : IHrotToGatewayMessage;

/// <summary>
/// A full snapshot of a HROT entity. <see cref="DisType"/> is the DIS entity type packed in 64 bits (kind 8, domain 8,
/// country 16, category 8, subcategory 8, specific 8, extra 8 bits, kind in the top byte), 0 = none. <see cref="Force"/>
/// uses the DIS numbering (0 other, 1 friendly, 2 opposing, 3 neutral). <see cref="CommanderId"/> 0 = none.
/// </summary>
public readonly record struct HrotEntityState(
    long Id,
    long TkbType,
    long DisType,
    string Marking,
    int Force,
    long CommanderId,
    HrotVec3 Position,
    HrotQuat Rotation,
    HrotVec3 Velocity,
    HrotVec3 AngularVelocity,
    double HealthCurrent,
    double HealthMax,
    double SimTime) : IHrotToGatewayMessage;

public readonly record struct HrotEntityRemoved(long Id) : IHrotToGatewayMessage;

/// <summary>The pose of one turret (articulated part) of an entity, radians (stage H4).</summary>
public readonly record struct HrotTurretPose(long Id, int PartId, double Azimuth, double Elevation) : IHrotToGatewayMessage;

/// <summary><see cref="TargetId"/> 0 = none. <see cref="Origin"/> and <see cref="Velocity"/> are ENU.</summary>
public readonly record struct HrotFire(
    long ShooterId,
    long TargetId,
    long MunitionTkb,
    long MunitionDisType,
    HrotVec3 Origin,
    HrotVec3 Velocity,
    int Quantity) : IHrotToGatewayMessage;

/// <summary><see cref="TargetId"/> 0 = ground. <see cref="Location"/> and <see cref="Velocity"/> are ENU.</summary>
public readonly record struct HrotDetonation(
    long ShooterId,
    long TargetId,
    long MunitionTkb,
    long MunitionDisType,
    HrotVec3 Location,
    HrotVec3 Velocity) : IHrotToGatewayMessage;

// ---- gateway -> pack ---------------------------------------------------------------------------------------------

/// <summary>
/// A DIS entity as a HROT replica (create or update by <see cref="Id"/>, a HROT entity id the gateway allocated):
/// sent only when a new DIS state arrived. <see cref="HealthCurrent"/> and <see cref="HealthMax"/> follow the DIS
/// damage state; <see cref="Deactivated"/> says the entity announced that it is leaving.
/// </summary>
public readonly record struct ReplicaState(
    long Id,
    long TkbType,
    long DisType,
    string Marking,
    int Force,
    HrotDrSnapshot State,
    double HealthCurrent,
    double HealthMax,
    bool Deactivated) : IGatewayToHrotMessage;

public readonly record struct ReplicaRemoved(long Id) : IGatewayToHrotMessage;

/// <summary>A DIS Fire as HROT effects; <see cref="TargetIsHrot"/> says that <see cref="TargetId"/> is an entity simulated by HROT.</summary>
public readonly record struct InjectFire(
    long ShooterId,
    long TargetId,
    long MunitionTkb,
    long MunitionDisType,
    HrotVec3 Origin,
    HrotVec3 Velocity,
    int Quantity,
    bool TargetIsHrot) : IGatewayToHrotMessage;

/// <summary>A DIS Detonation; with <see cref="TargetIsHrot"/> HROT computes the damage itself (DD-007 section 3 item 7).</summary>
public readonly record struct InjectDetonation(
    long ShooterId,
    long TargetId,
    long MunitionTkb,
    long MunitionDisType,
    HrotVec3 Location,
    HrotVec3 Velocity,
    bool TargetIsHrot) : IGatewayToHrotMessage;

/// <summary>Pause or resume HROT's time; sent only when the configuration says that DIS controls HROT.</summary>
public readonly record struct TimeRequest(TimeRequestKind Kind) : IGatewayToHrotMessage;
