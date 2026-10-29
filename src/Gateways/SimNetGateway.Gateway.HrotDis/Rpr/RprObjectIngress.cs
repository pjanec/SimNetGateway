using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// RprObjectUpdate: creates or updates the pivot entity of origin rpr that the object handle names and binds <c>rpr.handle</c> (DD-009
/// section 3 item 3). The pivot is RPR aligned, so the attributes map 1:1 and only those the update carries are stored: the type is
/// packed, marking and force are copied, location / orientation / velocity / acceleration / angular velocity / dead-reckoning
/// algorithm are stored as they are on the wire (an algorithm without a predictor is read as Static), the damage state is the pivot's
/// damage level, and the state time is the engine clock at receipt (the one time axis of the pivot). An update of an object that
/// belongs to a hrot entity (a handle the RTI answered to the gateway) is an echo and ignored; the entity identifier is not used.
/// </summary>
public sealed class RprObjectUpdateIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;

    public RprObjectUpdateIngress(PivotSchema schema, HrotDisMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(RprObjectUpdateIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.EntityType, _schema.Marking, _schema.Force, _schema.Position, _schema.Orientation, _schema.Velocity,
            _schema.Acceleration, _schema.AngularVelocity, _schema.DrAlgorithm, _schema.StateTime, _schema.Damage);
        setup.OnMessage<RprObjectUpdate>(Handle);
    }

    private void Handle(in RprObjectUpdate m, IngressContext ctx)
    {
        // Validate before anything is created, so a bad update leaves no trace (the abort discards the transaction anyway).
        if (m.DamageState is { } state && !RprConversions.IsValid(state))
        {
            ctx.Abort($"damage state {(int)state} out of range");
            return;
        }

        if ((m.WorldLocation is { } l && !RprConversions.IsFinite(l)) || (m.Orientation is { } o && !RprConversions.IsFinite(o)) ||
            (m.VelocityVector is { } v && !RprConversions.IsFinite(v)) || (m.AccelerationVector is { } a && !RprConversions.IsFinite(a)) ||
            (m.AngularVelocity is { } w && !RprConversions.IsFinite(w)))
        {
            ctx.Abort($"object {m.Handle.Value}: a value is not finite");
            return;
        }

        var handle = m.Handle;
        if (HrotIngress.Resolve(ctx, _maps.RprHandle, in handle, create: true, out var id) == EntityResolution.Echo)
        {
            return;
        }

        var changes = ctx.Changes;
        if (m.Marking is { } marking)
        {
            changes.Set(id, _schema.Marking, AttrValue.From(marking));
        }

        if (m.EntityType is { } type)
        {
            changes.Set(id, _schema.EntityType, AttrValue.From(RprConversions.PackType(type)));
        }

        if (m.ForceId is { } force)
        {
            changes.Set(id, _schema.Force, AttrValue.From((long)force));
        }

        bool spatial = false;
        if (m.WorldLocation is { } location)
        {
            changes.Set(id, _schema.Position, AttrValue.From(RprConversions.ToVec(location)));
            spatial = true;
        }

        if (m.Orientation is { } orientation)
        {
            changes.Set(id, _schema.Orientation, AttrValue.From(RprConversions.ToVec(orientation)));
            spatial = true;
        }

        if (m.VelocityVector is { } velocity)
        {
            changes.Set(id, _schema.Velocity, AttrValue.From(RprConversions.ToVec(velocity)));
            spatial = true;
        }

        if (m.AccelerationVector is { } acceleration)
        {
            changes.Set(id, _schema.Acceleration, AttrValue.From(RprConversions.ToVec(acceleration)));
            spatial = true;
        }

        if (m.AngularVelocity is { } angular)
        {
            changes.Set(id, _schema.AngularVelocity, AttrValue.From(RprConversions.ToVec(angular)));
            spatial = true;
        }

        if (m.DeadReckoningAlgorithm is { } algorithm)
        {
            changes.Set(id, _schema.DrAlgorithm, AttrValue.From((long)DisConversions.NormalizeAlgorithm(algorithm)));
            spatial = true;
        }

        if (spatial)
        {
            changes.Set(id, _schema.StateTime, AttrValue.From(ctx.ReceivedAt)); // one time axis in the pivot: the engine clock at receipt
        }

        if (m.DamageState is { } damage)
        {
            changes.Set(id, _schema.Damage, AttrValue.From((long)damage));
        }
    }
}

