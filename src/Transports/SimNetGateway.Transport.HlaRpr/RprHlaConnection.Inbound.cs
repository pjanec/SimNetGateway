using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.HlaRpr;

// The RTI -> gateway direction (DD-008 sections 3.4, 5.2 - 5.4): callbacks only enqueue; Pump processes the queue.
public sealed partial class RprHlaConnection
{
    private delegate bool Decoder<T>(ReadOnlySpan<byte> data, out T value);

    private abstract record CallbackEvent;

    private sealed record DiscoverEvent(ObjectInstanceHandle Instance, ObjectClassHandle Class, string Name) : CallbackEvent;

    private sealed record ReflectEvent(ObjectInstanceHandle Instance, AttributeHandleValueMap Values) : CallbackEvent;

    private sealed record RemoveEvent(ObjectInstanceHandle Instance) : CallbackEvent;

    private sealed record InteractionEvent(InteractionClassHandle Class, ParameterHandleValueMap Values) : CallbackEvent;

    private sealed record ProvideEvent(ObjectInstanceHandle Instance) : CallbackEvent;

    // ---- IFederateAmbassador: only enqueue (R-018) -------------------------------------------------------------------

    void IFederateAmbassador.ObjectInstanceNameReservationSucceeded(string objectName)
    {
        // names are RTI generated (R-104): no reservation is ever requested
    }

    void IFederateAmbassador.ObjectInstanceNameReservationFailed(string objectName)
    {
    }

    void IFederateAmbassador.DiscoverObjectInstance(ObjectInstanceHandle theObject, ObjectClassHandle theObjectClass, string objectName) =>
        _events.Enqueue(new DiscoverEvent(theObject, theObjectClass, objectName));

    void IFederateAmbassador.ReflectAttributeValues(ObjectInstanceHandle theObject, AttributeHandleValueMap theAttributes, byte[] userSuppliedTag) =>
        _events.Enqueue(new ReflectEvent(theObject, theAttributes));

    void IFederateAmbassador.RemoveObjectInstance(ObjectInstanceHandle theObject, byte[] userSuppliedTag) =>
        _events.Enqueue(new RemoveEvent(theObject));

    void IFederateAmbassador.ReceiveInteraction(InteractionClassHandle interactionClass, ParameterHandleValueMap theParameters, byte[] userSuppliedTag) =>
        _events.Enqueue(new InteractionEvent(interactionClass, theParameters));

    void IFederateAmbassador.ProvideAttributeValueUpdate(ObjectInstanceHandle theObject, AttributeHandleSet theAttributes, byte[] userSuppliedTag) =>
        _events.Enqueue(new ProvideEvent(theObject));

    // ---- processing, inside Pump ---------------------------------------------------------------------------------------

    private void Process(CallbackEvent e)
    {
        switch (e)
        {
            case DiscoverEvent d: OnDiscover(d); break;
            case ReflectEvent r: OnReflect(r); break;
            case RemoveEvent r: OnRemove(r); break;
            case InteractionEvent i: OnInteraction(i); break;
            case ProvideEvent p: OnProvide(p); break;
        }
    }

    private void OnDiscover(DiscoverEvent d)
    {
        bool aggregate;
        if (d.Class == _h.PhysicalEntity)
        {
            aggregate = false;
        }
        else if (d.Class == _h.AggregateEntity)
        {
            aggregate = true;
        }
        else
        {
            UnknownObjectCallbacks++; // we subscribed to nothing else
            return;
        }

        var obj = new RemoteObject(d.Instance, d.Name, aggregate);
        _remote[d.Instance.Value] = obj;
        _handleToName[obj.Handle.Value] = d.Name;
        _nameToHandle[d.Name] = obj.Handle.Value;
        // Disco precedent: the attribute values of a newly discovered object are requested, the owner answers with an update
        try
        {
            _rti.RequestAttributeValueUpdate(d.Instance, (aggregate ? _h.AggregateSet : _h.PhysicalSet).Clone(), NoTag);
        }
        catch (RtiException)
        {
            SendErrors++;
        }
    }

