namespace SimNetGateway.Engine;

internal enum CommandKind : byte
{
    Create,
    Destroy,
    Set,
    Clear,
    TransferOwner,
}

/// <summary>One buffered command. <c>Source</c> is the origin (Create) or the new owner (TransferOwner).</summary>
internal readonly struct Command
{
    public readonly CommandKind Kind;
    public readonly EntityId Entity;
    public readonly AttrId Attr;
    public readonly AttrValue Value;
    public readonly SourceId Source;

    public Command(CommandKind kind, EntityId entity, AttrId attr, AttrValue value, SourceId source)
    {
        Kind = kind;
        Entity = entity;
        Attr = attr;
        Value = value;
        Source = source;
    }
}

/// <summary>
/// The single reusable transaction buffer. Handlers record commands; <see cref="Commit"/> validates all of
/// them and applies them atomically, or applies nothing. Reads of the repository see committed state only;
/// <see cref="TryGetPending"/> sees this buffer.
/// </summary>
public sealed class ChangeSet
{
    private readonly StateRepository _repository;
    private readonly List<Command> _commands = new();
    private readonly List<IChangeParticipant> _participants = new();

    internal ChangeSet(StateRepository repository) => _repository = repository;

    public SourceId Source { get; private set; }
    public CorrelationId Correlation { get; private set; }
    public CorrelationId CausedBy { get; private set; }
    public double WallTime { get; private set; }
    public double SimTime { get; private set; }
    public bool IsOpen { get; private set; }
    public int CommandCount => _commands.Count;

    internal List<Command> Commands => _commands;

    /// <summary>The participants enlisted in this transaction (reused list, cleared on open and close).</summary>
    internal List<IChangeParticipant> Participants => _participants;

    /// <summary>Adds a participant to the open transaction; the caller guarantees it enlists at most once.</summary>
    internal void Enlist(IChangeParticipant participant)
    {
        EnsureOpen();
        _participants.Add(participant);
    }

    internal void Open(SourceId source, CorrelationId correlation, CorrelationId causedBy, double wallTime, double simTime)
    {
        Source = source;
        Correlation = correlation;
        CausedBy = causedBy;
        WallTime = wallTime;
        SimTime = simTime;
        _commands.Clear();
        _participants.Clear();
        IsOpen = true;
    }

    internal void Close()
    {
        for (int i = 0; i < _participants.Count; i++)
        {
            _participants[i].Discard(); // drops whatever was not applied
        }

        _participants.Clear();
        _commands.Clear();
        IsOpen = false;
    }

    /// <summary>Reserves an entity id immediately (burned if the transaction is discarded). Origin defaults to Source.</summary>
    public EntityId CreateEntity(SourceId? origin = null)
    {
        EnsureOpen();
        var id = _repository.AllocateEntityId();
        _commands.Add(new Command(CommandKind.Create, id, default, default, origin ?? Source));
        return id;
    }

    public void DestroyEntity(EntityId id)
    {
        EnsureOpen();
        _commands.Add(new Command(CommandKind.Destroy, id, default, default, default));
    }

    /// <summary>Records a write. The value kind is checked now (programming error: ArgumentException).</summary>
    public void Set(EntityId id, AttrId attr, AttrValue value)
    {
        EnsureOpen();
        if (!_repository.Attributes.TryGet(attr, out var def))
        {
            throw new ArgumentException($"Unknown attribute id {attr.Value}.", nameof(attr));
        }

        if (value.IsNone)
        {
            throw new ArgumentException($"Cannot Set attribute '{def.Name}' to None; use Clear.", nameof(value));
        }

        if (value.Kind != def.Kind)
        {
            throw new ArgumentException(
                $"Attribute '{def.Name}' is {def.Kind}, but the value is {value.Kind}.", nameof(value));
        }

        _commands.Add(new Command(CommandKind.Set, id, attr, value, default));
    }

    public void Clear(EntityId id, AttrId attr)
    {
        EnsureOpen();
        _commands.Add(new Command(CommandKind.Clear, id, attr, default, default));
    }

    public void TransferOwner(EntityId id, SourceId newOwner)
    {
        EnsureOpen();
        _commands.Add(new Command(CommandKind.TransferOwner, id, default, default, newOwner));
    }

    /// <summary>
    /// The value of the last pending Set of the slot in this transaction. Returns false when there is none, and
    /// also when the last pending command on the slot is a Clear (or the entity is pending destruction).
    /// </summary>
    public bool TryGetPending(EntityId id, AttrId attr, out AttrValue value)
    {
        var cmds = _commands;
        for (int i = cmds.Count - 1; i >= 0; i--)
        {
            ref readonly var c = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cmds)[i];
            if (c.Entity != id)
            {
                continue;
            }

            if (c.Kind == CommandKind.Destroy || (c.Kind == CommandKind.Clear && c.Attr == attr))
            {
                break;
            }

            if (c.Kind == CommandKind.Set && c.Attr == attr)
            {
                value = c.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public CommitResult Commit()
    {
        EnsureOpen();
        return _repository.Commit(this);
    }

    /// <summary>Drops all commands and closes the change set; a no-op when already closed.</summary>
    public void Discard()
    {
        if (IsOpen)
        {
            _repository.Discard(this);
        }
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("The change set is not open; call StateRepository.Begin first.");
        }
    }
}
