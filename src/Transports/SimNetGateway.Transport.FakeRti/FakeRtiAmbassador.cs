using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.FakeRti;

/// <summary>
/// One federate's view of the <see cref="FakeRtiHub"/>: implements <see cref="IRtiAmbassador"/> with the semantics table of
/// DD-008 section 3.6. Precondition order of every service: called from inside a callback
/// (<see cref="CallNotAllowedFromWithinCallback"/>), not connected (<see cref="NotConnected"/>), not a federation member
/// (<see cref="FederateNotExecutionMember"/>), then the checks of the service in the order its comments list them. A service
/// that throws has no effect.
/// </summary>
public sealed class FakeRtiAmbassador : IRtiAmbassador
{
    private enum Need
    {
        /// <summary>Only the callback check (Connect).</summary>
        Nothing,

        /// <summary>A connection.</summary>
        Connected,

        /// <summary>A connection and membership of a federation execution.</summary>
        Member,
    }

    private sealed record PendingCallback(string Text, Action<IFederateAmbassador> Invoke);

    private readonly FakeRtiHub _hub;
    private readonly Queue<PendingCallback> _queue = new();
    private IFederateAmbassador? _callbacks;
    private bool _inCallback;
    private FakeFederation? _federation;
    private FakeFederate? _self;

    internal FakeRtiAmbassador(FakeRtiHub hub, int id)
    {
        _hub = hub;
        Id = id;
    }

    /// <summary>Creation number within the hub, 1-based; the "A<c>n</c>" of the trace.</summary>
    public int Id { get; }

    public bool IsConnected => _callbacks is not null;

    /// <summary>The handle of this federate while it is joined.</summary>
    public FederateHandle? FederateHandle => _self is null ? null : new FederateHandle(_self.Handle);

    /// <summary>Callbacks waiting for an evoke (test hook).</summary>
    public int PendingCallbackCount => _queue.Count;

    // --- federation management ---

    public void Connect(IFederateAmbassador federateAmbassador)
    {
        ArgumentNullException.ThrowIfNull(federateAmbassador);
        Run("Connect", Need.Nothing, () =>
        {
            if (_callbacks is not null)
            {
                throw new AlreadyConnected("already connected");
            }
            _callbacks = federateAmbassador;
        });
    }

    public void CreateFederationExecution(string federationExecutionName, IReadOnlyList<string> fomModules)
    {
        ArgumentNullException.ThrowIfNull(federationExecutionName);
        ArgumentNullException.ThrowIfNull(fomModules);
        Run($"CreateFederationExecution({federationExecutionName}, [{string.Join(",", fomModules)}])", Need.Connected, () =>
        {
            if (_hub.Find(federationExecutionName) is not null)
            {
                throw new FederationExecutionAlreadyExists(federationExecutionName);
            }
            var models = fomModules.Select(_hub.ResolveModule).ToList(); // NameNotFound for an unknown module
            var merged = new FomModelBuilder().Build();
            foreach (var model in models)
            {
                merged = merged.Merge(model); // InconsistentFdd for conflicting modules
            }
            _hub.Add(new FakeFederation(federationExecutionName, merged));
        });
    }

    public FederateHandle JoinFederationExecution(string federateName, string federateType, string federationExecutionName)
    {
        ArgumentException.ThrowIfNullOrEmpty(federateName);
        ArgumentNullException.ThrowIfNull(federateType);
        ArgumentNullException.ThrowIfNull(federationExecutionName);
        return Run($"JoinFederationExecution({federateName}, {federateType}, {federationExecutionName})", Need.Connected, () =>
        {
            if (_self is not null)
            {
                throw new FederateAlreadyExecutionMember(federateName);
            }
            var federation = _hub.Find(federationExecutionName)
                ?? throw new FederationExecutionDoesNotExist(federationExecutionName);
            if (federation.Federates.Values.Any(f => f.Name == federateName))
            {
                throw new FederateNameAlreadyInUse(federateName);
            }
            var federate = new FakeFederate(federation.NextFederateHandle++, federateName, federateType, this);
            federation.Federates.Add(federate.Handle, federate);
            _federation = federation;
            _self = federate;
            return new FederateHandle(federate.Handle);
        });
    }

