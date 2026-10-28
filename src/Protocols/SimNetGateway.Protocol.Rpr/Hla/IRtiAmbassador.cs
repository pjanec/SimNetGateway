namespace SimNetGateway.Protocol.Rpr.Hla;

/// <summary>
/// The RTI as the gateway sees it: a C# port mirroring the IEEE 1516.1-2010 (1516e) <c>RTIambassador</c> with the 1516e
/// service names, exceptions and semantics, restricted to the services the RPR transport uses (DD-008 sections 3.1 and 4)
/// and to the HLA_EVOKED callback model: callbacks reach the <see cref="IFederateAmbassador"/> only from inside
/// <see cref="EvokeCallback"/> / <see cref="EvokeMultipleCallbacks"/>. Values are opaque byte arrays.
/// Every service throws <see cref="CallNotAllowedFromWithinCallback"/> when called from inside a callback and
/// <see cref="NotConnected"/> before <see cref="Connect"/>; the services that need federation membership throw
/// <see cref="FederateNotExecutionMember"/> when the federate has not joined. A call that throws has no effect.
/// </summary>
public interface IRtiAmbassador
{
    // --- federation management ---

    /// <summary>Connects with the callback target (HLA_EVOKED only). <see cref="AlreadyConnected"/> when connected.</summary>
    void Connect(IFederateAmbassador federateAmbassador);

    /// <summary>
    /// Creates a federation execution from FOM modules given by name (resolved by a code registry; the real binding takes
    /// URLs). <see cref="FederationExecutionAlreadyExists"/>, <see cref="NameNotFound"/> for an unknown module,
    /// <see cref="InconsistentFdd"/> for modules that define the same class member differently.
    /// </summary>
    void CreateFederationExecution(string federationExecutionName, IReadOnlyList<string> fomModules);

    /// <summary>
    /// Joins a federation execution. <see cref="FederateAlreadyExecutionMember"/>, <see cref="FederationExecutionDoesNotExist"/>,
    /// <see cref="FederateNameAlreadyInUse"/>.
    /// </summary>
    FederateHandle JoinFederationExecution(string federateName, string federateType, string federationExecutionName);

    /// <summary>Resigns; the action decides what happens to the federate's objects.</summary>
    void ResignFederationExecution(ResignAction resignAction);

    /// <summary>Destroys a federation execution; <see cref="FederatesCurrentlyJoined"/> while federates are joined.</summary>
    void DestroyFederationExecution(string federationExecutionName);

    // --- handles ---

    /// <summary>Handle of an object class by its fully qualified dotted name (<c>HLAobjectRoot.BaseEntity...</c>).</summary>
    ObjectClassHandle GetObjectClassHandle(string name);

    /// <summary>Handle of an attribute of a class by simple name; inherited attributes are found and keep the declaring class's handle.</summary>
    AttributeHandle GetAttributeHandle(ObjectClassHandle whichClass, string name);

    /// <summary>Handle of an interaction class by its fully qualified dotted name (<c>HLAinteractionRoot.WeaponFire</c>).</summary>
    InteractionClassHandle GetInteractionClassHandle(string name);

    /// <summary>Handle of a parameter of an interaction class by simple name (inherited ones included).</summary>
    ParameterHandle GetParameterHandle(InteractionClassHandle whichClass, string name);

    // --- declaration management ---

    /// <summary>Publishes attributes of a class; a later call for the same class replaces the set, an empty set unpublishes.</summary>
    void PublishObjectClassAttributes(ObjectClassHandle theClass, AttributeHandleSet attributeList);

    /// <summary>Unpublishes a class (1516e <c>unpublishObjectClass</c>; not in the DD-008 section 4 diagram, needed by its rule table).</summary>
    void UnpublishObjectClass(ObjectClassHandle theClass);

    /// <summary>Subscribes to attributes of a class; replaces the set for that class, an empty set unsubscribes.</summary>
    void SubscribeObjectClassAttributes(ObjectClassHandle theClass, AttributeHandleSet attributeList);

    /// <summary>Unsubscribes a class (1516e <c>unsubscribeObjectClass</c>).</summary>
    void UnsubscribeObjectClass(ObjectClassHandle theClass);

    void PublishInteractionClass(InteractionClassHandle theInteraction);

    void UnpublishInteractionClass(InteractionClassHandle theInteraction);

    void SubscribeInteractionClass(InteractionClassHandle theClass);

    void UnsubscribeInteractionClass(InteractionClassHandle theClass);

    // --- object management ---

    /// <summary>Reserves an object instance name; the answer arrives later through the two reservation callbacks.</summary>
    void ReserveObjectInstanceName(string theObjectName);

    /// <summary>Registers an instance of a published class with an RTI-generated name.</summary>
    ObjectInstanceHandle RegisterObjectInstance(ObjectClassHandle theClass);

    /// <summary>Registers an instance with a previously reserved name.</summary>
    ObjectInstanceHandle RegisterObjectInstance(ObjectClassHandle theClass, string theObjectName);

    string GetObjectInstanceName(ObjectInstanceHandle theObject);

    void UpdateAttributeValues(ObjectInstanceHandle theObject, AttributeHandleValueMap theAttributes, byte[] userSuppliedTag);

    void DeleteObjectInstance(ObjectInstanceHandle objectHandle, byte[] userSuppliedTag);

    void SendInteraction(InteractionClassHandle theInteraction, ParameterHandleValueMap theParameters, byte[] userSuppliedTag);

    /// <summary>Asks the owner(s) of an instance to provide the given attributes.</summary>
    void RequestAttributeValueUpdate(ObjectInstanceHandle theObject, AttributeHandleSet theAttributes, byte[] userSuppliedTag);

    /// <summary>Asks the owners of all instances of a class (and subclasses) known to the caller to provide the given attributes.</summary>
    void RequestAttributeValueUpdate(ObjectClassHandle theClass, AttributeHandleSet theAttributes, byte[] userSuppliedTag);

    // --- callbacks (HLA_EVOKED) ---

    /// <summary>Delivers at most one queued callback; true when more are pending. The time argument is ignored by the fake RTI.</summary>
    bool EvokeCallback(double approximateMinimumTimeInSeconds);

    /// <summary>Delivers every callback queued when the call started; true when more are pending afterwards.</summary>
    bool EvokeMultipleCallbacks(double approximateMinimumTimeInSeconds, double approximateMaximumTimeInSeconds);
}
