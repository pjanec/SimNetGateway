using CycloneDDS.Schema;

namespace SimNetGateway.Protocol.DdsPlayer;

// The DPS topics (DD-017 sections 3 and 4). Frames: the simulation's local Cartesian frame in metres (x east, y north, z up),
// quaternion (x, y, z, w), simulation time in seconds. EntityId is the gateway's network id of the entity. Fields are only ever
// appended. Strings and sequences are bounded: names 64, kinds 32, colours as RGBA bytes.
//
// Rate classes (DpsRate attribute on each topic, DD-020 3.1): Fixed = once per instance or type; Discrete = on events, with no continuous field (a value that moves
// over time is a start time + target + duration); Continuous = by threshold plus a heartbeat, the client extrapolates; Event = as they happen. A new field goes into a
// topic of its class.
//
// Vocabulary: free strings that carry the producer's own names (garment kinds, camouflage, hair, objective kinds and groups, impact materials, weapon and ammunition
// names, effect types, gizmo layers and families) are names from the producer's vocabulary, which Session.Vocabulary names (e.g. "hrot/1"). TkbType and BuildingDamage
// are producer-specific extensions a client may ignore.
//
// QoS: state topics Reliable + TransientLocal + KeepLast 1 (a late joiner gets every live instance); event topics Reliable +
// Volatile + KeepLast 1024 (MoverTracks 16); player topics (client to gateway) Reliable + Volatile + KeepLast 1 with automatic liveliness.

#region Shared structs

[DdsStruct]
public partial struct Vec3d { public double X, Y, Z; }

[DdsStruct]
public partial struct Vec3f { public float X, Y, Z; }

[DdsStruct]
public partial struct Quat { public float X, Y, Z, W; }

/// <summary>A DIS entity type (7 fields).</summary>
[DdsStruct]
public partial struct DisType
{
    public byte Kind, Domain;
    public ushort Country;
    public byte Category, Subcategory, Specific, Extra;
}

[DdsStruct]
[DdsManaged]
public partial struct NameItem
{
    [MaxLength(32)] public string Name;
}

#endregion

#region World state (gateway to client)

/// <summary>One instance: the exercise and the geo reference.</summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.Session)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct Session
{
    public int ProtocolMajor, ProtocolMinor;
    public SessionMode Mode;
    /// <summary>The simulation time at the last change of the session (mode, running, origin), not a clock: a client takes the time from <c>EntityState.SimTime</c>.</summary>
    public double SimTime;
    public double TimeScale;
    /// <summary>Changes on a world boundary (a reload or an in-place restore): a client drops its caches, joins again and re-sends its <c>GizmoView</c> (DD-017 3.11).</summary>
    public uint WorldEpoch;
    [MaxLength(64)] public string Terrain;
    [MaxLength(64)] public string StationName;
    public int UtmZone;
    public bool UtmNorthern;
    public double OriginEasting, OriginNorthing;
    /// <summary>The producer vocabulary the free strings of this session come from, as "name/version" (e.g. "hrot/1"); empty = not stated. See the client guide for the lists.</summary>
    [MaxLength(32)] public string Vocabulary;
    /// <summary>The format of the terrain export that <see cref="BuildingDamage.Building"/> keys refer to, as "name/version" (e.g. "hrot-terrain/1"); empty = no such export.</summary>
    [MaxLength(32)] public string TerrainFormat;
}

