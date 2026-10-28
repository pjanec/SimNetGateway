namespace SimNetGateway.Protocol.Rpr.Hla;

/// <summary>
/// The callbacks an RTI delivers to a federate (1516e <c>FederateAmbassador</c>, HLA_EVOKED model, receive-ordered only).
/// Called only from inside <see cref="IRtiAmbassador.EvokeCallback"/> / <see cref="IRtiAmbassador.EvokeMultipleCallbacks"/>;
/// the implementation must not call RTI services from here (<see cref="CallNotAllowedFromWithinCallback"/>) and should only
/// enqueue. Value arrays and tags belong to the callee.
/// </summary>
public interface IFederateAmbassador
{
    void ObjectInstanceNameReservationSucceeded(string objectName);

    void ObjectInstanceNameReservationFailed(string objectName);

    /// <summary>An instance became known; <paramref name="theObjectClass"/> is the closest class the federate subscribed to (not necessarily the registered one).</summary>
    void DiscoverObjectInstance(ObjectInstanceHandle theObject, ObjectClassHandle theObjectClass, string objectName);

    /// <summary>Attribute values filtered to the federate's subscription at the class it discovered the instance as.</summary>
    void ReflectAttributeValues(ObjectInstanceHandle theObject, AttributeHandleValueMap theAttributes, byte[] userSuppliedTag);

    void RemoveObjectInstance(ObjectInstanceHandle theObject, byte[] userSuppliedTag);

    /// <summary>An interaction, as the closest class the federate subscribed to, with only that class's parameters.</summary>
    void ReceiveInteraction(InteractionClassHandle interactionClass, ParameterHandleValueMap theParameters, byte[] userSuppliedTag);

    /// <summary>Another federate asked for the attributes to be provided; answer with an update.</summary>
    void ProvideAttributeValueUpdate(ObjectInstanceHandle theObject, AttributeHandleSet theAttributes, byte[] userSuppliedTag);
}
