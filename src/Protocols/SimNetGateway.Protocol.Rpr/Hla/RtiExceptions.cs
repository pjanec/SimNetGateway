namespace SimNetGateway.Protocol.Rpr.Hla;

/// <summary>Base of the typed RTI exceptions (1516e <c>hla.rti1516e.exceptions.RTIexception</c>); the class names are the 1516e names.</summary>
public abstract class RtiException : Exception
{
    protected RtiException(string message) : base(message)
    {
    }
}

public sealed class FederateNotExecutionMember : RtiException { public FederateNotExecutionMember(string message) : base(message) { } }

public sealed class NotConnected : RtiException { public NotConnected(string message) : base(message) { } }

public sealed class AlreadyConnected : RtiException { public AlreadyConnected(string message) : base(message) { } }

public sealed class FederationExecutionAlreadyExists : RtiException { public FederationExecutionAlreadyExists(string message) : base(message) { } }

public sealed class FederationExecutionDoesNotExist : RtiException { public FederationExecutionDoesNotExist(string message) : base(message) { } }

public sealed class FederateAlreadyExecutionMember : RtiException { public FederateAlreadyExecutionMember(string message) : base(message) { } }

public sealed class FederateNameAlreadyInUse : RtiException { public FederateNameAlreadyInUse(string message) : base(message) { } }

public sealed class FederatesCurrentlyJoined : RtiException { public FederatesCurrentlyJoined(string message) : base(message) { } }

/// <summary>Resign without divesting while the federate still owns attributes (1516e FederateOwnsAttributes).</summary>
public sealed class FederateOwnsAttributes : RtiException { public FederateOwnsAttributes(string message) : base(message) { } }

public sealed class NameNotFound : RtiException { public NameNotFound(string message) : base(message) { } }

public sealed class InvalidObjectClassHandle : RtiException { public InvalidObjectClassHandle(string message) : base(message) { } }

public sealed class InvalidInteractionClassHandle : RtiException { public InvalidInteractionClassHandle(string message) : base(message) { } }

public sealed class ObjectClassNotDefined : RtiException { public ObjectClassNotDefined(string message) : base(message) { } }

public sealed class InteractionClassNotDefined : RtiException { public InteractionClassNotDefined(string message) : base(message) { } }

public sealed class AttributeNotDefined : RtiException { public AttributeNotDefined(string message) : base(message) { } }

public sealed class InteractionParameterNotDefined : RtiException { public InteractionParameterNotDefined(string message) : base(message) { } }

public sealed class ObjectClassNotPublished : RtiException { public ObjectClassNotPublished(string message) : base(message) { } }

public sealed class InteractionClassNotPublished : RtiException { public InteractionClassNotPublished(string message) : base(message) { } }

public sealed class AttributeNotOwned : RtiException { public AttributeNotOwned(string message) : base(message) { } }

public sealed class ObjectInstanceNotKnown : RtiException { public ObjectInstanceNotKnown(string message) : base(message) { } }

public sealed class ObjectInstanceNameInUse : RtiException { public ObjectInstanceNameInUse(string message) : base(message) { } }

public sealed class ObjectInstanceNameNotReserved : RtiException { public ObjectInstanceNameNotReserved(string message) : base(message) { } }

public sealed class IllegalName : RtiException { public IllegalName(string message) : base(message) { } }

public sealed class DeletePrivilegeNotHeld : RtiException { public DeletePrivilegeNotHeld(string message) : base(message) { } }

public sealed class CallNotAllowedFromWithinCallback : RtiException { public CallNotAllowedFromWithinCallback(string message) : base(message) { } }

/// <summary>Conflicting or unusable FOM modules (1516e InconsistentFDD).</summary>
public sealed class InconsistentFdd : RtiException { public InconsistentFdd(string message) : base(message) { } }
