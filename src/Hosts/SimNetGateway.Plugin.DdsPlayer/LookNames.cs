using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// HROT's look enumerations arrive by NAME (the host writes them from the enumeration, DD-017 3.10); this is the one place that maps a name to the DPS contract's
/// enumeration. A name this map does not know becomes the contract's unknown value, never a different kind, so a value HROT appends shows up as unknown and is
/// never mislabelled. Comparison is ordinal (the host sends the enumeration's own spelling).
/// </summary>
internal static class LookNames
{
    /// <summary>The effect kinds DPS carries; false for a kind it deliberately leaves out (tracer, flare, chaff). Any other name is sent as None.</summary>
    public static bool TryEffectKindOf(string? name, out D.EffectKind kind)
    {
        switch (name)
        {
            case "MuzzleFlash":
                kind = D.EffectKind.EffectKindMuzzleFlash;
                return true;
            case "Explosion":
                kind = D.EffectKind.EffectKindExplosion;
                return true;
            case "Decal":
                kind = D.EffectKind.EffectKindGroundMark;
                return true;
            case "Fire":
                kind = D.EffectKind.EffectKindWreckFlame;
                return true;
            case "Smoke":
                kind = D.EffectKind.EffectKindWreckSmoke;
                return true;
            case "Tracer" or "Flare" or "Chaff":
                kind = D.EffectKind.EffectKindNone;
                return false;
            default:
                kind = D.EffectKind.EffectKindNone;   // unknown: the client still has the effect's TKB type
                return true;
        }
    }

    /// <summary>HROT's calibre class; Huge is drawn as Large (DPS has three sizes).</summary>
    public static D.EffectSize EffectSizeOf(string? name) => name switch
    {
        "Small" => D.EffectSize.EffectSizeSmall,
        "Medium" => D.EffectSize.EffectSizeMedium,
        "Large" or "Huge" => D.EffectSize.EffectSizeLarge,
        _ => D.EffectSize.EffectSizeUnknown,
    };

    public static D.EffectSurface EffectSurfaceOf(string? name) => name switch
    {
        "Ground" => D.EffectSurface.EffectSurfaceGround,
        "Water" => D.EffectSurface.EffectSurfaceWater,
        "Building" => D.EffectSurface.EffectSurfaceBuilding,
        "Vehicle" => D.EffectSurface.EffectSurfaceVehicle,
        "Person" => D.EffectSurface.EffectSurfacePerson,
        "Air" => D.EffectSurface.EffectSurfaceAir,
        _ => D.EffectSurface.EffectSurfaceUnknown,
    };

    /// <summary>HROT's destruction kind; an empty name is "not destroyed", an unknown one "destroyed, kind unknown".</summary>
    public static D.KillKind KillKindOf(string? name) => name switch
    {
        null or "" or "None" => D.KillKind.KillKindNone,
        "KnockedOut" => D.KillKind.KillKindKnockedOut,
        "BurntOut" => D.KillKind.KillKindBurntOut,
        "Catastrophic" => D.KillKind.KillKindCatastrophic,
        _ => D.KillKind.KillKindUnknown,
    };

    /// <summary>
    /// A HROT enumeration name as the contract's enumerator <c>&lt;prefix&gt;&lt;name&gt;</c> (every enumerator carries its enum's name). A name the contract does not have
    /// (a value HROT appends, or HROT's own "none" spelled differently) is the enumeration's value 0, its unknown / none value, never another value.
    /// </summary>
    public static T ByName<T>(string prefix, string? name)
        where T : struct, Enum =>
        !string.IsNullOrEmpty(name) && Enum.TryParse<T>(prefix + name, out var value) && Enum.IsDefined(value) && prefix + name == value.ToString() ? value : default;

