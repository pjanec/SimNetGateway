using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Dis7;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>Where a HROT entity is on its way to DIS.</summary>
public enum DisPublicationStatus : byte
{
    /// <summary>No Entity State PDU sent yet (type or position unknown, session closed, no entity number available).</summary>
    None,

    /// <summary>At least one Entity State PDU was sent; the entity has a DIS entity id.</summary>
    Sent,

    /// <summary>The final (Deactivated) Entity State PDU was sent.</summary>
    Removed,
}

/// <summary>The attribute groups a contributor rule can mark as changed (R-045). The PDU is always built in full; the groups say why a send is due.</summary>
[Flags]
public enum DisGroups : byte
{
    None = 0,
    Identity = 1,
    Spatial = 2,
    Damage = 4,
}

/// <summary>
/// Per-entity publication state of the DIS egress: the status, the wall time of the last send, the groups marked since the last
/// flush, and the dead-reckoning model a receiver holds after the last Entity State PDU (the values as they went on the wire, so
/// float rounding is part of the model).
/// </summary>
public struct DisPublicationState
{
    public DisPublicationStatus Status;

    /// <summary>The dead-reckoning model a receiver holds after the last Entity State PDU (its <see cref="SentModel.Time"/> is the wall time of that PDU).</summary>
    public SentModel Sent;

    public bool Queued;
    public bool AllocationWarned;
    public DisGroups Pending;

    /// <summary>Entity State PDUs sent for the entity (the final one included).</summary>
    public int Sends;

    /// <summary>Wall time of the last Entity State PDU.</summary>
    public readonly double LastSendWall => Sent.Time;

    /// <summary>The state as the inspector shows it (R-077).</summary>
    public override readonly string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"status {Status}, sends {Sends}, last send {LastSendWall:F3} s, queued {Queued}, pending {Pending}, held algorithm {Sent.Algorithm}");
}

/// <summary>
/// The DIS publication service, shared by <see cref="DisEntityStateEgress"/> and <see cref="DisHeartbeatEgress"/>: an
/// <see cref="EntityTable{T}"/> of <see cref="DisPublicationState"/> and the logic of when and what to send for the entities of
/// origin hrot (DD-006 sections 6.1 and 7). DIS has no partial update, so a send is always ONE full Entity State PDU assembled from
/// all contributor groups out of the entity's current state, its position predicted to now (R-028). A send is due when
/// <list type="bullet">
/// <item>the entity has not been sent yet and its type and position are known in an open session (first send),</item>
/// <item>its identity (type, marking, force) or damage changed,</item>
/// <item>the DIS dead-reckoning prediction of the last sent PDU is farther from the truth than <c>dis.positionThresholdM</c> or
/// <c>dis.orientationThresholdDeg</c> (checked on every change of the spatial state and at the end of every tick),</item>
/// <item>nothing was sent for <c>dis.heartbeatS</c> (checked at the end of every tick),</item>
/// <item>the entity was removed (the final PDU with the Deactivated appearance, exactly once).</item>
/// </list>
/// The drift check is on demand, not a time-sliced processor: it writes nothing to the pivot, costs one prediction pair per sent
/// entity per tick, and the DIS sender duty (IEEE 1278.1 dead-reckoning threshold) is a per-frame comparison, so the invariant
/// "after every tick the held model is within the thresholds and younger than the heartbeat" holds exactly.
/// </summary>
public sealed class DisPublication
{
    private const string Origin = nameof(DisPublication);

    /// <summary>DIS entity kind 11: HROT units, not a SISO kind; not sent unless <c>shared.forwardUnitEntities</c> (R-088).</summary>
    public const int UnitKind = 11;

    private readonly IOutboundChannel<Pdu> _output;
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;
    private readonly byte _exercise;
    private readonly DeadReckonedSendPolicy _policy;
    private readonly List<EntityId> _queue = new();
    private readonly List<EntityRecord> _scratch = new();
    private EntityTable<DisPublicationState>? _table;
    private IGatewayContext? _gateway;

