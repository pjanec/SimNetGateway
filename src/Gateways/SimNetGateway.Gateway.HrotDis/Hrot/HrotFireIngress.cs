using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotFire: publishes a <c>WeaponFire</c> pivot event (DD-006 3.1) with the location and velocity converted to ECEF, the munition
/// from the HROT-provided DIS type or the catalogue, and a DIS event id that the <see cref="FireCorrelator"/> remembers for the
/// detonation. The shooter must be a known HROT entity (else the message aborts); a target may be any known entity, a DIS replica
/// included. Needs a valid origin (else the message aborts: it cannot be placed). The shot of a replica is an echo and ignored.
/// </summary>
public sealed class HrotFireIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisServices _services;

    public HrotFireIngress(PivotSchema schema, HrotDisMaps maps, HrotDisServices services)
    {
        _schema = schema;
        _maps = maps;
        _services = services;
    }

    public string Name => nameof(HrotFireIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.EventId, _schema.EventFiring, _schema.EventTarget, _schema.EventMunitionType,
            _schema.EventMunitionTkb, _schema.EventWarhead, _schema.EventFuse, _schema.EventQuantity, _schema.EventRate,
            _schema.EventLocation, _schema.EventVelocity, _schema.EventRange);
        setup.Produces(_schema.WeaponFire);
        setup.Consumes(_schema.Position); // the range to the target
        setup.OnMessage<HrotFire>(Handle);
    }

    private void Handle(in HrotFire m, IngressContext ctx)
    {
        var geo = _services.Geo;
        if (!geo.IsValid)
        {
            ctx.Abort("no valid HROT origin: the fire cannot be placed");
            return;
        }

        if (!GeoFrame.IsFinite(m.Origin) || !GeoFrame.IsFinite(m.Velocity))
        {
            ctx.Abort("fire: a value is not finite");
            return;
        }

        var resolution = HrotIngress.Resolve(ctx, _maps, m.ShooterId, create: false, out var shooter);
        if (resolution == EntityResolution.Missing)
        {
            ctx.Abort($"unknown shooter {m.ShooterId}");
            return;
        }

        if (resolution == EntityResolution.Echo)
        {
            return;
        }

        var munition = _services.Munitions.Describe(m.MunitionTkb, m.MunitionDisType, out bool known);
        if (!known && munition.DisType == 0 && _services.Munitions.NoteUnknownTkb(m.MunitionTkb))
        {
            ctx.Diagnostics.Warn(Name, $"munition TKB {m.MunitionTkb} has no DIS type.");
        }

        var location = geo.PointToEcef(m.Origin);
        var id = _services.EventIds.Next();
        _services.Correlator.OnFire(m.ShooterId, HrotEventPayload.MunitionKey(m.MunitionTkb, m.MunitionDisType), id, ctx.WallTime);

        var bag = ctx.RentPayload();
        bag.Set(_schema.EventId, AttrValue.FromObject(id));
        bag.Set(_schema.EventFiring, AttrValue.From(shooter));
        double range = 0.0;
        if (HrotIngress.TryFind(ctx, _maps, m.TargetId, out var target))
        {
            bag.Set(_schema.EventTarget, AttrValue.From(target));
            if (ctx.State.Find(target) is { } record && record.TryGet(_schema.Position, out var p))
            {
                range = (p.AsVec3() - location).Length;
            }
        }

        bag.Set(_schema.EventMunitionType, AttrValue.From(munition.DisType));
        bag.Set(_schema.EventMunitionTkb, AttrValue.From(m.MunitionTkb));
        bag.Set(_schema.EventWarhead, AttrValue.From((long)munition.Warhead));
        bag.Set(_schema.EventFuse, AttrValue.From((long)munition.Fuse));
        bag.Set(_schema.EventQuantity, AttrValue.From((long)m.Quantity));
        bag.Set(_schema.EventRate, AttrValue.From(0L));
        bag.Set(_schema.EventLocation, AttrValue.From(location));
        bag.Set(_schema.EventVelocity, AttrValue.From(geo.VectorToEcef(m.Velocity)));
        bag.Set(_schema.EventRange, AttrValue.From(range));
        ctx.Publish(_schema.WeaponFire, bag);
    }
}

/// <summary>Helpers the HROT event modules share.</summary>
internal static class HrotEventPayload
{
    /// <summary>What identifies a munition for the fire / detonation correlation: its TKB type, else its DIS type.</summary>
    public static long MunitionKey(long tkb, long disType) => tkb != 0 ? tkb : disType;
}
