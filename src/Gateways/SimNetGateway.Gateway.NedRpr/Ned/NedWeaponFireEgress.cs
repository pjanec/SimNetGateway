using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Rule "Fire": a WeaponFire event of another source as NedWeaponFire (ids 0 when NED does not know an entity). An entity that
/// is a member of a unit is known to NED only as (unit id, member index): a target member becomes <c>Target</c> = the unit's id
/// and <c>TargetMember</c> = its index (C9); a shooter member likewise as (unit id, <c>ShooterMember</c> = its index).
/// </summary>
public sealed class NedWeaponFireEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly SourceId _ned;
    private readonly IOutboundChannel<INedMessage> _output;
    private IGatewayContext _gateway = null!;

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
        _gateway = setup.Gateway;
        setup.Consumes(_schema.Parent, _schema.MemberIndex);
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
        ulong shooter = NedIdOf(payload, _schema.EventShooter, out int shooterMember);
        ulong target = NedIdOf(payload, _schema.EventTarget, out int targetMember);
        int munition = payload.TryGet(_schema.EventMunitionTkb, out var m) ? (int)m.AsInt() : 0;
        int quantity = payload.TryGet(_schema.EventQuantity, out var q) ? (int)q.AsInt() : 1;
        double latitude = 0, longitude = 0, altitude = 0;
        if (payload.TryGet(_schema.EventLocation, out var location))
        {
            NedRprConversions.EcefToGeodetic(location.AsVec3(), out latitude, out longitude, out altitude);
        }

        _output.Send(new NedWeaponFire(fireId, shooter, target, munition, latitude, longitude, altitude, quantity, targetMember, shooterMember));
    }

    private ulong NedIdOf(AttributeBag payload, AttrId attr, out int member)
    {
        member = -1;
        if (!payload.TryGet(attr, out var value))
        {
            return 0;
        }

        var entity = value.AsEntity();
        var record = _gateway.State.Find(entity);
        if (record is not null && record.TryGet(_schema.Parent, out var parent))
        {
            if (!_maps.NedEntity.TryGetKey(parent.AsEntity(), out ulong unit))
            {
                return 0; // the unit was never described to NED
            }

            member = record.TryGet(_schema.MemberIndex, out var index) ? (int)index.AsInt() : -1;
            return unit;
        }

        return _maps.NedEntity.TryGetKey(entity, out ulong id) ? id : 0;
    }
}
