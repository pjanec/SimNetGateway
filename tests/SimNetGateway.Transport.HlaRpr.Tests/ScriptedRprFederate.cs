using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;
using SimNetGateway.Transport.FakeRti;

namespace SimNetGateway.Transport.HlaRpr.Tests;

/// <summary>What the peer last decoded of one object, attribute by attribute, with how many reflections carried each attribute.</summary>
public sealed class PeerObject
{
    public PeerObject(ObjectInstanceHandle handle, string name, string className)
    {
        Handle = handle;
        Name = name;
        ClassName = className;
    }

    public ObjectInstanceHandle Handle { get; }

    public string Name { get; }

    /// <summary>The class the peer knows the object as (the closest class it subscribed to).</summary>
    public string ClassName { get; }

    public EntityTypeStruct? EntityType { get; set; }
    public EntityIdentifierStruct? EntityIdentifier { get; set; }
    public MarkingStruct? Marking { get; set; }
    public ForceIdentifierEnum8? Force { get; set; }
    public DamageStatusEnum32? Damage { get; set; }
    public SpatialVariantStruct? Spatial { get; set; }
    public AggregateMarkingStruct? AggregateMarking { get; set; }
    public string[]? EntityIdentifiers { get; set; }

    /// <summary>How many reflections carried each attribute (by simple name).</summary>
    public Dictionary<string, int> Carried { get; } = new();

    public int Count(string attribute) => Carried.TryGetValue(attribute, out var n) ? n : 0;
}

/// <summary>A WeaponFire as the peer decoded it with RprFom2 (independently of the transport).</summary>
public sealed record PeerFire(
    EventIdentifierStruct Event, string Firing, string Target, string MunitionObject, EntityTypeStruct Munition,
    WorldLocationStruct Location, ushort Quantity, ushort Rate, FuseTypeEnum16 Fuse, WarheadTypeEnum16 Warhead, float Range,
    uint MissionIndex, VelocityVectorStruct Velocity);

/// <summary>A MunitionDetonation as the peer decoded it.</summary>
public sealed record PeerDetonation(
    EventIdentifierStruct Event, string Firing, string Target, string MunitionObject, EntityTypeStruct Munition,
    WorldLocationStruct Location, ushort Quantity, ushort Rate, FuseTypeEnum16 Fuse, WarheadTypeEnum16 Warhead,
    DetonationResultCodeEnum8 Result, VelocityVectorStruct Velocity, RelativePositionStruct Relative, int ArticulatedPartDataLength);

/// <summary>
/// The test peer (DD-008 section 8): another federate on the same <see cref="FakeRtiHub"/> that encodes and decodes every value
/// itself by calling <see cref="RprFom2"/> directly - it shares no mapping code with the transport under test. Callbacks are
/// recorded and decoded when <see cref="Evoke"/> is called.
/// </summary>
public sealed class ScriptedRprFederate : IFederateAmbassador
{
    private readonly Dictionary<int, PeerObject> _objects = new();
    private readonly AttributeHandle _entityType, _entityIdentifier, _spatial, _marking, _force, _damage;
    private readonly AttributeHandle _aggMarking, _aggForce, _aggIds;
    private readonly Dictionary<string, ParameterHandle> _fireParams = new(), _detParams = new();
    private readonly Dictionary<ParameterHandle, string> _fireNames = new(), _detNames = new();
    private readonly InteractionClassHandle _fireClass, _detClass;
    private readonly Dictionary<ObjectClassHandle, string> _classNames = new();

