namespace SimNetGateway.Protocol.Rpr.Hla;

/// <summary>What a federate does with its objects when it resigns (1516e <c>ResignAction</c>).</summary>
public enum ResignAction
{
    /// <summary>1516e UNCONDITIONALLY_DIVEST_ATTRIBUTES.</summary>
    UnconditionallyDivestAttributes,

    /// <summary>1516e DELETE_OBJECTS.</summary>
    DeleteObjects,

    /// <summary>1516e CANCEL_PENDING_OWNERSHIP_ACQUISITIONS.</summary>
    CancelPendingOwnershipAcquisitions,

    /// <summary>1516e DELETE_OBJECTS_THEN_DIVEST.</summary>
    DeleteObjectsThenDivest,

    /// <summary>1516e CANCEL_THEN_DELETE_THEN_DIVEST.</summary>
    CancelThenDeleteThenDivest,

    /// <summary>1516e NO_ACTION.</summary>
    NoAction,
}
