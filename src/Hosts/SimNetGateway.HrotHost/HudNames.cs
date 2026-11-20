using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Behavior.Possession;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Tkb.Domain;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The words of HROT's player HUD: a weapon's kind, a round's name and a use action's label. HROT keeps them in <c>PossessionStation.Hud</c> (Hrot.Presentation:
/// <c>KindOf</c>, <c>RoundName</c>, <c>TypeName</c>, <c>Label</c>, <c>HandsKind</c>), an assembly the host must not reference, so these are the same rules ported
/// as they stand; <c>HudNamesTests</c> pins the table. When HROT's HUD names change, change them here.
/// </summary>
internal static class HudNames
{
    /// <summary>A person's hands mount (HROT's <c>Hud.HandsKind</c>).</summary>
    public const string HandsKind = "HANDS";

    /// <summary>A weapon's kind as the HUD names it (HROT's <c>Hud.KindOf</c>).</summary>
    public static string KindOf(WeaponCategory c) => c switch
    {
        WeaponCategory.SmallArms => "RIFLE",
        WeaponCategory.MachineGun => "MG",
        WeaponCategory.Autocannon => "AUTOCANNON",
        WeaponCategory.TankGun => "CANNON",
        WeaponCategory.Atgm => "MISSILE",
        WeaponCategory.Mortar => "MORTAR",
        WeaponCategory.Grenade => "GRENADE",
        WeaponCategory.Bomb => "BOMB",
        WeaponCategory.Rocket => "ROCKETS",
        WeaponCategory.Howitzer => "HOWITZER",
        WeaponCategory.Sam => "SAM",
        _ => "WEAPON",
    };

    /// <summary>A round's name from the TKB (its munition template; a trailing " round" dropped), or null (HROT's <c>Hud.RoundName</c>).</summary>
    public static string? RoundName(EntityRepository w, long ammo)
    {
        if (ammo == 0 || !w.HasSingletonManaged<ITkbDatabase>())
        {
            return null;
        }

        if (!w.GetSingletonManaged<ITkbDatabase>()!.TryGetByType(ammo, out var t) || t == null)
        {
            return null;
        }

        return t.Name.EndsWith(" round", StringComparison.Ordinal) ? t.Name[..^6] : t.Name;
    }

    /// <summary>A unit's type name from the TKB, or null (HROT's <c>Hud.TypeName</c>).</summary>
    public static string? TypeName(EntityRepository w, Entity e)
    {
        if (e.IsNull || !w.IsAlive(e) || !w.IsComponentTypeRegistered<TkbIdentity>() || !w.HasComponent<TkbIdentity>(e) || !w.HasSingletonManaged<ITkbDatabase>())
        {
            return null;
        }

        long type = w.GetComponentRO<TkbIdentity>(e).TkbType;
        return w.GetSingletonManaged<ITkbDatabase>()!.TryGetByType(type, out var t) && t != null ? t.Name : null;
    }

    /// <summary>An action as the HUD names it: "Open door", "Drag", "Drive BMP-2" (HROT's <c>Hud.Label</c>).</summary>
    public static string Label(EntityRepository w, in UseAction a) =>
        a.Verb is UseVerbKind.Drive or UseVerbKind.Pilot or UseVerbKind.Board or UseVerbKind.Man or UseVerbKind.Take or UseVerbKind.Drop or UseVerbKind.PickUpRound
            && TypeName(w, a.Target) is { } name
            ? $"{a.Label} {name}" : a.Label;

    /// <summary>The entity and its own mount a slot fires: a controlled weapon's mount 0, else the unit's mount (HROT's <c>Hud.Resolve</c>).</summary>
    public static (Entity Holder, int Mount) Resolve(EntityRepository w, Entity unit, int mount)
    {
        if (!Fdp.Toolkit.Combat.WeaponSystems.IsControlledIndex(mount))
        {
            return (unit, mount);
        }

        var weapon = Fdp.Toolkit.Combat.WeaponSystems.ControlledAt(w, unit, mount);
        return weapon.IsNull ? (unit, mount) : (weapon, 0);
    }
}
