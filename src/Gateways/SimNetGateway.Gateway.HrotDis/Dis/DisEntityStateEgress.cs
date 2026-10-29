using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// Publishes the entities of origin hrot to DIS as Entity State PDUs (DD-006 sections 6.1 and 7, R-045). The contributor rules
/// "Identity", "Spatial" and "Damage" mark what changed; rule "Flush" (change set end) sends ONE full PDU per entity when it is due
/// (first send, identity or damage change, drift beyond the thresholds); rule "Drift" (tick end) catches the drift of the held
/// dead-reckoning model between HROT states; rule "Reconcile" sends the first PDUs when the session opens; rule "Remove" sends the
/// final Deactivated PDU. Entities of origin dis are never sent (G-1, R-022). The keep-alive is <see cref="DisHeartbeatEgress"/>.
/// </summary>
public sealed class DisEntityStateEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly DisPublication _publication;
    private readonly SourceId _hrot;

    public DisEntityStateEgress(PivotSchema schema, DisPublication publication, HrotDisSources sources)
    {
        _schema = schema;
        _publication = publication;
        _hrot = sources.Hrot;
    }

    public string Name => nameof(DisEntityStateEgress);

    public void Register(ModuleSetup setup)
    {
        _publication.Attach(setup);
        _publication.IdentityRule = setup.OnAttributesChanged(_schema.EntityType, _schema.Marking, _schema.Force)
            .Named("Identity").When(IsHrot).Do(t => _publication.Mark(t.Id, DisGroups.Identity));
        _publication.SpatialRule = setup.OnAttributesChanged(_schema.Position, _schema.Orientation, _schema.Velocity, _schema.Acceleration,
                _schema.AngularVelocity, _schema.DrAlgorithm, _schema.StateTime)
            .Named("Spatial").When(IsHrot).Do(t => _publication.Mark(t.Id, DisGroups.Spatial));
        _publication.DamageRule = setup.OnAttributesChanged(_schema.Damage)
            .Named("Damage").When(IsHrot).Do(t => _publication.Mark(t.Id, DisGroups.Damage));
        setup.OnChangeSetEnd().Named("Flush").Do(_ => _publication.Flush());
        setup.OnSessionChanged().Named("Reconcile").Do(_ => _publication.Reconcile());
        setup.OnEntityRemoved().Named("Remove").When(IsHrot).Do(t => _publication.OnRemoved(t.Entity));
        setup.OnTickEnd().Named("Drift").Do(_ => _publication.SweepDrift());
    }

    private bool IsHrot(EntityTrigger t) => t.Entity.Origin == _hrot;
}

/// <summary>
/// Rule "Heartbeat" (DIS's protocol timing, DD-006 6.1): at the end of every tick, a full Entity State PDU with the position
/// predicted to now for each sent entity for which nothing was sent for <c>dis.heartbeatS</c>. It stays quiet while updates flow.
/// </summary>
public sealed class DisHeartbeatEgress : IGatewayModule
{
    private readonly DisPublication _publication;

    public DisHeartbeatEgress(DisPublication publication) => _publication = publication;

    public string Name => nameof(DisHeartbeatEgress);

    public void Register(ModuleSetup setup)
    {
        _publication.Attach(setup);
        setup.OnTickEnd().Named("Heartbeat").Do(_ => _publication.SweepHeartbeat());
    }
}
