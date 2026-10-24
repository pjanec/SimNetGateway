using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// NedWeaponFire: the shooter must be known (else the transaction aborts); the shooter's ammunition is decremented in
/// the same transaction (so event rules see the new value, R-016) and a WeaponFire event is published.
/// </summary>
public sealed class NedWeaponFireIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;

    public NedWeaponFireIngress(PivotSchema schema, NedRprMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(NedWeaponFireIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.Ammo, _schema.EventId, _schema.EventShooter, _schema.EventTarget, _schema.EventMunitionTkb,
            _schema.EventLocation, _schema.EventQuantity);
        setup.Produces(_schema.WeaponFire);
        setup.Consumes(_schema.Ammo); // read to decrement
        setup.OnMessage<NedWeaponFire>(Handle);
    }

    private void Handle(in NedWeaponFire m, IngressContext ctx)
    {
        var resolution = IngressEntities.Resolve(ctx, _maps.NedEntity, m.Shooter, create: false, out var shooter);
        if (resolution == EntityResolution.Missing)
        {
            ctx.Abort("unknown shooter");
            return;
        }

        if (resolution == EntityResolution.Echo)
        {
            return;
        }

        var changes = ctx.Changes;
        if (TryReadAmmo(ctx, shooter, out long ammo))
        {
            long remaining = Math.Max(0, ammo - Math.Max(0, m.Quantity));
            changes.Set(shooter, _schema.Ammo, AttrValue.From(remaining));
        }

        var bag = ctx.RentPayload();
        bag.Set(_schema.EventId, AttrValue.FromObject(m.FireId));
        bag.Set(_schema.EventShooter, AttrValue.From(shooter));
        if (m.Target != 0 && _maps.NedEntity.TryResolve(changes, m.Target, out var target))
        {
            bag.Set(_schema.EventTarget, AttrValue.From(target));
        }

        bag.Set(_schema.EventMunitionTkb, AttrValue.From(m.MunitionTkb));
        bag.Set(_schema.EventLocation,
            AttrValue.From(NedRprConversions.GeodeticToEcef(m.LatitudeDeg, m.LongitudeDeg, m.Altitude)));
        bag.Set(_schema.EventQuantity, AttrValue.From(m.Quantity));
        ctx.Publish(_schema.WeaponFire, bag);
    }

    private bool TryReadAmmo(IngressContext ctx, EntityId shooter, out long ammo)
    {
        if (ctx.Changes.TryGetPending(shooter, _schema.Ammo, out var pending))
        {
            ammo = pending.AsInt();
            return true;
        }

        var record = ctx.State.Find(shooter);
        if (record is not null && record.TryGet(_schema.Ammo, out var stored))
        {
            ammo = stored.AsInt();
            return true;
        }

        ammo = 0;
        return false;
    }
}
