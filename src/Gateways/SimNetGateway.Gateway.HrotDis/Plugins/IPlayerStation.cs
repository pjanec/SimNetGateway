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

    /// <summary>
    /// Is a player message to (<paramref name="kind"/>, <paramref name="value"/>) for this seat (T-054, DD-023 D-15)? <paramref name="kind"/> is the NAME of the address (All, Side, Station, Unit, Instructor), a side is
    /// in DIS force numbering. The host answers with the product's own addressing rule for this seat's unit, side and station; a host with no rule delivers everything (the default).
    /// </summary>
    bool Addressed(string kind, int value) => true;

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

/// <summary>
/// A join request: a placed unit by id, a role by name (or by the id of its offer), or observe. Appended with the deploy flow (T-053): <see cref="Side"/> is the DIS force the
/// player wants (0 = not stated), <see cref="Kit"/> the role's kit by index (0 = the role's own), <see cref="AreaId"/> the spawn area chosen (an area offer's id; 0 = any of the side).
/// </summary>
public readonly record struct PlayerJoin(PlayerJoinKind Kind, int UnitId, string? Role, byte Side = 0, byte Kit = 0, int AreaId = 0);

/// <summary>
/// The slot as of <see cref="Version"/>; <see cref="RespawnAt"/> is the simulation time it returns to choosing after death (0 = never); <see cref="Reason"/> is the host's
/// text for the last refused join (HROT's own refusal text; empty or null = none), shown to the client as <c>PlayerSlot.Reason</c>; <see cref="Side"/> is the DIS force of the side the seat plays
/// while it holds a unit (0 = not stated: the slot derives it from the offer of the unit).
/// </summary>
public readonly record struct PlayerSlotSnapshot(PlayerSlotState State, int UnitId, double RespawnAt, int Version, string? Reason = null, byte Side = 0);

/// <summary>What a <see cref="PlayerOffer"/> offers (T-053): a placed unit, a role (a man is spawned for the player) or a spawn area.</summary>
public enum PlayerOfferKind : byte
{
    Unit = 0,
    Role,
    Area,
}

/// <summary>
/// One playable unit (<see cref="Role"/> is its slot name) and the side (DIS force numbering) it belongs to. Appended with the deploy flow (T-053): <see cref="Kind"/>; for a role
/// <see cref="Count"/> men alive of at most <see cref="Max"/> (0 = unlimited) and its <see cref="Kits"/> (names by kit index, "" = unset; null = none); <see cref="Free"/> = no
/// station holds it; for an area <see cref="Outline"/> is its outline in world metres (null = none). Equality compares the lists by value.
/// </summary>
public readonly record struct PlayerOffer(int UnitId, string? Role, int Force, PlayerOfferKind Kind = PlayerOfferKind.Unit, int Count = 0, int Max = 0, bool Free = true,
    IReadOnlyList<string>? Kits = null, IReadOnlyList<HrotVec3>? Outline = null) : IEquatable<PlayerOffer>
{
    public bool Equals(PlayerOffer other) =>
        UnitId == other.UnitId && Role == other.Role && Force == other.Force && Kind == other.Kind && Count == other.Count && Max == other.Max && Free == other.Free
        && SameList(Kits, other.Kits) && SameList(Outline, other.Outline);

    public override int GetHashCode() => HashCode.Combine(UnitId, Role, Force, Kind, Count, Max, Free);

    private static bool SameList<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        int na = a?.Count ?? 0, nb = b?.Count ?? 0;
        if (na != nb)
        {
            return false;
        }

        for (int i = 0; i < na; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(a![i], b![i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One weapon mount of the player's unit as the HUD shows it. <see cref="Weapon"/> is the mount's kind as HROT's HUD names it (RIFLE, MG, CANNON, HANDS ...) and
/// <see cref="AmmoName"/> the loaded round's name (empty = none); <see cref="Magazine"/> -1 = no magazine model, <see cref="LoadedRounds"/> -1 = not stated,
/// <see cref="Max"/> 0 = none stated.
/// </summary>
public readonly record struct PlayerWeapon(
    byte Mount, string Weapon, string AmmoName, long AmmoTypeId, int Rounds, int Max, int Magazine, int LoadedRounds, bool Busy, bool Overheated, int HeatPercent);

/// <summary>
/// The data a HUD needs about the player's own unit: health, ammunition per weapon mount, what the use key does now and the verdict of the last press, the fire mode and
/// spread of the selected weapon, and the aircraft warnings. HROT's enumerations are the NAMES of the enumeration ("" = none): <see cref="FireMode"/> (Sight, Lock),
/// <see cref="UseOutcome"/>, <see cref="UseReason"/>, <see cref="RadarWarning"/> (the warning receiver level) and <see cref="MissileSeeker"/>. Bearings are degrees, 0 = north,
/// clockwise; <see cref="MissileTimeToImpactS"/> is the time left at the moment of reading.
/// <see cref="UseVerbName"/> is the NAME of HROT's use verb (empty = none), the same verb as the number <see cref="UseVerb"/> (DD-020 3.3).
/// </summary>
public readonly record struct PlayerUnitStatus(
    double Health, double HealthMax, IReadOnlyList<PlayerWeapon> Ammo, byte UseVerb, int UseTarget, byte ActiveMount = 0, string FireMode = "", string UseLabel = "",
    byte UseSeq = 0, string UseOutcome = "", string UseReason = "", float SpreadRad = 0f, string RadarWarning = "", float RadarBearingDeg = 0f, bool MissileLaunch = false,
    float MissileBearingDeg = 0f, float MissileTimeToImpactS = 0f, string MissileSeeker = "", string UseVerbName = "")
{
    public static PlayerUnitStatus None { get; } = new(0.0, 0.0, Array.Empty<PlayerWeapon>(), 0, 0);
}

/// <summary>
/// One control frame; the fields of the product's operator control frame. <see cref="Seq"/> grows (the newest wins); the press counters
/// (<see cref="ShotSeq"/>, <see cref="CancelSeq"/>, <see cref="AmmoSeq"/>, <see cref="UseSeq"/>) are passed through unchanged. <see cref="Lean"/> is 0 none,
/// 1 left, 2 right; <see cref="Stance"/> is the wanted stance + 1 (0 = no request).
/// <see cref="UseVerbName"/> is the NAME of HROT's use verb the client asked for (empty = use the number <see cref="UseVerb"/>); the host maps a known name to the verb and ignores an unknown one (DD-020 3.3).
/// </summary>
public readonly record struct PlayerControl(
    uint Seq, float Forward, float Lateral, float Steer, float Yaw, float Vertical, byte Gait, bool Trigger, byte Mount, HrotVec3 AimPoint, byte Stance,
    byte ShotSeq, byte CancelSeq, byte AmmoSeq, int ShotTarget, byte UseSeq, byte UseVerb, int UseTarget, float Lean, bool Binoculars, HrotVec3 LookFrom,
    HrotVec3 LookDir, byte FirePref, string UseVerbName = "");
