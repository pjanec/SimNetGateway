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

    private readonly IPlayerStation? _station;
    private readonly DpsChannel<D.PlayerSlot> _slot;
    private readonly DpsChannel<D.OwnUnit> _unit;
    private int _version = -1;
    private string _reason = string.Empty;
    private PlayerOffer[]? _offers;
    private bool _wroteUnit;
    private D.OwnUnit _lastUnit;

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
        if (slot.Version != _version || _offers is null || !offers.SequenceEqual(_offers) || reason != _reason)
        {
            _version = slot.Version;
            _reason = reason;
            _offers = [.. offers];
            byte side = 0;
            foreach (var o in offers)
            {
                if (o.UnitId == slot.UnitId && slot.UnitId != 0)
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
                Offers = [.. offers.Take(128).Select(o => new D.Offer
                {
                    Kind = D.OfferKind.OfferKindUnit,
                    Id = o.UnitId,
                    Name = o.Role is { Length: > 64 } r ? r[..64] : o.Role ?? string.Empty,
                    Side = (byte)Math.Clamp(o.Force, 0, 255),
                    Free = true,
                    Kits = [],      // filled from HROT's deploy offers once seats have the deploy flow (HROT Q114, T-049)
                    Outline = [],
                })],
            });
        }

        var status = _station.Unit;
        var unit = new D.OwnUnit
        {
            UnitId = slot.State is PlayerSlotState.Playing or PlayerSlotState.Dead ? slot.UnitId : 0,
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
        };

        if (!_wroteUnit || !SameUnit(in _lastUnit, in unit))
        {
            _wroteUnit = true;
            _lastUnit = unit;
            _unit.Write(unit);
        }
    }

    private static string Cut(string? s, int max) => s is null ? string.Empty : s.Length <= max ? s : s[..max];

    // everything but the time to impact, which counts down: it only matters when it moved by a second (the client counts down from the sample it has)
    private static bool SameUnit(in D.OwnUnit a, in D.OwnUnit b) =>
        a.UnitId == b.UnitId && a.Health == b.Health && a.MaxHealth == b.MaxHealth && a.ActiveSlot == b.ActiveSlot && a.CanUse == b.CanUse && a.UseVerb == b.UseVerb && a.Verb == b.Verb
        && a.UseTargetId == b.UseTargetId && a.UseLabel == b.UseLabel && a.FireMode == b.FireMode && a.UseSeq == b.UseSeq && a.UseOutcome == b.UseOutcome
        && a.UseReason == b.UseReason && a.SpreadRad == b.SpreadRad && a.RadarWarning == b.RadarWarning && a.RadarBearingDeg == b.RadarBearingDeg
        && a.MissileLaunch == b.MissileLaunch && a.MissileBearingDeg == b.MissileBearingDeg && a.MissileSeeker == b.MissileSeeker
        && MathF.Abs(a.MissileTimeToImpactS - b.MissileTimeToImpactS) < MissileRefreshS
        && a.Ammo.Count == b.Ammo.Count
        && a.Ammo.Zip(b.Ammo).All(p => p.First.Slot == p.Second.Slot && p.First.Rounds == p.Second.Rounds && p.First.AmmoType == p.Second.AmmoType && p.First.Weapon == p.Second.Weapon
            && p.First.AmmoTypeId == p.Second.AmmoTypeId && p.First.Max == p.Second.Max && p.First.Magazine == p.Second.Magazine && p.First.LoadedRounds == p.Second.LoadedRounds
            && p.First.Busy == p.Second.Busy && p.First.Overheated == p.Second.Overheated && p.First.HeatPercent == p.Second.HeatPercent);
}
