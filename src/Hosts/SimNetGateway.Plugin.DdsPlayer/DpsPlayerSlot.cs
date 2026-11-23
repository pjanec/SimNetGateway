using SimNetGateway.Gateway.HrotDis;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Publishes the player slot, the offers and the player's own unit (DD-017 3.6) on change only: <c>PlayerSlot</c> when the seam's slot version moved or its offers
/// changed, <c>OwnUnit</c> when the unit status differs. Without a station (a host with no player mode) the slot is Observing and stays so. Main thread only.
/// </summary>
internal sealed class DpsPlayerSlot
{
    private const float MissileRefreshS = 1f;

    /// <summary>The bound of <c>OwnUnit.Actions</c> (HROT offers at most <c>OperatorUseActions.MaxActions</c> verbs).</summary>
    private const int MaxActions = 8;

    private readonly IPlayerStation? _station;
    private readonly DpsChannel<D.PlayerSlot> _slot;
    private readonly DpsChannel<D.OwnUnit> _unit;
    private int _version = -1;
    private string _reason = string.Empty;
    private PlayerOffer[]? _offers;
    private bool _wroteUnit;
    private D.OwnUnit _lastUnit;
    private PlayerUnitStatus _lastStatus;
    private int _lastUnitId;
    private PlayerWeapon[] _lastAmmo = Array.Empty<PlayerWeapon>();
    private int _lastAmmoCount;
    private PlayerUseAction[] _lastActions = Array.Empty<PlayerUseAction>();
    private int _lastActionCount;

    public DpsPlayerSlot(IPlayerStation? station, DpsChannel<D.PlayerSlot> slot, DpsChannel<D.OwnUnit> unit)
    {
        _station = station;
        _slot = slot;
        _unit = unit;
    }

