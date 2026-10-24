namespace SimNetGateway.Engine.Monitoring;

public enum InspectorState
{
    /// <summary>Nothing selected (or an id that never existed while the inspector watched).</summary>
    NoSelection,

    /// <summary>The entity is alive; everything below is current as of the last refresh.</summary>
    Alive,

    /// <summary>The entity was destroyed; everything below is its last copy.</summary>
    Removed,
}

/// <summary>One attribute slot of the selected entity.</summary>
public readonly record struct SlotRow(
    AttrId Attr, string Name, AttrKind Kind, AttrValue Value, string Text, SourceId Source, string SourceName,
    CorrelationId Correlation, double WallTime, double SimTime, double ChangedWallTime, double ChangedSimTime, uint Version);

/// <summary>The entity's identity in one map: <c>Key</c> is null when the map does not know it ("not known to RPR").</summary>
public readonly record struct IdentityRow(string Map, string Side, string KeyType, string? Key)
{
    public bool Known => Key is not null;
}

/// <summary>The entry of one module's entity table (<see cref="IEntityTable.TryDescribe"/>).</summary>
public readonly record struct ModuleStateRow(string Table, string Text);

/// <summary>The entities that point at the selected one through an attribute with reference index (e.g. the members of a unit).</summary>
public readonly record struct MemberGroup(AttrId Attr, string AttrName, EntityId[] Members);

/// <summary>An entity reference the selected entity holds (e.g. its parent).</summary>
public readonly record struct ReferenceRow(AttrId Attr, string AttrName, EntityId Target);

/// <summary>One write of the selected entity found in the journal; an entity touched without any attribute (creation, owner change) has one row with a none attribute.</summary>
public readonly record struct WriteRow(
    CorrelationId Correlation, CorrelationId CausedBy, SourceId Source, string SourceName, double WallTime, double SimTime,
    AttrId Attr, string AttrName, AttrValue Old, AttrValue New, bool Changed, bool Created, bool Destroyed, bool OwnerChanged);

/// <summary>
/// The Inspector panel's model (DD-005 section 3): everything the gateway knows about one entity, copied on
/// <see cref="Refresh"/> - slots with their metadata, the identity in every map, the state of every module's entity table,
/// the structure (references held, members found through reference-indexed attributes) and the most recent journal writes.
/// A destroyed entity stays shown as <see cref="InspectorState.Removed"/> with its last copy (R-075).
/// </summary>
public sealed class InspectorModel
{
    private readonly GatewayEngine _engine;
    private readonly List<SlotRow> _slots = new();
    private readonly List<IdentityRow> _identities = new();
    private readonly List<ModuleStateRow> _moduleState = new();
    private readonly List<MemberGroup> _members = new();
    private readonly List<ReferenceRow> _references = new();
    private readonly List<WriteRow> _writes = new();
    private readonly List<EntityRecord> _scratch = new();

