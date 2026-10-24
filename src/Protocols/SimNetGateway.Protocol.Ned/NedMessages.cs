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

public readonly record struct NedWeaponStatus(ulong Id, int SelectedMunitionTkb, int Ammo) : INedMessage;

public readonly record struct NedEntityDisposed(ulong Id) : INedMessage;

/// <summary><c>MunitionTkb</c> 0 = the shooter's selected weapon; <c>Target</c> 0 = none.</summary>
public readonly record struct NedWeaponFire(
    Guid FireId,
    ulong Shooter,
    ulong Target,
    int MunitionTkb,
    double LatitudeDeg,
    double LongitudeDeg,
    double Altitude,
    int Quantity) : INedMessage;

/// <summary><c>TargetDamage</c> -1 = not reported.</summary>
public readonly record struct NedDetonation(
    Guid DetonationId,
    ulong Target,
    int MunitionTkb,
    double LatitudeDeg,
    double LongitudeDeg,
    double Altitude,
    NedDetonationResult Result,
    int TargetDamage) : INedMessage;

public readonly record struct NedSimControl(NedControlKind Kind, string? Scenario) : INedMessage;