    public ScriptedRprFederate(FakeRtiHub hub, string federation, string name)
    {
        Name = name;
        Rti = hub.CreateAmbassador();
        Rti.Connect(this);
        try
        {
            Rti.CreateFederationExecution(federation, [RprFom2.ModuleName]);
        }
        catch (FederationExecutionAlreadyExists)
        {
        }

        Rti.JoinFederationExecution(name, "TestPeer", federation);
        var physical = Class(RprFom2.ObjectClasses.PhysicalEntity);
        var aggregate = Class(RprFom2.ObjectClasses.AggregateEntity);
        _entityType = Rti.GetAttributeHandle(physical, "EntityType");
        _entityIdentifier = Rti.GetAttributeHandle(physical, "EntityIdentifier");
        _spatial = Rti.GetAttributeHandle(physical, "Spatial");
        _marking = Rti.GetAttributeHandle(physical, "Marking");
        _force = Rti.GetAttributeHandle(physical, "ForceIdentifier");
        _damage = Rti.GetAttributeHandle(physical, "DamageState");
        _aggMarking = Rti.GetAttributeHandle(aggregate, "AggregateMarking");
        _aggForce = Rti.GetAttributeHandle(aggregate, "ForceIdentifier");
        _aggIds = Rti.GetAttributeHandle(aggregate, "EntityIdentifiers");
        _fireClass = Rti.GetInteractionClassHandle(RprFom2.InteractionClasses.WeaponFire);
        _detClass = Rti.GetInteractionClassHandle(RprFom2.InteractionClasses.MunitionDetonation);
        foreach (var p in typeof(RprFom2.WeaponFireParameters).GetFields().Select(f => (string)f.GetRawConstantValue()!))
        {
            var h = Rti.GetParameterHandle(_fireClass, p);
            _fireParams[p] = h;
            _fireNames[h] = p;
        }

        foreach (var p in typeof(RprFom2.MunitionDetonationParameters).GetFields().Select(f => (string)f.GetRawConstantValue()!))
        {
            var h = Rti.GetParameterHandle(_detClass, p);
            _detParams[p] = h;
            _detNames[h] = p;
        }
    }

    public string Name { get; }

    public FakeRtiAmbassador Rti { get; }

    /// <summary>The objects the peer discovered, in discovery order.</summary>
    public List<PeerObject> Discovered { get; } = new();

    public IEnumerable<PeerObject> Objects => _objects.Values;

    public List<string> RemovedNames { get; } = new();

    public List<PeerFire> Fires { get; } = new();

    public List<PeerDetonation> Detonations { get; } = new();

    /// <summary>Parameter values that did not decode (name of the parameter).</summary>
    public List<string> InteractionDecodeFailures { get; } = new();

    /// <summary>Attribute values that did not decode.</summary>
    public List<string> AttributeDecodeFailures { get; } = new();

    public List<ObjectInstanceHandle> ProvideRequests { get; } = new();

    public ObjectClassHandle Class(string fullName) => Rti.GetObjectClassHandle(fullName);

    // ---- declarations ----------------------------------------------------------------------------------------------

    private AttributeHandleSet PhysicalAttributes() => new([_entityType, _entityIdentifier, _spatial, _marking, _force, _damage]);

    private AttributeHandleSet AggregateAttributes() => new([_entityType, _aggMarking, _aggForce, _aggIds]);

    /// <summary>Subscribes the six physical attributes at each given class.</summary>
    public void SubscribePhysical(params string[] classNames)
    {
        foreach (var c in classNames)
        {
            var handle = Class(c);
            _classNames[handle] = c;
            Rti.SubscribeObjectClassAttributes(handle, PhysicalAttributes());
        }
    }

    public void SubscribeAggregate()
    {
        var handle = Class(RprFom2.ObjectClasses.AggregateEntity);
        _classNames[handle] = RprFom2.ObjectClasses.AggregateEntity;
        Rti.SubscribeObjectClassAttributes(handle, AggregateAttributes());
    }

    public void SubscribeInteractions()
    {
        Rti.SubscribeInteractionClass(_fireClass);
        Rti.SubscribeInteractionClass(_detClass);
    }

    public void PublishPhysical(params string[] classNames)
    {
        foreach (var c in classNames)
        {
            Rti.PublishObjectClassAttributes(Class(c), PhysicalAttributes());
        }
    }