    public InspectorModel(GatewayEngine engine, int maxWrites = 64)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentOutOfRangeException.ThrowIfNegative(maxWrites);
        _engine = engine;
        MaxWrites = maxWrites;
    }

    /// <summary>At most this many of the most recent writes are kept.</summary>
    public int MaxWrites { get; }

    public EntityId Selected { get; private set; }

    public InspectorState State { get; private set; }

    public bool IsRemoved => State == InspectorState.Removed;

    public SourceId Origin { get; private set; }

    public string OriginName { get; private set; } = string.Empty;

    public SourceId Owner { get; private set; }

    public string OwnerName { get; private set; } = string.Empty;

    public double CreatedWallTime { get; private set; }

    public double LastWriteWallTime { get; private set; }

    public double LastChangeWallTime { get; private set; }

    public double LastOriginWriteWallTime { get; private set; }

    /// <summary>When the entity was removed: from the journal when it still holds the removal, else the engine time of the refresh that noticed it.</summary>
    public double RemovedWallTime { get; private set; }

    public double RemovedSimTime { get; private set; }

    /// <summary>The journal is off (capacity 0): <see cref="RecentWrites"/> is always empty and the panel says "history off".</summary>
    public bool HistoryOff { get; private set; }

    public IReadOnlyList<SlotRow> Slots => _slots;
    public IReadOnlyList<IdentityRow> Identities => _identities;
    public IReadOnlyList<ModuleStateRow> ModuleState => _moduleState;

    /// <summary>The entities that reference the selected one, per reference-indexed attribute (only attributes with members).</summary>
    public IReadOnlyList<MemberGroup> Members => _members;

    /// <summary>The entity references the selected entity holds.</summary>
    public IReadOnlyList<ReferenceRow> References => _references;

    /// <summary>The most recent writes of the entity found in the journal, oldest first.</summary>
    public IReadOnlyList<WriteRow> RecentWrites => _writes;

    /// <summary>Selects an entity and refreshes.</summary>
    public void Load(EntityId id)
    {
        Selected = id;
        State = InspectorState.NoSelection;
        Clear();
        Refresh();
    }

    public void Refresh()
    {
        if (Selected.IsNone)
        {
            return;
        }

        var engine = _engine;
        if (engine.State.TryGet(Selected, out var entity) && entity.IsAlive)
        {
            State = InspectorState.Alive;
            Copy(entity);
            return;
        }

        if (State == InspectorState.Alive)
        {
            State = InspectorState.Removed;
            RemovedWallTime = engine.WallTime;
            RemovedSimTime = engine.SimTime;
            FindRemoval();
        }

        // Removed keeps its last copy; an id that was never alive stays NoSelection with empty lists.
    }

    private void Clear()
    {
        _slots.Clear();
        _identities.Clear();
        _moduleState.Clear();
        _members.Clear();
        _references.Clear();
        _writes.Clear();
        HistoryOff = _engine.State.Journal.Capacity == 0;
    }

    private void Copy(EntityRecord entity)
    {
        var engine = _engine;
        var id = entity.Id;
        Clear();
        Origin = entity.Origin;
        OriginName = Names.Source(engine, entity.Origin);
        Owner = entity.Owner;
        OwnerName = Names.Source(engine, entity.Owner);
        CreatedWallTime = entity.CreatedWallTime;
        LastWriteWallTime = entity.LastWriteWallTime;
        LastChangeWallTime = entity.LastChangeWallTime;
        LastOriginWriteWallTime = entity.LastOriginWriteWallTime;

        foreach (var (attr, slot) in entity.Attributes)
        {
            var meta = slot.Meta;
            _slots.Add(new SlotRow(attr, Names.Attribute(engine, attr), slot.Value.Kind, slot.Value, slot.Value.ToString(),
                meta.Source, Names.Source(engine, meta.Source), meta.Correlation, meta.WallTime, meta.SimTime,
                meta.ChangedWallTime, meta.ChangedSimTime, meta.Version));
            if (slot.Value.Kind == AttrKind.EntityRef)
            {
                _references.Add(new ReferenceRow(attr, Names.Attribute(engine, attr), slot.Value.AsEntity()));
            }
        }

        _slots.Sort(static (a, b) => a.Attr.Value.CompareTo(b.Attr.Value));
        _references.Sort(static (a, b) => a.Attr.Value.CompareTo(b.Attr.Value));

        foreach (var map in engine.Identities.All)
        {
            _identities.Add(new IdentityRow(map.Name, Names.Source(engine, map.Side), map.KeyType.Name,
                map.TryDescribeKey(id, out var key) ? key : null));
        }

        foreach (var table in engine.EntityTables)
        {
            if (table.TryDescribe(id, out var text))
            {
                _moduleState.Add(new ModuleStateRow(table.Name, text));
            }
        }

        foreach (var def in engine.Attributes)
        {
            if ((def.Options & AttrOptions.IndexReferences) == 0)
            {
                continue;
            }

            engine.State.Query().ReferencedBy(def.Id, id).CopyTo(_scratch);
            if (_scratch.Count == 0)
            {
                continue;
            }

            var members = new EntityId[_scratch.Count];
            for (int i = 0; i < members.Length; i++)
            {
                members[i] = _scratch[i].Id;
            }

            Array.Sort(members, static (a, b) => a.Value.CompareTo(b.Value));
            _members.Add(new MemberGroup(def.Id, def.Name, members));
        }

        _scratch.Clear();
        CopyWrites(id);
    }

    private void CopyWrites(EntityId id)
    {
        var engine = _engine;
        var journal = engine.State.Journal;
        if (journal.Capacity == 0 || MaxWrites == 0)
        {
            return;
        }

        foreach (var record in journal.Since(double.NegativeInfinity))
        {
            foreach (var delta in record.Entities)
            {
                if (delta.Id != id)
                {
                    continue;
                }

                string sourceName = Names.Source(engine, record.Source);
                if (delta.Changes.Count == 0)
                {
                    Add(new WriteRow(record.Correlation, record.CausedBy, record.Source, sourceName, record.WallTime, record.SimTime,
                        default, "(none)", default, default, false, delta.Created, delta.Destroyed, delta.OwnerChanged));
                    continue;
                }

                foreach (var change in delta.Changes)
                {
                    Add(new WriteRow(record.Correlation, record.CausedBy, record.Source, sourceName, record.WallTime, record.SimTime,
                        change.Attr, Names.Attribute(engine, change.Attr), change.Old, change.New, change.Changed, delta.Created,
                        delta.Destroyed, delta.OwnerChanged));
                }
            }
        }
    }

    private void Add(WriteRow row)
    {
        if (_writes.Count == MaxWrites)
        {
            _writes.RemoveAt(0);
        }

        _writes.Add(row);
    }

    /// <summary>Looks for the removal in the journal, to report the time it happened rather than the time it was noticed.</summary>
    private void FindRemoval()
    {
        var journal = _engine.State.Journal;
        if (journal.Capacity == 0)
        {
            return;
        }

        foreach (var record in journal.Since(double.NegativeInfinity))
        {
            foreach (var delta in record.Entities)
            {
                if (delta.Id == Selected && delta.Destroyed)
                {
                    RemovedWallTime = record.WallTime;
                    RemovedSimTime = record.SimTime;
                }
            }
        }
    }
}