    public void Pump()
    {
        if (_station is null)
        {
            if (_offers is null)
            {
                _offers = [];
                _slot.Write(new D.PlayerSlot { State = D.SlotState.SlotStateObserving, Reason = string.Empty, Offers = [] });
            }

            return;
        }

        var slot = _station.Slot;
        var offers = _station.Offers;
        string reason = slot.Reason is { Length: > 64 } r ? r[..64] : slot.Reason ?? string.Empty;
        if (slot.Version != _version || _offers is null || !SameOffers(offers, _offers) || reason != _reason)
        {
            _version = slot.Version;
            _reason = reason;
            _offers = [.. offers];
            byte side = slot.Side;
            foreach (var o in offers)
            {
                if (side == 0 && o.UnitId == slot.UnitId && slot.UnitId != 0)
                {
                    side = (byte)Math.Clamp(o.Force, 0, 255);
                }
            }

            _slot.Write(new D.PlayerSlot
            {
                State = (D.SlotState)(byte)slot.State,
                UnitId = slot.UnitId,
                Side = side,
                RespawnAtSim = slot.RespawnAt,
                Reason = reason,
                Offers = [.. offers.Take(128).Select(OfferOf)],
            });
        }

        var status = _station.Unit;
        int unitId = slot.State is PlayerSlotState.Playing or PlayerSlotState.Dead ? slot.UnitId : 0;
        if (_wroteUnit && unitId == _lastUnitId && SameStatus(in status))
        {
            return;   // nothing the client sees changed: no wire value is built (steady frame: 0 B)
        }

        _lastUnitId = unitId;
        _lastStatus = status;
        _lastAmmoCount = Math.Min(status.Ammo.Count, 16);
        if (_lastAmmo.Length < _lastAmmoCount)
        {
            _lastAmmo = new PlayerWeapon[_lastAmmoCount];
        }

        for (int i = 0; i < _lastAmmoCount; i++)
        {
            _lastAmmo[i] = status.Ammo[i];
        }

        int actionCount = Math.Min(status.Actions?.Count ?? 0, MaxActions);
        _lastActionCount = actionCount;
        if (_lastActions.Length < actionCount)
        {
            _lastActions = new PlayerUseAction[actionCount];
        }

        for (int i = 0; i < actionCount; i++)
        {
            _lastActions[i] = status.Actions![i];   // a copy: the station reuses its list
        }

        var unit = new D.OwnUnit
        {
            UnitId = unitId,
            Health = (float)status.Health,
            MaxHealth = (float)status.HealthMax,
            ActiveSlot = status.ActiveMount,
            CanUse = status.UseVerb != 0,
            UseTargetId = status.UseTarget,
            UseVerb = status.UseVerb,
            Verb = LookNames.ByName<D.UseVerb>("UseVerb", status.UseVerbName),
            UseLabel = Cut(status.UseLabel, 32),
            FireMode = LookNames.ByName<D.FireMode>("FireMode", status.FireMode),
            UseSeq = status.UseSeq,
            UseOutcome = LookNames.ByName<D.UseOutcome>("UseOutcome", status.UseOutcome),
            UseReason = LookNames.ByName<D.UseReason>("UseReason", status.UseReason),
            SpreadRad = status.SpreadRad,
            RadarWarning = LookNames.ByName<D.RadarWarningLevel>("RadarWarningLevel", status.RadarWarning),
            RadarBearingDeg = status.RadarBearingDeg,
            MissileLaunch = status.MissileLaunch,
            MissileBearingDeg = status.MissileBearingDeg,
            MissileTimeToImpactS = status.MissileTimeToImpactS,
            MissileSeeker = LookNames.ByName<D.MissileSeeker>("MissileSeeker", status.MissileSeeker),
            Ammo =
            [
                .. status.Ammo.Take(16).Select(a => new D.AmmoEntry
                {
                    Slot = a.Mount,
                    Weapon = Cut(a.Weapon, 32),
                    AmmoType = Cut(a.AmmoName, 32),
                    Rounds = a.Rounds,
                    AmmoTypeId = a.AmmoTypeId,
                    Max = a.Max,
                    Magazine = a.Magazine,
                    LoadedRounds = a.LoadedRounds,
                    Busy = a.Busy,
                    Overheated = a.Overheated,
                    HeatPercent = a.HeatPercent,
                }),
            ],
            Actions =
            [
                .. status.Actions is null ? [] : status.Actions.Take(MaxActions).Select(a => new D.UseActionEntry
                {
                    Verb = LookNames.ByName<D.UseVerb>("UseVerb", a.Verb),
                    Label = Cut(a.Label, 32),
                    Available = a.Available,
                    Reason = LookNames.ByName<D.UseReason>("UseReason", a.Reason),
                    HoldOnly = a.HoldOnly,
                }),
            ],
        };

        if (!_wroteUnit || !SameUnit(in _lastUnit, in unit))
        {
            _wroteUnit = true;
            _lastUnit = unit;
            _unit.Write(unit);
        }
    }

    /// <summary>One seam offer as the wire's <c>Offer</c> (T-053): kind, role count / max, free, the role's kits (<= 8 names of 32) and an area's outline (<= 32 points).</summary>
    private static D.Offer OfferOf(PlayerOffer o) => new()
    {
        Kind = o.Kind switch { PlayerOfferKind.Role => D.OfferKind.OfferKindRole, PlayerOfferKind.Area => D.OfferKind.OfferKindArea, _ => D.OfferKind.OfferKindUnit },
        Id = o.UnitId,
        Name = o.Role is { Length: > 64 } r ? r[..64] : o.Role ?? string.Empty,
        Side = (byte)Math.Clamp(o.Force, 0, 255),
        Free = o.Free,
        Count = o.Count,
        Max = o.Max,
        Kits = o.Kits is null ? [] : [.. o.Kits.Take(8).Select(k => new D.NameItem { Name = Cut(k, 32) })],
        Outline = o.Outline is null ? [] : [.. o.Outline.Take(32).Select(p => new D.Vec3d { X = p.X, Y = p.Y, Z = p.Z })],
    };

