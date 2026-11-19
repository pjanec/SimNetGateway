using SimNetGateway.Gateway.HrotDis;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Publishes the player slot, the offers and the player's own unit (DD-017 3.6) on change only: <c>PlayerSlot</c> when the seam's slot version moved or its offers
/// changed, <c>OwnUnit</c> when the unit status differs. Without a station (a host with no player mode) the slot is Observing and stays so. Main thread only.
/// </summary>
internal sealed class DpsPlayerSlot
{
    private readonly IPlayerStation? _station;
    private readonly DpsChannel<D.PlayerSlot> _slot;
    private readonly DpsChannel<D.OwnUnit> _unit;
    private int _version = -1;
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
        if (slot.Version != _version || _offers is null || !offers.SequenceEqual(_offers))
        {
            _version = slot.Version;
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
                Reason = string.Empty,
                Offers = [.. offers.Take(128).Select(o => new D.Offer
                {
                    Kind = D.OfferKind.OfferKindUnit,
                    Id = o.UnitId,
                    Name = o.Role is { Length: > 64 } r ? r[..64] : o.Role ?? string.Empty,
                    Side = (byte)Math.Clamp(o.Force, 0, 255),
                    Free = true,
                })],
            });
        }

        var status = _station.Unit;
        var unit = new D.OwnUnit
        {
            UnitId = slot.State is PlayerSlotState.Playing or PlayerSlotState.Dead ? slot.UnitId : 0,
            Health = (float)status.Health,
            MaxHealth = (float)status.HealthMax,
            CanUse = status.UseVerb != 0,
            UseTargetId = status.UseTarget,
            UseVerb = status.UseVerb,
            UseLabel = string.Empty,
            Ammo = [.. status.Ammo.Take(16).Select(a => new D.AmmoEntry { Slot = a.Mount, Weapon = string.Empty, AmmoType = a.Ammo == 0 ? string.Empty : a.Ammo.ToString(System.Globalization.CultureInfo.InvariantCulture), Rounds = a.Rounds })],
        };
        if (!_wroteUnit || !SameUnit(in _lastUnit, in unit))
        {
            _wroteUnit = true;
            _lastUnit = unit;
            _unit.Write(unit);
        }
    }

    private static bool SameUnit(in D.OwnUnit a, in D.OwnUnit b) =>
        a.UnitId == b.UnitId && a.Health == b.Health && a.MaxHealth == b.MaxHealth && a.UseVerb == b.UseVerb && a.UseTargetId == b.UseTargetId
        && a.Ammo.Count == b.Ammo.Count && a.Ammo.Zip(b.Ammo).All(p => p.First.Slot == p.Second.Slot && p.First.Rounds == p.Second.Rounds && p.First.AmmoType == p.Second.AmmoType);
}