/// <summary>RprObjectRemoved: destroys the pivot entity of origin rpr that the handle names; an unknown handle, or one of a hrot entity, is ignored.</summary>
public sealed class RprObjectRemovedIngress : IGatewayModule
{
    private readonly HrotDisMaps _maps;

    public RprObjectRemovedIngress(HrotDisMaps maps) => _maps = maps;

    public string Name => nameof(RprObjectRemovedIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<RprObjectRemoved>(Handle);

    private void Handle(in RprObjectRemoved m, IngressContext ctx)
    {
        var handle = m.Handle;
        if (HrotIngress.Resolve(ctx, _maps.RprHandle, in handle, create: false, out var id) == EntityResolution.Existing)
        {
            ctx.Changes.DestroyEntity(id);
        }
    }
}

/// <summary>
/// RprObjectRegistered: the RTI answered a registration request. Binds the handle to the entity that requested the token (in this
/// transaction) and publishes the internal "handle bound" event; <see cref="RprEntityEgress"/> sends the first full update on it. A
/// transaction that only binds has no change set record and so no change-set-end trigger, which is why an event carries the news.
/// An answer for an entity that was removed meanwhile makes the object an orphan, deleted again by the publication; an answer for an
/// unknown token is a warning.
/// </summary>
public sealed class RprObjectRegisteredIngress : IGatewayModule
{
    private readonly HrotDisMaps _maps;
    private readonly RprPublication _publication;
    private EventTypeId _handleBound;

    public RprObjectRegisteredIngress(HrotDisMaps maps, RprPublication publication)
    {
        _maps = maps;
        _publication = publication;
    }

    public string Name => nameof(RprObjectRegisteredIngress);

    public void Register(ModuleSetup setup)
    {
        _publication.Attach(setup);
        _handleBound = _publication.HandleBoundEvent;
        setup.Produces(_handleBound);
        setup.OnMessage<RprObjectRegistered>(Handle);
    }

    private void Handle(in RprObjectRegistered m, IngressContext ctx)
    {
        var answer = _publication.Registration.OnRegistered(m.Token, m.Handle, out var entity, id => ctx.State.Find(id) is { IsAlive: true });
        switch (answer)
        {
            case RegistrationAnswer.Unknown:
                ctx.Diagnostics.Warn(Name, $"Registration answer for unknown token {m.Token} (handle {m.Handle.Value}); ignored.");
                return;
            case RegistrationAnswer.Accepted:
                _maps.RprHandle.Bind(ctx.Changes, entity, m.Handle);
                break;
        }

        ctx.Publish(_handleBound, ctx.RentPayload());
    }
}

/// <summary>
/// RprUpdateRequested (R-105, DD-008 section 3.4): another federate asked for the attributes of one of our objects. Ingress cannot
/// send, so the handler notes the entity (one of our own publications; an unknown handle or an rpr entity is ignored) and publishes
/// the internal "update requested" event; rule "Send" answers with a full update, built the way every full update is.
/// </summary>
public sealed class RprUpdateRequestedResponder : IGatewayModule
{
    private readonly HrotDisMaps _maps;
    private readonly RprPublication _publication;
    private EventTypeId _requested;

    public RprUpdateRequestedResponder(HrotDisMaps maps, RprPublication publication)
    {
        _maps = maps;
        _publication = publication;
    }

    public string Name => nameof(RprUpdateRequestedResponder);

    public void Register(ModuleSetup setup)
    {
        _publication.Attach(setup);
        _requested = _publication.UpdateRequestedEvent;
        setup.Produces(_requested);
        setup.OnMessage<RprUpdateRequested>(Handle);
        setup.OnEvent(_requested).Named("Send").Do(_ => _publication.SendRequestedUpdates());
    }

    private void Handle(in RprUpdateRequested m, IngressContext ctx)
    {
        var handle = m.Handle;
        // Resolve reports an entity of another origin than rpr as Echo: that is one of our own publications.
        if (HrotIngress.Resolve(ctx, _maps.RprHandle, in handle, create: false, out var entity) != EntityResolution.Echo)
        {
            return;
        }

        _publication.AddUpdateRequest(entity);
        ctx.Publish(_requested, ctx.RentPayload());
    }
}
