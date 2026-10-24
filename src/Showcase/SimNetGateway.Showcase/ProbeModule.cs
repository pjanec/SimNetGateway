using SimNetGateway.Engine;
using SimNetGateway.Gateway.NedRpr;

namespace SimNetGateway.Showcase;

/// <summary>A record or an event as the probe saw it dispatched.</summary>
public readonly record struct DispatchEntry(bool IsEvent, long Correlation);

/// <summary>The shape of a committed change set that created entities (what a formation message must look like: one record).</summary>
public sealed record RecordShape(long Correlation, string Source, int Deltas, int Created, int ParentWrites);

/// <summary>A write the probe saw: entity, correlation, wall time.</summary>
public readonly record struct SpatialObservation(CorrelationId Correlation, EntityId Entity, double WallTime);

/// <summary>A WeaponFire event as dispatched, with the ammunition the shooter had at that moment (null: none stored).</summary>
public readonly record struct FireObservation(CorrelationId Correlation, Guid EventId, EntityId Shooter, long? Ammo);

/// <summary>
/// The passive probe (DD-002 section 3, G-6): a module of the harness that subscribes to triggers and records what it sees at
/// trigger time - the order of dispatch, the correlation of each record and event, the state visible when an event is
/// dispatched (the shooter's ammunition), which entities were written (even unchanged) or changed in their position,
/// and whether any entity was ever seen half-linked (a member without its parent, or with a parent that is not alive).
/// It declares what it consumes, so it does not disturb the vocabulary validation of the gateway it is added to.
/// </summary>
public sealed class ProbeModule : IGatewayModule
{
    private static readonly List<string> NoViolations = new();

    private readonly PivotSchema _schema;
    private readonly List<string> _violations = new();
    private IGatewayContext _gateway = null!;

    public ProbeModule(PivotSchema schema)
    {
        _schema = schema;
    }

    public string Name => "Probe";

    /// <summary>Every record and event in the order the engine dispatched them.</summary>
    public List<DispatchEntry> Dispatches { get; } = new();

    /// <summary>The records that created entities.</summary>
    public List<RecordShape> CreatingRecords { get; } = new();

    /// <summary>For each WeaponFire event: the ammunition the shooter had when it was dispatched.</summary>
    public List<FireObservation> FireSeen { get; } = new();

    /// <summary>The dispatch order of damage changes and events: "damage:corr:entity:level", "fire:corr", "detonation:corr".</summary>
    public List<string> Order { get; } = new();

    /// <summary>Position was written (even with the same value), seen by the rule <c>Probe/SpatialWritten</c> (<c>OnAttributesWritten</c>).</summary>
    public List<SpatialObservation> PositionWritten { get; } = new();

    /// <summary>Position, orientation, velocity or dead-reckoning algorithm changed, seen by the rule <c>Probe/SpatialChanged</c> (<c>OnAttributesChanged</c>).</summary>
    public List<SpatialObservation> SpatialChanged { get; } = new();

    /// <summary>Entities seen created, with the correlation of their creating record.</summary>
    public List<(CorrelationId Correlation, EntityId Entity)> Created { get; } = new();

    /// <summary>The G-6 findings not yet taken by the checker.</summary>
    public IReadOnlyList<string> Violations => _violations;

    /// <summary>The findings since the last call (the checker reports each once).</summary>
    public List<string> TakeViolations()
    {
        if (_violations.Count == 0)
        {
            return NoViolations;
        }

        var copy = new List<string>(_violations);
        _violations.Clear();
        return copy;
    }

    public void Register(ModuleSetup setup)
    {
        var s = _schema;
        _gateway = setup.Gateway;
        setup.Consumes(s.EventShooter, s.EventId, s.Ammo, s.Parent, s.MemberIndex);
        setup.OnEvent(s.WeaponFire).Named("Fire").Do(OnFire);
        setup.OnEvent(s.Detonation).Named("Detonation").Do(t => Order.Add("detonation:" + t.Correlation.Value));
        setup.OnAttributesChanged(s.Damage).Named("Damage").Do(t =>
            Order.Add($"damage:{t.Correlation.Value}:{t.Id.Value}:{t.Entity.Get(s.Damage).AsInt()}"));
        setup.OnAnyEvent().Named("AnyEvent").Do(t => Dispatches.Add(new DispatchEntry(true, t.Correlation.Value)));
        setup.OnChangeSetEnd().Named("Record").Do(OnRecord);
        setup.OnAttributesWritten(s.Position).Named("SpatialWritten")
            .Do(t => PositionWritten.Add(new SpatialObservation(t.Correlation, t.Id, t.ChangeSet.WallTime)));
        setup.OnAttributesChanged(s.Position, s.Orientation, s.Velocity, s.DrAlgorithm).Named("SpatialChanged")
            .Do(t => SpatialChanged.Add(new SpatialObservation(t.Correlation, t.Id, t.ChangeSet.WallTime)));
        setup.OnEntityCreated().Named("Created").Do(t =>
        {
            Created.Add((t.Correlation, t.Id));
            CheckLinked(t);
        });
        setup.OnAttributesChanged(s.Parent, s.MemberIndex).Named("Linked").Do(CheckLinked);
    }

    private void OnFire(EventTrigger t)
    {
        long? ammo = null;
        EntityId shooter = default;
        if (t.Payload.TryGet(_schema.EventShooter, out var value))
        {
            shooter = value.AsEntity();
            if (_gateway.State.Find(shooter) is { } record && record.TryGet(_schema.Ammo, out var a))
            {
                ammo = a.AsInt();
            }
        }

        Guid id = t.Payload.TryGet(_schema.EventId, out var eventId) ? eventId.AsObject<Guid>() : Guid.Empty;
        FireSeen.Add(new FireObservation(t.Correlation, id, shooter, ammo));
        Order.Add("fire:" + t.Correlation.Value);
    }

    private void OnRecord(ChangeSetTrigger t)
    {
        var record = t.ChangeSet;
        Dispatches.Add(new DispatchEntry(false, record.Correlation.Value));
        int created = 0, parents = 0;
        foreach (var delta in record.Entities)
        {
            if (delta.Created)
            {
                created++;
            }

            if (delta.WasWritten(_schema.Parent))
            {
                parents++;
            }
        }

        if (created > 0)
        {
            CreatingRecords.Add(new RecordShape(record.Correlation.Value, _gateway.Sources.NameOf(record.Source), record.Count, created, parents));
        }
    }

    /// <summary>G-6: at every trigger a member has its parent and its index together, and the parent is alive.</summary>
    private void CheckLinked(EntityTrigger t)
    {
        var entity = t.Entity;
        if (!entity.IsAlive)
        {
            return;
        }

        bool parent = entity.TryGet(_schema.Parent, out var parentValue);
        bool index = entity.Has(_schema.MemberIndex);
        if (parent != index)
        {
            _violations.Add($"the probe sees entity {entity.Id.Value} with {(parent ? "Structure.Parent" : "Structure.MemberIndex")} " +
                $"but without {(parent ? "Structure.MemberIndex" : "Structure.Parent")} (correlation {t.Correlation.Value})");
        }
        else if (parent && _gateway.State.Find(parentValue.AsEntity()) is not { IsAlive: true })
        {
            _violations.Add($"the probe sees entity {entity.Id.Value} as a member of {parentValue.AsEntity().Value}, which is not alive " +
                $"(correlation {t.Correlation.Value})");
        }
    }
}
