using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Per-entity publication state of the RPR egress (spec section 2): the wall time of the last update sent and the fields of
/// the one pending update that the contributor rules fill (R-045). The registration status and token live in
/// <see cref="RprPublication.Registration"/> (Common's <see cref="AsyncRegistration{THandle}"/>, DD-009).
/// </summary>
public struct RprPublicationState
{
    /// <summary>Wall time at which the last RprObjectUpdate (egress, first full update or heartbeat) was sent.</summary>
    public double LastSendWall;

    /// <summary>The entity is queued for the next flush.</summary>
    public bool Queued;

    /// <summary>A warning about an exhausted entity number pool was already given for this entity.</summary>
    public bool AllocationWarned;

    public string? Marking;
    public DisEntityType? EntityType;
    public RprForceId? ForceId;
    public WorldLocation? Location;
    public RprOrientation? Orientation;
    public RprVector? Velocity;
    public RprVector? Acceleration;
    public RprVector? AngularVelocity;
    public byte? DrAlgorithm;
    public RprDamageState? DamageState;

    /// <summary>Damage in percent, contributed by an alternative damage contributor (R-046); the default one fills <see cref="DamageState"/>.</summary>
    public byte? DamagePercent;

    public readonly bool HasPending =>
        Marking is not null || EntityType is not null || ForceId is not null || Location is not null ||
        Orientation is not null || Velocity is not null || Acceleration is not null || AngularVelocity is not null ||
        DrAlgorithm is not null || DamageState is not null || DamagePercent is not null;

    /// <summary>The state as the inspector shows it (R-077): last send, queued flag and the names of the pending fields.</summary>
    public override readonly string ToString()
    {
        var pending = new List<string>();
        if (Marking is not null) { pending.Add("marking"); }
        if (EntityType is not null) { pending.Add("type"); }
        if (ForceId is not null) { pending.Add("force"); }
        if (Location is not null) { pending.Add("location"); }
        if (Orientation is not null) { pending.Add("orientation"); }
        if (Velocity is not null) { pending.Add("velocity"); }
        if (Acceleration is not null) { pending.Add("acceleration"); }
        if (AngularVelocity is not null) { pending.Add("angular velocity"); }
        if (DrAlgorithm is not null) { pending.Add("dr algorithm"); }
        if (DamageState is not null) { pending.Add("damage state"); }
        if (DamagePercent is not null) { pending.Add("damage percent"); }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"last send {LastSendWall:F3} s, queued {Queued}, allocation warned {AllocationWarned}, pending: {(pending.Count == 0 ? "none" : string.Join(", ", pending))}");
    }

    public void ClearPending()
    {
        Marking = null;
        EntityType = null;
        ForceId = null;
        Location = null;
        Orientation = null;
        Velocity = null;
        Acceleration = null;
        AngularVelocity = null;
        DrAlgorithm = null;
        DamageState = null;
        DamagePercent = null;
    }
}

/// <summary>
/// The RPR publication service (spec section 2), constructed by the profile and shared by <see cref="RprEntityEgress"/>,
/// <see cref="RprHeartbeatEgress"/> and <see cref="RprObjectRegisteredIngress"/> (and later modules): an
/// <see cref="EntityTable{T}"/> of <see cref="RprPublicationState"/>, the token map of pending registrations and the flush
/// logic. Registration is asynchronous (the RTI answers with a message), so an entity goes None -> Pending -> Bound.
/// </summary>
public sealed class RprPublication
{
    private const string Origin = nameof(RprPublication);

    private readonly IOutboundChannel<IRprMessage> _output;
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly TypeCatalog _catalog;
    private readonly PredictorRegistry _predictors;
    private readonly List<EntityId> _queue = new();
    private readonly AsyncRegistration<RprObjectHandle> _registration = new();
    private readonly List<EntityId> _requests = new();
    private EntityTable<RprPublicationState>? _table;
    private IGatewayContext? _gateway;