    private void OnRemove(RemoveEvent r)
    {
        if (!_remote.Remove(r.Instance.Value, out var obj))
        {
            UnknownObjectCallbacks++;
            return;
        }

        _handleToName.Remove(obj.Handle.Value);
        _nameToHandle.Remove(obj.Name);
        if (obj.EntityId is not null)
        {
            _entityIdByName.Remove(obj.Name);
        }

        _inbound.Enqueue(new RprObjectRemoved(obj.Handle));
    }

    private void OnProvide(ProvideEvent p)
    {
        if (_own.ContainsKey((uint)p.Instance.Value))
        {
            // several federates (or one, twice) asking before the engine drains: one request is enough, the answer is a full update
            if (_pendingRequests.Add((uint)p.Instance.Value))
            {
                _inbound.Enqueue(new RprUpdateRequested(new RprObjectHandle((uint)p.Instance.Value)));
            }
        }
        else
        {
            UnknownObjectCallbacks++;
        }
    }

    // ---- reflections ---------------------------------------------------------------------------------------------------

    private void OnReflect(ReflectEvent r)
    {
        if (!_remote.TryGetValue(r.Instance.Value, out var obj))
        {
            UnknownObjectCallbacks++;
            return;
        }

        if (obj.IsAggregate)
        {
            ReflectAggregate(obj, r.Values);
        }
        else
        {
            ReflectPhysical(obj, r.Values);
        }
    }

    /// <summary>
    /// The update carries exactly the attributes that decoded: an attribute that does not decode is counted and left out, the
    /// others are delivered (DD-008 section 5.4). A Spatial brings the fields its dead reckoning algorithm carries.
    /// </summary>
    private void ReflectPhysical(RemoteObject obj, AttributeHandleValueMap values)
    {
        string? marking = null;
        DisEntityType? type = null;
        RprForceId? force = null;
        DisEntityId? identifier = null;
        RprDamageState? damage = null;
        WorldLocation? location = null;
        RprOrientation? orientation = null;
        RprVector? velocity = null;
        RprVector? acceleration = null;
        RprVector? angular = null;
        byte? algorithm = null;
        bool any = false;

        foreach (var (attribute, bytes) in values)
        {
            if (attribute == _h.Spatial)
            {
                if (RprFom2.TryDecodeSpatial(bytes, out var s))
                {
                    location = new WorldLocation(s.WorldLocation.X, s.WorldLocation.Y, s.WorldLocation.Z);
                    orientation = new RprOrientation(s.Orientation.Psi, s.Orientation.Theta, s.Orientation.Phi);
                    algorithm = (byte)s.Algorithm;
                    velocity = s.HasVelocity ? new RprVector(s.Velocity.X, s.Velocity.Y, s.Velocity.Z) : null;
                    acceleration = s.HasAcceleration ? new RprVector(s.Acceleration.X, s.Acceleration.Y, s.Acceleration.Z) : null;
                    angular = s.HasAngularVelocity ? new RprVector(s.AngularVelocity.X, s.AngularVelocity.Y, s.AngularVelocity.Z) : null;
                    any = true;
                }
                else
                {
                    DecodeErrors++;
                }
            }
            else if (attribute == _h.EntityType)
            {
                if (RprFom2.TryDecodeEntityType(bytes, out var t))
                {
                    type = ToDis(t);
                    any = true;
                }
                else
                {
                    DecodeErrors++;
                }
            }
            else if (attribute == _h.EntityIdentifier)
            {
                if (RprFom2.TryDecodeEntityIdentifier(bytes, out var id))
                {
                    identifier = new DisEntityId(id.FederateIdentifier.SiteId, id.FederateIdentifier.ApplicationId, id.EntityNumber);
                    any = true;
                }
                else
                {
                    DecodeErrors++;
                }
            }
            else if (attribute == _h.Marking)
            {
                if (RprFom2.TryDecodeMarking(bytes, out var m))
                {
                    marking = m.AsciiText;
                    any = true;
                }
                else
                {
                    DecodeErrors++;
                }
            }
            else if (attribute == _h.Force)
            {
                if (RprFom2.TryDecodeForceIdentifier(bytes, out var f))
                {
                    force = BaseForce(f);
                    any = true;
                }
                else
                {
                    DecodeErrors++;
                }
            }
            else if (attribute == _h.Damage)
            {
                if (RprFom2.TryDecodeDamageStatus(bytes, out var d))
                {
                    damage = (RprDamageState)(byte)d;
                    any = true;
                }
                else
                {
                    DecodeErrors++;
                }
            }
        }

        if (!any)
        {
            return;
        }

        _inbound.Enqueue(new RprObjectUpdate(obj.Handle)
        {
            Marking = marking,
            EntityType = type,
            ForceId = force,
            EntityIdentifier = identifier,
            WorldLocation = location,
            Orientation = orientation,
            VelocityVector = velocity,
            AccelerationVector = acceleration,
            AngularVelocity = angular,
            DeadReckoningAlgorithm = algorithm,
            DamageState = damage,
        });

        // after the member's own update, so that an aggregate that names it is emitted once the gateway knows the member
        if (identifier is { } known)
        {
            LearnIdentifier(obj, known);
        }
    }

