using DISnet;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Dis7;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>What the Fire and Detonation egress modules read out of a pivot event of the HROT side.</summary>
internal static class DisEventSupport
{
    /// <summary>The DIS entity id of the entity an event names, or "no entity" when the event names none or the entity has no DIS id.</summary>
    public static EntityID IdOf(HrotDisMaps maps, AttributeBag payload, AttrId attr) =>
        TryIdOf(maps, payload, attr, out var id) ? id : DisConversions.NoEntity();

    public static bool TryIdOf(HrotDisMaps maps, AttributeBag payload, AttrId attr, out EntityID id)
    {
        if (payload.TryGet(attr, out var value) && maps.DisEntity.TryGetKey(value.AsEntity(), out var key))
        {
            id = DisConversions.ToEntityId(key);
            return true;
        }

        id = DisConversions.NoEntity();
        return false;
    }

    /// <summary>The event identifier the HROT ingress assigned (the correlator links a detonation to its fire there); a new one when missing.</summary>
    public static EventIdentifier EventIdOf(PivotSchema schema, HrotDisServices services, AttributeBag payload) =>
        DisConversions.ToEventIdentifier(
            payload.TryGet(schema.EventId, out var id) ? id.AsObject<DisEventId>() : services.EventIds.Next());

    public static MunitionDescriptor DescriptorOf(PivotSchema schema, AttributeBag payload) => new()
    {
        MunitionType = DisConversions.ToEntityType(payload.TryGet(schema.EventMunitionType, out var type) ? type.AsInt() : 0),
        Warhead = (ushort)Clamp(payload, schema.EventWarhead, 0),
        Fuse = (ushort)Clamp(payload, schema.EventFuse, 0),
        Quantity = (ushort)Clamp(payload, schema.EventQuantity, 1),
        Rate = (ushort)Clamp(payload, schema.EventRate, 0),
    };

    public static Vec3 Vec(AttributeBag payload, AttrId attr) => payload.TryGet(attr, out var v) ? v.AsVec3() : Vec3.Zero;

    private static long Clamp(AttributeBag payload, AttrId attr, long fallback) =>
        Math.Clamp(payload.TryGet(attr, out var v) ? v.AsInt() : fallback, 0, ushort.MaxValue);
}

/// <summary>
/// Rule "Fire": a <c>WeaponFire</c> event of the HROT side as a Fire PDU (DD-006 6.3, R-097). The PDU carries the event id the HROT
/// ingress assigned, the shooter's DIS id (the shooter must have been published to DIS: an event naming an entity DIS does not know
/// is dropped with one warning), the target's DIS id when it has one (a DIS entity's own id, a published HROT entity's gateway id),
/// the munition from the pivot and the location and velocity in ECEF. Nothing is sent outside an open session and, in a replay,
/// only with <c>shared.sendEventsInReplay</c>; events of the DIS side never come back (G-1).
/// </summary>
public sealed class DisFireEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;
    private readonly IOutboundChannel<Pdu> _output;
    private IGatewayContext _gateway = null!;
    private bool _warned;

    public DisFireEgress(PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, HrotDisServices services, IOutboundChannel<Pdu> output)
    {
        _schema = schema;
        _maps = maps;
        _sources = sources;
        _services = services;
        _output = output;
    }

    public string Name => nameof(DisFireEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        setup.Consumes(_schema.EventId, _schema.EventFiring, _schema.EventTarget, _schema.EventMunitionEntity, _schema.EventMunitionType,
            _schema.EventWarhead, _schema.EventFuse, _schema.EventQuantity, _schema.EventRate, _schema.EventLocation,
            _schema.EventVelocity, _schema.EventRange);
        setup.OnEvent(_schema.WeaponFire)
            .Named("Fire")
            .When(t => t.Source == _sources.Hrot && _services.DisEventsAllowed(_gateway.Session))
            .Do(OnFire);
    }

    private void OnFire(EventTrigger t)
    {
        var p = t.Payload;
        if (!DisEventSupport.TryIdOf(_maps, p, _schema.EventFiring, out var shooter))
        {
            if (!_warned)
            {
                _warned = true;
                _gateway.Diagnostics.Warn(Name, "A fire names a shooter that has no DIS entity id (not published to DIS); no Fire PDU is sent for it.");
            }

            return;
        }

        var fire = new FirePdu
        {
            ExerciseID = (byte)_services.Config.Dis!.ExerciseId,
            Timestamp = DisTimestamp.EncodeRelative(_gateway.WallTime),
            FiringEntityID = shooter,
            TargetEntityID = DisEventSupport.IdOf(_maps, p, _schema.EventTarget),
            MunitionExpendibleID = DisEventSupport.IdOf(_maps, p, _schema.EventMunitionEntity),
            EventID = DisEventSupport.EventIdOf(_schema, _services, p),
            FireMissionIndex = 0,
            LocationInWorldCoordinates = DisConversions.ToDouble(DisEventSupport.Vec(p, _schema.EventLocation)),
            Descriptor = DisEventSupport.DescriptorOf(_schema, p),
            Velocity = DisConversions.ToFloat(DisEventSupport.Vec(p, _schema.EventVelocity)),
            Range = p.TryGet(_schema.EventRange, out var range) ? (float)range.AsDouble() : 0f,
        };
        Pdu message = fire;
        _output.Send(in message);
    }
}