    public void ResignFederationExecution(ResignAction resignAction)
    {
        Run($"ResignFederationExecution({resignAction})", Need.Member, () =>
        {
            var federation = _federation!;
            var self = _self!;
            var owned = federation.Objects.Where(o => o.Alive && o.Owner == self).ToList();
            switch (resignAction)
            {
                case ResignAction.DeleteObjects:
                case ResignAction.DeleteObjectsThenDivest:
                case ResignAction.CancelThenDeleteThenDivest:
                    foreach (var obj in owned)
                    {
                        DeleteInstance(federation, obj, Array.Empty<byte>());
                    }
                    break;
                case ResignAction.UnconditionallyDivestAttributes:
                    foreach (var obj in owned)
                    {
                        obj.Owner = null;
                        obj.Registrant = null;
                        obj.Owned.Clear();
                    }
                    break;
                default: // NoAction, CancelPendingOwnershipAcquisitions: nothing may be left owned
                    if (owned.Count > 0)
                    {
                        throw new FederateOwnsAttributes($"{self.Name} still owns {owned.Count} instance(s)");
                    }
                    break;
            }
            foreach (var obj in federation.Objects.Where(o => o.Alive))
            {
                obj.Known.Remove(self.Handle);
            }
            foreach (var name in federation.Names.Where(e => e.Value.ReservedBy == self && e.Value.Object is null).Select(e => e.Key).ToList())
            {
                federation.Names.Remove(name);
            }
            federation.Federates.Remove(self.Handle);
            _queue.Clear();
            _federation = null;
            _self = null;
        });
    }

    public void DestroyFederationExecution(string federationExecutionName)
    {
        ArgumentNullException.ThrowIfNull(federationExecutionName);
        Run($"DestroyFederationExecution({federationExecutionName})", Need.Connected, () =>
        {
            var federation = _hub.Find(federationExecutionName)
                ?? throw new FederationExecutionDoesNotExist(federationExecutionName);
            if (federation.Federates.Count > 0)
            {
                throw new FederatesCurrentlyJoined($"{federation.Federates.Count} federate(s) joined");
            }
            _hub.Remove(federationExecutionName);
        });
    }

    // --- handles ---

    public ObjectClassHandle GetObjectClassHandle(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Run($"GetObjectClassHandle({name})", Need.Member, () =>
        {
            var cls = _federation!.Model.FindObjectClass(name) ?? throw new NameNotFound(name);
            return _federation.Tables.Handle(cls);
        });
    }

    public AttributeHandle GetAttributeHandle(ObjectClassHandle whichClass, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Run($"GetAttributeHandle({whichClass}, {name})", Need.Member, () =>
        {
            var cls = _federation!.Tables.Class(whichClass) ?? throw new InvalidObjectClassHandle(whichClass.ToString());
            var attribute = cls.FindAttribute(name) ?? throw new NameNotFound(name);
            return _federation.Tables.Handle(attribute);
        });
    }

    public InteractionClassHandle GetInteractionClassHandle(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Run($"GetInteractionClassHandle({name})", Need.Member, () =>
        {
            var cls = _federation!.Model.FindInteractionClass(name) ?? throw new NameNotFound(name);
            return _federation.Tables.Handle(cls);
        });
    }

    public ParameterHandle GetParameterHandle(InteractionClassHandle whichClass, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Run($"GetParameterHandle({whichClass}, {name})", Need.Member, () =>
        {
            var cls = _federation!.Tables.Interaction(whichClass) ?? throw new InvalidInteractionClassHandle(whichClass.ToString());
            var parameter = cls.FindParameter(name) ?? throw new NameNotFound(name);
            return _federation.Tables.Handle(parameter);
        });
    }

    // --- declaration management ---

    public void PublishObjectClassAttributes(ObjectClassHandle theClass, AttributeHandleSet attributeList)
    {
        ArgumentNullException.ThrowIfNull(attributeList);
        Run($"PublishObjectClassAttributes({theClass}, {Format(attributeList)})", Need.Member, () =>
        {
            var cls = _federation!.Tables.Class(theClass) ?? throw new ObjectClassNotDefined(theClass.ToString());
            var attributes = ResolveAttributes(cls, attributeList);
            if (attributes.Count == 0)
            {
                _self!.Published.Remove(cls);
            }
            else
            {
                _self!.Published[cls] = attributes;
            }
        });
    }

    public void UnpublishObjectClass(ObjectClassHandle theClass)
    {
        Run($"UnpublishObjectClass({theClass})", Need.Member, () =>
        {
            var cls = _federation!.Tables.Class(theClass) ?? throw new ObjectClassNotDefined(theClass.ToString());
            _self!.Published.Remove(cls);
        });
    }