    private void ReflectAggregate(RemoteObject obj, AttributeHandleValueMap values)
    {
        bool any = false;
        foreach (var (attribute, bytes) in values)
        {
            if (attribute == _h.AggregateMarking)
            {
                if (RprFom2.TryDecodeAggregateMarking(bytes, out var m))
                {
                    obj.Marking = m.AsciiText;
                    any = true;
                }
                else
                {
                    DecodeErrors++;
                }
            }
            else if (attribute == _h.EntityType)
            {
                if (RprFom2.TryDecodeEntityType(bytes, out var t))
                {
                    obj.EntityType = ToDis(t);
                    any = true;
                }
                else
                {
                    DecodeErrors++;
                }
            }
            else if (attribute == _h.AggregateForce)
            {
                if (RprFom2.TryDecodeForceIdentifier(bytes, out var f))
                {
                    obj.Force = BaseForce(f);
                    any = true;
                }
                else
                {
                    DecodeErrors++;
                }
            }
            else if (attribute == _h.AggregateEntityIdentifiers)
            {
                if (RprFom2.TryDecodeRtiObjectIdArray(bytes, out var names))
                {
                    obj.MemberNames = names;
                    any = true;
                }
                else
                {
                    DecodeErrors++;
                }
            }
        }

        if (any)
        {
            EmitAggregate(obj);
        }
    }

    /// <summary>
    /// The aggregate as the gateway wants it: the member list maps the member object names to the DIS entity ids learnt from the
    /// members' reflected EntityIdentifier (a member not known yet is left out and the aggregate is emitted again when it is learnt).
    /// </summary>
    private void EmitAggregate(RemoteObject aggregate)
    {
        var members = new List<DisEntityId>();
        if (aggregate.MemberNames is { } names)
        {
            foreach (var name in names)
            {
                if (_entityIdByName.TryGetValue(name, out var id))
                {
                    members.Add(id);
                }
            }
        }

        _inbound.Enqueue(new RprAggregateEntity(
            aggregate.Handle,
            aggregate.Marking ?? string.Empty,
            aggregate.EntityType ?? default,
            aggregate.Force ?? RprForceId.Other,
            members.ToArray()));
    }

    private void LearnIdentifier(RemoteObject obj, DisEntityId id)
    {
        if (obj.EntityId == id)
        {
            return;
        }

        obj.EntityId = id;
        _entityIdByName[obj.Name] = id;
        foreach (var other in _remote.Values)
        {
            if (other.IsAggregate && other.MemberNames is { } names && Array.IndexOf(names, obj.Name) >= 0)
            {
                EmitAggregate(other);
            }
        }
    }

