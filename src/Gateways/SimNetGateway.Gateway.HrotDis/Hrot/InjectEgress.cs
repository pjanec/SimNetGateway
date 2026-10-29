using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>What the two inject modules read out of a fire or detonation event of the DIS side.</summary>
internal static class InjectSupport
{
    /// <summary>The HROT id of an entity named by the event (0 when the event names none or HROT does not know it) and whether HROT simulates it.</summary>
    public static long HrotIdOf(IGatewayContext gateway, HrotDisMaps maps, HrotDisSources sources, AttributeBag payload, AttrId attr,
        out bool isHrotEntity)
    {
        isHrotEntity = false;
        if (!payload.TryGet(attr, out var value))
        {
            return 0;
        }

        var entity = value.AsEntity();
        if (!maps.HrotEntity.TryGetKey(entity, out long id))
        {
            return 0;
        }

        isHrotEntity = gateway.State.Find(entity) is { } record && record.Origin == sources.Hrot;
        return id;
    }
}

/// <summary>
/// Rule "Fire": a <c>WeaponFire</c> event of the DIS or the HLA side as <see cref="InjectFire"/> (DD-007 3 item 7, R-097). Events of HROT's own
/// side never come back (G-1); nothing is sent while the exercise does not run (not in Edit, paused or replay).
/// </summary>
public sealed class InjectFireEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;
    private readonly IOutboundChannel<IGatewayToHrotMessage> _output;
    private IGatewayContext _gateway = null!;

    public InjectFireEgress(PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, HrotDisServices services,
        IOutboundChannel<IGatewayToHrotMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _sources = sources;
        _services = services;
        _output = output;
    }

    public string Name => nameof(InjectFireEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        setup.Consumes(_schema.EventFiring, _schema.EventTarget, _schema.EventMunitionType, _schema.EventMunitionTkb,
            _schema.EventQuantity, _schema.EventLocation, _schema.EventVelocity);
        setup.OnEvent(_schema.WeaponFire)
            .Named("Fire")
            .When(t => _sources.IsReplicaOrigin(t.Source) && _services.EffectsAllowed(_gateway.Session))
            .Do(OnFire);
    }

    private void OnFire(EventTrigger t)
    {
        var p = t.Payload;
        var geo = _services.Geo;
        long shooter = InjectSupport.HrotIdOf(_gateway, _maps, _sources, p, _schema.EventFiring, out _);
        long target = InjectSupport.HrotIdOf(_gateway, _maps, _sources, p, _schema.EventTarget, out bool targetIsHrot);
        long munitionType = p.TryGet(_schema.EventMunitionType, out var mt) ? mt.AsInt() : 0;
        long tkb = p.TryGet(_schema.EventMunitionTkb, out var m) ? m.AsInt() : ResolveTkb(munitionType);
        var origin = p.TryGet(_schema.EventLocation, out var l) ? geo.PointFromEcef(l.AsVec3()) : default;
        var velocity = p.TryGet(_schema.EventVelocity, out var v) ? geo.VectorFromEcef(v.AsVec3()) : default;
        int quantity = p.TryGet(_schema.EventQuantity, out var q) ? (int)q.AsInt() : 1;
        _output.Send(new InjectFire(shooter, target, tkb, munitionType, origin, velocity, quantity, targetIsHrot));
    }

    private long ResolveTkb(long munitionType)
    {
        if (munitionType != 0 && _services.Munitions.TryDisToTkb(munitionType, out long tkb))
        {
            return tkb;
        }

        if (munitionType != 0 && _services.Munitions.NoteUnknownDis(munitionType))
        {
            _gateway.Diagnostics.Warn(Name, $"DIS munition type {munitionType:X16} has no TKB counterpart; HROT gets TKB {_services.EntityTypes.DefaultTkb}.");
        }

        return _services.EntityTypes.DefaultTkb;
    }
}

/// <summary>
/// Rule "Detonation": a <c>MunitionDetonation</c> event of the DIS side as <see cref="InjectDetonation"/>; with a target that HROT
/// simulates (<c>TargetIsHrot</c>) HROT computes the damage itself, else the detonation is effects only.
/// </summary>
public sealed class InjectDetonationEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;
    private readonly IOutboundChannel<IGatewayToHrotMessage> _output;
    private IGatewayContext _gateway = null!;

    public InjectDetonationEgress(PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, HrotDisServices services,
        IOutboundChannel<IGatewayToHrotMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _sources = sources;
        _services = services;
        _output = output;
    }

    public string Name => nameof(InjectDetonationEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        setup.Consumes(_schema.EventFiring, _schema.EventTarget, _schema.EventMunitionType, _schema.EventMunitionTkb,
            _schema.EventLocation, _schema.EventVelocity);
        setup.OnEvent(_schema.MunitionDetonation)
            .Named("Detonation")
            .When(t => _sources.IsReplicaOrigin(t.Source) && _services.EffectsAllowed(_gateway.Session))
            .Do(OnDetonation);
    }

    private void OnDetonation(EventTrigger t)
    {
        var p = t.Payload;
        var geo = _services.Geo;
        long shooter = InjectSupport.HrotIdOf(_gateway, _maps, _sources, p, _schema.EventFiring, out _);
        long target = InjectSupport.HrotIdOf(_gateway, _maps, _sources, p, _schema.EventTarget, out bool targetIsHrot);
        long munitionType = p.TryGet(_schema.EventMunitionType, out var mt) ? mt.AsInt() : 0;
        long tkb = p.TryGet(_schema.EventMunitionTkb, out var m) ? m.AsInt() : ResolveTkb(munitionType);
        var location = p.TryGet(_schema.EventLocation, out var l) ? geo.PointFromEcef(l.AsVec3()) : default;
        var velocity = p.TryGet(_schema.EventVelocity, out var v) ? geo.VectorFromEcef(v.AsVec3()) : default;
        _output.Send(new InjectDetonation(shooter, target, tkb, munitionType, location, velocity, targetIsHrot));
    }

    private long ResolveTkb(long munitionType)
    {
        if (munitionType != 0 && _services.Munitions.TryDisToTkb(munitionType, out long tkb))
        {
            return tkb;
        }

        if (munitionType != 0 && _services.Munitions.NoteUnknownDis(munitionType))
        {
            _gateway.Diagnostics.Warn(Name, $"DIS munition type {munitionType:X16} has no TKB counterpart; HROT gets TKB {_services.EntityTypes.DefaultTkb}.");
        }

        return _services.EntityTypes.DefaultTkb;
    }
}
