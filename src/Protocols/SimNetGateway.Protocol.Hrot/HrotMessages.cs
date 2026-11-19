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

/// <summary>What a person holds in his hand (<see cref="HrotHeldItem.None"/> = his own weapon is not singled out, or not a life form).</summary>
public enum HrotHeldItem : byte
{
    None = 0,
    Weapon = 1,
    Throwable = 2,
    Phone = 3,
    Binoculars = 4,
}

/// <summary>A person's gesture; the values are HROT's gesture kinds (0 = none).</summary>
public enum HrotGesture : byte
{
    None = 0,
    HandsUp = 1,
    FistRaised = 2,
    HoldSign = 3,
    Waving = 4,
    Throwing = 5,
    Warning = 6,
    IdleSmoking = 7,
    IdleShiftWeight = 8,
    IdleStretch = 9,
    IdleLookAbout = 10,
}

/// <summary>The interaction a person is performing (HROT's interaction channel); <see cref="None"/> = no action.</summary>
public enum HrotAction : byte
{
    None = 0,
    Embark = 1,
    Disembark = 2,
    OpenDoor = 3,
    CloseDoor = 4,
    LockDoor = 5,
    UnlockDoor = 6,
    BreachDoor = 7,
    PickUpCasualty = 8,
    PutDownCasualty = 9,
    PickUpRound = 10,
    Eject = 11,
}

public enum HrotActionState : byte
{
    None = 0,
    Running = 1,
    Success = 2,
    Failure = 3,
}

public enum HrotSeatRole : byte
{
    None = 0,
    Driver = 1,
    Gunner = 2,
    Commander = 3,
    Passenger = 4,
    Station = 5,
}

/// <summary>How a person is held aboard: seated in a vehicle, or carried / dragged / on a stretcher by a person.</summary>
public enum HrotCarryMode : byte
{
    None = 0,
    Seated = 1,
    Carried = 2,
    Dragged = 3,
    Stretcher = 4,
}

/// <summary>
/// The semantic state of a person's body beyond <see cref="HrotEntityState"/> (DD-017 3.5), sent for life forms when any field changes. Ids are HROT network
/// ids (0 = none). <see cref="LookTarget"/> is a local ENU point, <see cref="HeadYaw"/> radians, + to his left. <see cref="StanceProgress"/> is 0..1 (1 when
/// the stance is settled). <see cref="ActionStartedAt"/> is the simulation time the gateway first saw the action running (0 = no action).
/// </summary>
public readonly record struct HrotPersonDetail(
    int Id, HrotStance TargetStance, float StanceProgress, HrotHeldItem HeldItem, byte ActiveMount, HrotVec3 LookTarget, bool HasLookTarget, float HeadYaw,
    HrotGesture Gesture, HrotAction Action, int ActionTarget, HrotActionState ActionState, double ActionStartedAt, int SeatVehicle, HrotSeatRole SeatRole,
    HrotCarryMode Carry) : IHrotToGatewayMessage;

/// <summary>
/// One visual effect derived by HROT's look rules (muzzle flash, explosion, ground mark, backblast, ...). <paramref name="Kind"/>, <paramref name="SizeClass"/> and
/// <paramref name="Surface"/> are the NAMES of HROT's own enumerations (effect kind, calibre class, effect surface), written by the host from the enumeration, so a
/// side never decodes HROT's numbers (a value HROT appends or reorders cannot be silently mislabelled; a side maps the names it knows and treats any other as unknown).
/// <paramref name="MunitionDis"/> is the munition's packed DIS entity type (0 = none), <paramref name="EffectType"/> the
/// effect's template type. <paramref name="AnchorEntity"/> is the network id of the entity the effect hangs on (the shooter for a muzzle flash; 0 = placed at
/// <paramref name="At"/>), <paramref name="AnchorMount"/> the weapon mount that fired. Local ENU metres, seconds.
/// </summary>
public readonly record struct HrotEffect(
    string Kind, string SizeClass, string Surface, ulong MunitionDis, long EffectType, HrotVec3 At, HrotQuat Rotation, int AnchorEntity, byte AnchorMount, float Lifetime,
    float Size) : IHrotToGatewayMessage;

/// <summary>
/// A person's resolved outfit from HROT's look library, sent once per person and again when it changes. Colours are 0xRRGGBBAA; the kinds are the names of HROT's own outfit
/// enumerations (hair style, legwear, camouflage, top, head, over, extra, beard), written by the host from the enumeration, so the gateway copies no vocabulary. <paramref name="Side"/> is the DIS force (1 friendly, 2 opposing, 3 neutral);
/// <paramref name="Seed"/> is the value the outfit was drawn from (the network id).
/// </summary>
public readonly record struct HrotPersonAppearance(
    int Id, long Seed, byte Side,
    uint Top, uint Sleeve, uint Bottom, uint Shin, uint Skin, uint Hair, uint Tie, uint HeadCloth, uint OverColour, uint ExtraColour,
    string HairStyle, string Legwear, string Camo, string TopKind, string HeadKind, string OverKind, string ExtraKind, string Beard,
    bool Suit, bool Child, bool Soldier, bool Police, bool Riot, bool Woman, bool Dressed, bool FacePaint,
    float EyeSpacing, float MouthHalfWidth, float BrowHalfHeight, uint EyeColour, uint Lips) : IHrotToGatewayMessage;

/// <summary>
/// How a vehicle looks (HROT's vehicle look), sent when it changes. <paramref name="Destruction"/> is the NAME of HROT's destruction kind (its "none" value while
/// the vehicle is not destroyed), written by the host from the enumeration; <paramref name="Parts"/> lists the
/// parts that are not intact (part id, 1 damaged / 2 destroyed; an absent part is intact); <paramref name="Openings"/> every opening of the vehicle's type (opening id,
/// progress 0 closed .. 1 open, in steps of 1/50); <paramref name="Lights"/> the effective light mask (brake lights included). A part's burning needs the part's
/// centre, which only a renderer knows, so burning is the vehicle's.
/// </summary>
public readonly record struct HrotVehicleLook(
    int Id, string Destruction, bool TurretThrown, bool Burning, bool Smoking, uint Lights, IReadOnlyList<(byte Part, byte State)> Parts,
    IReadOnlyList<(byte Opening, float Progress)> Openings) : IHrotToGatewayMessage;