    public void PublishAggregate() => Rti.PublishObjectClassAttributes(Class(RprFom2.ObjectClasses.AggregateEntity), AggregateAttributes());

    public void PublishInteractions()
    {
        Rti.PublishInteractionClass(_fireClass);
        Rti.PublishInteractionClass(_detClass);
    }

    public ObjectInstanceHandle Register(string className) => Rti.RegisterObjectInstance(Class(className));

    public string NameOf(ObjectInstanceHandle handle) => Rti.GetObjectInstanceName(handle);

    // ---- encoding side (the peer is the sender) -----------------------------------------------------------------------

    public sealed class Attributes
    {
        internal readonly Dictionary<string, byte[]> Values = new();

        public Attributes EntityType(EntityTypeStruct v) => Set("EntityType", RprFom2.EncodeEntityType(v));
        public Attributes EntityIdentifier(EntityIdentifierStruct v) => Set("EntityIdentifier", RprFom2.EncodeEntityIdentifier(v));
        public Attributes Marking(string text) => Set("Marking", RprFom2.EncodeMarking(MarkingStruct.FromAscii(text)));
        public Attributes Force(ForceIdentifierEnum8 v) => Set("ForceIdentifier", RprFom2.EncodeForceIdentifier(v));
        public Attributes Damage(DamageStatusEnum32 v) => Set("DamageState", RprFom2.EncodeDamageStatus(v));
        public Attributes Spatial(SpatialVariantStruct v) => Set("Spatial", RprFom2.EncodeSpatial(v));
        public Attributes AggregateMarking(string text) => Set("AggregateMarking", RprFom2.EncodeAggregateMarking(AggregateMarkingStruct.FromAscii(text)));
        public Attributes EntityIdentifiers(params string[] names) => Set("EntityIdentifiers", RprFom2.EncodeRtiObjectIdArray(names));

        /// <summary>A raw (possibly corrupt) value for an attribute.</summary>
        public Attributes Raw(string attribute, byte[] value) => Set(attribute, value);

        private Attributes Set(string name, byte[] value)
        {
            Values[name] = value;
            return this;
        }
    }

    /// <summary>Updates attributes of an object the peer registered; names are simple attribute names.</summary>
    public void Update(ObjectInstanceHandle obj, string registeredClass, Attributes values)
    {
        var map = new AttributeHandleValueMap();
        var cls = Class(registeredClass);
        foreach (var (name, bytes) in values.Values)
        {
            map.Set(Rti.GetAttributeHandle(cls, name), bytes);
        }

        Rti.UpdateAttributeValues(obj, map, Array.Empty<byte>());
    }

    /// <summary>Sends a WeaponFire with the given encoded parameter values (any omitted parameter is absent).</summary>
    public void SendFire(Dictionary<string, byte[]> parameters) => SendInteraction(_fireClass, _fireParams, parameters);

    public void SendDetonation(Dictionary<string, byte[]> parameters) => SendInteraction(_detClass, _detParams, parameters);

    private void SendInteraction(InteractionClassHandle cls, Dictionary<string, ParameterHandle> handles, Dictionary<string, byte[]> parameters)
    {
        var map = new ParameterHandleValueMap();
        foreach (var (name, bytes) in parameters)
        {
            map.Set(handles[name], bytes);
        }

        Rti.SendInteraction(cls, map, Array.Empty<byte>());
    }