    public void SubscribeObjectClassAttributes(ObjectClassHandle theClass, AttributeHandleSet attributeList)
    {
        ArgumentNullException.ThrowIfNull(attributeList);
        Run($"SubscribeObjectClassAttributes({theClass}, {Format(attributeList)})", Need.Member, () =>
        {
            var cls = _federation!.Tables.Class(theClass) ?? throw new ObjectClassNotDefined(theClass.ToString());
            var attributes = ResolveAttributes(cls, attributeList);
            if (attributes.Count == 0)
            {
                _self!.Subscribed.Remove(cls);
            }
            else
            {
                _self!.Subscribed[cls] = attributes;
                DiscoverLate(_federation, _self);
            }
        });
    }

    public void UnsubscribeObjectClass(ObjectClassHandle theClass)
    {
        Run($"UnsubscribeObjectClass({theClass})", Need.Member, () =>
        {
            var cls = _federation!.Tables.Class(theClass) ?? throw new ObjectClassNotDefined(theClass.ToString());
            _self!.Subscribed.Remove(cls);
        });
    }

    public void PublishInteractionClass(InteractionClassHandle theInteraction) =>
        Run($"PublishInteractionClass({theInteraction})", Need.Member, () =>
            _self!.PublishedInteractions.Add(InteractionOf(theInteraction)));

    public void UnpublishInteractionClass(InteractionClassHandle theInteraction) =>
        Run($"UnpublishInteractionClass({theInteraction})", Need.Member, () =>
            _self!.PublishedInteractions.Remove(InteractionOf(theInteraction)));

    public void SubscribeInteractionClass(InteractionClassHandle theClass) =>
        Run($"SubscribeInteractionClass({theClass})", Need.Member, () =>
            _self!.SubscribedInteractions.Add(InteractionOf(theClass)));

    public void UnsubscribeInteractionClass(InteractionClassHandle theClass) =>
        Run($"UnsubscribeInteractionClass({theClass})", Need.Member, () =>
            _self!.SubscribedInteractions.Remove(InteractionOf(theClass)));

    // --- object management ---

    public void ReserveObjectInstanceName(string theObjectName)
    {
        ArgumentNullException.ThrowIfNull(theObjectName);
        Run($"ReserveObjectInstanceName({theObjectName})", Need.Member, () =>
        {
            if (!IsLegalName(theObjectName))
            {
                throw new IllegalName(theObjectName);
            }
            var names = _federation!.Names;
            if (names.ContainsKey(theObjectName))
            {
                Enqueue(this, $"ObjectInstanceNameReservationFailed({theObjectName})",
                    cb => cb.ObjectInstanceNameReservationFailed(theObjectName));
            }
            else
            {
                names[theObjectName] = new NameEntry { ReservedBy = _self };
                Enqueue(this, $"ObjectInstanceNameReservationSucceeded({theObjectName})",
                    cb => cb.ObjectInstanceNameReservationSucceeded(theObjectName));
            }
        });
    }

    public ObjectInstanceHandle RegisterObjectInstance(ObjectClassHandle theClass) =>
        Run($"RegisterObjectInstance({theClass})", Need.Member, () => Register(theClass, null));

    public ObjectInstanceHandle RegisterObjectInstance(ObjectClassHandle theClass, string theObjectName)
    {
        ArgumentNullException.ThrowIfNull(theObjectName);
        return Run($"RegisterObjectInstance({theClass}, {theObjectName})", Need.Member, () => Register(theClass, theObjectName));
    }

    public string GetObjectInstanceName(ObjectInstanceHandle theObject) =>
        Run($"GetObjectInstanceName({theObject})", Need.Member, () => KnownObject(theObject).Name);