[DpsRate(DpsRateClass.Fixed)]
[DdsTopic(DpsProtocol.Entity)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct Entity
{
    [DdsKey] public int EntityId;
    /// <summary>The simulation's type id (64-bit, as in the simulation's TKB).</summary>
    public long TypeId;
    public Domain Domain;
    public byte Side;
    [MaxLength(64)] public string Name;
    public DisType Dis;
    /// <summary>The unit a player can take (the simulation says so).</summary>
    public bool Playable;
    public int ParentId;
}

[DpsRate(DpsRateClass.Continuous)]
[DdsTopic(DpsProtocol.EntityState)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct EntityState
{
    [DdsKey] public int EntityId;
    public double SimTime;
    public Vec3d Position;
    public Quat Orientation;
    public Vec3f LinearVelocity, AngularVelocity;
    public Vec3f LinearAcceleration, AngularAcceleration;
    public PartCondition Damage;
    /// <summary>Roll about the body's forward axis in radians, right-hand rule: positive = the top toward the right, a left turn is negative. A two-wheeler's lean into a turn computed by the host; 0 for everything else. Orientation stays the simulation's.</summary>
    public float Bank;
}

/// <summary>A movable part of an entity (turret, gun, rotor, door ...).</summary>
[DpsRate(DpsRateClass.Continuous)]
[DdsTopic(DpsProtocol.Articulation)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct Articulation
{
    [DdsKey] public int EntityId;
    /// <summary>Identifies the part within its entity; opaque to a client (use <see cref="PartKind"/> and <see cref="Index"/> to know what it is).</summary>
    [DdsKey] public int PartId;
    /// <summary>Superseded by <see cref="PartKind"/>: the producer's name of the kind ("Turret"); kept equal to it.</summary>
    [MaxLength(32)] public string Kind;
    public double SimTime;
    /// <summary>Traverse in radians, counter-clockwise from the entity's forward (relative to the hull).</summary>
    public float Azimuth;
    /// <summary>Elevation in radians, up from the part's own plane.</summary>
    public float Elevation;
    /// <summary>Linear extension in meters; 0 = unused.</summary>
    public float Extension;
    /// <summary>Angular rate in radians per second; 0 = unused.</summary>
    public float Rate;
    /// <summary>What kind of part this is.</summary>
    public ArticulationKind PartKind;
    /// <summary>The part's ordinal among the parts of its kind on the entity, from 0 (the first turret is 0).</summary>
    public int Index;
}

/// <summary>What a person's body does; the client chooses clips, blends and IK (DD-017 3.5).</summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.PersonState)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct PersonState
{
    [DdsKey] public int EntityId;
    public double SimTime;
    public Stance Stance, TargetStance;
    /// <summary>Superseded by <see cref="StanceChangedAt"/> + <see cref="StanceSeconds"/>: the transition's progress 0..1 as of the last sample (0 when a transition began, 1 when settled); never streamed.</summary>
    public float StanceProgress;
    public WeaponPosture Posture;
    /// <summary>Superseded by <see cref="PersonAim"/>: the aim point as of the last sample of this topic; not re-sent when it moves.</summary>
    public Vec3d AimPoint;
    public byte ActiveSlot;
    public HeldItem Held;
    /// <summary>Superseded by <see cref="PersonAim"/> (<c>HasLookTarget</c>, <c>LookTarget</c>, <c>HeadYaw</c>): the values as of the last sample of this topic; not re-sent when they change.</summary>
    public bool HasLookTarget;
    public Vec3d LookTarget;
    public float HeadYaw;
    public Gesture Gesture;
    public Injury Injury;
    public ActionKind Action;
    /// <summary>The entity or door the action is about (0 = none).</summary>
    public int ActionTargetId;
    public ActionState ActionState;
    /// <summary>The simulation time the action started running.</summary>
    public double ActionStartSimTime;
    public int SeatVehicleId;
    public SeatRole SeatRole;
    public CarryMode CarryMode;
    public int CarryOtherId;
    /// <summary>The person is on fire.</summary>
    public bool Burning;
    /// <summary>Fear and anger in quarters, 0 calm .. 4 extreme.</summary>
    public byte Fear, Anger;
    public CivilianMode CivilianMode;
    /// <summary>The vehicle seat slot of a man seated in a vehicle (255 = not seated).</summary>
    public byte SeatIndex;
    /// <summary>The seat is in the open (the man is seen and can be hit); false in an enclosed seat.</summary>
    public bool SeatExposed;
    /// <summary>How the man sits (the seat's pose); Unknown = not named.</summary>
    public SeatPose SeatPose;
    /// <summary>How the man is held, or what he carries (the producer's carriage rule). With <see cref="CarryOtherId"/> = his holder (a held or seated man) or what he carries.</summary>
    public CarryRole CarryRole;
    /// <summary>The other man of a pair carrying one load (a stretcher, a two-man weapon); 0 = none.</summary>
    public int PartnerId;
    /// <summary>Simulation time the current stance transition began (0 = none yet). The transition's progress is clamp((now - StanceChangedAt) / <see cref="StanceSeconds"/>, 0, 1), from <see cref="StanceFrom"/> toward <see cref="TargetStance"/>.</summary>
    public double StanceChangedAt;
    /// <summary>How long the transition lasts in seconds; 0 = not stated (use the client's own clip length).</summary>
    public float StanceSeconds;
    /// <summary>The stance the current transition leaves (equal to <see cref="TargetStance"/> when none is stated), so a client that first sees a man mid-transition knows what to blend from.</summary>
    public Stance StanceFrom;
}

/// <summary>
/// Where a person aims and looks, as intent (DD-020 3.2): a target entity, or a point only when there is no entity. While <see cref="AimTargetId"/> is not 0 the client takes
/// the aim point from that entity's own <see cref="EntityState"/> (its chest: 1 m above its position, in the simulation's up axis) every frame and no further sample is sent
/// as it moves; <see cref="AimPoint"/> is then the chest at the time of the sample, a fallback for a client that does not have the entity. With no target
/// entity, <see cref="AimPoint"/> is the point aimed at (all 0 = not aiming) and a new sample is sent when it moves. The look works the same way with
/// <see cref="LookTargetId"/> and <see cref="LookTarget"/> (an entity's position, not its chest); <see cref="HasLookTarget"/> is false when the head is straight on the body.
/// <see cref="HeadYaw"/> is the head's turn in radians off the body (positive = to his left, counter-clockwise from above) at the time of the sample; between samples a client derives
/// it from the targets. One instance per person, written when something other than a heartbeat changes.
/// </summary>
[DpsRate(DpsRateClass.Continuous)]
[DdsTopic(DpsProtocol.PersonAim)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct PersonAim
{
    [DdsKey] public int EntityId;
    public double SimTime;
    /// <summary>The entity aimed at (0 = none, or a point aim).</summary>
    public int AimTargetId;
    /// <summary>The point aimed at in the local frame; see the type's comment.</summary>
    public Vec3d AimPoint;
    /// <summary>The entity looked at (0 = none, or a point look).</summary>
    public int LookTargetId;
    public bool HasLookTarget;
    /// <summary>The point looked at in the local frame.</summary>
    public Vec3d LookTarget;
    public float HeadYaw;
}

[DdsStruct]
[DdsManaged]
public partial struct AppearanceSlot
{
    /// <summary>The slot: Skin, Top, Legs, Over, Head, Hair, Beard, Extra.</summary>
    [MaxLength(32)] public string Slot;
    /// <summary>A garment kind name from the producer's vocabulary.</summary>
    [MaxLength(32)] public string Kind;
    /// <summary>Colour as 0xRRGGBBAA.</summary>
    public uint ColourRgba;
}

[DdsStruct]
[DdsManaged]
public partial struct EquipmentItem
{
    /// <summary>The slot: Helmet, Vest, Backpack, Weapon0 ... as far as the simulation provides them.</summary>
    [MaxLength(32)] public string Slot;
    /// <summary>The equipment kind, a name from the producer's vocabulary.</summary>
    [MaxLength(32)] public string Kind;
    public int TypeId;
    /// <summary>Attachments, comma separated.</summary>
    [MaxLength(64)] public string Attachments;
}

/// <summary>A person's resolved look (outfit colors and garment kinds, camouflage, face, flags).</summary>
[DpsRate(DpsRateClass.Fixed)]
[DdsTopic(DpsProtocol.Appearance)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct Appearance
{
    [DdsKey] public int EntityId;
    public uint Seed;
    public byte Side;
    [DdsBound(16)] public List<AppearanceSlot> Slots;
    /// <summary>A camouflage pattern name from the producer's vocabulary.</summary>
    [MaxLength(32)] public string Camouflage;
    /// <summary>A hair style name from the producer's vocabulary.</summary>
    [MaxLength(32)] public string Hair;
    /// <summary>The beard kind (a name from the producer's vocabulary) and face traits as text.</summary>
    [MaxLength(64)] public string FaceTraits;
    public Sex Sex;
    public AgeClass AgeClass;
    public bool IsSoldier, IsChild, IsWoman, IsPolice;
    [DdsBound(16)] public List<EquipmentItem> Equipment;
}

[DdsStruct]
public partial struct PartLook
{
    public int PartId;
    public PartCondition State;
}

[DdsStruct]
public partial struct OpeningLook
{
    /// <summary>The simulation's opening id.</summary>
    public int OpeningId;
    /// <summary>0 closed .. 1 open as of the sample (for a client that only needs a value); not re-sent while the opening moves.</summary>
    public float Progress;
    /// <summary>A crewman's head is out of this opening.</summary>
    public bool CrewExposed;
    /// <summary>Where the opening is going: 1 = open, 0 = closed. It moves at a constant rate from the other end: progress = lerp(1 - target, target, clamp((now - ChangedAt) / Seconds, 0, 1)).</summary>
    public float TargetProgress;
    /// <summary>Simulation time the motion toward <see cref="TargetProgress"/> began (0 = from the start of the session: already there).</summary>
    public double ChangedAt;
    /// <summary>How long the whole motion takes in seconds; 0 = instant.</summary>
    public float Seconds;
}

/// <summary>A vehicle's look: part conditions, openings and lights (its look state).</summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.VehicleLook)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct VehicleLook
{
    [DdsKey] public int EntityId;
    [DdsBound(32)] public List<PartLook> Parts;
    public KillKind KillKind;
    public bool TurretThrown;
    /// <summary>The vehicle burns; the client puts the fire on its own parts.</summary>
    public bool Burning;
    public bool Smoking;
    [DdsBound(16)] public List<OpeningLook> Openings;
    /// <summary>Effective light mask (headlights, brake lights, ...).</summary>
    public uint LightMask;
    /// <summary>0 stopped .. 1 full speed (rotor, engine level) as of the sample (for a client that only needs a value); not re-sent while it spools.</summary>
    public float RotorSpeed;
    /// <summary>Smoke launchers fired, 0 until the simulation provides it.</summary>
    public byte SmokeLaunchers;
    /// <summary>A mine clearer's flail is turning.</summary>
    public bool FlailActive;
    /// <summary>
    /// The speed the rotor is spooling to, 0..1. It moves at a constant rate from <see cref="RotorFrom"/>, a full scale per <see cref="RotorSpoolSeconds"/>:
    /// speed = RotorFrom moved toward RotorTarget by (now - <see cref="RotorChangedAt"/>) / RotorSpoolSeconds, not past the target.
    /// </summary>
    public float RotorTarget;
    /// <summary>The speed the spool started from, 0..1 (the speed at <see cref="RotorChangedAt"/>).</summary>
    public float RotorFrom;
    /// <summary>Simulation time the spool began (0 = from the start of the session: already at the target).</summary>
    public double RotorChangedAt;
    /// <summary>Seconds for a full scale (0 to 1 or 1 to 0) at the spool's rate; 0 = instant.</summary>
    public float RotorSpoolSeconds;
}

/// <summary>
/// A round with a look (tracer, shell, rocket, missile, grenade, bomb; ball ammo has none) beside its <see cref="Entity"/> (Domain Munition) and
/// <see cref="EntityState"/>. Sent on creation and on a change of <see cref="Lying"/> / <see cref="Stopped"/>; the client dead-reckons the pose and draws the look
/// (<see cref="MunitionLook"/>), the tracer streak and the smoke trail back toward <see cref="Muzzle"/>.
/// </summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.Round)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct Round
{
    [DdsKey] public int EntityId;
    public long MunitionTypeId;
    /// <summary>The entity that fired it (0 = unknown).</summary>
    public int ShooterId;
    /// <summary>The launch point (the round's own position when the simulation does not know it).</summary>
    public Vec3d Muzzle;
    public bool Lying;
    /// <summary>Seconds left on a time fuze; negative = no time fuze.</summary>
    public float FuzeLeftS;
    /// <summary>Frozen where it hit a wall or the ground.</summary>
    public bool Stopped;
    /// <summary>Simulation time the round was fired. For a round the simulation did not fire itself (a replicated copy, no ballistic record) it is the time the gateway first saw it.</summary>
    public double LaunchedAt;
    /// <summary>How long the missile's motor burns in seconds (the TKB missile record); 0 = no motor data. The trail is full while <c>now - LaunchedAt &lt; BurnSeconds</c>.</summary>
    public float BurnSeconds;
}

/// <summary>How a munition type's round looks: the simulation's resolved look, built-in looks included. Sent once per munition type that is fired or seen.</summary>
[DpsRate(DpsRateClass.Fixed)]
[DdsTopic(DpsProtocol.MunitionLook)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct MunitionLook
{
    [DdsKey] public long MunitionTypeId;
    public MunitionShape Shape;
    /// <summary>Body length and diameter in metres.</summary>
    public float Length, Diameter;
    /// <summary>Colour as 0xRRGGBBAA.</summary>
    public uint ColourRgba;
    public bool SmokeTrail;
    public bool Tracer;
    /// <summary>The tracer's glow as 0xRRGGBBAA.</summary>
    public uint TracerColourRgba;
    /// <summary>The round sticks where it stops (an arrow in a wall).</summary>
    public bool Sticks;
    /// <summary>A sticking round that is a crossbow bolt.</summary>
    public bool Bolt;
}

/// <summary>A lit flame jet of a unit's flamethrower mount; the instance exists while the jet is lit.</summary>
[DpsRate(DpsRateClass.Continuous)]
[DdsTopic(DpsProtocol.FlameJet)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct FlameJet
{
    [DdsKey] public int EntityId;
    /// <summary>The unit's mount index, where the jet is anchored (its muzzle).</summary>
    [DdsKey] public int Mount;
    public Vec3d AimPoint;
    /// <summary>The aimed entity (0 = the aim point only).</summary>
    public int TargetId;
    public float RangeM;
    public float HalfAngleRad;
    /// <summary>0..1, how hard it burns.</summary>
    public float Intensity;
    /// <summary>The simulation time the burst began.</summary>
    public double BurstStartedAt;
    /// <summary>How long what it lights burns (0 = not stated).</summary>
    public float BurnSeconds;
}

/// <summary>One burning spot, in world coordinates.</summary>
[DdsStruct]
public partial struct FireSpot
{
    public Vec3d Position;
    public float Radius;
    /// <summary>0..1, how hard it burns.</summary>
    public float Intensity;
    /// <summary>The simulation time it was lit and how long it burns.</summary>
    public double Since;
    public float Seconds;
}

/// <summary>The burning spots of one holder (a vehicle, a building, a terrain cell); the instance exists while any spot burns.</summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.FireSpots)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct FireSpots
{
    [DdsKey] public int EntityId;
    [DdsBound(8)] public List<FireSpot> Spots;
}

/// <summary>One ground heat mark: where something stood or burned.</summary>
[DdsStruct]
public partial struct GroundMark
{
    public HeatMarkKind Kind;
    /// <summary>The centre in the local frame; z is the ground height when the simulation provides it, else 0.</summary>
    public Vec3d Centre;
    /// <summary>Radians.</summary>
    public float Heading;
    public float Length, Width;
    public HeatMarkEnd HotEnd;
    /// <summary>The simulation time the stand began and ended.</summary>
    public double StandSince, LeftAt;
}

/// <summary>The heat marks of one terrain cell; a renderer draws BurnedArea as a scorch, an infrared renderer all kinds.</summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.GroundMarks)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct GroundMarks
{
    [DdsKey] public int EntityId;
    [DdsBound(16)] public List<GroundMark> Marks;
}

/// <summary>An animal's state (behavior mode, fear, flock); for animals only, beside their <see cref="Entity"/> (Domain Animal). The species is the entity's type.</summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.Animal)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct Animal
{
    [DdsKey] public int EntityId;
    public AnimalMode Mode;
    /// <summary>0 calm, 1 uneasy, 2 frightened, 3 panic.</summary>
    public byte FearBand;
    /// <summary>A ridden horse bolting.</summary>
    public bool Bolting;
    /// <summary>Attacking right now.</summary>
    public bool Dangerous;
    /// <summary>Calling (a moo, a bark): the mouth may open.</summary>
    public bool Calling;
    /// <summary>A flock is one entity drawn as <see cref="FlockCount"/> birds from <see cref="FlockSeed"/>; count 0 = a single body.</summary>
    public int FlockCount, FlockInitialCount;
    public uint FlockSeed;
    public FlockMode FlockMode;
    /// <summary>The simulation's own flying state: a flock whose mode is Flying. A single body (a lone bird) has no such state in the simulation and reads false.</summary>
    public bool Airborne;
}

/// <summary>
/// A producer-specific extension, optional for a client. One type-database record as the running simulation has it (a scenario may replace the database, so a file shipped with a client would drift from the run). <see cref="Json"/> is the
/// simulation's own serialization of the template, restricted to its presentation descriptors; <see cref="ContentHash"/> is the FNV-1a 64 of <see cref="Json"/> (UTF-8).
/// Published once per type when first referenced by an entity, a fire, a round or a loaded weapon.
/// </summary>
[DpsRate(DpsRateClass.Fixed)]
[DdsTopic(DpsProtocol.TkbType)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct TkbType
{
    [DdsKey] public long TypeId;
    [MaxLength(64)] public string Name;
    [MaxLength(128)] public string CategoryPath;
    /// <summary>The DIS entity type as text.</summary>
    [MaxLength(64)] public string DisType;
    /// <summary>The shape of <see cref="Json"/>, a producer-specific name and version (e.g. "hrot/1"); a client that does not know it ignores <see cref="Json"/>.</summary>
    [MaxLength(16)] public string Schema;
    public ulong ContentHash;
    /// <summary>{"tkbType", "name", "categoryPath", "disType", "descriptors": [{"type", "partId", "data"}]}; a descriptor that failed to serialize carries "serializationError" instead of "data".</summary>
    public string Json;
}

/// <summary>A hole in a wall (a wall breach): metres along the panel from its A end, absolute heights.</summary>
[DdsStruct]
public partial struct Breach
{
    /// <summary>The panel's index within the building (the terrain export's own order of the building's wall panels).</summary>
    public int Panel;
    public float From, To, Bottom, Top;
    public BreachKind Kind;
}

/// <summary>A hit window: <see cref="Hits"/> 1..2 = cracked with up to two crack points (bytes 0..255 across the pane), 3 = broken.</summary>
[DdsStruct]
public partial struct WindowDamage
{
    /// <summary>The panel's index within the building.</summary>
    public int Panel;
    /// <summary>The opening's index on the panel.</summary>
    public int Opening;
    public byte Hits;
    public byte U0, V0, U1, V1;
}

/// <summary>
/// A producer-specific extension, optional for a client. The damage of one building as the simulation reports it (wall breaches, collapse and hit windows, all damage entities of the building merged), keyed by the building's terrain
/// object label "terrain/building label" (the identity the terrain export keeps). A renderer of the terrain applies it to the same building. The instance exists while the
/// building has damage.
/// </summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.BuildingDamage)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct BuildingDamage
{
    [DdsKey, MaxLength(256)] public string Building;
    public bool Collapsed;
    [DdsBound(64)] public List<Breach> Breaches;
    [DdsBound(256)] public List<WindowDamage> Windows;
}

#endregion

#region Events (gateway to client)

[DpsRate(DpsRateClass.Event)]
[DdsTopic(DpsProtocol.Fire)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1024)]
public partial struct Fire
{
    public double SimTime;
    public int ShooterId, TargetId;
    public long MunitionTypeId;
    public DisType MunitionDis;
    public byte WeaponSlot;
    public Vec3d Origin;
    public Vec3f Velocity;
    /// <summary>The shooter's mount category; a client draws tracer streams for machine guns and autocannons and knows a flamethrower.</summary>
    public WeaponCategory WeaponCategory;
}

[DpsRate(DpsRateClass.Event)]
[DdsTopic(DpsProtocol.Detonation)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1024)]
public partial struct Detonation
{
    public double SimTime;
    public int ShooterId, TargetId;
    public long MunitionTypeId;
    public DisType MunitionDis;
    public Vec3d Position;
    public Vec3f Velocity;
    public byte Result;
}

/// <summary>An effect derived by the simulation's effect rules (muzzle flash, explosion, ground mark, wreck flames / smoke).</summary>
[DpsRate(DpsRateClass.Event)]
[DdsTopic(DpsProtocol.Effect)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1024)]
public partial struct Effect
{
    /// <summary>The simulation time the effect began.</summary>
    public double SimTime;
    public EffectKind Kind;
    public EffectSize SizeClass;
    public EffectSurface Surface;
    /// <summary>The munition's DIS entity type.</summary>
    public DisType MunitionDis;
    /// <summary>The simulation's effect type id, a number from the producer's vocabulary (its type database).</summary>
    public int EffectType;
    public Vec3d Position;
    public Quat Rotation;
    /// <summary>The entity the effect is anchored to (the shooter for a muzzle flash), 0 = world position.</summary>
    public int AnchorId;
    public byte AnchorWeaponSlot;
    public float LifetimeSeconds;
    public float Size;
}

/// <summary>
/// A round marked a surface of the static world (DD-019 3.2): it entered or left a piece it passed through (a fence, a wall) or stopped on one (a wall, the
/// ground). A strike on a unit stays a <see cref="Detonation"/> with a target. The derived puff and mark still arrive as <see cref="Effect"/>.
/// </summary>
[DpsRate(DpsRateClass.Event)]
[DdsTopic(DpsProtocol.Impact)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1024)]
[DdsManaged]
public partial struct Impact
{
    public double SimTime;
    public Vec3d Position;
    /// <summary>The outward normal of the face it met; all 0 = the simulation did not say.</summary>
    public Vec3f Normal;
    /// <summary>The surface class by the simulation's effect rule (Ground within 0.25 m of the terrain, else Building); Unknown when the simulation draws no effect for this round.</summary>
    public EffectSurface Surface;
    public ImpactKind Kind;
    public long MunitionTypeId;
    /// <summary>The entity that fired the round (0 = unknown).</summary>
    public int ShooterId;
    /// <summary>The impact material (a class such as Concrete or Soil), a name from the producer's vocabulary; empty when the simulation does not say.</summary>
    [MaxLength(32)] public string Material;
}

#endregion

#region Suggested view (gateway to client)

/// <summary>
/// What the simulation suggests the camera shows now (DD-019 3.1): the film director's shot, the follow camera on a unit, or the operator's own camera. One instance.
/// The client owns its camera: it may follow this, follow only the binding (target, mode, distance) with its own controller, or ignore it. A client blends between
/// samples of the same <see cref="CutSeq"/> and jumps on a new one. <see cref="Source"/> None = the simulation suggests nothing now.
/// </summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.View)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct View
{
    public ViewSource Source;
    public ViewMode Mode;
    /// <summary>The entity the camera is bound to (0 = none).</summary>
    public int TargetId;
    /// <summary>The continuum distance in metres: 0 = at the eyes, negative = ahead of the eyes.</summary>
    public float Distance;
    /// <summary>The shoulder side: +1 right, -1 left, 0 = none.</summary>
    public float Side;
    /// <summary>The camera pose the simulation computed (simulation frame), for a client that reproduces the shot exactly.</summary>
    public Vec3d Position;
    public Quat Orientation;
    /// <summary>The vertical field of view in degrees; 0 = not stated.</summary>
    public float FovDeg;
    /// <summary>Grows at every hard cut (a new source, mode, target or shot).</summary>
    public uint CutSeq;
    /// <summary>The take or shot name, for display.</summary>
    [MaxLength(64)] public string Shot;
    public double SimTime;
}

#endregion

#region Player (gateway to client)

[DdsStruct]
[DdsManaged]
public partial struct Offer
{
    public OfferKind Kind;
    /// <summary>A unit's entity id, or the role's id.</summary>
    public int Id;
    [MaxLength(64)] public string Name;
    public byte Side;
    /// <summary>A unit or role is free to take.</summary>
    public bool Free;
    public int Count, Max;
}

[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.PlayerSlot)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct PlayerSlot
{
    public SlotState State;
    public int UnitId;
    public byte Side;
    /// <summary>Simulation time the slot returns to choosing after death; 0 = never.</summary>
    public double RespawnAtSim;
    [MaxLength(64)] public string Reason;
    [DdsBound(128)] public List<Offer> Offers;
}

[DdsStruct]
[DdsManaged]
public partial struct AmmoEntry
{
    public byte Slot;
    /// <summary>The weapon's kind label as a HUD shows it (RIFLE, MG, CANNON, HANDS ...), a name from the producer's vocabulary.</summary>
    [MaxLength(32)] public string Weapon;
    /// <summary>The loaded round's name, from the producer's vocabulary; empty = none.</summary>
    [MaxLength(32)] public string AmmoType;
    public int Rounds, Reserve;
    /// <summary>The loaded round's type id (0 = none).</summary>
    public long AmmoTypeId;
    /// <summary>The mount's maximum (0 = none stated).</summary>
    public int Max;
    /// <summary>Rounds in the magazine, -1 = no magazine model.</summary>
    public int Magazine;
    /// <summary>Rounds of the loaded type in the breech, -1 = not stated.</summary>
    public int LoadedRounds;
    /// <summary>Loading or switching.</summary>
    public bool Busy;
    public bool Overheated;
    /// <summary>Barrel heat 0..100.</summary>
    public int HeatPercent;
}

/// <summary>The data a HUD needs about the player's own unit.</summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.OwnUnit)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct OwnUnit
{
    public int UnitId;
    public float Health, MaxHealth;
    public byte ActiveSlot;
    [DdsBound(16)] public List<AmmoEntry> Ammo;
    /// <summary>Something can be used right now (door, casualty, vehicle, weapon).</summary>
    public bool CanUse;
    public int UseTargetId;
    /// <summary>Superseded by <see cref="Verb"/>: the producer's verb number, 0 = none; kept equal to it (<see cref="CanUse"/> is true when it is not 0).</summary>
    public byte UseVerb;
    [MaxLength(32)] public string UseLabel;
    /// <summary>How the selected weapon's trigger works.</summary>
    public FireMode FireMode;
    /// <summary>The use press the verdict answers; a client shows it when this equals its last press.</summary>
    public byte UseSeq;
    public UseOutcome UseOutcome;
    public UseReason UseReason;
    /// <summary>Half-width of the spread of the selected weapon's shots, radians (0 = none stated).</summary>
    public float SpreadRad;
    public RadarWarningLevel RadarWarning;
    /// <summary>Bearings in degrees, 0 = north, clockwise.</summary>
    public float RadarBearingDeg;
    public bool MissileLaunch;
    public float MissileBearingDeg;
    /// <summary>Seconds to impact when the sample was written (refreshed about once a second).</summary>
    public float MissileTimeToImpactS;
    public MissileSeeker MissileSeeker;
    /// <summary>What the use action does now (replaces <see cref="UseVerb"/>); None = nothing to use.</summary>
    public UseVerb Verb;
}

#endregion

#region HUD (gateway to client)

/// <summary>A man went out of the fight (the simulation's one kill authority); local and remote kills alike. Ids are entity ids, 0 = unknown.</summary>
[DpsRate(DpsRateClass.Event)]
[DdsTopic(DpsProtocol.ManDowned)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1024)]
public partial struct ManDowned
{
    public double SimTime;
    public int VictimId, SourceId, OriginalSourceId;
    /// <summary>What the hit made of him: Incapacitated or Dead.</summary>
    public Injury State;
    /// <summary>Superseded by <see cref="Part"/>: 0 = unknown, else the producer's body part id + 1; kept equal to it.</summary>
    public byte BodyPart;
    /// <summary>The victim's side in the contract's force numbering (1 friendly, 2 opposing, 3 neutral).</summary>
    public byte VictimSide;
    /// <summary>The body part the hit landed on; Unknown = not stated.</summary>
    public BodyPart Part;
}

/// <summary>A message to the players with its addressing; the client filters (All, its side, its unit, its station; Instructor only for an instructor view).</summary>
[DpsRate(DpsRateClass.Event)]
[DdsTopic(DpsProtocol.PlayerMessage)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1024)]
[DdsManaged]
public partial struct PlayerMessage
{
    public int MessageId;
    /// <summary>The simulation node that raised it (0 = this node).</summary>
    public int OriginNode;
    public MessageAddress To;
    /// <summary>The side (force numbering of the contract), station or unit id <see cref="To"/> names; 0 for All and Instructor.</summary>
    public int ToValue;
    [MaxLength(256)] public string Text;
    /// <summary>How long a subtitle shows.</summary>
    public float Seconds;
    public byte Priority;
    public double SentAt;
    /// <summary>The entity that sent it (0 = none).</summary>
    public int SourceId;
}

/// <summary>An objective, measure or trigger of the scenario rules (scenario objectives); the instance exists while the rule entity does.</summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.Status)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct Status
{
    /// <summary>The rule entity's id.</summary>
    [DdsKey] public int StatusId;
    public ObjectiveRole Role;
    public ObjectiveState State;
    public TriggerState TriggerState;
    public bool Closed;
    /// <summary>The side it belongs to (force numbering of the contract: 1 friendly, 2 opposing, 3 neutral).</summary>
    public byte Owner;
    public int ProgressCur, ProgressNeed;
    public float Value, Target;
    /// <summary>0..1; GO is Score at or above Pass.</summary>
    public float Score, Pass;
    public float Points;
    public double ChangedAt;
    [MaxLength(64)] public string Reason;
    [MaxLength(64)] public string Title;
    /// <summary>What the rule is (a kind name) and the group it belongs to, both names from the producer's vocabulary.</summary>
    [MaxLength(32)] public string Kind;
    [MaxLength(32)] public string Group;
    public byte Phase;
    public bool Required, Scored;
    public float Weight;
    /// <summary>Bit n set = side n sees it (force numbering of the contract); 0 = the display decides.</summary>
    public byte VisibleSides;
    public byte StepCur, StepCount;
    public float HoldFor;
    public float Deadline;
    public double ExpectedAt;
    /// <summary>A capture point; <see cref="Challenger"/> is the side whose clock runs against the owner (0 = none).</summary>
    public bool Capture;
    public byte Challenger;
    public Vec3d AreaCentre;
    public float AreaRadius;
    public bool EndsExercise;
    /// <summary>The entity whose men it watches and the parent rule (0 = none).</summary>
    public int SubjectId, ParentId;
}

/// <summary>One side's result.</summary>
[DdsStruct]
public partial struct SideResult
{
    public SideOutcome Outcome;
    public float Points;
    /// <summary>0..1.</summary>
    public float Score;
}

/// <summary>The exercise's phase and the result per side (exercise result); one instance.</summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.Exercise)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct Exercise
{
    public ExercisePhase Phase;
    /// <summary>The simulation time the exercise ended.</summary>
    public double EndedAt;
    [MaxLength(64)] public string Reason;
    public SideResult Friendly, Opposing, Neutral;
}

#endregion

#region Ambient traffic (gateway to client)

/// <summary>
/// An ambient mover (a car or a pedestrian of the simulation's traffic, DD-018 3.6b): who and what it is, written at spawn and disposed at despawn. Its motion is in
/// <see cref="MoverTracks"/>. A mover the simulation turns into an entity of its own is disposed here and appears as an <see cref="Entity"/>.
/// </summary>
[DpsRate(DpsRateClass.Fixed)]
[DdsTopic(DpsProtocol.Mover)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct Mover
{
    /// <summary>The simulation's network id of the mover.</summary>
    [DdsKey] public int MoverId;
    public MoverKind Kind;
    /// <summary>The simulation's TKB type (a car the traffic source gave no type is the generic traffic car's).</summary>
    public long TypeId;
    /// <summary>A car's paint as 0xRRGGBBAA (already resolved); 0 for a pedestrian.</summary>
    public uint ColourRgba;
    /// <summary>The seed of a pedestrian's clothes and face (the simulation's own: the network id); 0 for a car.</summary>
    public uint Seed;
    /// <summary>Metres; 0 = take the type's.</summary>
    public float Length, Width;
    public MoverSource Source;
}

/// <summary>
/// One knot of a mover's track. The time is <see cref="MoverTracks.SimTime"/> + <see cref="Dt"/> (seconds, at most 0). Position in the local frame (float metres: 1 cm up to
/// about 100 km from the origin). <see cref="HeadingCentiDeg"/> is the heading about up in hundredths of a degree (0 = +x, counter-clockwise, -18000 .. 18000),
/// <see cref="PitchCentiDeg"/> the pitch in hundredths of a degree (positive = nose up). <see cref="Speed"/> is m/s along the heading. The curve between knots is the
/// cubic Hermite with the knots' velocities as tangents (DD-018 3.6c).
/// </summary>
[DdsStruct]
public partial struct MoverKnot
{
    public int MoverId;
    public float Dt;
    public float X, Y, Z;
    public short HeadingCentiDeg, PitchCentiDeg;
    public float Speed;
}

/// <summary>The knots added to the movers' tracks since the last batch (about 10 Hz); a late joiner shows a mover from its second knot.</summary>
[DpsRate(DpsRateClass.Continuous)]
[DdsTopic(DpsProtocol.MoverTracks)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 16)]
[DdsManaged]
public partial struct MoverTracks
{
    /// <summary>The simulation time the batch was made.</summary>
    public double SimTime;
    [DdsBound(4096)] public List<MoverKnot> Knots;
}

/// <summary>
/// One signal head of a traffic light, placed in the local frame by the host so a client needs no road network. <see cref="HeadingDeg"/> is the direction of travel of the
/// controlled lane at its stop line (0 = +x, counter-clockwise); the head faces the traffic coming the opposite way.
/// </summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.TrafficLights)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct TrafficLights
{
    [DdsKey] public int SignalId;
    public Vec3d Position;
    public float HeadingDeg;
    public TrafficLightState State;
}

#endregion

#region Player (client to gateway)

/// <summary>Choose a playable unit by id, a role by name, or observe.</summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.JoinRequest)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1,
        LivelinessKind = DdsLivelinessKind.Automatic, LivelinessLeaseDurationMs = DpsProtocol.DefaultLeaseMs)]
