using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Dis7;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>Builders of the PDUs a DIS peer sends to the gateway (the tests' side of the wire); every one goes through the codec first, like on a real network.</summary>
public static class DisPdus
{
    /// <summary>The identifiers of a foreign DIS application used by the tests.</summary>
    public const ushort PeerSite = 5;
    public const ushort PeerApplication = 77;

    public static T ThroughTheWire<T>(T pdu)
        where T : Pdu
    {
        var result = PduCodec.Decode(PduCodec.Encode(pdu));
        Assert.False(result.HasErrors);
        return (T)result.Items.Single().Pdu!;
    }

    public static EntityStatePdu EntityState(ushort number, Vec3 ecef = default, Vec3 euler = default, Vec3 velocity = default,
        Vec3 acceleration = default, Vec3 angularVelocity = default, int algorithm = 2, int damage = 0, bool deactivated = false,
        string marking = "PEER", byte force = 2, long? type = null, ushort site = PeerSite, ushort application = PeerApplication,
        byte exercise = 1)
    {
        var pdu = new EntityStatePdu
        {
            ExerciseID = exercise,
            Timestamp = DisTimestamp.EncodeRelative(1.0),
            EntityID = DisConversions.ToEntityId(new DisEntityKey(site, application, number)),
            ForceId = force,
            EntityType = DisConversions.ToEntityType(type ?? new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack()),
            EntityLocation = DisConversions.ToDouble(ecef),
            EntityOrientation = DisConversions.ToEuler(euler),
            EntityLinearVelocity = DisConversions.ToFloat(velocity),
            EntityAppearance = DisConversions.AppearanceOf(damage, deactivated),
        };
        EntityMarkingText.Set(pdu.Marking, marking);
        pdu.DeadReckoningParameters.DeadReckoningAlgorithm = (byte)algorithm;
        pdu.DeadReckoningParameters.EntityLinearAcceleration = DisConversions.ToFloat(acceleration);
        pdu.DeadReckoningParameters.EntityAngularVelocity = DisConversions.ToFloat(angularVelocity);
        return ThroughTheWire(pdu);
    }

    public static FirePdu Fire(DisEntityKey shooter, DisEntityKey? target, Vec3 location, Vec3 velocity, long munition, ushort eventNumber = 1,
        ushort eventSite = PeerSite, ushort eventApplication = PeerApplication, ushort warhead = 1000, ushort fuse = 8000, ushort quantity = 1,
        ushort rate = 0, float range = 0f, byte exercise = 1)
    {
        var pdu = new FirePdu
        {
            ExerciseID = exercise,
            Timestamp = DisTimestamp.EncodeRelative(1.0),
            FiringEntityID = DisConversions.ToEntityId(shooter),
            TargetEntityID = target is { } t ? DisConversions.ToEntityId(t) : DisConversions.NoEntity(),
            MunitionExpendibleID = DisConversions.NoEntity(),
            EventID = DisConversions.ToEventIdentifier(new DisEventId(eventSite, eventApplication, eventNumber)),
            LocationInWorldCoordinates = DisConversions.ToDouble(location),
            Velocity = DisConversions.ToFloat(velocity),
            Range = range,
            Descriptor = new MunitionDescriptor
            {
                MunitionType = DisConversions.ToEntityType(munition),
                Warhead = warhead,
                Fuse = fuse,
                Quantity = quantity,
                Rate = rate,
            },
        };
        return ThroughTheWire(pdu);
    }

    public static DetonationPdu Detonation(DisEntityKey shooter, DisEntityKey? target, Vec3 location, Vec3 relative, Vec3 velocity, long munition,
        byte result = 1, ushort eventNumber = 1, ushort eventSite = PeerSite, ushort eventApplication = PeerApplication, byte exercise = 1)
    {
        var pdu = new DetonationPdu
        {
            ExerciseID = exercise,
            Timestamp = DisTimestamp.EncodeRelative(1.0),
            FiringEntityID = DisConversions.ToEntityId(shooter),
            TargetEntityID = target is { } t ? DisConversions.ToEntityId(t) : DisConversions.NoEntity(),
            ExplodingEntityID = DisConversions.NoEntity(),
            EventID = DisConversions.ToEventIdentifier(new DisEventId(eventSite, eventApplication, eventNumber)),
            Velocity = DisConversions.ToFloat(velocity),
            LocationInWorldCoordinates = DisConversions.ToDouble(location),
            LocationOfEntityCoordinates = DisConversions.ToFloat(relative),
            DetonationResult = result,
            Descriptor = new MunitionDescriptor
            {
                MunitionType = DisConversions.ToEntityType(munition),
                Warhead = 1000,
                Fuse = 8000,
                Quantity = 1,
            },
        };
        return ThroughTheWire(pdu);
    }

    public static StartResumePdu StartResume(EntityID? receiving = null, ushort site = PeerSite, ushort application = PeerApplication, byte exercise = 1)
    {
        var pdu = new StartResumePdu
        {
            ExerciseID = exercise,
            OriginatingEntityID = DisConversions.ToEntityId(new DisEntityKey(site, application, 0)),
            ReceivingEntityID = receiving ?? DisConversions.AllEntities(),
            RealWorldTime = DisConversions.ClockOf(10),
            SimulationTime = DisConversions.ClockOf(10),
            RequestID = 1,
        };
        return ThroughTheWire(pdu);
    }

    public static StopFreezePdu StopFreeze(EntityID? receiving = null, ushort site = PeerSite, ushort application = PeerApplication, byte exercise = 1)
    {
        var pdu = new StopFreezePdu
        {
            ExerciseID = exercise,
            OriginatingEntityID = DisConversions.ToEntityId(new DisEntityKey(site, application, 0)),
            ReceivingEntityID = receiving ?? DisConversions.AllEntities(),
            RealWorldTime = DisConversions.ClockOf(10),
            Reason = 1,
            RequestID = 1,
        };
        return ThroughTheWire(pdu);
    }

    public static RemoveEntityPdu RemoveEntity(DisEntityKey receiving, ushort site = PeerSite, ushort application = PeerApplication, byte exercise = 1)
    {
        var pdu = new RemoveEntityPdu
        {
            ExerciseID = exercise,
            OriginatingEntityID = DisConversions.ToEntityId(new DisEntityKey(site, application, 0)),
            ReceivingEntityID = DisConversions.ToEntityId(receiving),
            RequestID = 1,
        };
        return ThroughTheWire(pdu);
    }

    public static EntityID Receiving(ushort site, ushort application, ushort entity = 0) =>
        DisConversions.ToEntityId(new DisEntityKey(site, application, entity));
}
