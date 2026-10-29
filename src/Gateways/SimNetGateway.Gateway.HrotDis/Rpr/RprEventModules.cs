using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>What the RPR event modules read out of and write into a pivot event payload.</summary>
internal static class RprEventSupport
{
    /// <summary>The RPR object handle of the entity an event names, or null ("no object") when the event names none or the entity has no handle.</summary>
    public static RprObjectHandle? HandleOf(HrotDisMaps maps, AttributeBag payload, AttrId attr) =>
        payload.TryGet(attr, out var value) && maps.RprHandle.TryGetKey(value.AsEntity(), out var handle) ? handle : null;

    /// <summary>The guid of the event: the one derived from the DIS event identifier the HROT ingress assigned (a fire and its detonation share it), else a new one from the gateway's counter.</summary>
    public static Guid EventGuid(PivotSchema schema, HrotDisServices services, AttributeBag payload) =>
        RprConversions.EventGuid(payload.TryGet(schema.EventId, out var id) ? id.AsObject<DisEventId>() : services.EventIds.Next());

    public static Vec3 Vec(AttributeBag payload, AttrId attr) => payload.TryGet(attr, out var v) ? v.AsVec3() : Vec3.Zero;

    public static ushort? UShort(AttributeBag payload, AttrId attr) =>
        payload.TryGet(attr, out var v) ? (ushort)Math.Clamp(v.AsInt(), 0, ushort.MaxValue) : null;
}

/// <summary>
/// RprWeaponFire: publishes a <c>WeaponFire</c> pivot event of the HLA side (the RPR counterpart of <see cref="DisFireIngress"/>,
/// DD-009 section 3 item 3). Shooter and target are the pivot entities their object handles resolve to (the target may be a hrot
/// entity: the handle the RTI gave it resolves too), absent when unknown or "no object"; the munition type is packed, location and
/// initial velocity are ECEF as on the wire. The munition's TKB number is left to <c>InjectFireEgress</c>, which owns the way to
/// HROT. A fire whose firing object belongs to a hrot entity is an echo and ignored.
/// </summary>
public sealed class RprFireIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;

    public RprFireIngress(PivotSchema schema, HrotDisMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(RprFireIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.EventFiring, _schema.EventTarget, _schema.EventMunitionEntity, _schema.EventMunitionType, _schema.EventWarhead,
            _schema.EventFuse, _schema.EventQuantity, _schema.EventRate, _schema.EventLocation, _schema.EventVelocity, _schema.EventRange);
        setup.Produces(_schema.WeaponFire);
        setup.OnMessage<RprWeaponFire>(Handle);
    }

    private void Handle(in RprWeaponFire m, IngressContext ctx)
    {
        if (!RprConversions.IsFinite(m.Location) || (m.InitialVelocity is { } iv && !RprConversions.IsFinite(iv)) ||
            (m.FireControlSolutionRange is { } r && !float.IsFinite(r)))
        {
            ctx.Abort("fire: a value is not finite");
            return;
        }

        var bag = ctx.RentPayload();
        if (m.FiringObject is { } firing)
        {
            var resolution = HrotIngress.Resolve(ctx, _maps.RprHandle, in firing, create: false, out var shooter);
            if (resolution == EntityResolution.Echo)
            {
                return; // a fire of one of our own hrot entities, looped back
            }

            if (resolution == EntityResolution.Existing)
            {
                bag.Set(_schema.EventFiring, AttrValue.From(shooter));
            }
        }

        if (m.TargetObject is { } targeted && _maps.RprHandle.TryResolve(ctx.Changes, targeted, out var target) && ctx.State.Find(target) is not null)
        {
            bag.Set(_schema.EventTarget, AttrValue.From(target));
        }

        if (m.MunitionObject is { } munitionObject && _maps.RprHandle.TryResolve(ctx.Changes, munitionObject, out var munition) &&
            ctx.State.Find(munition) is not null)
        {
            bag.Set(_schema.EventMunitionEntity, AttrValue.From(munition));
        }

        bag.Set(_schema.EventMunitionType, AttrValue.From(RprConversions.PackType(m.Munition)));
        bag.Set(_schema.EventWarhead, AttrValue.From((long)(m.WarheadType ?? 0)));
        bag.Set(_schema.EventFuse, AttrValue.From((long)(m.FuseType ?? 0)));
        bag.Set(_schema.EventQuantity, AttrValue.From((long)m.Quantity));
        bag.Set(_schema.EventRate, AttrValue.From((long)(m.RateOfFire ?? 0)));
        bag.Set(_schema.EventLocation, AttrValue.From(RprConversions.ToVec(m.Location)));
        bag.Set(_schema.EventVelocity, AttrValue.From(m.InitialVelocity is { } v ? RprConversions.ToVec(v) : Vec3.Zero));
        bag.Set(_schema.EventRange, AttrValue.From((double)(m.FireControlSolutionRange ?? 0f)));
        ctx.Publish(_schema.WeaponFire, bag);
    }
}

