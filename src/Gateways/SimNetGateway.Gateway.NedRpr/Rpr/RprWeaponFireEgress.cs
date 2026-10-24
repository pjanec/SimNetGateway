using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Rule "Fire": a WeaponFire event of another source as RprWeaponFire; handles are null ("no object") when the entity has none.
/// An event that names an entity whose registration is pending is held until its handle arrives (R-047, see
/// <see cref="RprEventHold"/>; rules "Released" on the handle-bound event and "ReleaseHeld" on a timer). In a replay session the
/// event is dispatched but nothing is sent (C16).
/// </summary>
public sealed class RprWeaponFireEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly TypeCatalog _catalog;
    private readonly SourceId _rpr;
    private readonly IOutboundChannel<IRprMessage> _output;
    private readonly RprEventHold _hold;
    private readonly TimeSpan _releasePeriod;
    private IGatewayContext _gateway = null!;

    public RprWeaponFireEgress(PivotSchema schema, NedRprMaps maps, TypeCatalog catalog, NedRprSources sources,
        IOutboundChannel<IRprMessage> output, RprEventHold hold, TimeSpan releasePeriod)
    {
        _hold = hold;
        _releasePeriod = releasePeriod;
        _schema = schema;
        _maps = maps;
        _catalog = catalog;
        _rpr = sources.Rpr;
        _output = output;
    }

    public string Name => nameof(RprWeaponFireEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        _hold.Attach(setup);
        setup.Consumes(_schema.EventId, _schema.EventShooter, _schema.EventTarget, _schema.EventMunitionTkb,
            _schema.EventLocation, _schema.EventQuantity, _schema.SelectedMunition);
        setup.OnEvent(_schema.WeaponFire)
            .Named("Fire")
            .When(t => t.Source != _rpr)
            .Do(OnFire);
        setup.OnEvent(_hold.Publication.HandleBoundEvent).Named("Released").Do(_ => _hold.Release());
        setup.Every(_releasePeriod).Named("ReleaseHeld").Do(_ => _hold.Release());
    }

    private void OnFire(EventTrigger t)
    {
        if (_gateway.Session.Mode != SessionMode.Live)
        {
            return; // replay policy (C16): no weapon effects into the federation during a debrief
        }

        var payload = t.Payload;
        var id = payload.TryGet(_schema.EventId, out var eventId) ? eventId.AsObject<Guid>() : Guid.Empty;
        var location = payload.TryGet(_schema.EventLocation, out var l) ? NedRprConversions.ToWorldLocation(l.AsVec3()) : default;
        int quantity = payload.TryGet(_schema.EventQuantity, out var q) ? (int)q.AsInt() : 1;
        var munition = RprEventWire.Munition(_schema, _catalog, _gateway, Name, payload, useSelected: true);
        bool hasShooter = RprEventWire.TryEntity(payload, _schema.EventShooter, out var shooter);
        bool hasTarget = RprEventWire.TryEntity(payload, _schema.EventTarget, out var target);
        if (_hold.MustHold(hasShooter, shooter, hasTarget, target))
        {
            _hold.Hold(new HeldEvent(HeldEventKind.Fire, id, shooter, hasShooter, target, hasTarget, location, munition, quantity,
                RprDetonationResult.Other, _gateway.WallTime));
            return;
        }

        _output.Send(new RprWeaponFire(
            id,
            RprEventWire.HandleOf(_maps, payload, _schema.EventShooter),
            RprEventWire.HandleOf(_maps, payload, _schema.EventTarget),
            location,
            munition,
            quantity));
    }
}
