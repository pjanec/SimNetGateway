using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>The attribute groups a contributor rule can mark as changed (R-045). The update is always built in full; the groups say why a send is due.</summary>
[Flags]
public enum RprGroups : byte
{
    None = 0,
    Identity = 1,
    Spatial = 2,
    Damage = 4,
}

/// <summary>
/// Per-entity state of the HROT gateway's RPR publication: the groups marked since the last flush, the dead-reckoning model a
/// federate holds after the last update (the values as they travel: single precision where the FOM has it), and the number of
/// updates sent. The registration status lives in <see cref="RprPublication.Registration"/> (Common's
/// <see cref="AsyncRegistration{THandle}"/>).
/// </summary>
public struct RprPublicationState
{
    public bool Queued;
    public bool AllocationWarned;
    public RprGroups Pending;

    /// <summary>Full updates sent for the entity (the first one is sent when the registration answer is bound).</summary>
    public int Sends;

    /// <summary>The model a federate holds after the last update (its <see cref="SentModel.Time"/> is the wall time of that update).</summary>
    public SentModel Sent;

    /// <summary>The state as the inspector shows it (R-077).</summary>
    public override readonly string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"sends {Sends}, last send {Sent.Time:F3} s, queued {Queued}, pending {Pending}, held algorithm {Sent.Algorithm}");
}

/// <summary>
/// The RPR publication service of the HROT gateway (DD-009 section 3 item 3, R-117): the logic of when and what to send to the
/// HLA side for the entities of origin hrot, built from the two shared pieces. Registration is Common's
/// <see cref="AsyncRegistration{THandle}"/> (None -> Pending -> Bound; the first full update goes out when the RTI's answer is
/// bound, what changes while it is pending is coalesced into that update); the send policy is Common's
/// <see cref="DeadReckonedSendPolicy"/> with the <c>hla</c> thresholds and heartbeat (never the dis section's). A send is ONE
/// <see cref="RprObjectUpdate"/> carrying every attribute of every enabled contributor, the position predicted to now (R-028), and
/// is due when
/// <list type="bullet">
/// <item>the registration answer was bound (first update),</item>
/// <item>identity or damage changed,</item>
/// <item>the federate's dead-reckoning prediction of the last update drifted beyond <c>hla.positionThresholdM</c> /
/// <c>hla.orientationThresholdDeg</c> (checked on every change of the spatial state and at the end of every tick),</item>
/// <item>nothing was sent for <c>hla.heartbeatS</c> (end of every tick), or another federate asked for the object (R-105).</item>
/// </list>
/// The invariant "after every tick the held model is within the thresholds and younger than the heartbeat" therefore holds exactly,
/// as for the DIS side.
/// </summary>
public sealed class RprPublication
{
    private const string Origin = nameof(RprPublication);

    private readonly IOutboundChannel<IRprMessage> _output;
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;
    private readonly DeadReckonedSendPolicy _policy;
    private readonly AsyncRegistration<RprObjectHandle> _registration = new();
    private readonly List<EntityId> _queue = new();
    private readonly List<EntityId> _requests = new();
    private readonly List<EntityRecord> _scratch = new();
    private EntityTable<RprPublicationState>? _table;
    private IGatewayContext? _gateway;

