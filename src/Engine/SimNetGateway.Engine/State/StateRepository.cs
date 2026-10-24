using System.Runtime.CompilerServices;

namespace SimNetGateway.Engine;

/// <summary>
/// The central state: live entities, the single reusable <see cref="ChangeSet"/>, the journal and the pools.
/// Single-threaded. Writes go through <see cref="Begin"/> and <see cref="ChangeSet.Commit"/> only. The indexes behind
/// <see cref="Query"/> (recency lists, presence sets, reverse references, spatial grids) are maintained at commit.
/// </summary>
public sealed partial class StateRepository : IStateView
{
    private readonly Dictionary<EntityId, EntityRecord> _entities = new();
    private readonly AttributeRegistry _attributes;
    private readonly IWriteAuthorityPolicy _policy;
    private readonly RecordPools _pools = new();
    private readonly ChangeSet _changeSet;
    private readonly ChangeJournal _journal;

    // Scratch collections reused by every commit (cleared, never reallocated).
    private readonly HashSet<EntityId> _createdInTx = new();
    private readonly HashSet<EntityId> _destroyedInTx = new();
    private readonly Dictionary<EntityId, EntityDelta> _deltaByEntity = new();

    // Index of the last Set/Clear of every (entity, attribute) in the transaction. Only that command is
    // applied, so a slot is written at most once per transaction and Version / ChangedWallTime move only
    // when the final value differs from the value before the transaction (DD-001 3.1).
    private readonly Dictionary<(EntityId, AttrId), int> _lastSlotWrite = new();

    private int _nextEntity = 1;
    private long _nextCorrelation = 1;