    public DisPublication(IOutboundChannel<Pdu> output, PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, HrotDisServices services)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(services);
        var dis = services.Config.Dis ?? throw new InvalidOperationException("The DIS side is not configured.");
        _output = output;
        _schema = schema;
        _maps = maps;
        _sources = sources;
        _services = services;
        _exercise = (byte)dis.ExerciseId;
        _policy = new DeadReckonedSendPolicy(
            new DrAttributeSet(schema.Position, schema.Velocity, schema.Acceleration, schema.Orientation, schema.AngularVelocity,
                schema.DrAlgorithm, schema.StateTime),
            SendPolicySettings.FromDegrees(dis.PositionThresholdM, dis.OrientationThresholdDeg, dis.HeartbeatS),
            services.Predictors);
    }

    // ---- the contributor rules (R-045) --------------------------------------------------------------------------------------
    // A group whose contributor rule is disabled is left out of every PDU (its fields stay at their zero values), so an operator
    // override can disable a contributor and replace it; a group whose contributor is not registered counts as enabled.

    /// <summary>The rule that contributes type, marking and force.</summary>
    public RuleInfo? IdentityRule { get; set; }

    /// <summary>The rule that contributes location, orientation, velocity and the dead-reckoning parameters.</summary>
    public RuleInfo? SpatialRule { get; set; }

    /// <summary>The rule that contributes the damage appearance.</summary>
    public RuleInfo? DamageRule { get; set; }

    private static bool IsOn(RuleInfo? rule) => rule is null || rule.Enabled;

    public EntityTable<DisPublicationState> Table => _table ?? throw NotAttached();

    private IGatewayContext Gateway => _gateway ?? throw NotAttached();

    private static InvalidOperationException NotAttached() =>
        new("The DIS publication service is not attached to an engine; a module that uses it must call Attach in Register.");

    /// <summary>True while the session shows HROT entities to DIS (Running or Paused).</summary>
    public bool IsOpen => _services.DisSessionOpen(Gateway.Session);

    /// <summary>The heartbeat period in seconds.</summary>
    public double Heartbeat => _policy.Settings.HeartbeatS;

    /// <summary>The send policy of this side (shared with the RPR side, DD-009).</summary>
    public DeadReckonedSendPolicy Policy => _policy;

    /// <summary>Creates the entity table (once); every module that uses the service calls it from Register.</summary>
    public void Attach(ModuleSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (_table is not null)
        {
            return;
        }

        _gateway = setup.Gateway;
        _table = setup.CreateEntityTable<DisPublicationState>("DisPublication.State");
    }

    // ---- contributors -------------------------------------------------------------------------------------------------

    /// <summary>A contributor marks groups of the entity as changed; the entity is queued for the flush of the current change set.</summary>
    public void Mark(EntityId id, DisGroups groups)
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
    /// Called at the end of a change set: for every queued entity of origin hrot, sends the first full PDU when it has none yet and
    /// is ready, a full PDU when its identity or damage changed, and one when its spatial change drifted beyond the thresholds.
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
            state.Pending = DisGroups.None;
            var entity = gateway.State.Find(id);
            if (entity is null || !entity.IsAlive || entity.Origin != _sources.Hrot || !IsOpen || !IsEligible(entity))
            {
                continue;
            }

            switch (state.Status)
            {
                case DisPublicationStatus.None:
                    Send(entity, ref state, final: false);
                    break;
                case DisPublicationStatus.Sent:
                    if ((groups & (DisGroups.Identity | DisGroups.Damage)) != 0 ||
                        ((groups & DisGroups.Spatial) != 0 && Drifted(entity, in state, now)))
                    {
                        Send(entity, ref state, final: false);
                    }

                    break;
            }
        }

        queue.Clear();
    }

    /// <summary>The session changed: when it is open, sends the first PDU of every ready HROT entity that waited for it (e.g. entities created in Edit).</summary>
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
                if (state.Status == DisPublicationStatus.None)
                {
                    Send(entity, ref state, final: false);
                }
            }
        }
        finally
        {
            _scratch.Clear();
        }
    }

    /// <summary>End of tick: a full PDU for every sent entity whose held dead-reckoning model drifted beyond the thresholds.</summary>
    public void SweepDrift()
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
            ref var state = ref table.ValueAt(i);
            if (state.Status != DisPublicationStatus.Sent)
            {
                continue;
            }

            var entity = gateway.State.Find(table.IdAt(i));
            if (entity is not null && entity.IsAlive && Drifted(entity, in state, now))
            {
                Send(entity, ref state, final: false);
            }
        }
    }

    /// <summary>End of tick: a full PDU for every sent entity for which nothing was sent for the heartbeat period (DIS's keep-alive, R-028).</summary>
    public void SweepHeartbeat()
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
            ref var state = ref table.ValueAt(i);
            if (state.Status != DisPublicationStatus.Sent || !_policy.HeartbeatDue(in state.Sent, now))
            {
                continue;
            }

            var entity = gateway.State.Find(table.IdAt(i));
            if (entity is not null && entity.IsAlive)
            {
                Send(entity, ref state, final: false);
            }
        }
    }

    /// <summary>
    /// An entity was removed: when it was sent, one final Entity State PDU with the Deactivated appearance (never twice); its entity
    /// number goes back to the pool (quarantine) when the engine releases the key after the removal triggers.
    /// </summary>
    public void OnRemoved(EntityRecord entity)
    {
        if (!Table.Contains(entity.Id))
        {
            return;
        }

        ref var state = ref Table.GetOrAdd(entity.Id);
        state.Pending = DisGroups.None;
        if (state.Status == DisPublicationStatus.Sent)
        {
            Send(entity, ref state, final: true);
        }
    }

    // ---- readiness and drift ------------------------------------------------------------------------------------------

    /// <summary>The entity has what a DIS entity needs (type and position with their time), the origin is valid and a unit is allowed.</summary>
    public bool IsEligible(EntityRecord entity)
    {
        var s = _schema;
        if (!_services.Geo.IsValid || !entity.TryGet(s.EntityType, out var type) || !entity.Has(s.Position) || !entity.Has(s.StateTime))
        {
            return false;
        }

        return _services.Config.Shared.ForwardUnitEntities || DisTypeCode.Unpack(type.AsInt()).Kind != UnitKind;
    }

    /// <summary>True when the receiver's dead-reckoning prediction of the last sent PDU is beyond the thresholds from the entity's state at <paramref name="now"/>.</summary>
    public bool Drifted(EntityRecord entity, in DisPublicationState state, double now) => _policy.Drifted(entity, in state.Sent, now);

    /// <summary>The state a DIS receiver predicts at <paramref name="time"/> from the last sent PDU.</summary>
    public PredictedState Held(in DisPublicationState state, double time) => _policy.Held(in state.Sent, time);

    // ---- building and sending -----------------------------------------------------------------------------------------

    private bool Send(EntityRecord entity, ref DisPublicationState state, bool final)
    {
        if (!_maps.DisEntity.TryGetOrAllocate(entity.Id, out var key))
        {
            if (!state.AllocationWarned)
            {
                state.AllocationWarned = true;
                Gateway.Diagnostics.Warn(Origin,
                    $"No DIS entity number available: entity {entity.Id.Value} is not published to DIS (retried on its next change).");
            }

            return false;
        }

        double now = Gateway.WallTime;
        var pdu = BuildFull(entity, key, now, final);
        Pdu message = pdu;
        _output.Send(in message);

        state.Status = final ? DisPublicationStatus.Removed : DisPublicationStatus.Sent;
        state.Sends++;
        state.Sent = SentModel.Of(
            DisConversions.ToVec(pdu.EntityLocation),
            DisConversions.ToVec(pdu.EntityLinearVelocity),
            DisConversions.ToVec(pdu.DeadReckoningParameters.EntityLinearAcceleration),
            DisConversions.ToVec(pdu.EntityOrientation),
            DisConversions.ToVec(pdu.DeadReckoningParameters.EntityAngularVelocity),
            pdu.DeadReckoningParameters.DeadReckoningAlgorithm,
            now);
        return true;
    }

    /// <summary>
    /// One full Entity State PDU of the entity at <paramref name="now"/>: every group of every enabled contributor, the position
    /// (and with it velocity and orientation) dead-reckoned from the pivot's state time to now; <paramref name="deactivated"/> sets
    /// the Deactivated appearance bit (the final PDU of a removed entity).
    /// </summary>
    public EntityStatePdu BuildFull(EntityRecord entity, DisEntityKey key, double now, bool deactivated)
    {
        var s = _schema;
        var pdu = new EntityStatePdu
        {
            ExerciseID = _exercise,
            Timestamp = DisTimestamp.EncodeRelative(now),
            EntityID = DisConversions.ToEntityId(key),
        };

        if (IsOn(IdentityRule))
        {
            if (entity.TryGet(s.Force, out var force))
            {
                pdu.ForceId = (byte)Math.Clamp(force.AsInt(), 0, 255);
            }

            if (entity.TryGet(s.EntityType, out var type))
            {
                pdu.EntityType = DisConversions.ToEntityType(type.AsInt());
            }

            if (entity.TryGet(s.Marking, out var marking))
            {
                EntityMarkingText.Set(pdu.Marking, marking.AsString());
            }
        }

        if (IsOn(SpatialRule) && _policy.TryRead(entity, out var basis, out int algorithm, DrAlgorithms.Static))
        {
            var predicted = _policy.Predict(in basis, algorithm, now);
            double dt = now - basis.StateTime;
            var velocity = predicted.Velocity;
            if (DrAlgorithms.IsBodyAxis(algorithm))
            {
                // the PDU carries body-axis velocity for the body-axis algorithms (the predictor's is rotated into ECEF)
                velocity = algorithm >= 8 ? basis.Velocity + basis.Acceleration * dt : basis.Velocity;
            }

            pdu.EntityLocation = DisConversions.ToDouble(predicted.Position);
            pdu.EntityLinearVelocity = DisConversions.ToFloat(velocity);
            pdu.EntityOrientation = DisConversions.ToEuler(predicted.Orientation);
            pdu.DeadReckoningParameters.DeadReckoningAlgorithm = (byte)algorithm;
            pdu.DeadReckoningParameters.EntityLinearAcceleration = DisConversions.ToFloat(basis.Acceleration);
            pdu.DeadReckoningParameters.EntityAngularVelocity = DisConversions.ToFloat(basis.AngularVelocity);
        }

        int damage = IsOn(DamageRule) && entity.TryGet(s.Damage, out var d) ? (int)d.AsInt() : 0;
        pdu.EntityAppearance = DisConversions.AppearanceOf(damage, deactivated);
        return pdu;
    }
}