    public RprPublication(IOutboundChannel<IRprMessage> output, PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, HrotDisServices services)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(services);
        var hla = services.Config.Hla ?? throw new InvalidOperationException("The HLA side is not configured.");
        _output = output;
        _schema = schema;
        _maps = maps;
        _sources = sources;
        _services = services;
        _policy = new DeadReckonedSendPolicy(
            new DrAttributeSet(schema.Position, schema.Velocity, schema.Acceleration, schema.Orientation, schema.AngularVelocity,
                schema.DrAlgorithm, schema.StateTime),
            SendPolicySettings.FromDegrees(hla.PositionThresholdM, hla.OrientationThresholdDeg, hla.HeartbeatS),
            services.Predictors);
    }

    /// <summary>The send policy of the HLA side (its own instance and thresholds; the DIS side has another).</summary>
    public DeadReckonedSendPolicy Policy => _policy;

    /// <summary>The asynchronous registration machine: status, tokens, arrivals and orphans of the registrations with the RTI.</summary>
    public AsyncRegistration<RprObjectHandle> Registration => _registration;

    /// <summary>The internal event "a registration answer was bound to its entity" (the registration transaction only binds an identity, which has no change-set-end trigger).</summary>
    public EventTypeId HandleBoundEvent { get; private set; }

    /// <summary>The internal event "another federate asked for the full state of one of our objects" (R-105).</summary>
    public EventTypeId UpdateRequestedEvent { get; private set; }

    // ---- the contributor rules (R-045) --------------------------------------------------------------------------------------
    // A group whose contributor rule is disabled is left out of every update, so an operator override can disable a contributor and
    // replace it; a group whose contributor is not registered counts as enabled.

    public RuleInfo? IdentityRule { get; set; }

    public RuleInfo? SpatialRule { get; set; }

    public RuleInfo? DamageRule { get; set; }

    private static bool IsOn(RuleInfo? rule) => rule is null || rule.Enabled;

    public EntityTable<RprPublicationState> Table => _table ?? throw NotAttached();

    private IGatewayContext Gateway => _gateway ?? throw NotAttached();

    private static InvalidOperationException NotAttached() =>
        new("The RPR publication service is not attached to an engine; a module that uses it must call Attach in Register.");

    /// <summary>True while the session shows HROT entities to HLA (Running or Paused).</summary>
    public bool IsOpen => _services.HlaSessionOpen(Gateway.Session);

    /// <summary>Creates the entity table and the internal event types (once); every module that uses the service calls it from Register.</summary>
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
            "An RTI registration answer was bound to its hrot entity (internal)");
        UpdateRequestedEvent = setup.EventTypes.Register("RprPublication.UpdateRequested",
            "An RTI federate asked for the full state of a published hrot entity (internal)");
    }

    // ---- contributors -------------------------------------------------------------------------------------------------

    /// <summary>A contributor marks groups of the entity as changed; the entity is queued for the flush of the current change set.</summary>
    public void Mark(EntityId id, RprGroups groups)
    {
        ref var state = ref Table.GetOrAdd(id);
        state.Pending |= groups;
        if (!state.Queued)
        {
            state.Queued = true;
            _queue.Add(id);
        }
    }

    /// <summary>
    /// Called at the end of a change set: for every queued entity of origin hrot, registers it with the RTI when it has none yet and
    /// is ready (None), keeps coalescing while the registration is pending, and for a bound entity sends a full update when its
    /// identity or damage changed or its spatial change drifted beyond the thresholds.
    /// </summary>
    public void Flush()
    {
        var queue = _queue;
        if (queue.Count == 0)
        {
            return;
        }

        var gateway = Gateway;
        double now = gateway.WallTime;
        for (int i = 0; i < queue.Count; i++)
        {
            var id = queue[i];
            if (!Table.Contains(id))
            {
                continue;
            }

            ref var state = ref Table.GetOrAdd(id);
            state.Queued = false;
            var groups = state.Pending;
            state.Pending = RprGroups.None;
            var entity = gateway.State.Find(id);
            if (entity is null || !entity.IsAlive || entity.Origin != _sources.Hrot || !IsOpen || !IsEligible(entity))
            {
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
                    if ((groups & (RprGroups.Identity | RprGroups.Damage)) != 0 ||
                        ((groups & RprGroups.Spatial) != 0 && _policy.Drifted(entity, in state.Sent, now)))
                    {
                        SendFull(entity, ref state);
                    }

                    break;
            }
        }

        queue.Clear();
    }

    /// <summary>The session changed: when it is open, registers every ready hrot entity that waited for it and sends the first update of one that was bound meanwhile.</summary>
    public void Reconcile()
    {
        if (!IsOpen)
        {
            return;
        }

        var gateway = Gateway;
        gateway.State.Query().FromOrigin(_sources.Hrot).CopyTo(_scratch);
        try
        {
            foreach (var entity in _scratch)
            {
                if (!entity.IsAlive || !IsEligible(entity))
                {
                    continue;
                }

                ref var state = ref Table.GetOrAdd(entity.Id);
                switch (_registration.StatusOf(entity.Id))
                {
                    case RegistrationStatus.None:
                        TryRegister(entity, ref state);
                        break;
                    case RegistrationStatus.Bound when state.Sends == 0:
                        SendFull(entity, ref state);
                        break;
                }
            }
        }
        finally
        {
            _scratch.Clear();
        }
    }

    /// <summary>The entity has what a published entity needs (type and position with their time), the origin is valid and a unit is allowed.</summary>
    public bool IsEligible(EntityRecord entity) => Publishable.IsEligible(_schema, _services, entity);

    private void TryRegister(EntityRecord entity, ref RprPublicationState state)
    {
        if (!_maps.DisEntity.TryGetOrAllocate(entity.Id, out _))
        {
            if (!state.AllocationWarned)
            {
                state.AllocationWarned = true;
                Gateway.Diagnostics.Warn(Origin,
                    $"No RPR entity number available: entity {entity.Id.Value} is not published to HLA (retried on its next change).");
            }

            return;
        }

        long token = _registration.Request(entity.Id);
        _output.Send(new RprRegisterObject(token, RprConversions.ToRprType(entity.Get(_schema.EntityType).AsInt())));
    }

    // ---- registration answers -----------------------------------------------------------------------------------------

    /// <summary>
    /// After the registration transaction committed: confirms the arrivals whose binding committed (the entity becomes Bound) and
    /// sends ONE full update each (everything the entity has, with the entity identifier), which replaces the updates that were
    /// coalesced while pending; deletes the objects whose entity was removed meanwhile.
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
            if (entity is null || !entity.IsAlive || !_registration.Complete(id, handle))
            {
                continue;
            }

            ref var state = ref Table.GetOrAdd(id);
            state.Pending = RprGroups.None;
            if (IsOpen)
            {
                SendFull(entity, ref state);
            }
        }

        _registration.ClearArrivals();
    }

    // ---- end of tick ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// End of tick: a full update for every bound entity for which the policy says a send is due (the held model drifted beyond a
    /// threshold, or nothing was sent for the heartbeat period). At most one update per entity per tick: a send makes the entity
    /// not due.
    /// </summary>
    public void Sweep()
    {
        if (!IsOpen)
        {
            return;
        }

        var table = Table;
        var gateway = Gateway;
        double now = gateway.WallTime;
        for (int i = 0; i < table.Count; i++)
        {
            var id = table.IdAt(i);
            if (_registration.StatusOf(id) != RegistrationStatus.Bound)
            {
                continue;
            }

            ref var state = ref table.ValueAt(i);
            var entity = gateway.State.Find(id);
            if (entity is not null && entity.IsAlive && entity.Origin == _sources.Hrot &&
                (state.Sends == 0 || _policy.NeedsSend(entity, in state.Sent, now)))
            {
                SendFull(entity, ref state);
            }
        }
    }

    // ---- removal ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A hrot entity was removed: tells HLA when the entity has a handle. An entity that was still pending is dropped; its object is
    /// deleted when the RTI answers (the registration reports the answer as released).
    /// </summary>
    public void OnRemoved(EntityId id)
    {
        _registration.Release(id);
        if (_maps.RprHandle.TryGetKey(id, out var handle))
        {
            _output.Send(new RprObjectRemoved(handle));
        }
    }

    // ---- update requests (R-105) ----------------------------------------------------------------------------------------

    /// <summary>Notes an entity whose full state another federate asked for; sent by <see cref="SendRequestedUpdates"/>.</summary>
    public void AddUpdateRequest(EntityId entity) => _requests.Add(entity);

    /// <summary>Sends the full update of every entity noted by <see cref="AddUpdateRequest"/> that is bound. Returns how many were sent.</summary>
    public int SendRequestedUpdates()
    {
        int sent = 0;
        var gateway = Gateway;
        for (int i = 0; i < _requests.Count; i++)
        {
            var id = _requests[i];
            var entity = gateway.State.Find(id);
            if (_registration.StatusOf(id) != RegistrationStatus.Bound || entity is null || !entity.IsAlive || entity.Origin != _sources.Hrot)
            {
                continue;
            }

            ref var state = ref Table.GetOrAdd(id);
            SendFull(entity, ref state);
            sent++;
        }

        _requests.Clear();
        return sent;
    }

    // ---- building and sending ---------------------------------------------------------------------------------------------

    private void SendFull(EntityRecord entity, ref RprPublicationState state)
    {
        if (!_maps.RprHandle.TryGetKey(entity.Id, out var handle))
        {
            return;
        }

        double now = Gateway.WallTime;
        var update = BuildFull(entity, handle, now, out var model);
        _output.Send(update);
        state.Sends++;
        state.Sent = model;
    }

    /// <summary>
    /// One full update of the entity at <paramref name="now"/>: every attribute of every enabled contributor, the entity identifier,
    /// the position (and with it velocity and orientation) dead-reckoned from the pivot's state time to now. <paramref name="model"/>
    /// is what a federate holds after it (single precision where the FOM has it).
    /// </summary>
    public RprObjectUpdate BuildFull(EntityRecord entity, RprObjectHandle handle, double now, out SentModel model)
    {
        var s = _schema;
        string? marking = null;
        DisEntityType? type = null;
        RprForceId? force = null;
        if (IsOn(IdentityRule))
        {
            marking = entity.TryGet(s.Marking, out var m) ? m.AsString() : null;
            type = entity.TryGet(s.EntityType, out var t) ? RprConversions.ToRprType(t.AsInt()) : null;
            force = entity.TryGet(s.Force, out var f) ? RprConversions.ForceOf(f.AsInt()) : null;
        }

        DisEntityId? identifier = _maps.DisEntity.TryGetKey(entity.Id, out var key) ? new DisEntityId(key.Site, key.Application, key.Number) : null;
        WorldLocation? location = null;
        RprOrientation? orientation = null;
        RprVector? velocity = null, acceleration = null, angular = null;
        byte? algorithm = null;
        model = default;
        if (DrWire.TryProject(_policy, entity, now, out var k))
        {
            var v = RprConversions.Single(k.Velocity);
            var a = RprConversions.Single(k.Acceleration);
            var o = RprConversions.Single(k.Orientation);
            var w = RprConversions.Single(k.AngularVelocity);
            // the held model is recorded even while the spatial contributor is disabled, so the policy does not send on every tick
            model = SentModel.Of(k.Position, v, a, o, w, k.Algorithm, now);
            if (IsOn(SpatialRule))
            {
                location = RprConversions.ToLocation(k.Position);
                orientation = RprConversions.ToOrientation(o);
                velocity = RprConversions.ToVector(v);
                acceleration = RprConversions.ToVector(a);
                angular = RprConversions.ToVector(w);
                algorithm = (byte)k.Algorithm;
            }
        }

        RprDamageState? damage = IsOn(DamageRule) && entity.TryGet(s.Damage, out var d) ? RprConversions.DamageOf(d.AsInt()) : null;
        return new RprObjectUpdate(handle)
        {
            Marking = marking,
            EntityType = type,
            ForceId = force,
            EntityIdentifier = identifier,
            WorldLocation = location,
            Orientation = orientation,
            VelocityVector = velocity,
            AccelerationVector = acceleration,
            AngularVelocity = angular,
            DeadReckoningAlgorithm = algorithm,
            DamageState = damage,
        };
    }
}
