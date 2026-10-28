#nullable enable
namespace SimNetGateway.Protocol.Dis7;

/// <summary>Damage state of an entity appearance (SISO-REF-010, "Damage").</summary>
public enum DamageState : uint
{
    NoDamage = 0,
    SlightDamage = 1,
    ModerateDamage = 2,
    Destroyed = 3,
}

/// <summary>Smoke bits of a platform appearance (SISO-REF-010, "Smoke", bits 5-6).</summary>
public enum SmokeState : uint
{
    NotSmoking = 0,
    SmokePlume = 1,
    EngineSmoke = 2,
    PlumeAndEngineSmoke = 3,
}

/// <summary>
/// Get and set helpers for the general entity appearance word of the Entity State PDU (SISO-REF-010, DIS 7), shared by
/// platforms and life forms: damage bits 3-4, frozen status bit 21, state (deactivated) bit 23; smoke bits 5-6 apply to
/// platforms. Pure functions on the 32-bit word: every setter changes only its own bits.
/// </summary>
public static class EntityAppearance
{
    public const int DamageShift = 3;
    public const uint DamageMask = 0x3u << DamageShift;
    public const int SmokeShift = 5;
    public const uint SmokeMask = 0x3u << SmokeShift;
    public const int FrozenBit = 21;
    public const uint FrozenMask = 1u << FrozenBit;
    public const int DeactivatedBit = 23;
    public const uint DeactivatedMask = 1u << DeactivatedBit;

    public static DamageState GetDamage(uint appearance) => (DamageState)((appearance & DamageMask) >> DamageShift);

    public static uint SetDamage(uint appearance, DamageState damage) =>
        (appearance & ~DamageMask) | (((uint)damage & 0x3u) << DamageShift);

    public static SmokeState GetSmoke(uint appearance) => (SmokeState)((appearance & SmokeMask) >> SmokeShift);

    public static uint SetSmoke(uint appearance, SmokeState smoke) =>
        (appearance & ~SmokeMask) | (((uint)smoke & 0x3u) << SmokeShift);

    /// <summary>True when the entity is frozen (bit 21).</summary>
    public static bool GetFrozen(uint appearance) => (appearance & FrozenMask) != 0;

    public static uint SetFrozen(uint appearance, bool frozen) =>
        frozen ? appearance | FrozenMask : appearance & ~FrozenMask;

    /// <summary>True when the entity is deactivated (the State bit 23: 0 = active, 1 = deactivated); removal marker.</summary>
    public static bool GetDeactivated(uint appearance) => (appearance & DeactivatedMask) != 0;

    public static uint SetDeactivated(uint appearance, bool deactivated) =>
        deactivated ? appearance | DeactivatedMask : appearance & ~DeactivatedMask;
}
