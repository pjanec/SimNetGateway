using CycloneDDS.Schema;

namespace SimNetGateway.Protocol.DdsPlayer;

// The DPS topics (DD-017 sections 3 and 4). Frames: the simulation's local Cartesian frame in metres (x east, y north, z up),
// quaternion (x, y, z, w), simulation time in seconds. EntityId is the gateway's network id of the entity. Fields are only ever
// appended. Strings and sequences are bounded: names 64, kinds 32, colours as RGBA bytes.
//
// QoS: state topics Reliable + TransientLocal + KeepLast 1 (a late joiner gets every live instance); event topics Reliable +
// Volatile + KeepLast 16; player topics (client to gateway) Reliable + Volatile + KeepLast 1 with automatic liveliness.

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
}

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
[DdsTopic(DpsProtocol.Articulation)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct Articulation
{
    [DdsKey] public int EntityId;
    [DdsKey] public int PartId;
    [MaxLength(32)] public string Kind;
    public double SimTime;
    public float Azimuth, Elevation, Extension;
    public float Rate;
}

/// <summary>What a person's body does; the client chooses clips, blends and IK (DD-017 3.5).</summary>
[DdsTopic(DpsProtocol.PersonState)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct PersonState
{
    [DdsKey] public int EntityId;
    public double SimTime;
    public Stance Stance, TargetStance;
    /// <summary>Progress of the stance transition, 0..1.</summary>
    public float StanceProgress;
    public WeaponPosture Posture;
    public Vec3d AimPoint;
    public byte ActiveSlot;
    public HeldItem Held;
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
}

[DdsStruct]
[DdsManaged]
public partial struct AppearanceSlot
{
    /// <summary>Skin, Top, Legs, Over, Head, Hair, Beard, Extra.</summary>
    [MaxLength(32)] public string Slot;
    /// <summary>The simulation's garment kind name.</summary>
    [MaxLength(32)] public string Kind;
    /// <summary>Colour as 0xRRGGBBAA.</summary>
    public uint ColourRgba;
}

[DdsStruct]
[DdsManaged]
public partial struct EquipmentItem
{
    /// <summary>Helmet, Vest, Backpack, Weapon0 ... as far as the simulation provides them.</summary>
    [MaxLength(32)] public string Slot;
    [MaxLength(32)] public string Kind;
    public int TypeId;
    /// <summary>Attachments, comma separated.</summary>
    [MaxLength(64)] public string Attachments;
}

/// <summary>A person's resolved look (HROT's person look: outfit colours and garment kinds, camouflage, face, flags).</summary>
[DdsTopic(DpsProtocol.Appearance)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct Appearance
{
    [DdsKey] public int EntityId;
    public uint Seed;
    public byte Side;
    [DdsBound(16)] public List<AppearanceSlot> Slots;
    [MaxLength(32)] public string Camouflage;
    [MaxLength(32)] public string Hair;
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
    /// <summary>0 closed .. 1 open.</summary>
    public float Progress;
    /// <summary>A crewman's head is out of this opening.</summary>
    public bool CrewExposed;
}

/// <summary>A vehicle's look: part conditions, openings and lights (HROT's vehicle look).</summary>
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
    /// <summary>0 stopped .. 1 full speed (rotor, engine level); sent on a change of 0.05 and when it reaches 0 or 1.</summary>
    public float RotorSpeed;
    /// <summary>Smoke launchers fired, 0 until the simulation provides it.</summary>
    public byte SmokeLaunchers;
}

/// <summary>
/// A round with a look (tracer, shell, rocket, missile, grenade, bomb; ball ammo has none) beside its <see cref="Entity"/> (Domain Munition) and
/// <see cref="EntityState"/>. Sent on creation and on a change of <see cref="Lying"/> / <see cref="Stopped"/>; the client dead-reckons the pose and draws the look
/// (<see cref="MunitionLook"/>), the tracer streak and the smoke trail back toward <see cref="Muzzle"/>.
/// </summary>
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
}

/// <summary>How a munition type's round looks: the simulation's resolved look, built-in looks included. Sent once per munition type that is fired or seen.</summary>
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
[DdsTopic(DpsProtocol.GroundMarks)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
[DdsManaged]
public partial struct GroundMarks
{
    [DdsKey] public int EntityId;
    [DdsBound(16)] public List<GroundMark> Marks;
}

/// <summary>An animal's state (HROT's animal state and flock state); for animals only, beside their <see cref="Entity"/> (Domain Animal). The species is the entity's type.</summary>
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
}

/// <summary>
/// One TKB type as the running simulation has it (a scenario may replace the TKB, so a file shipped with a client would drift from the run). <see cref="Json"/> is the
/// simulation's own serialization of the template, restricted to its presentation descriptors; <see cref="ContentHash"/> is the FNV-1a 64 of <see cref="Json"/> (UTF-8).
/// Published once per type when first referenced by an entity, a fire, a round or a loaded weapon.
/// </summary>
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
    /// <summary>The shape of <see cref="Json"/>: "hrot/1".</summary>
    [MaxLength(16)] public string Schema;
    public ulong ContentHash;
    /// <summary>{"tkbType", "name", "categoryPath", "disType", "descriptors": [{"type", "partId", "data"}]}; a descriptor that failed to serialize carries "serializationError" instead of "data".</summary>
    public string Json;
}

