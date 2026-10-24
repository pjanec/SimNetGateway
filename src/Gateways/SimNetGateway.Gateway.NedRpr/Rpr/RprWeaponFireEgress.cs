using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>Rule "Fire": a WeaponFire event of another source as RprWeaponFire; handles are null ("no object") when not bound (the hold is stage 7, R-047).</summary>
public sealed class RprWeaponFireEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly TypeCatalog _catalog;
    private readonly SourceId _rpr;
    private readonly IOutboundChannel<IRprMessage> _output;
    private IGatewayContext _gateway = null!;

    public RprWeaponFireEgress(PivotSchema schema, NedRprMaps maps, TypeCatalog catalog, NedRprSources sources,
        IOutboundChannel<IRprMessage> output)
    {
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
        setup.Consumes(_schema.EventId, _schema.EventShooter, _schema.EventTarget, _schema.EventMunitionTkb,
            _schema.EventLocation, _schema.EventQuantity, _schema.SelectedMunition);
        setup.OnEvent(_schema.WeaponFire)
            .Named("Fire")
            .When(t => t.Source != _rpr)
            .Do(OnFire);
    }

    private void OnFire(EventTrigger t)
    {
        var payload = t.Payload;
        var id = payload.TryGet(_schema.EventId, out var eventId) ? eventId.AsObject<Guid>() : Guid.Empty;
        var location = payload.TryGet(_schema.EventLocation, out var l) ? NedRprConversions.ToWorldLocation(l.AsVec3()) : default;
        int quantity = payload.TryGet(_schema.EventQuantity, out var q) ? (int)q.AsInt() : 1;
        var munition = RprEventWire.Munition(_schema, _catalog, _gateway, Name, payload, useSelected: true);
        _output.Send(new RprWeaponFire(
            id,
            RprEventWire.HandleOf(_maps, payload, _schema.EventShooter),
            RprEventWire.HandleOf(_maps, payload, _schema.EventTarget),
            location,
            munition,
            quantity));
    }
}