    /// <summary>The complete encoded parameter set of a WeaponFire (everything the FOM has).</summary>
    public static Dictionary<string, byte[]> FireParameters(
        ushort count, string issuer, string firing, string target, string munitionObject, EntityTypeStruct munition,
        WorldLocationStruct location, ushort quantity, ushort rate, FuseTypeEnum16 fuse, WarheadTypeEnum16 warhead, float range,
        uint mission, VelocityVectorStruct velocity) => new()
    {
        [RprFom2.WeaponFireParameters.EventIdentifier] = RprFom2.EncodeEventIdentifier(new EventIdentifierStruct(count, issuer)),
        [RprFom2.WeaponFireParameters.FireControlSolutionRange] = RprFom2.EncodeLengthMeterFloat32(range),
        [RprFom2.WeaponFireParameters.FireMissionIndex] = RprFom2.EncodeUnsignedInteger32(mission),
        [RprFom2.WeaponFireParameters.FiringLocation] = RprFom2.EncodeWorldLocation(location),
        [RprFom2.WeaponFireParameters.FiringObjectIdentifier] = RprFom2.EncodeRtiObjectId(firing),
        [RprFom2.WeaponFireParameters.FuseType] = RprFom2.EncodeFuseType(fuse),
        [RprFom2.WeaponFireParameters.InitialVelocityVector] = RprFom2.EncodeVelocityVector(velocity),
        [RprFom2.WeaponFireParameters.MunitionObjectIdentifier] = RprFom2.EncodeRtiObjectId(munitionObject),
        [RprFom2.WeaponFireParameters.MunitionType] = RprFom2.EncodeEntityType(munition),
        [RprFom2.WeaponFireParameters.QuantityFired] = RprFom2.EncodeUnsignedInteger16(quantity),
        [RprFom2.WeaponFireParameters.RateOfFire] = RprFom2.EncodeUnsignedInteger16(rate),
        [RprFom2.WeaponFireParameters.TargetObjectIdentifier] = RprFom2.EncodeRtiObjectId(target),
        [RprFom2.WeaponFireParameters.WarheadType] = RprFom2.EncodeWarheadType(warhead),
    };

    public static Dictionary<string, byte[]> DetonationParameters(
        ushort count, string issuer, string firing, string target, string munitionObject, EntityTypeStruct munition,
        WorldLocationStruct location, ushort quantity, ushort rate, FuseTypeEnum16 fuse, WarheadTypeEnum16 warhead,
        DetonationResultCodeEnum8 result, VelocityVectorStruct velocity, RelativePositionStruct relative) => new()
    {
        [RprFom2.MunitionDetonationParameters.ArticulatedPartData] = RprFom2.EncodeArticulatedParameters([]),
        [RprFom2.MunitionDetonationParameters.DetonationLocation] = RprFom2.EncodeWorldLocation(location),
        [RprFom2.MunitionDetonationParameters.DetonationResultCode] = RprFom2.EncodeDetonationResultCode(result),
        [RprFom2.MunitionDetonationParameters.EventIdentifier] = RprFom2.EncodeEventIdentifier(new EventIdentifierStruct(count, issuer)),
        [RprFom2.MunitionDetonationParameters.FiringObjectIdentifier] = RprFom2.EncodeRtiObjectId(firing),
        [RprFom2.MunitionDetonationParameters.FinalVelocityVector] = RprFom2.EncodeVelocityVector(velocity),
        [RprFom2.MunitionDetonationParameters.FuseType] = RprFom2.EncodeFuseType(fuse),
        [RprFom2.MunitionDetonationParameters.MunitionObjectIdentifier] = RprFom2.EncodeRtiObjectId(munitionObject),
        [RprFom2.MunitionDetonationParameters.MunitionType] = RprFom2.EncodeEntityType(munition),
        [RprFom2.MunitionDetonationParameters.QuantityFired] = RprFom2.EncodeUnsignedInteger16(quantity),
        [RprFom2.MunitionDetonationParameters.RateOfFire] = RprFom2.EncodeUnsignedInteger16(rate),
        [RprFom2.MunitionDetonationParameters.RelativeDetonationLocation] = RprFom2.EncodeRelativePosition(relative),
        [RprFom2.MunitionDetonationParameters.TargetObjectIdentifier] = RprFom2.EncodeRtiObjectId(target),
        [RprFom2.MunitionDetonationParameters.WarheadType] = RprFom2.EncodeWarheadType(warhead),
    };