    /// <summary>HROT's civilian mode; "None" and an empty name are "no mode".</summary>
    public static D.CivilianMode CivilianModeOf(string? name) => name switch
    {
        "Routine" => D.CivilianMode.CivilianModeRoutine,
        "Alarmed" => D.CivilianMode.CivilianModeAlarmed,
        "Panic" => D.CivilianMode.CivilianModePanic,
        "Shelter" => D.CivilianMode.CivilianModeShelter,
        "Inside" => D.CivilianMode.CivilianModeInside,
        "Cower" => D.CivilianMode.CivilianModeCower,
        "Casualty" => D.CivilianMode.CivilianModeCasualty,
        "Protest" => D.CivilianMode.CivilianModeProtest,
        "Riot" => D.CivilianMode.CivilianModeRiot,
        "Dispersing" => D.CivilianMode.CivilianModeDispersing,
        "ArmedHostile" => D.CivilianMode.CivilianModeArmedHostile,
        "Gather" => D.CivilianMode.CivilianModeGather,
        "Surrender" => D.CivilianMode.CivilianModeSurrender,
        "Watch" => D.CivilianMode.CivilianModeWatch,
        _ => D.CivilianMode.CivilianModeUnknown,
    };

    /// <summary>HROT's weapon category of a mount; an unknown or empty name is the unknown value.</summary>
    public static D.WeaponCategory WeaponCategoryOf(string? name) => name switch
    {
        "SmallArms" => D.WeaponCategory.WeaponCategorySmallArms,
        "MachineGun" => D.WeaponCategory.WeaponCategoryMachineGun,
        "Autocannon" => D.WeaponCategory.WeaponCategoryAutocannon,
        "TankGun" => D.WeaponCategory.WeaponCategoryTankGun,
        "Atgm" => D.WeaponCategory.WeaponCategoryAtgm,
        "Mortar" => D.WeaponCategory.WeaponCategoryMortar,
        "Grenade" => D.WeaponCategory.WeaponCategoryGrenade,
        "Bomb" => D.WeaponCategory.WeaponCategoryBomb,
        "Rocket" => D.WeaponCategory.WeaponCategoryRocket,
        "Howitzer" => D.WeaponCategory.WeaponCategoryHowitzer,
        "Sam" => D.WeaponCategory.WeaponCategorySam,
        "Countermeasure" => D.WeaponCategory.WeaponCategoryCountermeasure,
        "AirToAir" => D.WeaponCategory.WeaponCategoryAirToAir,
        "Bow" => D.WeaponCategory.WeaponCategoryBow,
        "SmokeDischarger" => D.WeaponCategory.WeaponCategorySmokeDischarger,
        "Flamethrower" => D.WeaponCategory.WeaponCategoryFlamethrower,
        _ => D.WeaponCategory.WeaponCategoryUnknown,
    };

    /// <summary>HROT's munition shape; an unknown name is the unknown value.</summary>
    public static D.MunitionShape MunitionShapeOf(string? name) => name switch
    {
        "Grenade" => D.MunitionShape.MunitionShapeGrenade,
        "Bomb" => D.MunitionShape.MunitionShapeBomb,
        "Shell" => D.MunitionShape.MunitionShapeShell,
        "Rocket" => D.MunitionShape.MunitionShapeRocket,
        "Missile" => D.MunitionShape.MunitionShapeMissile,
        "Bullet" => D.MunitionShape.MunitionShapeBullet,
        "Axe" => D.MunitionShape.MunitionShapeAxe,
        "Spear" => D.MunitionShape.MunitionShapeSpear,
        "Star" => D.MunitionShape.MunitionShapeStar,
        "Arrow" => D.MunitionShape.MunitionShapeArrow,
        _ => D.MunitionShape.MunitionShapeUnknown,
    };

    /// <summary>HROT's heat mark kind; "NoMark" and an unknown name are the unknown value.</summary>
    public static D.HeatMarkKind HeatMarkKindOf(string? name) => name switch
    {
        "VehicleStand" => D.HeatMarkKind.HeatMarkKindVehicleStand,
        "BurnedArea" => D.HeatMarkKind.HeatMarkKindBurnedArea,
        "BodyImprint" => D.HeatMarkKind.HeatMarkKindBodyImprint,
        _ => D.HeatMarkKind.HeatMarkKindUnknown,
    };

    /// <summary>HROT's hot end of a heat mark; "NoHotEnd" and an unknown name are the unknown value.</summary>
    public static D.HeatMarkEnd HeatMarkEndOf(string? name) => name switch
    {
        "Front" => D.HeatMarkEnd.HeatMarkEndFront,
        "Rear" => D.HeatMarkEnd.HeatMarkEndRear,
        _ => D.HeatMarkEnd.HeatMarkEndUnknown,
    };
}
