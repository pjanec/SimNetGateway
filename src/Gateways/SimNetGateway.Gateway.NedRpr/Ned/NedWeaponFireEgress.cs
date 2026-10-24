using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>Rule "Fire": a WeaponFire event of another source as NedWeaponFire (ids 0 when NED does not know an entity).</summary>
public sealed class NedWeaponFireEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly SourceId _ned;
    private readonly IOutboundChannel<INedMessage> _output;

    public NedWeaponFireEgress(PivotSchema schema, NedRprMaps maps, NedRprSources sources, IOutboundChannel<INedMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _ned = sources.Ned;
        _output = output;
    }

    public string Name => nameof(NedWeaponFireEgress);

    public void Register(ModuleSetup setup)
    {
        setup.Consumes(_schema.EventId, _schema.EventShooter, _schema.EventTarget, _schema.EventMunitionTkb,
            _schema.EventLocation, _schema.EventQuantity);
        setup.OnEvent(_schema.WeaponFire)
            .Named("Fire")
            .When(t => t.Source != _ned)
            .Do(OnFire);
    }

    private void OnFire(EventTrigger t)
    {
        var payload = t.Payload;
        var fireId = payload.TryGet(_schema.EventId, out var id) ? id.AsObject<Guid>() : Guid.Empty;
        ulong shooter = NedIdOf(payload, _schema.EventShooter);
        ulong target = NedIdOf(payload, _schema.EventTarget);
        int munition = payload.TryGet(_schema.EventMunitionTkb, out var m) ? (int)m.AsInt() : 0;
        int quantity = payload.TryGet(_schema.EventQuantity, out var q) ? (int)q.AsInt() : 1;
        double latitude = 0, longitude = 0, altitude = 0;
        if (payload.TryGet(_schema.EventLocation, out var location))
        {
            NedRprConversions.EcefToGeodetic(location.AsVec3(), out latitude, out longitude, out altitude);
        }

        _output.Send(new NedWeaponFire(fireId, shooter, target, munition, latitude, longitude, altitude, quantity));
    }

    private ulong NedIdOf(AttributeBag payload, AttrId attr) =>
        payload.TryGet(attr, out var entity) && _maps.NedEntity.TryGetKey(entity.AsEntity(), out ulong id) ? id : 0;
}