    public void UpdateAttributeValues(ObjectInstanceHandle theObject, AttributeHandleValueMap theAttributes, byte[] userSuppliedTag)
    {
        ArgumentNullException.ThrowIfNull(theAttributes);
        ArgumentNullException.ThrowIfNull(userSuppliedTag);
        Run($"UpdateAttributeValues({theObject}, {Format(theAttributes)}, tag {userSuppliedTag.Length}b)", Need.Member, () =>
        {
            var obj = KnownObject(theObject);
            var known = KnownClassOf(obj, _self!);
            var values = new List<(FomAttribute Attribute, byte[] Value)>();
            foreach (var pair in theAttributes)
            {
                var attribute = _federation!.Tables.Attribute(pair.Key);
                if (attribute is null || !known.AllAttributes.Contains(attribute))
                {
                    throw new AttributeNotDefined(pair.Key.ToString());
                }
                values.Add((attribute, pair.Value));
            }
            if (values.Any(v => obj.Owner != _self || !obj.Owned.Contains(v.Attribute)))
            {
                throw new AttributeNotOwned(theObject.ToString());
            }
            foreach (var (federate, knownClass) in obj.Known.Values.ToList())
            {
                if (federate == _self || !federate.Subscribed.TryGetValue(knownClass, out var subscription))
                {
                    continue;
                }
                var filtered = new AttributeHandleValueMap();
                foreach (var (attribute, value) in values)
                {
                    if (subscription.Contains(attribute))
                    {
                        filtered.Set(_federation!.Tables.Handle(attribute), (byte[])value.Clone());
                    }
                }
                if (!filtered.IsEmpty)
                {
                    var tag = (byte[])userSuppliedTag.Clone();
                    Enqueue(federate.Ambassador, $"ReflectAttributeValues({theObject}, {Format(filtered)}, tag {tag.Length}b)",
                        cb => cb.ReflectAttributeValues(theObject, filtered, tag));
                }
            }
        });
    }

    public void DeleteObjectInstance(ObjectInstanceHandle objectHandle, byte[] userSuppliedTag)
    {
        ArgumentNullException.ThrowIfNull(userSuppliedTag);
        Run($"DeleteObjectInstance({objectHandle}, tag {userSuppliedTag.Length}b)", Need.Member, () =>
        {
            var obj = KnownObject(objectHandle);
            if (obj.Owner != _self)
            {
                throw new DeletePrivilegeNotHeld(objectHandle.ToString());
            }
            DeleteInstance(_federation!, obj, userSuppliedTag);
        });
    }

    public void SendInteraction(InteractionClassHandle theInteraction, ParameterHandleValueMap theParameters, byte[] userSuppliedTag)
    {
        ArgumentNullException.ThrowIfNull(theParameters);
        ArgumentNullException.ThrowIfNull(userSuppliedTag);
        Run($"SendInteraction({theInteraction}, {Format(theParameters)}, tag {userSuppliedTag.Length}b)", Need.Member, () =>
        {
            var cls = _federation!.Tables.Interaction(theInteraction)
                ?? throw new InteractionClassNotDefined(theInteraction.ToString());
            if (!_self!.PublishedInteractions.Contains(cls))
            {
                throw new InteractionClassNotPublished(cls.FullName);
            }
            var values = new List<(FomParameter Parameter, byte[] Value)>();
            foreach (var pair in theParameters)
            {
                var parameter = _federation.Tables.Parameter(pair.Key);
                if (parameter is null || !cls.AllParameters.Contains(parameter))
                {
                    throw new InteractionParameterNotDefined(pair.Key.ToString());
                }
                values.Add((parameter, pair.Value));
            }
            foreach (var federate in _federation.Federates.Values.ToList())
            {
                if (federate == _self)
                {
                    continue;
                }
                var received = cls.SelfAndAncestors.FirstOrDefault(c => federate.SubscribedInteractions.Contains(c));
                if (received is null)
                {
                    continue;
                }
                var filtered = new ParameterHandleValueMap();
                foreach (var (parameter, value) in values)
                {
                    if (received.AllParameters.Contains(parameter))
                    {
                        filtered.Set(_federation.Tables.Handle(parameter), (byte[])value.Clone());
                    }
                }
                var tag = (byte[])userSuppliedTag.Clone();
                var receivedHandle = _federation.Tables.Handle(received);
                Enqueue(federate.Ambassador, $"ReceiveInteraction({receivedHandle}, {Format(filtered)}, tag {tag.Length}b)",
                    cb => cb.ReceiveInteraction(receivedHandle, filtered, tag));
            }
        });
    }

    public void RequestAttributeValueUpdate(ObjectInstanceHandle theObject, AttributeHandleSet theAttributes, byte[] userSuppliedTag)
    {
        ArgumentNullException.ThrowIfNull(theAttributes);
        ArgumentNullException.ThrowIfNull(userSuppliedTag);
        Run($"RequestAttributeValueUpdate({theObject}, {Format(theAttributes)}, tag {userSuppliedTag.Length}b)", Need.Member, () =>
        {
            var obj = KnownObject(theObject);
            var requested = ResolveAttributes(KnownClassOf(obj, _self!), theAttributes);
            ProvideUpdate(obj, requested, userSuppliedTag);
        });
    }

