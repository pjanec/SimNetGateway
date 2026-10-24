using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>RprWeaponFire: a WeaponFire event with shooter and target resolved from their handles when known.</summary>
public sealed class RprWeaponFireIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly TypeCatalog _catalog;

    public RprWeaponFireIngress(PivotSchema schema, NedRprMaps maps, TypeCatalog catalog)
    {
        _schema = schema;
        _maps = maps;
        _catalog = catalog;
    }

    public string Name => nameof(RprWeaponFireIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.EventId, _schema.EventShooter, _schema.EventTarget, _schema.EventMunitionTkb,
            _schema.EventLocation, _schema.EventQuantity);
        setup.Produces(_schema.WeaponFire);
        setup.OnMessage<RprWeaponFire>(Handle);
    }

    private void Handle(in RprWeaponFire m, IngressContext ctx)
    {
        EntityId shooter = default;
        bool hasShooter = false;
        if (m.FiringObject is { } firing)
        {
            var resolution = IngressEntities.Resolve(ctx, _maps.RprHandle, firing, create: false, out shooter);
            if (resolution == EntityResolution.Echo)
            {
                return; // a fire of one of our own (non-RPR) entities, looped back
            }

            hasShooter = resolution == EntityResolution.Existing;
        }

        EntityId target = default;
        bool hasTarget = m.TargetObject is { } targeted && _maps.RprHandle.TryResolve(ctx.Changes, targeted, out target);

        int tkb = 0;
        if (!_catalog.TryToTkb(m.Munition, out tkb))
        {
            tkb = TypeCatalog.DefaultTkb;
            if (_catalog.NoteUnknownDis(m.Munition))
            {
                ctx.Diagnostics.Warn(Name, $"Unknown DIS munition type {m.Munition}; the munition is not given.");
            }
        }

        var bag = ctx.RentPayload();
        bag.Set(_schema.EventId, AttrValue.FromObject(m.EventId));
        if (hasShooter)
        {
            bag.Set(_schema.EventShooter, AttrValue.From(shooter));
        }

        if (hasTarget)
        {
            bag.Set(_schema.EventTarget, AttrValue.From(target));
        }

        bag.Set(_schema.EventMunitionTkb, AttrValue.From(tkb));
        bag.Set(_schema.EventLocation, AttrValue.From(NedRprConversions.ToVec3(m.Location)));
        bag.Set(_schema.EventQuantity, AttrValue.From(m.Quantity));
        ctx.Publish(_schema.WeaponFire, bag);
    }
}