    public RprPublication(IOutboundChannel<IRprMessage> output, PivotSchema schema, NedRprMaps maps, TypeCatalog catalog,
        PredictorRegistry predictors)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(predictors);
        _output = output;
        _schema = schema;
        _maps = maps;
        _catalog = catalog;
        _predictors = predictors;
    }

    /// <summary>The asynchronous registration machine (Common, DD-009 section 3 item 2): status, tokens, arrivals and orphans of the registrations.</summary>
    public AsyncRegistration<RprObjectHandle> Registration => _registration;

    /// <summary>
    /// The internal event "a registration answer was bound to its entity". The registration transaction only binds an
    /// identity, which commits without a change set record (and so without a change-set-end trigger); the event carries the
    /// news to the egress. Valid after <see cref="Attach"/>.
    /// </summary>
    public EventTypeId HandleBoundEvent { get; private set; }

    /// <summary>
    /// The internal event "another federate asked for the full state of one of our objects" (<see cref="RprUpdateRequested"/>,
    /// R-105): ingress cannot send, so <see cref="RprUpdateRequestedResponder"/> notes the entity and publishes this event, and
    /// its rule sends the updates. Valid after <see cref="Attach"/>.
    /// </summary>
    public EventTypeId UpdateRequestedEvent { get; private set; }

    // ---- the contributor rules (R-045) --------------------------------------------------------------------------------------
    // A full update (first update after the handle arrives, heartbeat) is built through the same attribute groups as a partial
    // one: a group is present only while the rule of its contributor is enabled, so an operator override that disables a
    // contributor removes its group from full updates, too. A group whose contributor is not registered counts as enabled.

    /// <summary>The rule that contributes marking, entity type and force.</summary>
    public RuleInfo? IdentityRule { get; set; }

    /// <summary>The rule that contributes location, orientation, velocities and the dead-reckoning algorithm.</summary>
    public RuleInfo? SpatialRule { get; set; }

    /// <summary>The rule that contributes the damage state.</summary>
    public RuleInfo? DamageRule { get; set; }

    /// <summary>The rule that contributes the damage in percent; the group exists only while this rule is registered and enabled.</summary>
    public RuleInfo? DamagePercentRule { get; set; }

    private static bool IsOn(RuleInfo? rule) => rule is null || rule.Enabled;

    public EntityTable<RprPublicationState> Table => _table ?? throw NotAttached();

    private IGatewayContext Gateway => _gateway ?? throw NotAttached();

    private static InvalidOperationException NotAttached() =>
        new("The RPR publication service is not attached to an engine; a module that uses it must call Attach in Register.");

    /// <summary>Creates the entity table and the internal event type (once); every module that uses the service calls it from Register.</summary>
    public void Attach(ModuleSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (_table is not null)
        {
            return;
        }

        _gateway = setup.Gateway;
        _table = setup.CreateEntityTable<RprPublicationState>("RprPublication.State");
        HandleBoundEvent = setup.EventTypes.Register("RprPublication.HandleBound",
            "An RTI registration answer was bound to its entity (internal)");
        UpdateRequestedEvent = setup.EventTypes.Register("RprPublication.UpdateRequested",
            "An RTI federate asked for the full state of a published entity (internal)");
    }

    // ---- contributors ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The pending update of the entity, to be filled by a contributor rule; the entity is queued for the flush of the
    /// current change set. The reference is valid until the next call that adds or removes a table entry.
    /// </summary>
    public ref RprPublicationState Pending(EntityId id)
    {
        ref var state = ref Table.GetOrAdd(id);
        if (!state.Queued)
        {
            state.Queued = true;
            _queue.Add(id);
        }

        return ref state;
    }

    /// <summary>The contribution API of an alternative damage contributor: the damage in percent of the entity's pending update.</summary>
    public void ContributeDamagePercent(EntityId id, byte percent) => Pending(id).DamagePercent = percent;

    public DisEntityType ToDis(int tkb)
    {
        if (_catalog.TryToDis(tkb, out var dis))
        {
            return dis;
        }

        if (_catalog.NoteUnknownTkb(tkb))
        {
            Gateway.Diagnostics.Warn(Origin, $"Unknown TKB type {tkb}; using the default DIS type.");
        }

        return TypeCatalog.DefaultDis;
    }

    public static RprForceId ToForce(int pivot) =>
        NedRprConversions.TryForceToRpr(pivot, out var force) ? force : RprForceId.Other;

    // ---- flush ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Called at the end of a change set: for every queued entity, registers it with the RTI when it has a type and a
    /// position (None), keeps accumulating while the registration is pending, and sends one RprObjectUpdate with the pending
    /// fields once it has a handle.
    /// </summary>
    public void Flush()
    {
        var queue = _queue;
        if (queue.Count == 0)
        {
            return;
        }

        var gateway = Gateway;
        for (int i = 0; i < queue.Count; i++)
        {
            var id = queue[i];
            var entity = gateway.State.Find(id);
            ref var state = ref Table.GetOrAdd(id);
            state.Queued = false;
            if (entity is null || !entity.IsAlive)
            {
                state.ClearPending();
                continue;
            }

            switch (_registration.StatusOf(id))
            {
                case RegistrationStatus.None:
                    TryRegister(entity, ref state);
                    break;
                case RegistrationStatus.Pending:
                    break; // coalesce: the first update after the handle arrives carries everything
                case RegistrationStatus.Bound:
                    if (state.HasPending && _maps.RprHandle.TryGetKey(id, out var handle))
                    {
                        var update = new RprObjectUpdate(handle)
                        {
                            Marking = state.Marking,
                            EntityType = state.EntityType,
                            ForceId = state.ForceId,
                            WorldLocation = state.Location,
                            Orientation = state.Orientation,
                            VelocityVector = state.Velocity,
                            AccelerationVector = state.Acceleration,
                            AngularVelocity = state.AngularVelocity,
                            DeadReckoningAlgorithm = state.DrAlgorithm,
                            DamageState = state.DamageState,
                            DamagePercent = state.DamagePercent,
                        };
                        SendUpdate(update, ref state);
                    }

                    state.ClearPending();
                    break;
            }
        }

        queue.Clear();
    }

    private void TryRegister(EntityRecord entity, ref RprPublicationState state)
    {
        if (!entity.TryGet(_schema.TypeTkb, out var tkb) || !entity.Has(_schema.Position))
        {
            return; // not published until type and position are known
        }

        if (!_maps.RprEntityId.TryGetOrAllocate(entity.Id, out _))
        {
            if (!state.AllocationWarned)
            {
                state.AllocationWarned = true;
                Gateway.Diagnostics.Warn(Origin,
                    $"No DIS entity number available: entity {entity.Id.Value} is not published to RPR (retried on its next change).");
            }

            return;
        }

        long token = _registration.Request(entity.Id);
        _output.Send(new RprRegisterObject(token, ToDis((int)tkb.AsInt())));
    }

    // ---- registration answers ---------------------------------------------------------------------------------------

    /// <summary>
    /// After the registration transaction committed: marks the entities Bound and sends ONE full update each (everything the
    /// entity has, with the entity identifier), which replaces the updates that were coalesced while pending.
    /// </summary>
    public void CompleteRegistrations()
    {
        var gateway = Gateway;
        var orphans = _registration.Orphans;
        for (int i = 0; i < orphans.Count; i++)
        {
            _output.Send(new RprObjectRemoved(orphans[i]));
        }

        _registration.ClearOrphans();

        var arrivals = _registration.Arrivals;
        for (int i = 0; i < arrivals.Count; i++)
        {
            var (id, handle) = arrivals[i];
            if (!_maps.RprHandle.TryGetKey(id, out var bound) || bound != handle)
            {
                continue; // the binding did not commit
            }

            var entity = gateway.State.Find(id);
            if (entity is null || !entity.IsAlive)
            {
                continue;
            }

            if (!_registration.Complete(id, handle))
            {
                continue;
            }

            ref var state = ref Table.GetOrAdd(id);
            state.ClearPending();
            SendUpdate(BuildFull(entity, handle, predictToNow: false), ref state);
        }

        _registration.ClearArrivals();
    }

    // ---- removal ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// An entity was removed: tells RPR when the entity has a handle. An entity that was still pending is dropped; its
    /// object is deleted when the RTI answers (see <see cref="AddOrphan"/>).
    /// </summary>
    public void OnRemoved(EntityId id)
    {
        _registration.Release(id);
        if (_maps.RprHandle.TryGetKey(id, out var handle))
        {
            _output.Send(new RprObjectRemoved(handle));
        }
    }

    // ---- heartbeat ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends a full update (position predicted to now) for every bound entity for which nothing was sent for at least
    /// <paramref name="period"/> of wall time.
    /// </summary>
    public void SendHeartbeats(double wallNow, double period)
    {
        var table = Table;
        var gateway = Gateway;
        for (int i = 0; i < table.Count; i++)
        {
            ref var state = ref table.ValueAt(i);
            if (_registration.StatusOf(table.IdAt(i)) != RegistrationStatus.Bound || wallNow - state.LastSendWall < period)
            {
                continue;
            }

            var id = table.IdAt(i);
            var entity = gateway.State.Find(id);
            if (entity is null || !entity.IsAlive || !_maps.RprHandle.TryGetKey(id, out var handle))
            {
                continue;
            }

            state.ClearPending();
            SendUpdate(BuildFull(entity, handle, predictToNow: true), ref state);
        }
    }

    // ---- update requests (R-105) ---------------------------------------------------------------------------------------

    /// <summary>Notes an entity whose full state another federate asked for; sent by <see cref="SendRequestedUpdates"/>.</summary>
    public void AddUpdateRequest(EntityId entity) => _requests.Add(entity);

    /// <summary>
    /// Sends the full update (the heartbeat's: everything the entity has, position predicted to now) of every entity noted by
    /// <see cref="AddUpdateRequest"/> that is bound. Returns how many were sent.
    /// </summary>
    public int SendRequestedUpdates()
    {
        int sent = 0;
        var gateway = Gateway;
        for (int i = 0; i < _requests.Count; i++)
        {
            var id = _requests[i];
            ref var state = ref Table.GetOrAdd(id);
            var entity = gateway.State.Find(id);
            if (_registration.StatusOf(id) != RegistrationStatus.Bound || entity is null || !entity.IsAlive || !_maps.RprHandle.TryGetKey(id, out var handle))
            {
                continue;
            }

            state.ClearPending();
            SendUpdate(BuildFull(entity, handle, predictToNow: true), ref state);
            sent++;
        }

        _requests.Clear();
        return sent;
    }

    // ---- building and sending --------------------------------------------------------------------------------------

    /// <summary>Sends an update and records the send time of its entity (every send counts for the heartbeat).</summary>
    public void SendUpdate(RprObjectUpdate update, ref RprPublicationState state)
    {
        state.LastSendWall = Gateway.WallTime;
        _output.Send(update);
    }

    /// <summary>
    /// Every attribute the entity has, plus the entity identifier; with <paramref name="predictToNow"/> the position is the
    /// dead-reckoned one at the current simulation time (R-028), everything else is as stored.
    /// </summary>
    public RprObjectUpdate BuildFull(EntityRecord e, RprObjectHandle handle, bool predictToNow)
    {
        var s = _schema;
        bool identity = IsOn(IdentityRule);
        bool spatial = IsOn(SpatialRule);
        string? marking = identity && e.TryGet(s.Name, out var name) ? name.AsString() : null;
        DisEntityType? type = identity && e.TryGet(s.TypeTkb, out var tkb) ? ToDis((int)tkb.AsInt()) : null;
        RprForceId? force = identity && e.TryGet(s.Force, out var f) ? ToForce((int)f.AsInt()) : null;
        DisEntityId? entityId = _maps.RprEntityId.TryGetKey(e.Id, out var identifier) ? identifier : null;
        WorldLocation? location = null;
        if (spatial && e.TryGet(s.Position, out var position))
        {
            var p = predictToNow ? Predict(e, position.AsVec3(), Gateway.SimTime) : position.AsVec3();
            location = NedRprConversions.ToWorldLocation(p);
        }

        RprOrientation? orientation = spatial && e.TryGet(s.Orientation, out var o) ? NedRprConversions.ToRprOrientation(o.AsVec3()) : null;
        RprVector? velocity = spatial && e.TryGet(s.Velocity, out var v) ? NedRprConversions.ToRprVector(v.AsVec3()) : null;
        RprVector? acceleration = spatial && e.TryGet(s.Acceleration, out var a) ? NedRprConversions.ToRprVector(a.AsVec3()) : null;
        RprVector? angular = spatial && e.TryGet(s.AngularVelocity, out var w) ? NedRprConversions.ToRprVector(w.AsVec3()) : null;
        byte? algorithm = spatial && e.TryGet(s.DrAlgorithm, out var alg) ? (byte)alg.AsInt() : null;
        RprDamageState? damage = IsOn(DamageRule) && e.TryGet(s.Damage, out var d) &&
                                 NedRprConversions.TryDamageToRpr((int)d.AsInt(), out var state)
            ? state
            : null;
        byte? percent = DamagePercentRule is { Enabled: true } && e.TryGet(s.Damage, out var level) &&
                        NedRprConversions.IsValidDamage((int)level.AsInt())
            ? CustomerDamagePercent.PercentOfLevel[level.AsInt()]
            : null;
        return new RprObjectUpdate(handle)
        {
            Marking = marking,
            EntityType = type,
            ForceId = force,
            EntityIdentifier = entityId,
            WorldLocation = location,
            Orientation = orientation,
            VelocityVector = velocity,
            AccelerationVector = acceleration,
            AngularVelocity = angular,
            DeadReckoningAlgorithm = algorithm,
            DamageState = damage,
            DamagePercent = percent,
        };
    }

    /// <summary>The entity's stored position dead-reckoned to the simulation time <paramref name="simNow"/> (the stored one when no predictor applies).</summary>
    private Vec3 Predict(EntityRecord e, Vec3 position, double simNow)
    {
        var s = _schema;
        if (!e.TryGet(s.DrAlgorithm, out var algorithm) || !e.TryGet(s.StateTime, out var stateTime) ||
            !_predictors.TryGet((int)algorithm.AsInt(), out var predictor))
        {
            return position;
        }

        var basis = new PredictionBasis(
            position,
            ReadVec(e, s.Velocity),
            ReadVec(e, s.Acceleration),
            ReadVec(e, s.Orientation),
            ReadVec(e, s.AngularVelocity),
            stateTime.AsDouble());
        return predictor.Predict(in basis, simNow).Position;
    }

    private static Vec3 ReadVec(EntityRecord e, AttrId attr) => e.TryGet(attr, out var v) ? v.AsVec3() : Vec3.Zero;
}
