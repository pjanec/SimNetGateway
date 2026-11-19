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
    public double SimTime;
    public double TimeScale;
    /// <summary>Changes on a world boundary (scenario reload) so a client drops its caches.</summary>
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
    /// <summary>The simulation's type id (an integer).</summary>
    public int TypeId;
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
    /// <summary>0 until the simulation provides it.</summary>
    public float RotorSpeed;
    /// <summary>Smoke launchers fired, 0 until the simulation provides it.</summary>
    public byte SmokeLaunchers;
}

#endregion

#region Events (gateway to client)

[DdsTopic(DpsProtocol.Fire)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 16)]
public partial struct Fire
{
    public double SimTime;
    public int ShooterId, TargetId;
    public int MunitionTypeId;
    public DisType MunitionDis;
    public byte WeaponSlot;
    public Vec3d Origin;
    public Vec3f Velocity;
}

[DdsTopic(DpsProtocol.Detonation)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 16)]
public partial struct Detonation
{
    public double SimTime;
    public int ShooterId, TargetId;
    public int MunitionTypeId;
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
    /// <summary>The client's identity: a random nonzero id it picks at start and puts on every player sample it writes (JoinRequest, ControlFrame, Leave). One player owns a station at a time.</summary>
    public ulong ClientId;
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
    /// <summary>The client's identity (see <see cref="JoinRequest.ClientId"/>); a frame of another client than the player's is dropped.</summary>
    public ulong ClientId;
}

[DdsTopic(DpsProtocol.Leave)]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1,
        LivelinessKind = DdsLivelinessKind.Automatic, LivelinessLeaseDurationMs = DpsProtocol.DefaultLeaseMs)]
public partial struct Leave
{
    public uint Seq;
    /// <summary>The client's identity (see <see cref="JoinRequest.ClientId"/>); a Leave of another client than the player's is dropped.</summary>
    public ulong ClientId;
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
    /// <summary>The client's identity (see <see cref="JoinRequest.ClientId"/>): the view of the player is followed; with no player, the first client that writes one.</summary>
    public ulong ClientId;
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