/// <summary>
/// RprMunitionDetonation: publishes a <c>MunitionDetonation</c> pivot event of the HLA side. Like <see cref="RprFireIngress"/>, plus
/// the location relative to the target (<c>Event.RelativeLocation</c>), the final velocity and the detonation result (the FOM
/// numbering is the pivot's).
/// </summary>
public sealed class RprDetonationIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;

    public RprDetonationIngress(PivotSchema schema, HrotDisMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(RprDetonationIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.EventFiring, _schema.EventTarget, _schema.EventMunitionEntity, _schema.EventMunitionType, _schema.EventWarhead,
            _schema.EventFuse, _schema.EventQuantity, _schema.EventRate, _schema.EventLocation, _schema.EventRelativeLocation,
            _schema.EventVelocity, _schema.EventResult);
        setup.Produces(_schema.MunitionDetonation);
        setup.OnMessage<RprMunitionDetonation>(Handle);
    }

    private void Handle(in RprMunitionDetonation m, IngressContext ctx)
    {
        if (!RprConversions.IsFinite(m.Location) || (m.FinalVelocity is { } fv && !RprConversions.IsFinite(fv)) ||
            (m.RelativeDetonationLocation is { } rl && !RprConversions.IsFinite(rl)))
        {
            ctx.Abort("detonation: a value is not finite");
            return;
        }

        var bag = ctx.RentPayload();
        if (m.FiringObject is { } firing)
        {
            var resolution = HrotIngress.Resolve(ctx, _maps.RprHandle, in firing, create: false, out var shooter);
            if (resolution == EntityResolution.Echo)
            {
                return;
            }

            if (resolution == EntityResolution.Existing)
            {
                bag.Set(_schema.EventFiring, AttrValue.From(shooter));
            }
        }

        if (m.TargetObject is { } targeted && _maps.RprHandle.TryResolve(ctx.Changes, targeted, out var target) && ctx.State.Find(target) is not null)
        {
            bag.Set(_schema.EventTarget, AttrValue.From(target));
        }

        if (m.MunitionObject is { } munitionObject && _maps.RprHandle.TryResolve(ctx.Changes, munitionObject, out var munition) &&
            ctx.State.Find(munition) is not null)
        {
            bag.Set(_schema.EventMunitionEntity, AttrValue.From(munition));
        }

        bag.Set(_schema.EventMunitionType, AttrValue.From(RprConversions.PackType(m.Munition)));
        bag.Set(_schema.EventWarhead, AttrValue.From((long)(m.WarheadType ?? 0)));
        bag.Set(_schema.EventFuse, AttrValue.From((long)(m.FuseType ?? 0)));
        bag.Set(_schema.EventQuantity, AttrValue.From(1L));
        bag.Set(_schema.EventRate, AttrValue.From((long)(m.RateOfFire ?? 0)));
        bag.Set(_schema.EventLocation, AttrValue.From(RprConversions.ToVec(m.Location)));
        bag.Set(_schema.EventRelativeLocation, AttrValue.From(m.RelativeDetonationLocation is { } rel ? RprConversions.ToVec(rel) : Vec3.Zero));
        bag.Set(_schema.EventVelocity, AttrValue.From(m.FinalVelocity is { } v ? RprConversions.ToVec(v) : Vec3.Zero));
        bag.Set(_schema.EventResult, AttrValue.From((long)m.Result));
        ctx.Publish(_schema.MunitionDetonation, bag);
    }
}

