using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotDetonation: publishes a <c>MunitionDetonation</c> pivot event with the DIS event id of the fire of the same shooter and
/// munition within 30 s (FIFO, <see cref="FireCorrelator"/>), else a new id. The result is entity impact when the target is a known
/// entity, else ground impact; the location relative to the target (its body axes) is added when the target has a pose. The
/// shooter and the target are optional (they may be gone already). Needs a valid origin.
/// </summary>
public sealed class HrotDetonationIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisServices _services;

    public HrotDetonationIngress(PivotSchema schema, HrotDisMaps maps, HrotDisServices services)
    {
        _schema = schema;
        _maps = maps;
        _services = services;
    }

    public string Name => nameof(HrotDetonationIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.EventId, _schema.EventFiring, _schema.EventTarget, _schema.EventMunitionType,
            _schema.EventMunitionTkb, _schema.EventWarhead, _schema.EventFuse, _schema.EventQuantity, _schema.EventLocation,
            _schema.EventRelativeLocation, _schema.EventVelocity, _schema.EventResult);
        setup.Produces(_schema.MunitionDetonation);
        setup.Consumes(_schema.Position, _schema.Orientation); // the location in the target's body axes
        setup.OnMessage<HrotDetonation>(Handle);
    }

    private void Handle(in HrotDetonation m, IngressContext ctx)
    {
        var geo = _services.Geo;
        if (!geo.IsValid)
        {
            ctx.Abort("no valid HROT origin: the detonation cannot be placed");
            return;
        }

        if (!GeoFrame.IsFinite(m.Location) || !GeoFrame.IsFinite(m.Velocity))
        {
            ctx.Abort("detonation: a value is not finite");
            return;
        }

        var munition = _services.Munitions.Describe(m.MunitionTkb, m.MunitionDisType, out bool known);
        if (!known && munition.DisType == 0 && _services.Munitions.NoteUnknownTkb(m.MunitionTkb))
        {
            ctx.Diagnostics.Warn(Name, $"munition TKB {m.MunitionTkb} has no DIS type.");
        }

        var location = geo.PointToEcef(m.Location);
        long key = HrotEventPayload.MunitionKey(m.MunitionTkb, m.MunitionDisType);
        var id = _services.Correlator.MatchDetonation(m.ShooterId, key, ctx.WallTime) ?? _services.EventIds.Next();

        var bag = ctx.RentPayload();
        bag.Set(_schema.EventId, AttrValue.FromObject(id));
        if (HrotIngress.Resolve(ctx, _maps, m.ShooterId, create: false, out var shooter) == EntityResolution.Existing)
        {
            bag.Set(_schema.EventFiring, AttrValue.From(shooter));
        }

        long result = DetonationResults.GroundImpact;
        if (HrotIngress.TryFind(ctx, _maps, m.TargetId, out var target))
        {
            result = DetonationResults.EntityImpact;
            bag.Set(_schema.EventTarget, AttrValue.From(target));
            if (ctx.State.Find(target) is { } record && record.TryGet(_schema.Position, out var p))
            {
                var euler = record.TryGet(_schema.Orientation, out var o) ? o.AsVec3() : default;
                var bodyToEcef = SimNetGateway.Common.Orientation.ToMatrix(euler);
                bag.Set(_schema.EventRelativeLocation, AttrValue.From(bodyToEcef.Transpose() * (location - p.AsVec3())));
            }
        }

        bag.Set(_schema.EventMunitionType, AttrValue.From(munition.DisType));
        bag.Set(_schema.EventMunitionTkb, AttrValue.From(m.MunitionTkb));
        bag.Set(_schema.EventWarhead, AttrValue.From((long)munition.Warhead));
        bag.Set(_schema.EventFuse, AttrValue.From((long)munition.Fuse));
        bag.Set(_schema.EventQuantity, AttrValue.From(1L));
        bag.Set(_schema.EventLocation, AttrValue.From(location));
        bag.Set(_schema.EventVelocity, AttrValue.From(geo.VectorToEcef(m.Velocity)));
        bag.Set(_schema.EventResult, AttrValue.From(result));
        ctx.Publish(_schema.MunitionDetonation, bag);
    }
}