[DdsManaged]
public partial struct JoinRequest
{
    /// <summary>The client's id of this request; grows with each request.</summary>
    public uint RequestSeq;
    public JoinKind Kind;
    public int UnitId;
    [MaxLength(64)] public string Role;
    public byte Side;
    public byte Kit;
    public int AreaId;
    /// <summary>
    /// The client's identity and the key of every player topic: a random nonzero id it picks at start and puts on every player sample it writes (JoinRequest,
    /// ControlFrame, Leave, GizmoView). One instance per client, so KEEP_LAST 1 holds the newest sample of each client and one client never displaces another's.
    /// One player owns a station at a time.
    /// </summary>
    [DdsKey] public ulong ClientId;
}

/// <summary>One per client frame; the operator's controls for this frame.</summary>
[DpsRate(DpsRateClass.Continuous)]
[DdsTopic(DpsProtocol.ControlFrame)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1,
        LivelinessKind = DdsLivelinessKind.Automatic, LivelinessLeaseDurationMs = DpsProtocol.DefaultLeaseMs)]
public partial struct ControlFrame
{
    /// <summary>Increasing; the newest wins.</summary>
    public uint Seq;
    public float Forward, Lateral, Steer, Yaw, Vertical;
    /// <summary>Superseded by <see cref="GaitMode"/>: 0 = run, 1 = walk. The gateway reads it only when <see cref="GaitMode"/> is unknown (0).</summary>
    public byte Gait;
    public byte Trigger;
    public byte Mount;
    public Vec3d AimPoint;
    /// <summary>Superseded by <see cref="StanceWanted"/>: the wanted stance as Stance + 1 (1 standing, 2 crouched, 3 prone), 0 = no request; the gateway reads it only when <see cref="StanceWanted"/> is None (0).</summary>
    public byte Stance;
    /// <summary>Press counters (wrap): a change is a press.</summary>
    public byte ShotSeq, CancelSeq, AmmoSeq;
    public int ShotTarget;
    public byte UseSeq;
    /// <summary>Superseded by <see cref="Verb"/>: the producer's verb number; the gateway reads it only when <see cref="Verb"/> is None (0).</summary>
    public byte UseVerb;
    public int UseTarget;
    /// <summary>0 none, 1 left, 2 right.</summary>
    public byte Lean;
    public byte Binoculars;
    public Vec3d LookFrom;
    public Vec3f LookDir;
    /// <summary>0 default, 1 sight, 2 lock.</summary>
    public byte FirePref;
    /// <summary>The client's identity and the key (see <see cref="JoinRequest.ClientId"/>); a frame of another client than the player's is dropped.</summary>
    [DdsKey] public ulong ClientId;
    /// <summary>How fast to move: Walk or Run (replaces <see cref="Gait"/>); Unknown (0) = fall back to the old byte.</summary>
    public Gait GaitMode;
    /// <summary>The stance asked for (replaces <see cref="Stance"/>); None (0) = fall back to the old byte.</summary>
    public StanceRequest StanceWanted;
    /// <summary>The use action asked for on <see cref="UseTarget"/> (replaces <see cref="UseVerb"/>); None (0) = fall back to the old byte.</summary>
    public UseVerb Verb;
}

