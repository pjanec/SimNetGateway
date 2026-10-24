using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// NedDetonation: a reported target damage (0..3) becomes the target's Status.Damage (a state effect, dispatched before
/// the event, R-016) and a Detonation event is published.
/// </summary>
public sealed class NedDetonationIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;

    public NedDetonationIngress(PivotSchema schema, NedRprMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(NedDetonationIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.Damage, _schema.EventId, _schema.EventTarget, _schema.EventMunitionTkb, _schema.EventLocation,
            _schema.EventResult);
        setup.Produces(_schema.Detonation);
        setup.OnMessage<NedDetonation>(Handle);
    }

    private void Handle(in NedDetonation m, IngressContext ctx)
    {
        if (!NedRprConversions.TryResultToPivot(m.Result, out int result))
        {
            ctx.Abort($"detonation result {(int)m.Result} out of range");
            return;
        }

        if (m.TargetDamage != -1 && !NedRprConversions.IsValidDamage(m.TargetDamage))
        {
            ctx.Abort($"target damage {m.TargetDamage} out of range");
            return;
        }

        var changes = ctx.Changes;
        EntityId target = default;
        bool hasTarget = m.Target != 0 && IngressEntities.TryResolveNedTarget(ctx, _maps, m.Target, m.TargetMember, out target);
        if (hasTarget && NedRprConversions.IsValidDamage(m.TargetDamage))
        {
            changes.Set(target, _schema.Damage, AttrValue.From(m.TargetDamage));
        }

        var bag = ctx.RentPayload();
        bag.Set(_schema.EventId, AttrValue.FromObject(m.DetonationId));
        if (hasTarget)
        {
            bag.Set(_schema.EventTarget, AttrValue.From(target));
        }

        bag.Set(_schema.EventMunitionTkb, AttrValue.From(m.MunitionTkb));
        bag.Set(_schema.EventLocation,
            AttrValue.From(NedRprConversions.GeodeticToEcef(m.LatitudeDeg, m.LongitudeDeg, m.Altitude)));
        bag.Set(_schema.EventResult, AttrValue.From(result));
        ctx.Publish(_schema.Detonation, bag);
    }
}
