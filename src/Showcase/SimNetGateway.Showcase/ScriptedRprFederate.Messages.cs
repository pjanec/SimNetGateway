using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;
using SimNetGateway.Transport.HlaRpr;

namespace SimNetGateway.Showcase;

// The message level API of the scripted federate (DD-008 section 12b): the scripts of the showcase, the demo and the viewer
// describe what the RPR side does as RPR messages of the gateway's contract (RprObjectUpdate, RprAggregateEntity, ...);
// Inject turns each into the RTI calls of a real federate - registerObjectInstance, updateAttributeValues with RprFom2 encoded
// values, sendInteraction, deleteObjectInstance. Nothing reaches the gateway except through the RTI.
public sealed partial class ScriptedRprFederate
{
    private readonly Dictionary<uint, InjectedObject> _injectedByScript = new();
    private readonly Dictionary<int, InjectedObject> _injectedByHandle = new();
    private readonly Dictionary<DisEntityId, InjectedObject> _injectedByDis = new();
    private readonly Dictionary<DisEntityId, string> _reservedNames = new();
    private readonly Dictionary<string, ushort> _eventCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, EventIdentifierStruct> _eventKeys = new(); // the identifier a scripted event id was sent with
    private bool _declared;

    /// <summary>An object the peer registered for a script handle.</summary>
    private sealed class InjectedObject
    {
        public InjectedObject(ObjectInstanceHandle handle, string name, string className, bool isAggregate)
        {
            Handle = handle;
            Name = name;
            ClassName = className;
            IsAggregate = isAggregate;
        }

        public ObjectInstanceHandle Handle { get; }

        public string Name { get; }

        public string ClassName { get; }

        public bool IsAggregate { get; }

        // the spatial state, merged from the partial updates (the HLA attribute is one variant record)
        public WorldLocation? Location { get; set; }

        public RprOrientation? Orientation { get; set; }

        public RprVector? Velocity { get; set; }

        public RprVector? Acceleration { get; set; }

        public RprVector? AngularVelocity { get; set; }

        public byte? Algorithm { get; set; }
    }

    /// <summary>Objects registered through <see cref="Inject"/> and not deleted since.</summary>
    public int InjectedObjectCount => _injectedByHandle.Count;

    /// <summary>
    /// Publishes and subscribes what the RPR side of the gateway federation needs (DD-008 section 3.5): the nine entity classes
    /// and the aggregate class with their attributes, both interactions; subscribes the entity classes (what the gateway
    /// publishes) and both interactions. Idempotent.
    /// </summary>
    public void DeclareForGateway()
    {
        if (_declared)
        {
            return;
        }

        _declared = true;
        PublishPhysical(RprHlaClasses.Published.ToArray());
        PublishAggregate();
        PublishInteractions();
        SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        SubscribeInteractions();
    }

    /// <summary>The RTI object instance handle of the object registered for a script handle; null before the first message for it.</summary>
    public RprObjectHandle? ActualHandle(uint scriptHandle) =>
        _injectedByScript.TryGetValue(scriptHandle, out var o) ? new RprObjectHandle((uint)o.Handle.Value) : null;

    /// <summary>The RTI object name of an object by its instance handle value: one this peer registered, or one it discovered; null when unknown.</summary>
    public string? NameOfHandle(uint instanceHandle)
    {
        if (_injectedByHandle.TryGetValue((int)instanceHandle, out var own))
        {
            return own.Name;
        }

        return _objects.TryGetValue((int)instanceHandle, out var found) ? found.Name : null;
    }

    /// <summary>The RTI event identifier a scripted event id was sent with (null before the event was sent).</summary>
    public EventIdentifierStruct? EventIdentifierOf(Guid scriptedId) => _eventKeys.TryGetValue(scriptedId, out var key) ? key : null;

    /// <summary>What the peer decoded of an object it discovered (by instance handle value); null when not discovered or removed.</summary>
    public PeerObject? DiscoveredObject(uint instanceHandle) =>
        _objects.TryGetValue((int)instanceHandle, out var found) ? found : null;

