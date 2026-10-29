using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// Sends <c>ReplicaState</c> for entities of origin dis (DD-007 section 3 item 4): rule "State" when a new DIS state arrived (the
/// state time, the type, marking, force, damage or deactivation changed - never per tick, never for another origin, R-022);
/// rule "Rebase" when the HROT origin became valid or changed (every replica again, in the new frame); rule "Reconcile" when the
/// session changes (introduces what waited for an open session; closing it, Idle or Replay, unloads and so removes the replicas).
/// </summary>
public sealed class ReplicaStateEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisSources _sources;
    private readonly ReplicaPublisher _publisher;

    public ReplicaStateEgress(PivotSchema schema, HrotDisSources sources, ReplicaPublisher publisher)
    {
        _schema = schema;
        _sources = sources;
        _publisher = publisher;
    }

    public string Name => nameof(ReplicaStateEgress);

    public void Register(ModuleSetup setup)
    {
        // Read when a state is built; the trigger list below is deliberately only "a new DIS state".
        setup.Consumes(_schema.Position, _schema.Orientation, _schema.Velocity, _schema.Acceleration, _schema.AngularVelocity,
            _schema.DrAlgorithm);

        setup.OnAttributesChanged(_schema.StateTime, _schema.EntityType, _schema.Marking, _schema.Force, _schema.Damage, _schema.Deactivated)
            .Named("State")
            .When(t => t.Entity.Origin == _sources.Dis)
            .Do(t => _publisher.Publish(t.Entity));

        setup.OnEvent(_schema.FrameRebased)
            .Named("Rebase")
            .Do(_ => _publisher.PublishAll(onlyNew: false));

        setup.OnSessionChanged()
            .Named("Reconcile")
            .Do(_ => _publisher.Reconcile());
    }
}

/// <summary>Rule "Remove": an entity of origin dis that HROT knows was removed: <c>ReplicaRemoved</c> (its HROT id is released afterwards).</summary>
public sealed class ReplicaRemovedEgress : IGatewayModule
{
    private readonly HrotDisSources _sources;
    private readonly ReplicaPublisher _publisher;

    public ReplicaRemovedEgress(HrotDisSources sources, ReplicaPublisher publisher)
    {
        _sources = sources;
        _publisher = publisher;
    }

    public string Name => nameof(ReplicaRemovedEgress);

    public void Register(ModuleSetup setup) =>
        setup.OnEntityRemoved()
            .Named("Remove")
            .When(t => t.Entity.Origin == _sources.Dis)
            .Do(t => _publisher.Remove(t.Id));
}