    private static DisEntityType ToDis(EntityTypeStruct t) =>
        new(t.EntityKind, t.Domain, t.CountryCode, t.Category, t.Subcategory, t.Specific, t.Extra);

    /// <summary>ForceIdentifierEnum8 values above 3 (Friendly_2, Opposing_2, Neutral_2, Friendly_3 ...) map to their base force.</summary>
    private static RprForceId BaseForce(ForceIdentifierEnum8 force)
    {
        int v = (byte)force;
        return v <= 3 ? (RprForceId)v : (RprForceId)(((v - 4) % 3) + 1);
    }

    // ---- interactions --------------------------------------------------------------------------------------------------

    private void OnInteraction(InteractionEvent e)
    {
        if (e.Class == _h.WeaponFire)
        {
            ReceiveFire(e.Values);
        }
        else if (e.Class == _h.Detonation)
        {
            ReceiveDetonation(e.Values);
        }
        else
        {
            UnknownObjectCallbacks++;
        }
    }

    private void ReceiveFire(ParameterHandleValueMap values)
    {
        var p = _h.FireParameters;
        var eventId = EventIdOf(values, p[RprFom2.WeaponFireParameters.EventIdentifier]);
        Read(values, p[RprFom2.WeaponFireParameters.FiringLocation], RprFom2.TryDecodeWorldLocation, out WorldLocationStruct location);
        Read(values, p[RprFom2.WeaponFireParameters.MunitionType], RprFom2.TryDecodeEntityType, out EntityTypeStruct munition);
        Read(values, p[RprFom2.WeaponFireParameters.QuantityFired], RprFom2.TryDecodeUnsignedInteger16, out ushort quantity);
        bool hasFuse = Read(values, p[RprFom2.WeaponFireParameters.FuseType], RprFom2.TryDecodeFuseType, out FuseTypeEnum16 fuse);
        bool hasWarhead = Read(values, p[RprFom2.WeaponFireParameters.WarheadType], RprFom2.TryDecodeWarheadType, out WarheadTypeEnum16 warhead);
        bool hasRate = Read(values, p[RprFom2.WeaponFireParameters.RateOfFire], RprFom2.TryDecodeUnsignedInteger16, out ushort rate);
        bool hasVelocity = Read(values, p[RprFom2.WeaponFireParameters.InitialVelocityVector], RprFom2.TryDecodeVelocityVector, out VelocityVectorStruct velocity);
        bool hasRange = Read(values, p[RprFom2.WeaponFireParameters.FireControlSolutionRange], RprFom2.TryDecodeLengthMeterFloat32, out float range);

        _inbound.Enqueue(new RprWeaponFire(
            eventId,
            ObjectOf(values, p[RprFom2.WeaponFireParameters.FiringObjectIdentifier]),
            ObjectOf(values, p[RprFom2.WeaponFireParameters.TargetObjectIdentifier]),
            new WorldLocation(location.X, location.Y, location.Z),
            ToDis(munition),
            quantity)
        {
            FuseType = hasFuse ? (ushort)fuse : null,
            WarheadType = hasWarhead ? (ushort)warhead : null,
            RateOfFire = hasRate ? rate : null,
            InitialVelocity = hasVelocity ? new RprVector(velocity.X, velocity.Y, velocity.Z) : null,
            FireControlSolutionRange = hasRange ? range : null,
            MunitionObject = ObjectOf(values, p[RprFom2.WeaponFireParameters.MunitionObjectIdentifier]),
        });
    }

