using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// The seam through which a plugin plays a unit of the simulation as this node's player station (DD-017 3.6): it joins (a placed unit, a role, or only
/// observes), sends one control frame per client frame and leaves; the host drives the product's own player mode behind it. Main thread only (the plugin's
/// pump). A plugin that does not play ignores it.
/// </summary>
public interface IPlayerStation
{
    /// <summary>Where the slot is now; <see cref="PlayerSlotSnapshot.Version"/> grows with every change.</summary>
    PlayerSlotSnapshot Slot { get; }

    /// <summary>What can be chosen now.</summary>
    IReadOnlyList<PlayerOffer> Offers { get; }

    /// <summary>The player's own unit (all zero while no unit is held).</summary>
    PlayerUnitStatus Unit { get; }

    /// <summary>Asks to join; false = not accepted now (the slot says where it is).</summary>
    bool Join(PlayerJoin request);

    /// <summary>One control frame for the held unit; ignored unless the slot is <see cref="PlayerSlotState.Playing"/>.</summary>
    void Control(in PlayerControl frame);

    /// <summary>Lets the held unit go (its own behavior resumes) and returns to choosing; does nothing when no unit is held.</summary>
    void Leave();
}

public enum PlayerSlotState : byte
{
    Waiting = 0,
    Observing,
    Choosing,
    Requesting,
    Spawning,
    TakingControl,
    Playing,
    Dead,
}

public enum PlayerJoinKind : byte
{
    Observe = 0,
    Unit,
    Role,
}

/// <summary>A join request: a placed unit by id, a role by name, or observe.</summary>
public readonly record struct PlayerJoin(PlayerJoinKind Kind, int UnitId, string? Role);

/// <summary>The slot as of <see cref="Version"/>; <see cref="RespawnAt"/> is the simulation time it returns to choosing after death (0 = never).</summary>
public readonly record struct PlayerSlotSnapshot(PlayerSlotState State, int UnitId, double RespawnAt, int Version);

/// <summary>One playable unit (<see cref="Role"/> is its slot name) and the side (DIS force numbering) it belongs to.</summary>
public readonly record struct PlayerOffer(int UnitId, string? Role, int Force);

/// <summary>The data a HUD needs about the player's own unit: health, ammunition per weapon mount, and what the use key does now.</summary>
public readonly record struct PlayerUnitStatus(double Health, double HealthMax, IReadOnlyList<(byte Mount, long Ammo, int Rounds)> Ammo, byte UseVerb, int UseTarget)
{
    public static PlayerUnitStatus None { get; } = new(0.0, 0.0, Array.Empty<(byte, long, int)>(), 0, 0);
}

/// <summary>
/// One control frame; the fields of the product's operator control frame. <see cref="Seq"/> grows (the newest wins); the press counters
/// (<see cref="ShotSeq"/>, <see cref="CancelSeq"/>, <see cref="AmmoSeq"/>, <see cref="UseSeq"/>) are passed through unchanged. <see cref="Lean"/> is 0 none,
/// 1 left, 2 right; <see cref="Stance"/> is the wanted stance + 1 (0 = no request).
/// </summary>
public readonly record struct PlayerControl(
    uint Seq, float Forward, float Lateral, float Steer, float Yaw, float Vertical, byte Gait, bool Trigger, byte Mount, HrotVec3 AimPoint, byte Stance,
    byte ShotSeq, byte CancelSeq, byte AmmoSeq, int ShotTarget, byte UseSeq, byte UseVerb, int UseTarget, float Lean, bool Binoculars, HrotVec3 LookFrom,
    HrotVec3 LookDir, byte FirePref);