    public void RequestAttributeValueUpdate(ObjectClassHandle theClass, AttributeHandleSet theAttributes, byte[] userSuppliedTag)
    {
        ArgumentNullException.ThrowIfNull(theAttributes);
        ArgumentNullException.ThrowIfNull(userSuppliedTag);
        Run($"RequestAttributeValueUpdate({theClass}, {Format(theAttributes)}, tag {userSuppliedTag.Length}b)", Need.Member, () =>
        {
            var cls = _federation!.Tables.Class(theClass) ?? throw new ObjectClassNotDefined(theClass.ToString());
            var requested = ResolveAttributes(cls, theAttributes);
            foreach (var obj in _federation.Objects.Where(o => o.Alive && o.Class.IsSameOrSubclassOf(cls) && Knows(o, _self!)).ToList())
            {
                ProvideUpdate(obj, requested, userSuppliedTag);
            }
        });
    }

    // --- callbacks ---

    public bool EvokeCallback(double approximateMinimumTimeInSeconds) =>
        Run("EvokeCallback", Need.Connected, () =>
        {
            if (_queue.Count > 0)
            {
                Deliver(_queue.Dequeue());
            }
            return _queue.Count > 0;
        });

    public bool EvokeMultipleCallbacks(double approximateMinimumTimeInSeconds, double approximateMaximumTimeInSeconds) =>
        Run("EvokeMultipleCallbacks", Need.Connected, () =>
        {
            var count = _queue.Count; // callbacks queued during delivery wait for the next evoke
            for (var i = 0; i < count && _queue.Count > 0; i++)
            {
                Deliver(_queue.Dequeue());
            }
            return _queue.Count > 0;
        });

    // --- internals ---

    private void Run(string text, Need need, Action body) =>
        Run<object?>(text, need, () =>
        {
            body();
            return null;
        });

    private T Run<T>(string text, Need need, Func<T> body)
    {
        _hub.Log($"A{Id} call {text}");
        try
        {
            if (_inCallback)
            {
                throw new CallNotAllowedFromWithinCallback(text);
            }
            if (need >= Need.Connected && _callbacks is null)
            {
                throw new NotConnected(text);
            }
            if (need == Need.Member && _self is null)
            {
                throw new FederateNotExecutionMember(text);
            }
            return body();
        }
        catch (RtiException e)
        {
            _hub.Log($"A{Id} throws {e.GetType().Name}");
            throw;
        }
    }

    private void Deliver(PendingCallback callback)
    {
        _hub.Log($"A{Id} callback {callback.Text}");
        _inCallback = true;
        try
        {
            callback.Invoke(_callbacks!);
        }
        finally
        {
            _inCallback = false;
        }
    }

    private static void Enqueue(FakeRtiAmbassador target, string text, Action<IFederateAmbassador> invoke) =>
        target._queue.Enqueue(new PendingCallback(text, invoke));

    private ObjectInstanceHandle Register(ObjectClassHandle theClass, string? name)
    {
        var federation = _federation!;
        var self = _self!;
        var cls = federation.Tables.Class(theClass) ?? throw new ObjectClassNotDefined(theClass.ToString());
        if (!self.Published.TryGetValue(cls, out var published))
        {
            throw new ObjectClassNotPublished(cls.FullName);
        }
        if (name is not null)
        {
            if (!IsLegalName(name))
            {
                throw new IllegalName(name);
            }
            if (federation.Names.TryGetValue(name, out var entry) && entry.Object is not null)
            {
                throw new ObjectInstanceNameInUse(name);
            }
            if (entry is null || entry.ReservedBy != self)
            {
                throw new ObjectInstanceNameNotReserved(name);
            }
        }
        var handle = federation.Objects.Count + 1;
        var objectName = name ?? "HLAobject_" + handle;
        var owned = new HashSet<FomAttribute>(published) { federation.PrivilegeToDelete };
        var obj = new FakeObject(handle, objectName, cls, self, owned);
        federation.Objects.Add(obj);
        if (federation.Names.TryGetValue(objectName, out var reserved))
        {
            reserved.Object = obj;
        }
        else
        {
            federation.Names[objectName] = new NameEntry { Object = obj };
        }
        foreach (var federate in federation.Federates.Values.ToList())
        {
            if (federate != self)
            {
                TryDiscover(federation, federate, obj);
            }
        }
        return new ObjectInstanceHandle(handle);
    }

