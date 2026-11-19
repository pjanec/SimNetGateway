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
}
