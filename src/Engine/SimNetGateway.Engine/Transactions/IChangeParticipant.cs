namespace SimNetGateway.Engine;

/// <summary>
/// Something other than commands that is buffered in the open <see cref="ChangeSet"/> and committed with it
/// (today: the buffered bindings of an <see cref="IdentityMap{TKey}"/>). It enlists itself on the first buffered
/// operation; the repository then validates it after the commands, applies it with them or discards it.
/// </summary>
internal interface IChangeParticipant
{
    /// <summary>Called after the commands validated; a non-null reason rejects the whole transaction.</summary>
    string? Validate();

    /// <summary>Makes the buffered operations permanent. Called after the commands were applied.</summary>
    void Apply();

    /// <summary>Drops the buffered operations; also called (harmlessly) after <see cref="Apply"/>.</summary>
    void Discard();
}
