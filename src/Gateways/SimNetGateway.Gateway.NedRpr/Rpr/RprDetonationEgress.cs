using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Rule "Detonation": a Detonation event of another source as RprMunitionDetonation (the damage goes out through
/// RprEntityEgress). Held for a pending RPR handle (R-047) and suppressed in a replay session (C16) like
/// <see cref="RprWeaponFireEgress"/>.
/// </summary>
public sealed class RprDetonationEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly TypeCatalog _catalog;
    private readonly SourceId _rpr;
    private readonly IOutboundChannel<IRprMessage> _output;
    private readonly RprEventHold _hold;
    private readonly TimeSpan _releasePeriod;
    private IGatewayContext _gateway = null!;

    public RprDetonationEgress(PivotSchema schema, NedRprMaps maps, TypeCatalog catalog, NedRprSources sources,
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

    public string Name => nameof(RprDetonationEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        _hold.Attach(setup);
        setup.Consumes(_schema.EventId, _schema.EventTarget, _schema.EventMunitionTkb, _schema.EventLocation, _schema.EventResult);
        setup.OnEvent(_schema.Detonation)
            .Named("Detonation")
            .When(t => t.Source != _rpr)
            .Do(OnDetonation);
        setup.OnEvent(_hold.Publication.HandleBoundEvent).Named("Released").Do(_ => _hold.Release());
        setup.Every(_releasePeriod).Named("ReleaseHeld").Do(_ => _hold.Release());
    }

    private void OnDetonation(EventTrigger t)
    {
        if (_gateway.Session.Mode != SessionMode.Live)
        {
            return; // replay policy (C16): no weapon effects into the federation during a debrief
        }

        var payload = t.Payload;
        var id = payload.TryGet(_schema.EventId, out var eventId) ? eventId.AsObject<Guid>() : Guid.Empty;
        var location = payload.TryGet(_schema.EventLocation, out var l) ? NedRprConversions.ToWorldLocation(l.AsVec3()) : default;
        var result = RprDetonationResult.Other;
        if (payload.TryGet(_schema.EventResult, out var r) && NedRprConversions.TryResultToRpr((int)r.AsInt(), out var mapped))
        {
            result = mapped;
        }

        var munition = RprEventWire.Munition(_schema, _catalog, _gateway, Name, payload, useSelected: false);
        bool hasShooter = RprEventWire.TryEntity(payload, _schema.EventShooter, out var shooter);
        bool hasTarget = RprEventWire.TryEntity(payload, _schema.EventTarget, out var target);
        if (_hold.MustHold(hasShooter, shooter, hasTarget, target))
        {
            _hold.Hold(new HeldEvent(HeldEventKind.Detonation, id, shooter, hasShooter, target, hasTarget, location, munition, 0,
                result, _gateway.WallTime));
            return;
        }

        _output.Send(new RprMunitionDetonation(
            id,
            RprEventWire.HandleOf(_maps, payload, _schema.EventShooter),
            RprEventWire.HandleOf(_maps, payload, _schema.EventTarget),
            location,
            munition,
            result));
    }
}