[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.Leave)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1,
        LivelinessKind = DdsLivelinessKind.Automatic, LivelinessLeaseDurationMs = DpsProtocol.DefaultLeaseMs)]
public partial struct Leave
{
    public uint Seq;
    /// <summary>The client's identity and the key (see <see cref="JoinRequest.ClientId"/>); a Leave of another client than the player's is dropped.</summary>
    [DdsKey] public ulong ClientId;
}

/// <summary>What the client wants of the gateway's gizmo projectors; none sent = no projectors run.</summary>
[DpsRate(DpsRateClass.Discrete)]
[DdsTopic(DpsProtocol.GizmoView)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1,
        LivelinessKind = DdsLivelinessKind.Automatic, LivelinessLeaseDurationMs = DpsProtocol.DefaultLeaseMs)]
[DdsManaged]
public partial struct GizmoView
{
    [DdsBound(64)] public List<NameItem> Layers;
    [DdsBound(256)] public List<int> SelectedIds;
    [DdsBound(256)] public List<int> PinnedIds;
    [DdsBound(32)] public List<NameItem> PinnedFamilies;
    public float RateHz;
    /// <summary>The client's identity and the key (see <see cref="JoinRequest.ClientId"/>): the view of the player is followed; with no player, the first client that writes one.</summary>
    [DdsKey] public ulong ClientId;
}

#endregion

#region Gizmos (gateway to client)

[DdsStruct]
[DdsManaged]
public partial struct GizmoPrimitive
{
    public GizmoShape Shape;
    [DdsBound(64)] public List<Vec3d> Points;
    /// <summary>Colour as 0xRRGGBBAA.</summary>
    public uint ColourRgba;
    public float Width;
    [MaxLength(64)] public string Text;
}

/// <summary>The complete primitives of one layer of one producer for the latest gizmo frame; each sample replaces the previous.</summary>
[DpsRate(DpsRateClass.Continuous)]
[DdsTopic(DpsProtocol.GizmoFrame)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct GizmoFrame
{
    [DdsKey, MaxLength(64)] public string Producer;
    [DdsKey, MaxLength(32)] public string Layer;
    public double SimTime;
    [DdsBound(4096)] public List<GizmoPrimitive> Primitives;
}

#endregion
