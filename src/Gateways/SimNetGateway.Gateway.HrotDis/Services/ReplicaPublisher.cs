using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// Turns pivot entities of origin dis or rpr (the replicas of the peer simulations, DD-009 section 3 item 4) into
/// <see cref="ReplicaState"/> / <see cref="ReplicaRemoved"/> messages for the pack
/// (DD-006 section 0, DD-007 sections 3 and 5.2). It remembers which entities HROT knows; the HROT id of an entity comes
/// from the allocator port once (on introduction) and goes back when the entity is removed. A state is sent only
/// when a new DIS state arrived (the callers' triggers), never per tick.
/// </summary>
public sealed class ReplicaPublisher
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;
    private readonly IOutboundChannel<IGatewayToHrotMessage> _output;
    private readonly IGatewayContext _gateway;
    private readonly HashSet<EntityId> _introduced = new();
    private readonly HashSet<EntityId> _exhaustionWarned = new();
    private readonly List<EntityRecord> _scratch = new();
    private readonly List<EntityRecord> _scratchRpr = new();

    public ReplicaPublisher(PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, HrotDisServices services,
        IOutboundChannel<IGatewayToHrotMessage> output, IGatewayContext gateway)
    {
        _schema = schema;
        _maps = maps;
        _sources = sources;
        _services = services;
        _output = output;
        _gateway = gateway;
    }

    /// <summary>Entities that HROT knows as replicas.</summary>
    public int IntroducedCount => _introduced.Count;

    public bool IsIntroduced(EntityId id) => _introduced.Contains(id);

    public bool IsOpen => _services.ReplicasAllowed(_gateway.Session);

    /// <summary>
    /// Sends the entity's DIS state to HROT, introducing it first when needed. Does nothing for an entity of another origin, one
    /// that has no type, position and state time yet, or when the session is closed for replicas. An exhausted id allocator is
    /// a warning (once per entity); the entity is tried again on its next state.
    /// </summary>
    public void Publish(EntityRecord entity)
    {
        if (!_sources.IsReplicaOrigin(entity.Origin) || !IsOpen || !IsEligible(entity))
        {
            return;
        }

        if (!_maps.HrotEntity.TryGetOrAllocate(entity.Id, out long hrotId))
        {
            if (_exhaustionWarned.Add(entity.Id))
            {
                _gateway.Diagnostics.Warn(nameof(ReplicaPublisher),
                    $"HROT id allocator exhausted: DIS entity {entity.Id.Value} is not shown to HROT (retried on its next state).");
            }

            return;
        }

        _exhaustionWarned.Remove(entity.Id);
        _introduced.Add(entity.Id);
        _output.Send(Build(entity, hrotId));
    }

    /// <summary>Publishes every replica of a peer simulation (the origin changed: the ENU values of all replicas change).</summary>
    public void PublishAll(bool onlyNew)
    {
        if (!IsOpen)
        {
            return;
        }

        _gateway.State.Query().FromOrigin(_sources.Dis).CopyTo(_scratch);
        bool rpr = _sources.Rpr != SourceId.None;
        if (rpr)
        {
            _gateway.State.Query().FromOrigin(_sources.Rpr).CopyTo(_scratchRpr);
        }

        try
        {
            foreach (var entity in _scratch)
            {
                if (!onlyNew || !_introduced.Contains(entity.Id))
                {
                    Publish(entity);
                }
            }

            foreach (var entity in _scratchRpr)
            {
                if (!onlyNew || !_introduced.Contains(entity.Id))
                {
                    Publish(entity);
                }
            }
        }
        finally
        {
            _scratch.Clear();
            _scratchRpr.Clear();
        }
    }

    /// <summary>The entity was removed from the pivot: HROT forgets the replica. Its HROT id is released by the engine afterwards.</summary>
    public void Remove(EntityId id)
    {
        _exhaustionWarned.Remove(id);
        if (_introduced.Remove(id) && _maps.HrotEntity.TryGetKey(id, out long hrotId))
        {
            _output.Send(new ReplicaRemoved(hrotId));
        }
    }

    /// <summary>
    /// The session changed: when it is open, introduces the DIS entities that waited for it. Closing needs no work here: a session
    /// leaves the open states only through Unload (Idle, or the reload into Replay), which destroys every pivot entity, and <see cref="Remove"/>
    /// then tells HROT and the engine gives the ids back.
    /// </summary>
    public void Reconcile()
    {
        if (IsOpen)
        {
            PublishAll(onlyNew: true);
        }
    }

    private bool IsEligible(EntityRecord entity) =>
        entity.Has(_schema.EntityType) && entity.Has(_schema.Position) && entity.Has(_schema.StateTime);

    private ReplicaState Build(EntityRecord entity, long hrotId)
    {
        var geo = _services.Geo;
        long packedType = entity.Get(_schema.EntityType).AsInt();
        var ecefPosition = entity.Get(_schema.Position).AsVec3();
        var velocity = entity.TryGet(_schema.Velocity, out var v) ? v.AsVec3() : default;
        var acceleration = entity.TryGet(_schema.Acceleration, out var a) ? a.AsVec3() : default;
        var euler = entity.TryGet(_schema.Orientation, out var o) ? o.AsVec3() : default;
        var omegaBody = entity.TryGet(_schema.AngularVelocity, out var w) ? w.AsVec3() : default;
        int algorithm = entity.TryGet(_schema.DrAlgorithm, out var d) ? (int)d.AsInt() : DrAlgorithms.Static;
        double stateTime = entity.Get(_schema.StateTime).AsDouble();

        var rotation = geo.FromEuler(euler);
        bool bodyAxes = DrAlgorithms.IsBodyAxis(algorithm);
        var snapshot = new HrotDrSnapshot(
            geo.PointFromEcef(ecefPosition),
            bodyAxes ? GeoFrame.FromVec(velocity) : geo.VectorFromEcef(velocity),
            bodyAxes ? GeoFrame.FromVec(acceleration) : geo.VectorFromEcef(acceleration),
            rotation,
            geo.AngularVelocityFromBody(rotation, omegaBody),
            algorithm,
            stateTime);

        long tkb = _services.EntityTypes.DefaultTkb;
        if (_services.EntityTypes.TryDisToTkb(packedType, out long matched))
        {
            tkb = matched;
        }
        else if (_services.EntityTypes.NoteUnknownDis(packedType))
        {
            _gateway.Diagnostics.Warn(nameof(ReplicaPublisher),
                $"DIS entity type {packedType:X16} has no TKB counterpart; replicas get TKB {tkb}.");
        }

        int damage = entity.TryGet(_schema.Damage, out var dm) ? (int)dm.AsInt() : DamageMapping.None;
        return new ReplicaState(
            hrotId,
            tkb,
            packedType,
            entity.TryGet(_schema.Marking, out var marking) ? marking.AsString() : string.Empty,
            entity.TryGet(_schema.Force, out var force) ? (int)force.AsInt() : 0,
            snapshot,
            _services.Damage.HealthOf(damage),
            _services.Damage.MaxHealth,
            entity.TryGet(_schema.Deactivated, out var deactivated) && deactivated.AsBool());
    }
}
