using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// C8, the showcase's replacement of <see cref="NedWeaponFireIngress"/>: a fire is split over two NED messages. The first,
/// <see cref="NedWeaponFire"/>, creates a helper entity (ned.fire: the fire id) that holds what is known (shooter, target,
/// location, the munition and quantity if given) and is <c>Fire.Pending</c>; no event is published and the ammunition is not
/// touched. The second, <see cref="NedFireAmmunition"/>, completes it (munition, quantity, not pending); the
/// <see cref="FireAssemblerProcessor"/> then publishes the one WeaponFire. An ammunition part for an unknown fire is a warning
/// and nothing else. The helper has no identity or position of its own, so no egress publishes it.
/// </summary>
public sealed class NedSplitFireIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;

    public NedSplitFireIngress(PivotSchema schema, NedRprMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(NedSplitFireIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.FireId, _schema.FireShooter, _schema.FireTarget, _schema.FireLocation, _schema.FireMunition,
            _schema.FireQuantity, _schema.FirePending, _schema.FireStartedAt);
        setup.OnMessage<NedWeaponFire>(HandleFire);
        setup.OnMessage<NedFireAmmunition>(HandleAmmunition);
    }

    private void HandleFire(in NedWeaponFire m, IngressContext ctx)
    {
        var resolution = IngressEntities.ResolveNed(ctx, _maps, m.Shooter, m.ShooterMember, create: false, out var shooter);
        if (resolution == EntityResolution.Missing)
        {
            ctx.Abort("unknown shooter");
            return;
        }

        if (resolution == EntityResolution.Echo)
        {
            return;
        }

        var changes = ctx.Changes;
        var fireId = m.FireId;
        if (_maps.NedFire.TryResolve(changes, fireId, out var existing))
        {
            ctx.Diagnostics.Warn(Name, $"Fire {fireId} is already being assembled; the repeated first part is ignored (helper {existing.Value}).");
            return;
        }

        var helper = changes.CreateEntity();
        _maps.NedFire.Bind(changes, helper, fireId);
        changes.Set(helper, _schema.FireId, AttrValue.FromObject(fireId));
        changes.Set(helper, _schema.FireShooter, AttrValue.From(shooter));
        if (m.Target != 0 && IngressEntities.TryResolveNedTarget(ctx, _maps, m.Target, m.TargetMember, out var target))
        {
            changes.Set(helper, _schema.FireTarget, AttrValue.From(target));
        }

        changes.Set(helper, _schema.FireLocation,
            AttrValue.From(NedRprConversions.GeodeticToEcef(m.LatitudeDeg, m.LongitudeDeg, m.Altitude)));
        changes.Set(helper, _schema.FireMunition, AttrValue.From(m.MunitionTkb));
        changes.Set(helper, _schema.FireQuantity, AttrValue.From(m.Quantity));
        changes.Set(helper, _schema.FirePending, AttrValue.From(true));
        changes.Set(helper, _schema.FireStartedAt, AttrValue.From(ctx.WallTime));
    }

    private void HandleAmmunition(in NedFireAmmunition m, IngressContext ctx)
    {
        if (!_maps.NedFire.TryResolve(ctx.Changes, m.FireId, out var helper))
        {
            ctx.Diagnostics.Warn(Name, $"Ammunition part for unknown fire {m.FireId} (it was assembled earlier, or never started); ignored.");
            return;
        }

        ctx.Changes.Set(helper, _schema.FireMunition, AttrValue.From(m.MunitionTkb));
        ctx.Changes.Set(helper, _schema.FireQuantity, AttrValue.From(m.Quantity));
        ctx.Changes.Set(helper, _schema.FirePending, AttrValue.From(false));
    }
}
