using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// Publishes the entities of origin hrot to HLA (DD-009 section 3 item 3, the RPR counterpart of <see cref="DisEntityStateEgress"/>).
/// The contributor rules "Identity", "Spatial" and "Damage" mark what changed; rule "Flush" (change set end) registers an entity with
/// the RTI once its type and position are known and sends ONE full update per bound entity when it is due (identity or damage
/// change, drift beyond the thresholds); rule "Registered" sends the first full update when the RTI's answer was bound; rule
/// "Reconcile" registers the entities that waited for the session to open; rule "Remove" deletes the object; rule "Sweep" (tick end)
/// sends the update of every entity whose held model drifted beyond the thresholds or whose heartbeat is due. Entities of origin dis
/// and rpr are never published (R-116, no relaying and no echo).
/// </summary>
public sealed class RprEntityEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly RprPublication _publication;
    private readonly SourceId _hrot;

    public RprEntityEgress(PivotSchema schema, RprPublication publication, HrotDisSources sources)
    {
        _schema = schema;
        _publication = publication;
        _hrot = sources.Hrot;
    }

    public string Name => nameof(RprEntityEgress);

    public void Register(ModuleSetup setup)
    {
        _publication.Attach(setup);
        _publication.IdentityRule = setup.OnAttributesChanged(_schema.EntityType, _schema.Marking, _schema.Force)
            .Named("Identity").When(IsHrot).Do(t => _publication.Mark(t.Id, RprGroups.Identity));
        _publication.SpatialRule = setup.OnAttributesChanged(_schema.Position, _schema.Orientation, _schema.Velocity, _schema.Acceleration,
                _schema.AngularVelocity, _schema.DrAlgorithm, _schema.StateTime)
            .Named("Spatial").When(IsHrot).Do(t => _publication.Mark(t.Id, RprGroups.Spatial));
        _publication.DamageRule = setup.OnAttributesChanged(_schema.Damage)
            .Named("Damage").When(IsHrot).Do(t => _publication.Mark(t.Id, RprGroups.Damage));
        setup.OnChangeSetEnd().Named("Flush").Do(_ => _publication.Flush());
        setup.OnEvent(_publication.HandleBoundEvent).Named("Registered").Do(_ => _publication.CompleteRegistrations());
        setup.OnSessionChanged().Named("Reconcile").Do(_ => _publication.Reconcile());
        setup.OnEntityRemoved().Named("Remove").When(IsHrot).Do(t => _publication.OnRemoved(t.Id));
        setup.OnTickEnd().Named("Sweep").Do(_ => _publication.Sweep());
    }

    private bool IsHrot(EntityTrigger t) => t.Entity.Origin == _hrot;
}
