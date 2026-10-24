namespace SimNetGateway.Engine;

/// <summary>
/// The engine's hook into <see cref="StateRepository.Commit"/>, so it sees every commit, also those made by
/// processors through <c>BeginChange</c>. Null on a repository used standalone.
/// </summary>
internal interface ICommitObserver
{
    /// <summary>Runs after successful validation, before anything is applied; a non-null reason refuses the commit.</summary>
    string? BeforeApply(ChangeSet changeSet);

    /// <summary>Called after the record is journaled and the change set is closed (so it may be opened again).</summary>
    void Committed(ChangeSet changeSet, ChangeSetRecord record);

    /// <summary>Called for every rejected commit (validation or <see cref="BeforeApply"/>); the change set is closed.</summary>
    void Rejected(ChangeSet changeSet, string reason);
}