    /// <summary>Index loops, no enumerator (a frame with no change costs nothing).</summary>
    private static bool SameOffers(IReadOnlyList<PlayerOffer> a, PlayerOffer[] b)
    {
        if (a.Count != b.Length)
        {
            return false;
        }

        for (int i = 0; i < b.Length; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the status is, for the client, the one last written: every field the wire carries equal, the time to impact within a second (the client counts down from the sample it has), the first 16 ammo entries equal.
    /// The inputs are compared (not the built wire value), so an unchanged frame builds nothing.
    /// </summary>
    private bool SameStatus(in PlayerUnitStatus b)
    {
        var a = _lastStatus;
        if (a.Health != b.Health || a.HealthMax != b.HealthMax || a.UseVerb != b.UseVerb || a.UseTarget != b.UseTarget || a.ActiveMount != b.ActiveMount || a.FireMode != b.FireMode
            || a.UseLabel != b.UseLabel || a.UseSeq != b.UseSeq || a.UseOutcome != b.UseOutcome || a.UseReason != b.UseReason || a.SpreadRad != b.SpreadRad || a.RadarWarning != b.RadarWarning
            || a.RadarBearingDeg != b.RadarBearingDeg || a.MissileLaunch != b.MissileLaunch || a.MissileBearingDeg != b.MissileBearingDeg || a.MissileSeeker != b.MissileSeeker
            || a.UseVerbName != b.UseVerbName || MathF.Abs(a.MissileTimeToImpactS - b.MissileTimeToImpactS) >= MissileRefreshS)
        {
            return false;
        }

        int count = Math.Min(b.Ammo.Count, 16);
        if (count != _lastAmmoCount)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (_lastAmmo[i] != b.Ammo[i])
            {
                return false;
            }
        }

        int actions = Math.Min(b.Actions?.Count ?? 0, MaxActions);
        if (actions != _lastActionCount)
        {
            return false;
        }

        for (int i = 0; i < actions; i++)
        {
            if (_lastActions[i] != b.Actions![i])
            {
                return false;
            }
        }

        return true;
    }

    private static string Cut(string? s, int max) => s is null ? string.Empty : s.Length <= max ? s : s[..max];

    // everything but the time to impact, which counts down: it only matters when it moved by a second (the client counts down from the sample it has)
    private static bool SameUnit(in D.OwnUnit a, in D.OwnUnit b) =>
        a.UnitId == b.UnitId && a.Health == b.Health && a.MaxHealth == b.MaxHealth && a.ActiveSlot == b.ActiveSlot && a.CanUse == b.CanUse && a.UseVerb == b.UseVerb && a.Verb == b.Verb
        && a.UseTargetId == b.UseTargetId && a.UseLabel == b.UseLabel && a.FireMode == b.FireMode && a.UseSeq == b.UseSeq && a.UseOutcome == b.UseOutcome
        && a.UseReason == b.UseReason && a.SpreadRad == b.SpreadRad && a.RadarWarning == b.RadarWarning && a.RadarBearingDeg == b.RadarBearingDeg
        && a.MissileLaunch == b.MissileLaunch && a.MissileBearingDeg == b.MissileBearingDeg && a.MissileSeeker == b.MissileSeeker
        && MathF.Abs(a.MissileTimeToImpactS - b.MissileTimeToImpactS) < MissileRefreshS
        && a.Ammo.Count == b.Ammo.Count && a.Actions.Count == b.Actions.Count
        && a.Actions.Zip(b.Actions).All(p => p.First.Verb == p.Second.Verb && p.First.Label == p.Second.Label && p.First.Available == p.Second.Available
            && p.First.Reason == p.Second.Reason && p.First.HoldOnly == p.Second.HoldOnly)
        && a.Ammo.Zip(b.Ammo).All(p => p.First.Slot == p.Second.Slot && p.First.Rounds == p.Second.Rounds && p.First.AmmoType == p.Second.AmmoType && p.First.Weapon == p.Second.Weapon
            && p.First.AmmoTypeId == p.Second.AmmoTypeId && p.First.Max == p.Second.Max && p.First.Magazine == p.Second.Magazine && p.First.LoadedRounds == p.Second.LoadedRounds
            && p.First.Busy == p.Second.Busy && p.First.Overheated == p.Second.Overheated && p.First.HeatPercent == p.Second.HeatPercent);
}