    private void ReceiveDetonation(ParameterHandleValueMap values)
    {
        var p = _h.DetonationParameters;
        var eventId = EventIdOf(values, p[RprFom2.MunitionDetonationParameters.EventIdentifier]);
        Read(values, p[RprFom2.MunitionDetonationParameters.DetonationLocation], RprFom2.TryDecodeWorldLocation, out WorldLocationStruct location);
        Read(values, p[RprFom2.MunitionDetonationParameters.MunitionType], RprFom2.TryDecodeEntityType, out EntityTypeStruct munition);
        var result = RprDetonationResult.Other;
        if (Read(values, p[RprFom2.MunitionDetonationParameters.DetonationResultCode], RprFom2.TryDecodeDetonationResultCode, out DetonationResultCodeEnum8 code) &&
            (byte)code <= (byte)RprDetonationResult.None)
        {
            result = (RprDetonationResult)(byte)code; // FOM codes above None have no RprDetonationResult: Other
        }

        bool hasFuse = Read(values, p[RprFom2.MunitionDetonationParameters.FuseType], RprFom2.TryDecodeFuseType, out FuseTypeEnum16 fuse);
        bool hasWarhead = Read(values, p[RprFom2.MunitionDetonationParameters.WarheadType], RprFom2.TryDecodeWarheadType, out WarheadTypeEnum16 warhead);
        bool hasRate = Read(values, p[RprFom2.MunitionDetonationParameters.RateOfFire], RprFom2.TryDecodeUnsignedInteger16, out ushort rate);
        bool hasVelocity = Read(values, p[RprFom2.MunitionDetonationParameters.FinalVelocityVector], RprFom2.TryDecodeVelocityVector, out VelocityVectorStruct velocity);
        bool hasRelative = Read(values, p[RprFom2.MunitionDetonationParameters.RelativeDetonationLocation], RprFom2.TryDecodeRelativePosition, out RelativePositionStruct relative);

        _inbound.Enqueue(new RprMunitionDetonation(
            eventId,
            ObjectOf(values, p[RprFom2.MunitionDetonationParameters.FiringObjectIdentifier]),
            ObjectOf(values, p[RprFom2.MunitionDetonationParameters.TargetObjectIdentifier]),
            new WorldLocation(location.X, location.Y, location.Z),
            ToDis(munition),
            result)
        {
            FuseType = hasFuse ? (ushort)fuse : null,
            WarheadType = hasWarhead ? (ushort)warhead : null,
            RateOfFire = hasRate ? rate : null,
            FinalVelocity = hasVelocity ? new RprVector(velocity.X, velocity.Y, velocity.Z) : null,
            RelativeDetonationLocation = hasRelative ? new RprVector(relative.BodyX, relative.BodyY, relative.BodyZ) : null,
            MunitionObject = ObjectOf(values, p[RprFom2.MunitionDetonationParameters.MunitionObjectIdentifier]),
        });
    }

    /// <summary>
    /// The Guid of the event: the same (event count, issuing object name) is the same Guid for 30 s. An identifier that is
    /// missing or does not decode cannot be correlated: the event gets a Guid of its own.
    /// </summary>
    private Guid EventIdOf(ParameterHandleValueMap values, ParameterHandle parameter)
    {
        if (Read(values, parameter, RprFom2.TryDecodeEventIdentifier, out EventIdentifierStruct id))
        {
            return _correlation.ForInbound(new EventKey(id.EventCount, id.IssuingObjectIdentifier), _clock.Now);
        }

        return _newEventId();
    }

    /// <summary>The handle of the object an RTIobjectId parameter names; null when the parameter is absent, empty, undecodable or names an object we do not know.</summary>
    private RprObjectHandle? ObjectOf(ParameterHandleValueMap values, ParameterHandle parameter)
    {
        if (Read(values, parameter, RprFom2.TryDecodeRtiObjectId, out string name) && name.Length > 0 &&
            _nameToHandle.TryGetValue(name, out var handle))
        {
            return new RprObjectHandle(handle);
        }

        return null;
    }

    /// <summary>Decodes one parameter: false when it is absent (not an error) or does not decode (counted, the value is default).</summary>
    private bool Read<TValue>(ParameterHandleValueMap values, ParameterHandle parameter, Decoder<TValue> decode, out TValue value)
    {
        value = default!;
        if (!values.TryGetValue(parameter, out var bytes))
        {
            return false;
        }

        if (decode(bytes, out value))
        {
            return true;
        }

        DecodeErrors++;
        value = default!;
        return false;
    }
}
