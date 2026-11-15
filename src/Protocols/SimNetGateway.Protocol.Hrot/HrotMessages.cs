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

/// <summary>
/// HROT's geo reference, protocol version 2 (DD-014): the frame of the local metres. <see cref="Frame"/> <see cref="FrameUtm"/> = UTM grid metres minus
/// (E0, N0, H0), x grid east, y grid north, z up; the origin is the latitude / longitude / altitude of local (0, 0, 0); the zone is the origin's
/// longitude band unless <see cref="UtmZoneOverride"/> is not 0, the hemisphere the latitude's sign unless an override zone is given (then
/// <see cref="UtmSouthOverride"/>). <see cref="FrameNone"/> = a local-only terrain: the local metres have no place on the Earth. <see cref="Generation"/>
/// grows with every different origin.
/// </summary>
public readonly record struct HrotOrigin(
    int ProtocolVersion,
    byte Frame,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeM,
    int UtmZoneOverride,
    bool UtmSouthOverride,
    int Generation) : IHrotToGatewayMessage
{
    /// <summary>The geo reference protocol version the gateway speaks.</summary>
    public const int CurrentProtocolVersion = 2;

    /// <summary>Frame 1: UTM.</summary>
    public const byte FrameUtm = 1;

    /// <summary>Frame 2: none (local-only).</summary>
    public const byte FrameNone = 2;

    /// <summary>A UTM frame at the given origin (zone override 0 = the origin's own zone).</summary>
    public static HrotOrigin Utm(double lat, double lon, double alt, int generation, int zoneOverride = 0, bool southOverride = false) =>
        new(CurrentProtocolVersion, FrameUtm, lat, lon, alt, zoneOverride, southOverride, generation);

    /// <summary>A local-only terrain: no frame.</summary>
    public static HrotOrigin None(int generation) =>
        new(CurrentProtocolVersion, FrameNone, 0.0, 0.0, 0.0, 0, false, generation);
}

/// <summary>The cluster state and the clock: <see cref="Advancing"/> says whether sim time runs.</summary>
public readonly record struct HrotSession(ClusterPhase Phase, bool Advancing, double TimeScale, double SimTime) : IHrotToGatewayMessage;

/// <summary>
/// A full snapshot of a HROT entity. <see cref="DisType"/> is the DIS entity type packed in 64 bits (kind 8, domain 8,
/// country 16, category 8, subcategory 8, specific 8, extra 8 bits, kind in the top byte), 0 = none. <see cref="Force"/>
/// uses the DIS numbering (0 other, 1 friendly, 2 opposing, 3 neutral). <see cref="CommanderId"/> 0 = none.
/// The last four fields describe a life form (DD-015): <see cref="AimPoint"/> is in local metres, the frame of <see cref="Position"/>, and means something only when
/// <see cref="Weapon"/> is <see cref="HrotWeaponPosture.Aimed"/>.
/// </summary>
public readonly record struct HrotEntityState(
    int Id,
    long TkbType,
    long DisType,
    string Marking,
    int Force,
    int CommanderId,
    HrotVec3 Position,
    HrotQuat Rotation,
    HrotVec3 Velocity,
    HrotVec3 AngularVelocity,
    double HealthCurrent,
    double HealthMax,
    double SimTime,
    HrotStance Stance = default,
    HrotWeaponPosture Weapon = default,
    HrotVec3 AimPoint = default,
    HrotInjury Injury = default) : IHrotToGatewayMessage;

/// <summary>The stance of a life form; <see cref="Unknown"/> for anything that has none (a vehicle).</summary>
public enum HrotStance : byte
{
    Unknown = 0,
    Standing = 1,
    Crouched = 2,
    Prone = 3,
}

/// <summary>What a life form does with the weapon it holds; <see cref="None"/> = no weapon in hand or not a life form.</summary>
public enum HrotWeaponPosture : byte
{
    None = 0,
    AtEase = 1,
    Ready = 2,
    Aimed = 3,
}

/// <summary>The injury state of a life form (Ok for anything else).</summary>
public enum HrotInjury : byte
{
    Ok = 0,
    Wounded = 1,
    Incapacitated = 2,
    Dead = 3,
}

