using DISnet;
using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// Fire PDU: publishes a <c>WeaponFire</c> pivot event of the DIS side (DD-006 3.1, 6.3). The DIS event identifier is kept as it
/// came (<c>Event.Id</c>); shooter, target and munition entity are the pivot entities their DIS ids resolve to (the target may be
/// an entity of the HROT side: the gateway's own ids resolve too), absent when unknown or "no entity"; the munition type, warhead,
/// fuse, quantity and rate are the descriptor's; location and velocity are ECEF as on the wire. The munition's TKB number is left
/// to <c>InjectFireEgress</c>, which owns the way to HROT. PDUs of another exercise and PDUs whose firing entity or event id carry the
/// gateway's own site and application (its own looped-back Fire PDUs) are ignored.
/// </summary>
public sealed class DisFireIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisServices _services;

    public DisFireIngress(PivotSchema schema, HrotDisMaps maps, HrotDisServices services)
    {
        _schema = schema;
        _maps = maps;
        _services = services;
    }

    public string Name => nameof(DisFireIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.EventId, _schema.EventFiring, _schema.EventTarget, _schema.EventMunitionEntity, _schema.EventMunitionType,
            _schema.EventWarhead, _schema.EventFuse, _schema.EventQuantity, _schema.EventRate, _schema.EventLocation,
            _schema.EventVelocity, _schema.EventRange);
        setup.Produces(_schema.WeaponFire);
        setup.OnMessage<FirePdu>(Handle);
    }

    private void Handle(in FirePdu m, IngressContext ctx)
    {
        var firing = m.FiringEntityID.SimulationAddress;
        var eventAddress = m.EventID.SimulationAddress;
        if (DisIngressSupport.Reject(_services, m.ExerciseID, firing.Site, firing.Application) ||
            _services.IsOwn(eventAddress.Site, eventAddress.Application))
        {
            return;
        }

        if (!DisConversions.IsFinite(m.LocationInWorldCoordinates) || !DisConversions.IsFinite(m.Velocity) || !float.IsFinite(m.Range))
        {
            ctx.Abort("fire: a value is not finite");
            return;
        }

        var bag = ctx.RentPayload();
        bag.Set(_schema.EventId, AttrValue.FromObject(DisConversions.EventIdOf(m.EventID)));
        if (DisIngressSupport.TryResolve(ctx, _maps, m.FiringEntityID, out var shooter))
        {
            bag.Set(_schema.EventFiring, AttrValue.From(shooter));
        }

        if (DisIngressSupport.TryResolve(ctx, _maps, m.TargetEntityID, out var target))
        {
            bag.Set(_schema.EventTarget, AttrValue.From(target));
        }

        if (DisIngressSupport.TryResolve(ctx, _maps, m.MunitionExpendibleID, out var munition))
        {
            bag.Set(_schema.EventMunitionEntity, AttrValue.From(munition));
        }

        var d = m.Descriptor;
        bag.Set(_schema.EventMunitionType, AttrValue.From(DisConversions.PackType(d.MunitionType)));
        bag.Set(_schema.EventWarhead, AttrValue.From((long)d.Warhead));
        bag.Set(_schema.EventFuse, AttrValue.From((long)d.Fuse));
        bag.Set(_schema.EventQuantity, AttrValue.From((long)d.Quantity));
        bag.Set(_schema.EventRate, AttrValue.From((long)d.Rate));
        bag.Set(_schema.EventLocation, AttrValue.From(DisConversions.ToVec(m.LocationInWorldCoordinates)));
        bag.Set(_schema.EventVelocity, AttrValue.From(DisConversions.ToVec(m.Velocity)));
        bag.Set(_schema.EventRange, AttrValue.From((double)m.Range));
        ctx.Publish(_schema.WeaponFire, bag);
    }
}

/// <summary>
/// Detonation PDU: publishes a <c>MunitionDetonation</c> pivot event of the DIS side. Like <see cref="DisFireIngress"/>, plus the
/// exploding (munition) entity, the location in the target's coordinates (<c>Event.RelativeLocation</c>) and the detonation result
/// as it came. A detonation names its fire only through the DIS event id, which is kept.
/// </summary>
public sealed class DisDetonationIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisServices _services;

    public DisDetonationIngress(PivotSchema schema, HrotDisMaps maps, HrotDisServices services)
    {
        _schema = schema;
        _maps = maps;
        _services = services;
    }

    public string Name => nameof(DisDetonationIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.EventId, _schema.EventFiring, _schema.EventTarget, _schema.EventMunitionEntity, _schema.EventMunitionType,
            _schema.EventWarhead, _schema.EventFuse, _schema.EventQuantity, _schema.EventRate, _schema.EventLocation,
            _schema.EventRelativeLocation, _schema.EventVelocity, _schema.EventResult);
        setup.Produces(_schema.MunitionDetonation);
        setup.OnMessage<DetonationPdu>(Handle);
    }

    private void Handle(in DetonationPdu m, IngressContext ctx)
    {
        var firing = m.FiringEntityID.SimulationAddress;
        var eventAddress = m.EventID.SimulationAddress;
        if (DisIngressSupport.Reject(_services, m.ExerciseID, firing.Site, firing.Application) ||
            _services.IsOwn(eventAddress.Site, eventAddress.Application))
        {
            return;
        }

        if (!DisConversions.IsFinite(m.LocationInWorldCoordinates) || !DisConversions.IsFinite(m.Velocity) ||
            !DisConversions.IsFinite(m.LocationOfEntityCoordinates))
        {
            ctx.Abort("detonation: a value is not finite");
            return;
        }

        var bag = ctx.RentPayload();
        bag.Set(_schema.EventId, AttrValue.FromObject(DisConversions.EventIdOf(m.EventID)));
        if (DisIngressSupport.TryResolve(ctx, _maps, m.FiringEntityID, out var shooter))
        {
            bag.Set(_schema.EventFiring, AttrValue.From(shooter));
        }

        if (DisIngressSupport.TryResolve(ctx, _maps, m.TargetEntityID, out var target))
        {
            bag.Set(_schema.EventTarget, AttrValue.From(target));
        }

        if (DisIngressSupport.TryResolve(ctx, _maps, m.ExplodingEntityID, out var munition))
        {
            bag.Set(_schema.EventMunitionEntity, AttrValue.From(munition));
        }

        var d = m.Descriptor;
        bag.Set(_schema.EventMunitionType, AttrValue.From(DisConversions.PackType(d.MunitionType)));
        bag.Set(_schema.EventWarhead, AttrValue.From((long)d.Warhead));
        bag.Set(_schema.EventFuse, AttrValue.From((long)d.Fuse));
        bag.Set(_schema.EventQuantity, AttrValue.From((long)d.Quantity));
        bag.Set(_schema.EventRate, AttrValue.From((long)d.Rate));
        bag.Set(_schema.EventLocation, AttrValue.From(DisConversions.ToVec(m.LocationInWorldCoordinates)));
        bag.Set(_schema.EventRelativeLocation, AttrValue.From(DisConversions.ToVec(m.LocationOfEntityCoordinates)));
        bag.Set(_schema.EventVelocity, AttrValue.From(DisConversions.ToVec(m.Velocity)));
        bag.Set(_schema.EventResult, AttrValue.From((long)m.DetonationResult));
        ctx.Publish(_schema.MunitionDetonation, bag);
    }
}