    /// <summary>
    /// Does what a federate does for an RPR message of the gateway's inbound contract. <see cref="RprObjectUpdate"/> registers
    /// the object on its first message (its handle is a SCRIPT handle - the RTI assigns the real one, see
    /// <see cref="ActualHandle"/>) and updates the attributes the message carries; <see cref="RprAggregateEntity"/> likewise
    /// for an aggregate, naming its members by the RTI names of the objects with those DIS identifiers (a member that does not
    /// exist yet gets a reserved name that its later registration takes); <see cref="RprObjectRemoved"/> deletes;
    /// fire and detonation are interactions whose object references are the RTI names of the objects named by handle (a script
    /// handle or an RTI instance handle).
    /// </summary>
    public void Inject(IRprMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        DeclareForGateway();
        switch (message)
        {
            case RprObjectUpdate m: InjectUpdate(m); break;
            case RprAggregateEntity m: InjectAggregate(m); break;
            case RprObjectRemoved m: InjectRemoved(m); break;
            case RprWeaponFire m: InjectFire(m); break;
            case RprMunitionDetonation m: InjectDetonation(m); break;
            default:
                throw new NotSupportedException($"{message.GetType().Name} is not something the RPR side sends over HLA.");
        }
    }

    // ---- objects ------------------------------------------------------------------------------------------------------

    private void InjectUpdate(RprObjectUpdate u)
    {
        var obj = FindOrRegister(u.Handle.Value, u.EntityType, u.EntityIdentifier, isAggregate: false);
        var values = new Attributes();
        if (obj.IsAggregate)
        {
            // an aggregate has no spatial state or identifier here: marking, type and force are its attributes (the keep-alive of a unit is its marking)
            if (u.Marking is { } aggregateMarking)
            {
                values.AggregateMarking(aggregateMarking);
            }

            if (u.EntityType is { } aggregateType)
            {
                values.EntityType(ToStruct(aggregateType));
            }

            if (u.ForceId is { } aggregateForce)
            {
                values.Force((ForceIdentifierEnum8)(byte)aggregateForce);
            }

            if (values.Values.Count > 0)
            {
                Update(obj.Handle, obj.ClassName, values);
            }

            return;
        }

        if (u.EntityType is { } t)
        {
            values.EntityType(ToStruct(t));
        }

        if (u.EntityIdentifier is { } id)
        {
            values.EntityIdentifier(ToStruct(id));
            _injectedByDis[id] = obj;
        }

        if (u.Marking is { } marking)
        {
            values.Marking(marking);
        }

        if (u.ForceId is { } force)
        {
            values.Force((ForceIdentifierEnum8)(byte)force);
        }

        if (u.DamageState is { } damage)
        {
            values.Damage((DamageStatusEnum32)(byte)damage);
        }

        // RPR FOM 2.0 has no damage percentage: u.DamagePercent never leaves the peer

        bool spatial = false;
        if (u.WorldLocation is { } location) { obj.Location = location; spatial = true; }
        if (u.Orientation is { } orientation) { obj.Orientation = orientation; spatial = true; }
        if (u.VelocityVector is { } velocity) { obj.Velocity = velocity; spatial = true; }
        if (u.AccelerationVector is { } acceleration) { obj.Acceleration = acceleration; spatial = true; }
        if (u.AngularVelocity is { } angular) { obj.AngularVelocity = angular; spatial = true; }
        if (u.DeadReckoningAlgorithm is { } algorithm) { obj.Algorithm = algorithm; spatial = true; }
        if (spatial && TryBuildSpatial(obj, out var variant))
        {
            values.Spatial(variant);
        }

        if (values.Values.Count > 0)
        {
            Update(obj.Handle, obj.ClassName, values);
        }
    }

    private void InjectAggregate(RprAggregateEntity a)
    {
        var obj = FindOrRegister(a.Handle.Value, a.EntityType, null, isAggregate: true);
        var members = new string[a.MemberIds.Length];
        for (int i = 0; i < members.Length; i++)
        {
            members[i] = MemberName(a.MemberIds[i]);
        }

        var values = new Attributes()
            .AggregateMarking(a.Marking)
            .EntityType(ToStruct(a.EntityType))
            .Force((ForceIdentifierEnum8)(byte)a.ForceId)
            .EntityIdentifiers(members);
        Update(obj.Handle, obj.ClassName, values);
    }

