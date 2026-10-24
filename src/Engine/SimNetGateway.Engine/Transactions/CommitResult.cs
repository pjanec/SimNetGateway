namespace SimNetGateway.Engine;

public enum CommitStatus
{
    /// <summary>Applied; a record was journaled.</summary>
    Committed,

    /// <summary>Nothing to apply (no commands, or effects that cancel out); no record, no state change.</summary>
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

    /// <summary>The journaled record; non-null only for <see cref="CommitStatus.Committed"/>.</summary>
    public ChangeSetRecord? Record { get; }

    /// <summary>Why the transaction was rejected; non-null only for <see cref="CommitStatus.Rejected"/>.</summary>
    public string? Reason { get; }

    public bool Succeeded => Status != CommitStatus.Rejected;

    internal static CommitResult Committed(ChangeSetRecord record) => new(CommitStatus.Committed, record, null);

    internal static CommitResult Empty() => new(CommitStatus.Empty, null, null);

    internal static CommitResult Rejected(string reason) => new(CommitStatus.Rejected, null, reason);
}
