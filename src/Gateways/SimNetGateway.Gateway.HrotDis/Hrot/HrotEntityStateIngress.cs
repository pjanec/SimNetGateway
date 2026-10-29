using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotEntityState: resolves or creates the pivot entity (origin hrot) and stores identity, health and, once the origin is valid,
/// the kinematics converted to the pivot frames (DD-006 section 5): ENU position and velocity to ECEF, the HROT quaternion to
/// geocentric Euler angles, the angular velocity to body axes, dead reckoning FPW, state time = the HROT sim time. Before a valid
/// origin the entity is created and typed but gets no position (DD-006 4.1, DD-007 section 7). An id that belongs to a DIS replica is
/// ignored (echo, R-022). The commander becomes <c>Structure.Parent</c> when it is a known HROT entity, else the link is absent.
/// </summary>
public sealed class HrotEntityStateIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisServices _services;

    public HrotEntityStateIngress(PivotSchema schema, HrotDisMaps maps, HrotDisServices services)
    {
        _schema = schema;
        _maps = maps;
        _services = services;
    }

    public string Name => nameof(HrotEntityStateIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.EntityType, _schema.Marking, _schema.Force, _schema.TkbType, _schema.Health, _schema.Damage,
            _schema.Parent, _schema.Position, _schema.Orientation, _schema.Velocity, _schema.Acceleration, _schema.AngularVelocity,
            _schema.DrAlgorithm, _schema.StateTime);
        setup.OnMessage<HrotEntityState>(Handle);
    }

    private void Handle(in HrotEntityState m, IngressContext ctx)
    {
        if (m.Id == 0)
        {
            ctx.Abort("entity id 0");
            return;
        }

        if (m.Force < 0 || m.Force > 255)
        {
            ctx.Abort($"force {m.Force} out of range");
            return;
        }

        if (!GeoFrame.IsFinite(m.Position) || !GeoFrame.IsFinite(m.Rotation) || !GeoFrame.IsFinite(m.Velocity) ||
            !GeoFrame.IsFinite(m.AngularVelocity) || !double.IsFinite(m.HealthCurrent) || !double.IsFinite(m.HealthMax) ||
            !double.IsFinite(m.SimTime))
        {
            ctx.Abort($"entity {m.Id}: a value is not finite");
            return;
        }

        var resolution = HrotIngress.Resolve(ctx, _maps, m.Id, create: true, out var id);
        if (resolution == EntityResolution.Echo)
        {
            return;
        }

        var changes = ctx.Changes;
        var types = _services.EntityTypes;
        if (types.TryResolveDisType(m.TkbType, m.DisType, out long disType))
        {
            changes.Set(id, _schema.EntityType, AttrValue.From(disType));
        }
        else if (types.NoteUnknownTkb(m.TkbType))
        {
            ctx.Diagnostics.Warn(Name, $"TKB type {m.TkbType} has no DIS type; entity {m.Id} is not typed for DIS.");
        }

        changes.Set(id, _schema.TkbType, AttrValue.From(m.TkbType));
        changes.Set(id, _schema.Marking, AttrValue.From(m.Marking ?? string.Empty));
        changes.Set(id, _schema.Force, AttrValue.From((long)m.Force));
        changes.Set(id, _schema.Health, AttrValue.From(new Vec3(m.HealthCurrent, m.HealthMax, 0.0)));
        changes.Set(id, _schema.Damage, AttrValue.From((long)_services.Damage.DamageOf(m.HealthCurrent, m.HealthMax)));
        SetCommander(ctx, id, m.CommanderId);

        var geo = _services.Geo;
        if (!geo.IsValid)
        {
            return;
        }

        changes.Set(id, _schema.Position, AttrValue.From(geo.PointToEcef(m.Position)));
        changes.Set(id, _schema.Velocity, AttrValue.From(geo.VectorToEcef(m.Velocity)));
        changes.Set(id, _schema.Acceleration, AttrValue.From(Vec3.Zero));
        changes.Set(id, _schema.Orientation, AttrValue.From(geo.ToEuler(m.Rotation)));
        changes.Set(id, _schema.AngularVelocity, AttrValue.From(geo.AngularVelocityToBody(m.Rotation, m.AngularVelocity)));
        changes.Set(id, _schema.DrAlgorithm, AttrValue.From((long)DrAlgorithms.Fpw));
        changes.Set(id, _schema.StateTime, AttrValue.From(m.SimTime));
    }

    private void SetCommander(IngressContext ctx, EntityId id, long commanderId)
    {
        if (commanderId != 0 &&
            _maps.HrotEntity.TryResolve(ctx.Changes, commanderId, out var commander) &&
            commander != id &&
            ctx.State.Find(commander) is { } record && record.Origin == ctx.Source)
        {
            ctx.Changes.Set(id, _schema.Parent, AttrValue.From(commander));
        }
        else
        {
            ctx.Changes.Clear(id, _schema.Parent);
        }
    }
}
