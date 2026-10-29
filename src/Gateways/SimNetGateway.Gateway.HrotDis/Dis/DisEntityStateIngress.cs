using DISnet;
using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// Entity State PDU: creates or updates the pivot entity of origin dis and binds <c>dis.entity</c> from the PDU's entity id (DD-006
/// sections 5, 6.2 and 7). The type is packed, the marking and force copied, location / orientation / velocity / acceleration /
/// angular velocity / dead-reckoning algorithm are stored as they are on the wire (ECEF, geocentric Euler, DIS body axes, the DIS
/// numbering; an algorithm without a predictor is read as Static), the damage comes from the appearance bits, and the state time is
/// the engine clock at receipt (the one time axis of the pivot). An Entity State PDU with the Deactivated appearance removes the
/// entity (and creates nothing). PDUs of another exercise and PDUs with the gateway's own site and application are ignored; so is
/// a PDU that names an entity the gateway itself published (a foreign simulation reusing the gateway's identifiers).
/// </summary>
public sealed class DisEntityStateIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisServices _services;
    private readonly SourceId _dis;

    public DisEntityStateIngress(PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, HrotDisServices services)
    {
        _schema = schema;
        _maps = maps;
        _dis = sources.Dis;
        _services = services;
    }

    public string Name => nameof(DisEntityStateIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.EntityType, _schema.Marking, _schema.Force, _schema.Position, _schema.Orientation, _schema.Velocity,
            _schema.Acceleration, _schema.AngularVelocity, _schema.DrAlgorithm, _schema.StateTime, _schema.Damage);
        setup.OnMessage<EntityStatePdu>(Handle);
    }

    private void Handle(in EntityStatePdu m, IngressContext ctx)
    {
        var address = m.EntityID.SimulationAddress;
        if (DisIngressSupport.Reject(_services, m.ExerciseID, address.Site, address.Application))
        {
            return;
        }

        var key = DisConversions.KeyOf(m.EntityID);
        bool known = _maps.DisEntity.TryResolve(ctx.Changes, key, out var id);
        if (known && ctx.State.Find(id) is { } existing && existing.Origin != _dis)
        {
            return; // the id belongs to an entity of the HROT side
        }

        if (EntityAppearanceDeactivated(m))
        {
            if (known)
            {
                ctx.Changes.DestroyEntity(id);
            }

            return;
        }

        var dr = m.DeadReckoningParameters;
        if (!DisConversions.IsFinite(m.EntityLocation) || !DisConversions.IsFinite(m.EntityLinearVelocity) ||
            !DisConversions.IsFinite(m.EntityOrientation) || !DisConversions.IsFinite(dr.EntityLinearAcceleration) ||
            !DisConversions.IsFinite(dr.EntityAngularVelocity))
        {
            ctx.Abort($"entity {key}: a value is not finite");
            return;
        }

        if (!known)
        {
            id = ctx.Changes.CreateEntity();
            _maps.DisEntity.Bind(ctx.Changes, id, key);
        }

        var changes = ctx.Changes;
        changes.Set(id, _schema.EntityType, AttrValue.From(DisConversions.PackType(m.EntityType)));
        changes.Set(id, _schema.Marking, AttrValue.From(Protocol.Dis7.EntityMarkingText.Get(m.Marking)));
        changes.Set(id, _schema.Force, AttrValue.From((long)m.ForceId));
        changes.Set(id, _schema.Position, AttrValue.From(DisConversions.ToVec(m.EntityLocation)));
        changes.Set(id, _schema.Orientation, AttrValue.From(DisConversions.ToVec(m.EntityOrientation)));
        changes.Set(id, _schema.Velocity, AttrValue.From(DisConversions.ToVec(m.EntityLinearVelocity)));
        changes.Set(id, _schema.Acceleration, AttrValue.From(DisConversions.ToVec(dr.EntityLinearAcceleration)));
        changes.Set(id, _schema.AngularVelocity, AttrValue.From(DisConversions.ToVec(dr.EntityAngularVelocity)));
        changes.Set(id, _schema.DrAlgorithm, AttrValue.From((long)DisConversions.NormalizeAlgorithm(dr.DeadReckoningAlgorithm)));
        changes.Set(id, _schema.Damage, AttrValue.From((long)DisConversions.DamageOf(m.EntityAppearance)));
        changes.Set(id, _schema.StateTime, AttrValue.From(ctx.ReceivedAt)); // one time axis in the pivot: the engine clock at receipt
    }

    private static bool EntityAppearanceDeactivated(EntityStatePdu m) => Protocol.Dis7.EntityAppearance.GetDeactivated(m.EntityAppearance);
}
