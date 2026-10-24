namespace SimNetGateway.Engine;

/// <summary>
/// Decides at commit whether a source may write an entity (R-057, DD-004). The writer is the change set's
/// source. Commands on an entity created in the same transaction are not checked.
/// </summary>
public interface IWriteAuthorityPolicy
{
    bool CanWrite(EntityRecord entity, SourceId writer, AttrId attr);

    bool CanDestroy(EntityRecord entity, SourceId writer) => true;

    bool CanTransferOwner(EntityRecord entity, SourceId writer, SourceId newOwner) => true;
}

/// <summary>Allows everything (the PoC default).</summary>
public sealed class PermissivePolicy : IWriteAuthorityPolicy
{
    public static readonly PermissivePolicy Instance = new();

    public bool CanWrite(EntityRecord entity, SourceId writer, AttrId attr) => true;
}

/// <summary>Only the owner, trusted sources and the engine may write, destroy or transfer an entity.</summary>
public sealed class SingleWriterPolicy : IWriteAuthorityPolicy
{
    private readonly HashSet<SourceId> _trusted;

    public SingleWriterPolicy(params SourceId[] trustedSources)
        : this((IEnumerable<SourceId>)trustedSources)
    {
    }

    public SingleWriterPolicy(IEnumerable<SourceId> trustedSources)
    {
        ArgumentNullException.ThrowIfNull(trustedSources);
        _trusted = new HashSet<SourceId>(trustedSources);
    }

    public bool CanWrite(EntityRecord entity, SourceId writer, AttrId attr) => IsAllowed(entity, writer);

    public bool CanDestroy(EntityRecord entity, SourceId writer) => IsAllowed(entity, writer);

    public bool CanTransferOwner(EntityRecord entity, SourceId writer, SourceId newOwner) => IsAllowed(entity, writer);

    private bool IsAllowed(EntityRecord entity, SourceId writer) =>
        writer == entity.Owner || writer == SourceRegistry.Engine || _trusted.Contains(writer);
}