    /// <summary>Asks the owners of an object to provide all its attributes.</summary>
    public void RequestAll(PeerObject obj) =>
        Rti.RequestAttributeValueUpdate(obj.Handle, PhysicalAttributes(), Array.Empty<byte>());

    // ---- callbacks: recorded and decoded with the codecs ---------------------------------------------------------------

    /// <summary>Delivers everything the RTI has queued for the peer.</summary>
    public void Evoke() => Rti.EvokeMultipleCallbacks(0, 0);

    void IFederateAmbassador.ObjectInstanceNameReservationSucceeded(string objectName)
    {
    }

    void IFederateAmbassador.ObjectInstanceNameReservationFailed(string objectName)
    {
    }

    void IFederateAmbassador.DiscoverObjectInstance(ObjectInstanceHandle theObject, ObjectClassHandle theObjectClass, string objectName)
    {
        var obj = new PeerObject(theObject, objectName, _classNames.TryGetValue(theObjectClass, out var c) ? c : "?");
        _objects[theObject.Value] = obj;
        Discovered.Add(obj);
    }

    void IFederateAmbassador.ReflectAttributeValues(ObjectInstanceHandle theObject, AttributeHandleValueMap theAttributes, byte[] userSuppliedTag)
    {
        var o = _objects[theObject.Value];
        foreach (var (attribute, bytes) in theAttributes)
        {
            string? simple = null;
            if (attribute == _entityType && RprFom2.TryDecodeEntityType(bytes, out var t)) { o.EntityType = t; simple = "EntityType"; }
            else if (attribute == _entityIdentifier && RprFom2.TryDecodeEntityIdentifier(bytes, out var id)) { o.EntityIdentifier = id; simple = "EntityIdentifier"; }
            else if (attribute == _marking && RprFom2.TryDecodeMarking(bytes, out var m)) { o.Marking = m; simple = "Marking"; }
            else if (attribute == _force && RprFom2.TryDecodeForceIdentifier(bytes, out var f)) { o.Force = f; simple = "ForceIdentifier"; }
            else if (attribute == _damage && RprFom2.TryDecodeDamageStatus(bytes, out var d)) { o.Damage = d; simple = "DamageState"; }
            else if (attribute == _spatial && RprFom2.TryDecodeSpatial(bytes, out var s)) { o.Spatial = s; simple = "Spatial"; }
            else if (attribute == _aggMarking && RprFom2.TryDecodeAggregateMarking(bytes, out var am)) { o.AggregateMarking = am; simple = "AggregateMarking"; }
            else if (attribute == _aggForce && RprFom2.TryDecodeForceIdentifier(bytes, out var af)) { o.Force = af; simple = "ForceIdentifier"; }
            else if (attribute == _aggIds && RprFom2.TryDecodeRtiObjectIdArray(bytes, out var names)) { o.EntityIdentifiers = names; simple = "EntityIdentifiers"; }

            if (simple is null)
            {
                AttributeDecodeFailures.Add(attribute.ToString());
            }
            else
            {
                o.Carried[simple] = o.Count(simple) + 1;
            }
        }
    }

    void IFederateAmbassador.RemoveObjectInstance(ObjectInstanceHandle theObject, byte[] userSuppliedTag)
    {
        if (_objects.Remove(theObject.Value, out var o))
        {
            RemovedNames.Add(o.Name);
        }
    }

    void IFederateAmbassador.ProvideAttributeValueUpdate(ObjectInstanceHandle theObject, AttributeHandleSet theAttributes, byte[] userSuppliedTag) =>
        ProvideRequests.Add(theObject);

    void IFederateAmbassador.ReceiveInteraction(InteractionClassHandle interactionClass, ParameterHandleValueMap theParameters, byte[] userSuppliedTag)
    {
        if (interactionClass == _fireClass)
        {
            DecodeFire(theParameters);
        }
        else if (interactionClass == _detClass)
        {
            DecodeDetonation(theParameters);
        }
    }