/// <summary>
/// Rule "Fire": a <c>WeaponFire</c> event of the HROT side as <see cref="RprWeaponFire"/> (DD-009 section 3 item 3). The interaction
/// carries the object handles of shooter and target (null, "no object", when the entity has none), the munition from the pivot and
/// location and velocity in ECEF. Nothing is sent outside an open session and, in a replay, only with
/// <c>shared.sendEventsInReplay</c> (R-087); events of the DIS and HLA sides never come back (R-116).
/// </summary>
public sealed class RprFireEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;
    private readonly IOutboundChannel<IRprMessage> _output;
    private IGatewayContext _gateway = null!;

    public RprFireEgress(PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, HrotDisServices services, IOutboundChannel<IRprMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _sources = sources;
        _services = services;
        _output = output;
    }

    public string Name => nameof(RprFireEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        setup.Consumes(_schema.EventId, _schema.EventFiring, _schema.EventTarget, _schema.EventMunitionEntity, _schema.EventMunitionType,
            _schema.EventWarhead, _schema.EventFuse, _schema.EventQuantity, _schema.EventRate, _schema.EventLocation,
            _schema.EventVelocity, _schema.EventRange);
        setup.OnEvent(_schema.WeaponFire)
            .Named("Fire")
            .When(t => t.Source == _sources.Hrot && _services.HlaEventsAllowed(_gateway.Session))
            .Do(OnFire);
    }

    private void OnFire(EventTrigger t)
    {
        var p = t.Payload;
        var fire = new RprWeaponFire(
            RprEventSupport.EventGuid(_schema, _services, p),
            RprEventSupport.HandleOf(_maps, p, _schema.EventFiring),
            RprEventSupport.HandleOf(_maps, p, _schema.EventTarget),
            RprConversions.ToLocation(RprEventSupport.Vec(p, _schema.EventLocation)),
            RprConversions.ToRprType(p.TryGet(_schema.EventMunitionType, out var type) ? type.AsInt() : 0),
            (int)Math.Clamp(p.TryGet(_schema.EventQuantity, out var q) ? q.AsInt() : 1, 0, ushort.MaxValue))
        {
            FuseType = RprEventSupport.UShort(p, _schema.EventFuse),
            WarheadType = RprEventSupport.UShort(p, _schema.EventWarhead),
            RateOfFire = RprEventSupport.UShort(p, _schema.EventRate),
            InitialVelocity = RprConversions.ToVector(RprEventSupport.Vec(p, _schema.EventVelocity)),
            FireControlSolutionRange = p.TryGet(_schema.EventRange, out var range) ? (float)range.AsDouble() : null,
            MunitionObject = RprEventSupport.HandleOf(_maps, p, _schema.EventMunitionEntity),
        };
        _output.Send(fire);
    }
}

/// <summary>
/// Rule "Detonation": a <c>MunitionDetonation</c> event of the HROT side as <see cref="RprMunitionDetonation"/>. The event guid is the
/// one of the fire it belongs to when the correlator found it (done by the HROT ingress). Gated like <see cref="RprFireEgress"/>.
/// </summary>
public sealed class RprDetonationEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;
    private readonly IOutboundChannel<IRprMessage> _output;
    private IGatewayContext _gateway = null!;

    public RprDetonationEgress(PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, HrotDisServices services, IOutboundChannel<IRprMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _sources = sources;
        _services = services;
        _output = output;
    }

    public string Name => nameof(RprDetonationEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        setup.Consumes(_schema.EventId, _schema.EventFiring, _schema.EventTarget, _schema.EventMunitionEntity, _schema.EventMunitionType,
            _schema.EventWarhead, _schema.EventFuse, _schema.EventRate, _schema.EventLocation, _schema.EventRelativeLocation,
            _schema.EventVelocity, _schema.EventResult);
        setup.OnEvent(_schema.MunitionDetonation)
            .Named("Detonation")
            .When(t => t.Source == _sources.Hrot && _services.HlaEventsAllowed(_gateway.Session))
            .Do(OnDetonation);
    }

    private void OnDetonation(EventTrigger t)
    {
        var p = t.Payload;
        var detonation = new RprMunitionDetonation(
            RprEventSupport.EventGuid(_schema, _services, p),
            RprEventSupport.HandleOf(_maps, p, _schema.EventFiring),
            RprEventSupport.HandleOf(_maps, p, _schema.EventTarget),
            RprConversions.ToLocation(RprEventSupport.Vec(p, _schema.EventLocation)),
            RprConversions.ToRprType(p.TryGet(_schema.EventMunitionType, out var type) ? type.AsInt() : 0),
            RprConversions.ResultOf(p.TryGet(_schema.EventResult, out var result) ? result.AsInt() : DetonationResults.Other))
        {
            FuseType = RprEventSupport.UShort(p, _schema.EventFuse),
            WarheadType = RprEventSupport.UShort(p, _schema.EventWarhead),
            RateOfFire = RprEventSupport.UShort(p, _schema.EventRate),
            FinalVelocity = RprConversions.ToVector(RprEventSupport.Vec(p, _schema.EventVelocity)),
            RelativeDetonationLocation = p.TryGet(_schema.EventRelativeLocation, out var rel) ? RprConversions.ToVector(rel.AsVec3()) : null,
            MunitionObject = RprEventSupport.HandleOf(_maps, p, _schema.EventMunitionEntity),
        };
        _output.Send(detonation);
    }
}
