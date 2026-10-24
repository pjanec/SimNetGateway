namespace SimNetGateway.Protocol.Ned;

/// <summary>Marker of every message on the NED (DDS-based simulation protocol) side.</summary>
public interface INedMessage
{
}

/// <summary>Allegiance of an entity as NED reports it.</summary>
public enum NedForce : byte
{
    Unknown = 0,
    Friendly = 1,
    Opposing = 2,
    Neutral = 3,
}

/// <summary>Outcome of a detonation as NED reports it; the numbering equals the RPR side.</summary>
public enum NedDetonationResult : byte
{
    Other = 0,
    EntityImpact = 1,
    GroundImpact = 2,
    Detonation = 3,
}

/// <summary>Simulation control commands from NED.</summary>
public enum NedControlKind : byte
{
    Load,
    LoadDebrief,
    Start,
    Pause,
    Resume,
    Unload,
}

/// <summary>Static description of an entity: name, TKB type id and force.</summary>
public readonly record struct NedEntityMaster(ulong Id, string Name, int TkbType, NedForce Force) : INedMessage;

/// <summary>Geodetic state of an entity (degrees, metres, m/s); <c>Speed</c> is along heading and pitch.</summary>
public readonly record struct NedGeoSpatial(
    ulong Id,
    double LatitudeDeg,
    double LongitudeDeg,
    double Altitude,
    double HeadingDeg,
    double PitchDeg,
    double RollDeg,
    double Speed) : INedMessage;

/// <summary>Damage level: 0 none, 1 light, 2 heavy, 3 destroyed; anything else is out of range.</summary>
public readonly record struct NedEntityDamage(ulong Id, int Level) : INedMessage;

/// <summary><c>Member</c> is the member index when <c>Id</c> is a formation and the status is that of one of its members (-1 = the entity itself).</summary>
public readonly record struct NedWeaponStatus(ulong Id, int SelectedMunitionTkb, int Ammo, int Member = -1) : INedMessage;

public readonly record struct NedEntityDisposed(ulong Id) : INedMessage;

/// <summary>
/// <c>MunitionTkb</c> 0 = the shooter's selected weapon; <c>Target</c> 0 = none; <c>TargetMember</c> is the member index
/// when the target is a member of the formation <c>Target</c> (-1 = the target is the entity itself); <c>ShooterMember</c>
/// likewise for the shooter.
/// </summary>
public readonly record struct NedWeaponFire(
    Guid FireId,
    ulong Shooter,
    ulong Target,
    int MunitionTkb,
    double LatitudeDeg,
    double LongitudeDeg,
    double Altitude,
    int Quantity,
    int TargetMember = -1,
    int ShooterMember = -1) : INedMessage;

/// <summary><c>TargetDamage</c> -1 = not reported; <c>TargetMember</c> as in <see cref="NedWeaponFire"/>.</summary>
public readonly record struct NedDetonation(
    Guid DetonationId,
    ulong Target,
    int MunitionTkb,
    double LatitudeDeg,
    double LongitudeDeg,
    double Altitude,
    NedDetonationResult Result,
    int TargetDamage,
    int TargetMember = -1) : INedMessage;

public readonly record struct NedSimControl(NedControlKind Kind, string? Scenario) : INedMessage;

/// <summary>One member of a <see cref="NedFormation"/>: geodetic state (as in <see cref="NedGeoSpatial"/>), type and damage.</summary>
public readonly record struct NedFormationMember(
    int Index,
    double LatitudeDeg,
    double LongitudeDeg,
    double Altitude,
    double HeadingDeg,
    double PitchDeg,
    double RollDeg,
    double Speed,
    int TkbType,
    int Damage);

/// <summary>
/// A formation (unit) with all its members in one descriptor, in both directions. The members are identified by
/// (formation id, index); an empty member list means "no members". The unit itself has no position.
/// </summary>
public sealed record NedFormation(ulong Id, string Name, int TkbType, NedForce Force, NedFormationMember[] Members) : INedMessage;

/// <summary>The second part of a split fire: the munition and the number of rounds of the fire <c>FireId</c>.</summary>
public readonly record struct NedFireAmmunition(Guid FireId, int MunitionTkb, int Quantity) : INedMessage;

/// <summary>Outbound: the damage level of an entity (or formation member; <c>Member</c> -1 = none) increased.</summary>
public readonly record struct NedDamageNotification(ulong Id, int Member, int OldLevel, int NewLevel) : INedMessage;

/// <summary>Identification-friend-or-foe mode; no module of the gateway handles it (it exists as a message to fail on).</summary>
public readonly record struct NedIffStatus(ulong Id, int Mode) : INedMessage;