    private T Get<T>(ParameterHandleValueMap values, Dictionary<string, ParameterHandle> handles, string name, TryDecode<T> decode)
    {
        if (!values.TryGetValue(handles[name], out var bytes) || !decode(bytes, out var value))
        {
            InteractionDecodeFailures.Add(name);
            return default!;
        }

        return value;
    }

    private delegate bool TryDecode<T>(ReadOnlySpan<byte> data, out T value);

    private void DecodeFire(ParameterHandleValueMap v)
    {
        var p = _fireParams;
        Fires.Add(new PeerFire(
            Get<EventIdentifierStruct>(v, p, "EventIdentifier", RprFom2.TryDecodeEventIdentifier),
            Get<string>(v, p, "FiringObjectIdentifier", RprFom2.TryDecodeRtiObjectId),
            Get<string>(v, p, "TargetObjectIdentifier", RprFom2.TryDecodeRtiObjectId),
            Get<string>(v, p, "MunitionObjectIdentifier", RprFom2.TryDecodeRtiObjectId),
            Get<EntityTypeStruct>(v, p, "MunitionType", RprFom2.TryDecodeEntityType),
            Get<WorldLocationStruct>(v, p, "FiringLocation", RprFom2.TryDecodeWorldLocation),
            Get<ushort>(v, p, "QuantityFired", RprFom2.TryDecodeUnsignedInteger16),
            Get<ushort>(v, p, "RateOfFire", RprFom2.TryDecodeUnsignedInteger16),
            Get<FuseTypeEnum16>(v, p, "FuseType", RprFom2.TryDecodeFuseType),
            Get<WarheadTypeEnum16>(v, p, "WarheadType", RprFom2.TryDecodeWarheadType),
            Get<float>(v, p, "FireControlSolutionRange", RprFom2.TryDecodeLengthMeterFloat32),
            Get<uint>(v, p, "FireMissionIndex", RprFom2.TryDecodeUnsignedInteger32),
            Get<VelocityVectorStruct>(v, p, "InitialVelocityVector", RprFom2.TryDecodeVelocityVector)));
    }

    private void DecodeDetonation(ParameterHandleValueMap v)
    {
        var p = _detParams;
        Detonations.Add(new PeerDetonation(
            Get<EventIdentifierStruct>(v, p, "EventIdentifier", RprFom2.TryDecodeEventIdentifier),
            Get<string>(v, p, "FiringObjectIdentifier", RprFom2.TryDecodeRtiObjectId),
            Get<string>(v, p, "TargetObjectIdentifier", RprFom2.TryDecodeRtiObjectId),
            Get<string>(v, p, "MunitionObjectIdentifier", RprFom2.TryDecodeRtiObjectId),
            Get<EntityTypeStruct>(v, p, "MunitionType", RprFom2.TryDecodeEntityType),
            Get<WorldLocationStruct>(v, p, "DetonationLocation", RprFom2.TryDecodeWorldLocation),
            Get<ushort>(v, p, "QuantityFired", RprFom2.TryDecodeUnsignedInteger16),
            Get<ushort>(v, p, "RateOfFire", RprFom2.TryDecodeUnsignedInteger16),
            Get<FuseTypeEnum16>(v, p, "FuseType", RprFom2.TryDecodeFuseType),
            Get<WarheadTypeEnum16>(v, p, "WarheadType", RprFom2.TryDecodeWarheadType),
            Get<DetonationResultCodeEnum8>(v, p, "DetonationResultCode", RprFom2.TryDecodeDetonationResultCode),
            Get<VelocityVectorStruct>(v, p, "FinalVelocityVector", RprFom2.TryDecodeVelocityVector),
            Get<RelativePositionStruct>(v, p, "RelativeDetonationLocation", RprFom2.TryDecodeRelativePosition),
            v.TryGetValue(p["ArticulatedPartData"], out var parts) && RprFom2.TryDecodeArticulatedParameters(parts, out var list) ? list.Length : -1));
    }
}
