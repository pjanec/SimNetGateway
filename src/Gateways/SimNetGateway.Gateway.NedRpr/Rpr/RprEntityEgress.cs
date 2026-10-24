using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Publishes entities to RPR with contributor rules (R-045). Rules "Identity", "Spatial" and "Damage" fill one pending
/// update per entity; rule "Flush" (change set end) registers an entity with the RTI once its type and position are known
/// and sends the pending update once it has a handle (one coalesced update per change set); rule "Registered" sends ONE full
/// update when the RTI's answer was bound; rule "Remove" deletes the object. One contributor can be disabled and replaced.
/// </summary>
public sealed class RprEntityEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly RprPublication _publication;
    private readonly SourceId _rpr;

    public RprEntityEgress(PivotSchema schema, RprPublication publication, NedRprSources sources)
    {
        _schema = schema;
        _publication = publication;
        _rpr = sources.Rpr;
    }

    public string Name => nameof(RprEntityEgress);

    public void Register(ModuleSetup setup)
    {
        _publication.Attach(setup);
        setup.Consumes(_schema.StateTime); // read for the heartbeat prediction, not forwarded (RPR has no state time)
        setup.OnAttributesChanged(_schema.Name, _schema.TypeTkb, _schema.Force)
            .Named("Identity").When(NotRpr).Do(FillIdentity);
        setup.OnAttributesChanged(_schema.Position, _schema.Orientation, _schema.Velocity, _schema.Acceleration,
                _schema.AngularVelocity, _schema.DrAlgorithm)
            .Named("Spatial").When(NotRpr).Do(FillSpatial);
        setup.OnAttributesChanged(_schema.Damage)
            .Named("Damage").When(NotRpr).Do(FillDamage);
        setup.OnChangeSetEnd().Named("Flush").Do(_ => _publication.Flush());
        setup.OnEvent(_publication.HandleBoundEvent).Named("Registered").Do(_ => _publication.CompleteRegistrations());
        setup.OnEntityRemoved().Named("Remove").When(NotRpr).Do(t => _publication.OnRemoved(t.Id));
    }

    private bool NotRpr(EntityTrigger t) => t.Entity.Origin != _rpr;

    private void FillIdentity(EntityTrigger t)
    {
        ref var pending = ref _publication.Pending(t.Id);
        var entity = t.Entity;
        if (t.Changed(_schema.Name) && entity.TryGet(_schema.Name, out var name))
        {
            pending.Marking = name.AsString();
        }

        if (t.Changed(_schema.TypeTkb) && entity.TryGet(_schema.TypeTkb, out var type))
        {
            pending.EntityType = _publication.ToDis((int)type.AsInt());
        }

        if (t.Changed(_schema.Force) && entity.TryGet(_schema.Force, out var force))
        {
            pending.ForceId = RprPublication.ToForce((int)force.AsInt());
        }
    }

    private void FillSpatial(EntityTrigger t)
    {
        ref var pending = ref _publication.Pending(t.Id);
        var entity = t.Entity;
        if (t.Changed(_schema.Position) && entity.TryGet(_schema.Position, out var position))
        {
            pending.Location = NedRprConversions.ToWorldLocation(position.AsVec3());
        }

        if (t.Changed(_schema.Orientation) && entity.TryGet(_schema.Orientation, out var orientation))
        {
            pending.Orientation = NedRprConversions.ToRprOrientation(orientation.AsVec3());
        }

        if (t.Changed(_schema.Velocity) && entity.TryGet(_schema.Velocity, out var velocity))
        {
            pending.Velocity = NedRprConversions.ToRprVector(velocity.AsVec3());
        }

        if (t.Changed(_schema.Acceleration) && entity.TryGet(_schema.Acceleration, out var acceleration))
        {
            pending.Acceleration = NedRprConversions.ToRprVector(acceleration.AsVec3());
        }

        if (t.Changed(_schema.AngularVelocity) && entity.TryGet(_schema.AngularVelocity, out var angular))
        {
            pending.AngularVelocity = NedRprConversions.ToRprVector(angular.AsVec3());
        }

        if (t.Changed(_schema.DrAlgorithm) && entity.TryGet(_schema.DrAlgorithm, out var algorithm))
        {
            pending.DrAlgorithm = (byte)algorithm.AsInt();
        }
    }

    private void FillDamage(EntityTrigger t)
    {
        if (t.Entity.TryGet(_schema.Damage, out var damage) &&
            NedRprConversions.TryDamageToRpr((int)damage.AsInt(), out var state))
        {
            _publication.Pending(t.Id).DamageState = state;
        }
    }
}