#endregion

#region Events (gateway to client)

[DdsTopic(DpsProtocol.Fire)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 16)]
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

[DdsTopic(DpsProtocol.Detonation)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 16)]
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

/// <summary>An effect derived by HROT's effect rules (muzzle flash, explosion, ground mark, wreck flames / smoke).</summary>
[DdsTopic(DpsProtocol.Effect)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 16)]
public partial struct Effect
{
    public double SimTime;
    public EffectKind Kind;
    public EffectSize SizeClass;
    public EffectSurface Surface;
    /// <summary>The munition's DIS entity type.</summary>
    public DisType MunitionDis;
    /// <summary>The simulation's effect type (TKB).</summary>
    public int EffectType;
    public Vec3d Position;
    public Quat Rotation;
    /// <summary>The entity the effect is anchored to (the shooter for a muzzle flash), 0 = world position.</summary>
    public int AnchorId;
    public byte AnchorWeaponSlot;
    public float LifetimeSeconds;
    public float Size;
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
    [MaxLength(32)] public string Weapon;
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
}

#endregion

#region HUD (gateway to client)

/// <summary>A man went out of the fight (HROT's one kill authority); local and remote kills alike. Ids are entity ids, 0 = unknown.</summary>
[DdsTopic(DpsProtocol.ManDowned)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 16)]
public partial struct ManDowned
{
    public double SimTime;
    public int VictimId, SourceId, OriginalSourceId;
    /// <summary>What the hit made of him: Incapacitated or Dead.</summary>
    public Injury State;
    /// <summary>0 = unknown, else the body part id + 1.</summary>
    public byte BodyPart;
    /// <summary>The victim's side in the contract's force numbering (1 friendly, 2 opposing, 3 neutral).</summary>
    public byte VictimSide;
}

/// <summary>A message to the players with its addressing; the client filters (All, its side, its unit, its station; Instructor only for an instructor view).</summary>
[DdsTopic(DpsProtocol.PlayerMessage)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 16)]
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

/// <summary>An objective, measure or trigger of the scenario rules (HROT's <c>ObjectiveStatus</c>); the instance exists while the rule entity does.</summary>
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

/// <summary>The exercise's phase and the result per side (HROT's <c>ExerciseResult</c>); one instance.</summary>
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

#region Player (client to gateway)

/// <summary>Choose a playable unit by id, a role by name, or observe.</summary>
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

/// <summary>One per client frame; the fields of HROT's operator control frame.</summary>
[DdsTopic(DpsProtocol.ControlFrame)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1,
        LivelinessKind = DdsLivelinessKind.Automatic, LivelinessLeaseDurationMs = DpsProtocol.DefaultLeaseMs)]
public partial struct ControlFrame
{
    /// <summary>Increasing; the newest wins.</summary>
    public uint Seq;
    public float Forward, Lateral, Steer, Yaw, Vertical;
    public byte Gait;
    public byte Trigger;
    public byte Mount;
    public Vec3d AimPoint;
    /// <summary>The wanted stance as Stance + 1 (1 standing, 2 crouched, 3 prone); 0 = no request.</summary>
    public byte Stance;
    /// <summary>Press counters (wrap): a change is a press.</summary>
    public byte ShotSeq, CancelSeq, AmmoSeq;
    public int ShotTarget;
    public byte UseSeq, UseVerb;
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
}

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
