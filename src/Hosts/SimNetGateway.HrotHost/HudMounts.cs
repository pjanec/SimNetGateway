using Fdp.Core;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The two HUD rules HROT keeps in <c>PossessionStation.Hud</c> (Hrot.Presentation) and has not moved to its look library: a person's hands mount and the entity a slot
/// fires. The HUD's words (kind, round name, type name, action label) are HROT's own <c>Hrot.Look.Hud.HudWords</c>, called directly.
/// </summary>
internal static class HudMounts
{
    /// <summary>A person's hands mount (HROT's <c>Hud.HandsKind</c>).</summary>
    public const string HandsKind = "HANDS";

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