    /// <summary>The RTI name of the member with a DIS identifier; a member that has no object yet gets a reserved name for its later registration.</summary>
    private string MemberName(DisEntityId id)
    {
        if (_injectedByDis.TryGetValue(id, out var existing))
        {
            return existing.Name;
        }

        if (_reservedNames.TryGetValue(id, out var reserved))
        {
            return reserved;
        }

        string name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Member-{id.Site}-{id.Application}-{id.Entity}");
        Rti.ReserveObjectInstanceName(name);
        _reservedNames[id] = name;
        return name;
    }

    private InjectedObject FindOrRegister(uint scriptHandle, DisEntityType? type, DisEntityId? id, bool isAggregate)
    {
        if (_injectedByScript.TryGetValue(scriptHandle, out var existing))
        {
            return existing;
        }

        string className = isAggregate
            ? RprFom2.ObjectClasses.AggregateEntity
            : type is { } t ? RprHlaClasses.NameFor(t) : RprFom2.ObjectClasses.PhysicalEntity;
        var cls = Class(className);
        ObjectInstanceHandle handle;
        if (id is { } disId && _reservedNames.Remove(disId, out string? reserved))
        {
            handle = Rti.RegisterObjectInstance(cls, reserved);
        }
        else
        {
            handle = Rti.RegisterObjectInstance(cls);
        }

        var created = new InjectedObject(handle, Rti.GetObjectInstanceName(handle), className, isAggregate);
        _injectedByScript[scriptHandle] = created;
        _injectedByHandle[handle.Value] = created;
        return created;
    }

    private void InjectRemoved(RprObjectRemoved r)
    {
        if (!_injectedByScript.Remove(r.Handle.Value, out var obj))
        {
            return; // a federate cannot delete an object it never registered (or already deleted): there is nothing to send
        }

        _injectedByHandle.Remove(obj.Handle.Value);
        foreach (var (id, o) in _injectedByDis.ToList())
        {
            if (ReferenceEquals(o, obj))
            {
                _injectedByDis.Remove(id);
            }
        }

        Rti.DeleteObjectInstance(obj.Handle, Array.Empty<byte>());
    }

    private bool TryBuildSpatial(InjectedObject o, out SpatialVariantStruct spatial)
    {
        spatial = default;
        if (o.Location is not { } location)
        {
            return false;
        }

        // the algorithm of the message when the FOM has an alternative for it (DIS 1 .. 9); else Static without a velocity, FPW with one
        var algorithm = o.Algorithm is >= 1 and <= 9
            ? (DeadReckoningAlgorithmEnum8)o.Algorithm.Value
            : o.Velocity is null ? DeadReckoningAlgorithmEnum8.Static : DeadReckoningAlgorithmEnum8.DRM_FPW;
        var probe = new SpatialVariantStruct(algorithm, default, false, default, default, default, default);
        var orientation = o.Orientation ?? default;
        var velocity = o.Velocity ?? default;
        var acceleration = o.Acceleration ?? default;
        var angular = o.AngularVelocity ?? default;
        spatial = new SpatialVariantStruct(
            algorithm,
            new WorldLocationStruct(location.X, location.Y, location.Z),
            false,
            new OrientationStruct((float)orientation.Psi, (float)orientation.Theta, (float)orientation.Phi),
            probe.HasVelocity ? new VelocityVectorStruct((float)velocity.X, (float)velocity.Y, (float)velocity.Z) : default,
            probe.HasAcceleration ? new AccelerationVectorStruct((float)acceleration.X, (float)acceleration.Y, (float)acceleration.Z) : default,
            probe.HasAngularVelocity ? new AngularVelocityVectorStruct((float)angular.X, (float)angular.Y, (float)angular.Z) : default);
        return true;
    }

    // ---- interactions -------------------------------------------------------------------------------------------------