    /// <summary>Discovers the instance for a federate that subscribed to its class or a superclass, as the closest such class.</summary>
    private static void TryDiscover(FakeFederation federation, FakeFederate federate, FakeObject obj)
    {
        if (obj.Registrant == federate || obj.Known.ContainsKey(federate.Handle))
        {
            return;
        }
        var closest = obj.Class.SelfAndAncestors.FirstOrDefault(c => federate.Subscribed.ContainsKey(c));
        if (closest is null)
        {
            return;
        }
        obj.Known[federate.Handle] = (federate, closest);
        var objectHandle = new ObjectInstanceHandle(obj.Handle);
        var classHandle = federation.Tables.Handle(closest);
        Enqueue(federate.Ambassador, $"DiscoverObjectInstance({objectHandle}, {classHandle}, {obj.Name})",
            cb => cb.DiscoverObjectInstance(objectHandle, classHandle, obj.Name));
    }

    /// <summary>A new subscription may make existing instances discoverable (late subscription); handle order.</summary>
    private static void DiscoverLate(FakeFederation federation, FakeFederate federate)
    {
        foreach (var obj in federation.Objects.Where(o => o.Alive).ToList())
        {
            TryDiscover(federation, federate, obj);
        }
    }

    private static void DeleteInstance(FakeFederation federation, FakeObject obj, byte[] tag)
    {
        obj.Alive = false;
        var objectHandle = new ObjectInstanceHandle(obj.Handle);
        foreach (var (federate, _) in obj.Known.Values.ToList())
        {
            var copy = (byte[])tag.Clone();
            Enqueue(federate.Ambassador, $"RemoveObjectInstance({objectHandle}, tag {copy.Length}b)",
                cb => cb.RemoveObjectInstance(objectHandle, copy));
        }
        obj.Known.Clear();
        federation.Names.Remove(obj.Name);
    }

    private void ProvideUpdate(FakeObject obj, HashSet<FomAttribute> requested, byte[] tag)
    {
        if (obj.Owner is not { } owner || owner == _self)
        {
            return;
        }
        var provided = new AttributeHandleSet(requested.Where(obj.Owned.Contains).Select(_federation!.Tables.Handle));
        if (provided.IsEmpty)
        {
            return;
        }
        var objectHandle = new ObjectInstanceHandle(obj.Handle);
        var copy = (byte[])tag.Clone();
        Enqueue(owner.Ambassador, $"ProvideAttributeValueUpdate({objectHandle}, {Format(provided)}, tag {copy.Length}b)",
            cb => cb.ProvideAttributeValueUpdate(objectHandle, provided, copy));
    }

    /// <summary>The instance when it exists and the caller registered or discovered it; otherwise <see cref="ObjectInstanceNotKnown"/>.</summary>
    private FakeObject KnownObject(ObjectInstanceHandle handle)
    {
        var obj = _federation!.FindObject(handle);
        if (obj is null || !obj.Alive || !Knows(obj, _self!))
        {
            throw new ObjectInstanceNotKnown(handle.ToString());
        }
        return obj;
    }

    private static bool Knows(FakeObject obj, FakeFederate federate) =>
        obj.Registrant == federate || obj.Known.ContainsKey(federate.Handle);

    private static FomObjectClass KnownClassOf(FakeObject obj, FakeFederate federate) =>
        obj.Registrant == federate ? obj.Class : obj.Known[federate.Handle].KnownClass;

    private FomInteractionClass InteractionOf(InteractionClassHandle handle) =>
        _federation!.Tables.Interaction(handle) ?? throw new InteractionClassNotDefined(handle.ToString());

    private HashSet<FomAttribute> ResolveAttributes(FomObjectClass cls, AttributeHandleSet handles)
    {
        var result = new HashSet<FomAttribute>();
        foreach (var handle in handles)
        {
            var attribute = _federation!.Tables.Attribute(handle);
            if (attribute is null || !cls.AllAttributes.Contains(attribute))
            {
                throw new AttributeNotDefined(handle.ToString());
            }
            result.Add(attribute);
        }
        return result;
    }

    private static bool IsLegalName(string name) =>
        name.Length is > 0 and <= FakeRtiHub.MaxObjectNameLength
        && !name.StartsWith("HLA", StringComparison.Ordinal)
        && name.All(c => c is >= ' ' and <= '~');

    private static string Format(HandleSet<AttributeHandle> set) => "{" + string.Join(",", set.Select(h => h.Value)) + "}";

    private static string Format<THandle>(HandleValueMap<THandle> map) where THandle : struct, IHandle =>
        "{" + string.Join(",", map.Select(p => p.Key.Value + "=" + p.Value.Length + "b")) + "}";
}
