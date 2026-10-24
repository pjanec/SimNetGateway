namespace SimNetGateway.Engine;

public enum CommitStatus
{
    /// <summary>Applied; a record was journaled.</summary>
    Committed,

    /// <summary>
    /// Nothing to journal (no commands, or effects that cancel out): no record, no state change. Buffered identity
    /// bindings of such a transaction are still applied (a transaction of only bindings ends here).
    /// </summary>
    Empty,

    /// <summary>Validation failed; nothing was applied.</summary>
    Rejected,
}

public readonly struct CommitResult
{
    private CommitResult(CommitStatus status, ChangeSetRecord? record, string? reason)
    {
        Status = status;
        Record = record;
        Reason = reason;
    }

    public CommitStatus Status { get; }

    /// <summary>
    /// The journaled record; non-null only for <see cref="CommitStatus.Committed"/>. With a journal capacity of 0
    /// (history off, R-070) nothing is retained: on a standalone <see cref="StateRepository"/> the record is valid
    /// only until the next commit; under the engine it is recycled when its dispatch has finished, so it must not
    /// be used after the commit call returned (the commit dispatches synchronously when the engine is idle).
    /// </summary>
    public ChangeSetRecord? Record { get; }

    /// <summary>Why the transaction was rejected; non-null only for <see cref="CommitStatus.Rejected"/>.</summary>
    public string? Reason { get; }

    public bool Succeeded => Status != CommitStatus.Rejected;

    internal static CommitResult Committed(ChangeSetRecord record) => new(CommitStatus.Committed, record, null);

    internal static CommitResult Empty() => new(CommitStatus.Empty, null, null);

    internal static CommitResult Rejected(string reason) => new(CommitStatus.Rejected, null, reason);
}