    private void InjectFire(RprWeaponFire f)
    {
        string firing = NameOfReference(f.FiringObject);
        var key = KeyFor(f.EventId, firing);
        var parameters = FireParameters(
            key.EventCount, key.IssuingObjectIdentifier, firing, NameOfReference(f.TargetObject), NameOfReference(f.MunitionObject),
            ToStruct(f.Munition), ToStruct(f.Location), (ushort)Math.Clamp(f.Quantity, 0, ushort.MaxValue), f.RateOfFire ?? 0,
            FuseOf(f.FuseType), WarheadOf(f.WarheadType), f.FireControlSolutionRange ?? 0f, 0, ToVelocity(f.InitialVelocity));
        SendFire(parameters);
    }

    private void InjectDetonation(RprMunitionDetonation d)
    {
        string firing = NameOfReference(d.FiringObject);
        var key = KeyFor(d.EventId, firing);
        var parameters = DetonationParameters(
            key.EventCount, key.IssuingObjectIdentifier, firing, NameOfReference(d.TargetObject), NameOfReference(d.MunitionObject),
            ToStruct(d.Munition), ToStruct(d.Location), 1, d.RateOfFire ?? 0, FuseOf(d.FuseType), WarheadOf(d.WarheadType),
            (DetonationResultCodeEnum8)(byte)d.Result, ToVelocity(d.FinalVelocity), ToRelative(d.RelativeDetonationLocation));
        SendDetonation(parameters);
    }

    /// <summary>The event identifier a scripted event id is sent with: a new count of the issuing object the first time, the same afterwards (a detonation of a fire).</summary>
    private EventIdentifierStruct KeyFor(Guid scripted, string issuer)
    {
        if (_eventKeys.TryGetValue(scripted, out var known))
        {
            return known;
        }

        // an event without a firing object is issued by the federate itself: two federates must not share an event identifier
        string issuingObject = issuer.Length > 0 ? issuer : Name;
        ushort count = _eventCounts.TryGetValue(issuingObject, out var last) ? (ushort)(last + 1) : (ushort)1;
        _eventCounts[issuingObject] = count;
        var key = new EventIdentifierStruct(count, issuingObject);
        _eventKeys[scripted] = key;
        return key;
    }

    /// <summary>
    /// The RTI name of an object reference: a script handle of an object this peer registered, else an RTI instance handle of an
    /// object it registered or discovered; empty ("no object") when null or unknown.
    /// </summary>
    private string NameOfReference(RprObjectHandle? handle)
    {
        if (handle is not { } h)
        {
            return string.Empty;
        }

        if (_injectedByScript.TryGetValue(h.Value, out var scripted))
        {
            return scripted.Name;
        }

        return NameOfHandle(h.Value) ?? string.Empty;
    }

    private static EntityTypeStruct ToStruct(DisEntityType t) =>
        new(t.Kind, t.Domain, t.Country, t.Category, t.Subcategory, t.Specific, t.Extra);

    private static EntityIdentifierStruct ToStruct(DisEntityId id) =>
        new(new FederateIdentifierStruct(id.Site, id.Application), id.Entity);

    private static WorldLocationStruct ToStruct(WorldLocation l) => new(l.X, l.Y, l.Z);

    private static VelocityVectorStruct ToVelocity(RprVector? v) =>
        v is { } x ? new VelocityVectorStruct((float)x.X, (float)x.Y, (float)x.Z) : default;

    private static RelativePositionStruct ToRelative(RprVector? v) =>
        v is { } x ? new RelativePositionStruct((float)x.X, (float)x.Y, (float)x.Z) : default;

    private static FuseTypeEnum16 FuseOf(ushort? code) =>
        code is { } c && Enum.IsDefined((FuseTypeEnum16)c) ? (FuseTypeEnum16)c : FuseTypeEnum16.Other;

    private static WarheadTypeEnum16 WarheadOf(ushort? code) =>
        code is { } c && Enum.IsDefined((WarheadTypeEnum16)c) ? (WarheadTypeEnum16)c : WarheadTypeEnum16.Other;
}
