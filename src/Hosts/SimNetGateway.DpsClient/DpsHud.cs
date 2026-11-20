using System.Globalization;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.DpsClient;

/// <summary>Who looks: the own unit (0 = none), its side in the contract's force numbering (1 friendly, 2 opposing, 3 neutral), its station id (-1 = unknown) and whether it is an instructor's view.</summary>
public readonly record struct DpsViewer(int UnitId, byte Side, int StationId = -1, bool Instructor = false);

/// <summary>
/// The reference client's HUD text, as HROT's own HUD words it (DD-018 3.1): the weapon line, the use prompt and verdict, the kill feed line, the filter of player messages and of
/// objectives. Pure functions of the received state, so they are tested without a window.
/// </summary>
public static class DpsHud
{
    /// <summary>The heat shown from this percent (HROT's HUD shows it from 25).</summary>
    public const int HeatShownFromPercent = 25;

    /// <summary>The line of the selected weapon: "&gt; 1 MG  7.62 mm 25  120/200 [3]  LOADING  OVERHEATED 87%  LOCK"; null with no weapon.</summary>
    public static string? WeaponLine(in D.OwnUnit own)
    {
        if (own.Ammo is not { Count: > 0 } ammo)
        {
            return null;
        }

        D.AmmoEntry? chosen = null;
        foreach (var a in ammo)
        {
            if (a.Slot == own.ActiveSlot)
            {
                chosen = a;
                break;
            }
        }

        var w = chosen ?? ammo[0];
        var line = new System.Text.StringBuilder();
        line.Append("> ").Append(w.Slot + 1).Append(' ').Append(w.Weapon);
        if (!string.IsNullOrEmpty(w.AmmoType))
        {
            line.Append("  ").Append(w.AmmoType);
            if (w.LoadedRounds >= 0)
            {
                line.Append(' ').Append(w.LoadedRounds.ToString(CultureInfo.InvariantCulture));
            }
        }

        line.Append("  ").Append(w.Rounds.ToString(CultureInfo.InvariantCulture));
        if (w.Max > 0)
        {
            line.Append('/').Append(w.Max.ToString(CultureInfo.InvariantCulture));
        }

        if (w.Magazine >= 0)
        {
            line.Append("  [").Append(w.Magazine.ToString(CultureInfo.InvariantCulture)).Append(']');
        }

        if (w.Busy)
        {
            line.Append("  LOADING");
        }

        if (w.Overheated)
        {
            line.Append("  OVERHEATED ").Append(w.HeatPercent.ToString(CultureInfo.InvariantCulture)).Append('%');
        }
        else if (w.HeatPercent >= HeatShownFromPercent)
        {
            line.Append("  HEAT ").Append(w.HeatPercent.ToString(CultureInfo.InvariantCulture)).Append('%');
        }

        line.Append("  ").Append(own.FireMode == D.FireMode.FireModeLock ? "LOCK" : "SIGHT");
        return line.ToString();
    }

    /// <summary>The use prompt "F  Open door" while something can be used, else null.</summary>
    public static string? UsePrompt(in D.OwnUnit own) => own.CanUse && !string.IsNullOrEmpty(own.UseLabel) ? "F  " + own.UseLabel : null;

    /// <summary>
    /// The verdict of the last press: shown only when the unit's <c>UseSeq</c> equals the client's last press (an older answer is not about it), "Open door: done" or
    /// "Open door: locked"; null otherwise.
    /// </summary>
    public static string? UseVerdict(in D.OwnUnit own, int? lastPress, string label)
    {
        if (lastPress is not { } press || own.UseSeq != (byte)press)
        {
            return null;
        }

        return own.UseOutcome switch
        {
            D.UseOutcome.UseOutcomeDone => $"{label}: done",
            D.UseOutcome.UseOutcomeRefused => $"{label}: {(own.UseReason is D.UseReason.UseReasonOk or D.UseReason.UseReasonUnknown ? "refused" : ReasonText(own.UseReason))}",
            _ => null,
        };
    }

    /// <summary>Why a use press was refused, in the HUD's words.</summary>
    public static string ReasonText(D.UseReason reason) => reason switch
    {
        D.UseReason.UseReasonOutOfReach => "too far",
        D.UseReason.UseReasonLocked => "locked",
        D.UseReason.UseReasonDoorRefuses => "will not move",
        D.UseReason.UseReasonNotDown => "not down",
        D.UseReason.UseReasonSeatTaken => "seat taken",
        D.UseReason.UseReasonOtherSide => "enemy aboard",
        D.UseReason.UseReasonUnfit => "you are down",
        D.UseReason.UseReasonCarrying => "hands full",
        D.UseReason.UseReasonNothingToDo => "nothing to do",
        D.UseReason.UseReasonFailed => "failed",
        _ => string.Empty,
    };

    /// <summary>One kill feed line: "Alpha killed Bravo", "Bravo downed ...", or "Bravo died" with no known source.</summary>
    public static string KillLine(in D.ManDowned kill, Func<int, string> nameOf)
    {
        bool dead = kill.State == D.Injury.InjuryDead;
        string victim = nameOf(kill.VictimId);
        return kill.SourceId == 0 ? $"{victim} {(dead ? "died" : "went down")}" : $"{nameOf(kill.SourceId)} {(dead ? "killed" : "downed")} {victim}";
    }

    /// <summary>
    /// The filter of HROT's HUD (<c>PlayerMessages.Addressed</c>): a message to All is for everyone; to a Side for that side; to a Unit for that unit; to a Station for that
    /// station (a client that does not know its station never shows one); to the Instructor only in an instructor's view. An address this contract does not name is not shown.
    /// </summary>
    public static bool Addressed(in D.PlayerMessage message, in DpsViewer viewer) => message.To switch
    {
        D.MessageAddress.MessageAddressAll => true,
        D.MessageAddress.MessageAddressSide => viewer.Side != 0 && message.ToValue == viewer.Side,
        D.MessageAddress.MessageAddressUnit => viewer.UnitId != 0 && message.ToValue == viewer.UnitId,
        D.MessageAddress.MessageAddressStation => viewer.StationId >= 0 && message.ToValue == viewer.StationId,
        D.MessageAddress.MessageAddressInstructor => viewer.Instructor,
        _ => false,
    };

    /// <summary>
    /// Whether an objective is for the side (HROT's player HUD): its visible-sides mask when it has one, else its owner or the neutral side; and only leaf objectives with a title
    /// (a rule that is some other rule's parent is a heading, not a task).
    /// </summary>
    public static bool VisibleTo(in D.Status status, byte side, bool isParent)
    {
        if (status.Role != D.ObjectiveRole.ObjectiveRoleObjective || isParent || string.IsNullOrEmpty(status.Title))
        {
            return false;
        }

        return status.VisibleSides != 0 ? (status.VisibleSides & (1 << side)) != 0 : status.Owner == side || status.Owner == 3;
    }

    /// <summary>"Hold the bridge - Active" (title and state).</summary>
    public static string StatusLine(in D.Status status) => $"{status.Title} - {status.State.ToString().Replace("ObjectiveState", string.Empty)}";
}