public readonly record struct HrotEntityRemoved(int Id) : IHrotToGatewayMessage;

/// <summary>The pose of one turret (articulated part) of an entity, radians (stage H4).</summary>
public readonly record struct HrotTurretPose(int Id, int PartId, double Azimuth, double Elevation) : IHrotToGatewayMessage;

/// <summary><see cref="TargetId"/> 0 = none. <see cref="Origin"/> and <see cref="Velocity"/> are ENU.</summary>
public readonly record struct HrotFire(
    int ShooterId,
    int TargetId,
    long MunitionTkb,
    long MunitionDisType,
    HrotVec3 Origin,
    HrotVec3 Velocity,
    int Quantity) : IHrotToGatewayMessage;

/// <summary><see cref="TargetId"/> 0 = ground. <see cref="Location"/> and <see cref="Velocity"/> are ENU.</summary>
public readonly record struct HrotDetonation(
    int ShooterId,
    int TargetId,
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
    int Id,
    long TkbType,
    long DisType,
    string Marking,
    int Force,
    HrotDrSnapshot State,
    double HealthCurrent,
    double HealthMax,
    bool Deactivated) : IGatewayToHrotMessage;

public readonly record struct ReplicaRemoved(int Id) : IGatewayToHrotMessage;

/// <summary>A DIS Fire as HROT effects; <see cref="TargetIsHrot"/> says that <see cref="TargetId"/> is an entity simulated by HROT.</summary>
public readonly record struct InjectFire(
    int ShooterId,
    int TargetId,
    long MunitionTkb,
    long MunitionDisType,
    HrotVec3 Origin,
    HrotVec3 Velocity,
    int Quantity,
    bool TargetIsHrot) : IGatewayToHrotMessage;

/// <summary>A DIS Detonation; with <see cref="TargetIsHrot"/> HROT computes the damage itself (DD-007 section 3 item 7).</summary>
public readonly record struct InjectDetonation(
    int ShooterId,
    int TargetId,
    long MunitionTkb,
    long MunitionDisType,
    HrotVec3 Location,
    HrotVec3 Velocity,
    bool TargetIsHrot) : IGatewayToHrotMessage;

/// <summary>Pause or resume HROT's time; sent only when the configuration says that DIS controls HROT.</summary>
public readonly record struct TimeRequest(TimeRequestKind Kind) : IGatewayToHrotMessage;

/// <summary>
/// The groups of an entity's state that are owned as a unit (descriptors, DD-012 section 4a): they can be owned by a source other
/// than the HROT side, see <see cref="HrotDescriptorOwnership"/>.
/// </summary>
[Flags]
public enum HrotDescriptor
{
    /// <summary>No group (all local).</summary>
    None = 0,

    /// <summary>Position, orientation, velocities, acceleration, dead-reckoning algorithm, state time.</summary>
    Spatial = 1,

    /// <summary>Marking and force.</summary>
    Info = 2,

    /// <summary>Damage state and health.</summary>
    Damage = 4,

    /// <summary>Articulated parts (turret poses).</summary>
    Articulation = 8,
}

/// <summary>
/// The whole set of groups of a HROT entity that is currently owned outside HROT (absolute and idempotent; <see cref="HrotDescriptor.None"/>
/// = all local), sent whenever the set changes. HROT stops writing the groups in the set and adopts <see cref="ExternalState"/> for them.
/// </summary>
public readonly record struct HrotDescriptorOwnership(int Id, HrotDescriptor External) : IGatewayToHrotMessage;

/// <summary>
/// The values of the externally owned groups of a HROT entity, sent when a writer other than HROT changes one of them. Only the fields of the
/// groups named in <see cref="Descriptors"/> are meaningful (<see cref="State"/> for Spatial, <see cref="Marking"/> and <see cref="Force"/>
/// for Info, the health pair for Damage); the others are default.
/// </summary>
public readonly record struct ExternalState(
    int Id,
    HrotDescriptor Descriptors,
    HrotDrSnapshot State,
    string Marking,
    int Force,
    double HealthCurrent,
    double HealthMax) : IGatewayToHrotMessage;