/// <summary>
/// Rule "Detonation": a <c>MunitionDetonation</c> event of the HROT side as a Detonation PDU. The event id is the one of the fire it
/// belongs to when the correlator found it (same shooter and munition within 30 s, done by the HROT ingress). The shooter is
/// optional (it may be gone), the target's and munition entity's DIS ids are included when they have one; the result and the
/// location in the target's coordinates come from the event. Gated like <see cref="DisFireEgress"/>.
/// </summary>
public sealed class DisDetonationEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;
    private readonly IOutboundChannel<Pdu> _output;
    private IGatewayContext _gateway = null!;

    public DisDetonationEgress(PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, HrotDisServices services, IOutboundChannel<Pdu> output)
    {
        _schema = schema;
        _maps = maps;
        _sources = sources;
        _services = services;
        _output = output;
    }

    public string Name => nameof(DisDetonationEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        setup.Consumes(_schema.EventId, _schema.EventFiring, _schema.EventTarget, _schema.EventMunitionEntity, _schema.EventMunitionType,
            _schema.EventWarhead, _schema.EventFuse, _schema.EventQuantity, _schema.EventRate, _schema.EventLocation,
            _schema.EventRelativeLocation, _schema.EventVelocity, _schema.EventResult);
        setup.OnEvent(_schema.MunitionDetonation)
            .Named("Detonation")
            .When(t => t.Source == _sources.Hrot && _services.DisEventsAllowed(_gateway.Session))
            .Do(OnDetonation);
    }

    private void OnDetonation(EventTrigger t)
    {
        var p = t.Payload;
        var detonation = new DetonationPdu
        {
            ExerciseID = (byte)_services.Config.Dis!.ExerciseId,
            Timestamp = DisTimestamp.EncodeRelative(_gateway.WallTime),
            FiringEntityID = DisEventSupport.IdOf(_maps, p, _schema.EventFiring),
            TargetEntityID = DisEventSupport.IdOf(_maps, p, _schema.EventTarget),
            ExplodingEntityID = DisEventSupport.IdOf(_maps, p, _schema.EventMunitionEntity),
            EventID = DisEventSupport.EventIdOf(_schema, _services, p),
            Velocity = DisConversions.ToFloat(DisEventSupport.Vec(p, _schema.EventVelocity)),
            LocationInWorldCoordinates = DisConversions.ToDouble(DisEventSupport.Vec(p, _schema.EventLocation)),
            Descriptor = DisEventSupport.DescriptorOf(_schema, p),
            LocationOfEntityCoordinates = DisConversions.ToFloat(DisEventSupport.Vec(p, _schema.EventRelativeLocation)),
            DetonationResult = (byte)Math.Clamp(p.TryGet(_schema.EventResult, out var result) ? result.AsInt() : DetonationResults.Other, 0, 255),
        };
        Pdu message = detonation;
        _output.Send(in message);
    }
}