    public StateRepository(AttributeRegistry attributes, IWriteAuthorityPolicy? policy = null, int journalCapacity = 1024,
        int eventLogCapacity = 256)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        _attributes = attributes;
        _policy = policy ?? PermissivePolicy.Instance;
        _journal = new ChangeJournal(journalCapacity, _pools, eventLogCapacity);
        _changeSet = new ChangeSet(this);
    }

    public AttributeRegistry Attributes => _attributes;

    /// <summary>The engine's view of every commit; null when the repository is used standalone.</summary>
    internal ICommitObserver? Observer { get; set; }

    internal RecordPools Pools => _pools;

    /// <summary>A fresh correlation id from the same counter as <see cref="Begin"/> (events published outside a transaction).</summary>
    internal CorrelationId AllocateCorrelation() => new(_nextCorrelation++);

    public IWriteAuthorityPolicy Policy => _policy;

    public int Count => _entities.Count;

    public EntityEnumerable Entities => new(_entities);

    public ChangeJournal Journal => _journal;

    public bool IsChangeOpen => _changeSet.IsOpen;

    public EntityRecord? Find(EntityId id) => _entities.TryGetValue(id, out var e) ? e : null;

    public bool TryGet(EntityId id, out EntityRecord entity)
    {
        if (_entities.TryGetValue(id, out var found))
        {
            entity = found;
            return true;
        }

        entity = null!;
        return false;
    }

    /// <summary>Opens the (single, reusable) change set and allocates the next correlation id.</summary>
    public ChangeSet Begin(SourceId source, double wallTime, double simTime, CorrelationId causedBy = default)
    {
        if (_changeSet.IsOpen)
        {
            throw new InvalidOperationException("A change set is already open; commit or discard it first.");
        }

        var correlation = new CorrelationId(_nextCorrelation++);
        _changeSet.Open(source, correlation, causedBy, wallTime, simTime);
        return _changeSet;
    }

    internal EntityId AllocateEntityId() => new(_nextEntity++);

    public void Discard(ChangeSet changeSet)
    {
        CheckOwn(changeSet);
        changeSet.Close();
    }

    public CommitResult Commit(ChangeSet changeSet)
    {
        CheckOwn(changeSet);
        if (!changeSet.IsOpen)
        {
            throw new InvalidOperationException("The change set is not open.");
        }

        string? reason = Validate(changeSet);
        if (reason is null)
        {
            reason = ValidateParticipants(changeSet);
        }

        var observer = Observer;
        if (reason is null && observer is not null && (changeSet.CommandCount > 0 || changeSet.Participants.Count > 0))
        {
            reason = observer.BeforeApply(changeSet);
        }

        if (reason is not null)
        {
            changeSet.Close();
            observer?.Rejected(changeSet, reason);
            return CommitResult.Rejected(reason);
        }

        var record = Apply(changeSet);
        var participants = changeSet.Participants;
        for (int i = 0; i < participants.Count; i++)
        {
            participants[i].Apply();
        }

        if (observer is null)
        {
            SetTime(changeSet.WallTime, changeSet.SimTime); // standalone: queries see the time of the last commit
        }

        changeSet.Close();
        if (record is null)
        {
            return CommitResult.Empty();
        }

        _journal.Add(record);
        observer?.Committed(changeSet, record);
        return CommitResult.Committed(record);
    }

    private void CheckOwn(ChangeSet changeSet)
    {
        if (!ReferenceEquals(changeSet, _changeSet))
        {
            throw new ArgumentException("The change set belongs to another repository.", nameof(changeSet));
        }
    }

    /// <summary>Validates the buffered operations of the participants (identity bindings) after the commands validated.</summary>
    private string? ValidateParticipants(ChangeSet cs)
    {
        var participants = cs.Participants;
        for (int i = 0; i < participants.Count; i++)
        {
            string? reason = participants[i].Validate();
            if (reason is not null)
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the entity is alive after the transaction being validated: it exists (or is created in it) and is not
    /// destroyed in it. Valid only while <see cref="Commit"/> validates participants.
    /// </summary>
    internal bool IsAliveAfterTransaction(EntityId id) =>
        !_destroyedInTx.Contains(id) && (_createdInTx.Contains(id) || _entities.ContainsKey(id));

    // ---- validation -------------------------------------------------------------------------------------

    private string? Validate(ChangeSet cs)
    {
        _createdInTx.Clear();
        _destroyedInTx.Clear();
        _lastSlotWrite.Clear();
        var writer = cs.Source;
        var commands = cs.Commands;

        for (int i = 0; i < commands.Count; i++)
        {
            var c = commands[i];
            if (c.Kind == CommandKind.Create)
            {
                _createdInTx.Add(c.Entity);
                continue;
            }

            bool createdHere = _createdInTx.Contains(c.Entity);
            EntityRecord? record = null;
            if (!createdHere && !_entities.TryGetValue(c.Entity, out record))
            {
                return $"command {i} ({c.Kind}) targets entity {c.Entity.Value}, which does not exist or is no longer alive";
            }

            if (_destroyedInTx.Contains(c.Entity))
            {
                return $"command {i} ({c.Kind}) targets entity {c.Entity.Value}, which is destroyed earlier in this transaction";
            }

            switch (c.Kind)
            {
                case CommandKind.Destroy:
                    if (!createdHere && !_policy.CanDestroy(record!, writer))
                    {
                        return Denied(i, c.Kind, writer, record!);
                    }

                    _destroyedInTx.Add(c.Entity);
                    break;
                case CommandKind.Set:
                case CommandKind.Clear:
                    if (!createdHere && !_policy.CanWrite(record!, writer, c.Attr))
                    {
                        return Denied(i, c.Kind, writer, record!);
                    }

                    _lastSlotWrite[(c.Entity, c.Attr)] = i;
                    break;
                case CommandKind.TransferOwner:
                    if (!createdHere && !_policy.CanTransferOwner(record!, writer, c.Source))
                    {
                        return Denied(i, c.Kind, writer, record!);
                    }

                    break;
            }
        }

        return null;
    }

    private static string Denied(int index, CommandKind kind, SourceId writer, EntityRecord record) =>
        $"command {index} ({kind}) denied: source {writer.Value} may not write entity {record.Id.Value}, " +
        $"which is owned by source {record.Owner.Value}";

    // ---- apply ------------------------------------------------------------------------------------------

    /// <summary>Applies the validated commands; returns the record, or null when nothing was applied.</summary>
    private ChangeSetRecord? Apply(ChangeSet cs)
    {
        var record = _pools.Records.Rent();
        record.Correlation = cs.Correlation;
        record.CausedBy = cs.CausedBy;
        record.Source = cs.Source;
        record.WallTime = cs.WallTime;
        record.SimTime = cs.SimTime;
        _deltaByEntity.Clear();

        var commands = cs.Commands;
        double wall = cs.WallTime;
        double sim = cs.SimTime;
        var src = cs.Source;
        var corr = cs.Correlation;

        for (int i = 0; i < commands.Count; i++)
        {
            var c = commands[i];
            switch (c.Kind)
            {
                case CommandKind.Create:
                {
                    var entity = new EntityRecord(c.Entity, c.Source, c.Source, corr, wall, sim);
                    _entities.Add(c.Entity, entity);
                    AppendRecency(entity);
                    GetDelta(record, entity).Created = true;
                    break;
                }

                case CommandKind.Destroy:
                {
                    var entity = _entities[c.Entity];
                    var delta = GetDelta(record, entity);
                    _entities.Remove(c.Entity);
                    UnindexEntity(entity);
                    entity.IsAlive = false;
                    entity.LastWriteWallTime = wall;
                    entity.LastChangeWallTime = wall;
                    entity.LastWriteSimTime = sim;
                    entity.LastChangeSimTime = sim;
                    if (delta.Created)
                    {
                        // Created and destroyed in one transaction: the effects cancel out.
                        record.EntityList.Remove(delta);
                        _deltaByEntity.Remove(c.Entity);
                        _pools.ReturnDelta(delta);
                    }
                    else
                    {
                        delta.Destroyed = true;
                    }

                    break;
                }

                case CommandKind.Set:
                {
                    if (_lastSlotWrite[(c.Entity, c.Attr)] != i)
                    {
                        break; // superseded by a later Set/Clear of the same slot
                    }

                    var entity = _entities[c.Entity];
                    var delta = GetDelta(record, entity);
                    ref AttrSlot slot = ref entity.SlotRef(c.Attr);
                    AttrValue old;
                    bool changed;
                    if (Unsafe.IsNullRef(ref slot))
                    {
                        old = default;
                        changed = true;
                        entity.AddSlot(c.Attr, new AttrSlot(c.Value, new AttrMeta(src, corr, wall, sim, wall, sim, 1)));
                        IndexSlotAdded(entity, c.Attr, c.Value);
                    }
                    else
                    {
                        old = slot.Value;
                        var meta = slot.Meta;
                        changed = !old.Equals(c.Value);
                        meta = changed
                            ? new AttrMeta(src, corr, wall, sim, wall, sim, meta.Version + 1)
                            : meta with { Source = src, Correlation = corr, WallTime = wall, SimTime = sim };
                        slot = new AttrSlot(c.Value, meta);
                        if (changed)
                        {
                            IndexSlotChanged(entity, c.Attr, old, c.Value);
                        }
                    }

                    MarkWritten(entity, wall, sim);
                    if (changed)
                    {
                        MarkChanged(entity, wall, sim);
                    }

                    RecordChange(delta, c.Attr, old, c.Value);
                    break;
                }

                case CommandKind.Clear:
                {
                    if (_lastSlotWrite[(c.Entity, c.Attr)] != i)
                    {
                        break; // superseded by a later Set/Clear of the same slot
                    }

                    var entity = _entities[c.Entity];
                    ref AttrSlot slot = ref entity.SlotRef(c.Attr);
                    if (Unsafe.IsNullRef(ref slot))
                    {
                        break; // absent: no-op, no change entry
                    }

                    var old = slot.Value;
                    entity.RemoveSlot(c.Attr);
                    IndexSlotRemoved(entity, c.Attr, old);
                    var delta = GetDelta(record, entity);
                    MarkWritten(entity, wall, sim);
                    MarkChanged(entity, wall, sim);
                    RecordChange(delta, c.Attr, old, default);
                    break;
                }

                case CommandKind.TransferOwner:
                {
                    var entity = _entities[c.Entity];
                    if (entity.Owner == c.Source)
                    {
                        break; // transfer to the current owner: no-op
                    }

                    var delta = GetDelta(record, entity);
                    if (!delta.OwnerChanged)
                    {
                        delta.OwnerChanged = true;
                        delta.PreviousOwner = entity.Owner;
                    }

                    entity.Owner = c.Source;
                    MarkWritten(entity, wall, sim);
                    MarkChanged(entity, wall, sim);
                    break;
                }
            }
        }

        _deltaByEntity.Clear();
        _lastSlotWrite.Clear();
        if (record.EntityList.Count == 0)
        {
            _pools.ReturnRecord(record);
            return null;
        }

        return record;
    }

    private EntityDelta GetDelta(ChangeSetRecord record, EntityRecord entity)
    {
        if (_deltaByEntity.TryGetValue(entity.Id, out var delta))
        {
            return delta;
        }

        delta = _pools.Deltas.Rent();
        delta.Entity = entity;
        record.EntityList.Add(delta);
        _deltaByEntity.Add(entity.Id, delta);
        return delta;
    }

    /// <summary>One entry per slot: only the last Set/Clear of a slot is applied (see _lastSlotWrite).</summary>
    private static void RecordChange(EntityDelta delta, AttrId attr, AttrValue old, AttrValue now) =>
        delta.ChangeList.Add(new AttrChange(attr, old, now, !old.Equals(now)));
}
