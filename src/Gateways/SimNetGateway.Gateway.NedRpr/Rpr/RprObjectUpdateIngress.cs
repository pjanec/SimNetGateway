using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// RprObjectUpdate: resolves or creates the RPR-origin entity of a handle and stores the attributes the update carries
/// (only present attributes), the entity identifier among them (bound once).
/// </summary>
public sealed class RprObjectUpdateIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly TypeCatalog _catalog;

    public RprObjectUpdateIngress(PivotSchema schema, NedRprMaps maps, TypeCatalog catalog)
    {
        _schema = schema;
        _maps = maps;
        _catalog = catalog;
    }

    public string Name => nameof(RprObjectUpdateIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.Name, _schema.TypeTkb, _schema.Force, _schema.Position, _schema.Orientation, _schema.Velocity,
            _schema.Acceleration, _schema.AngularVelocity, _schema.DrAlgorithm, _schema.StateTime, _schema.Damage);
        setup.OnMessage<RprObjectUpdate>(Handle);
    }

    private void Handle(in RprObjectUpdate m, IngressContext ctx)
    {
        // Validate before anything is created, so a bad update leaves no trace (the abort discards the transaction anyway).
        int? damageLevel = null;
        if (m.DamageState is { } state)
        {
            if (!NedRprConversions.TryDamageFromRpr(state, out int level))
            {
                ctx.Abort($"damage state {(int)state} out of range");
                return;
            }

            damageLevel = level;
        }

        var resolution = IngressEntities.Resolve(ctx, _maps.RprHandle, m.Handle, create: true, out var id);
        if (resolution == EntityResolution.Echo)
        {
            return;
        }

        var changes = ctx.Changes;
        if (m.Marking is { } marking)
        {
            changes.Set(id, _schema.Name, AttrValue.From(marking));
        }

        if (m.EntityType is { } dis)
        {
            if (!_catalog.TryToTkb(dis, out int tkb))
            {
                tkb = TypeCatalog.DefaultTkb;
                if (_catalog.NoteUnknownDis(dis))
                {
                    ctx.Diagnostics.Warn(Name, $"Unknown DIS entity type {dis}; using TKB type {TypeCatalog.DefaultTkb}.");
                }
            }

            changes.Set(id, _schema.TypeTkb, AttrValue.From(tkb));
        }

        if (m.ForceId is { } force && NedRprConversions.TryForceFromRpr(force, out int pivotForce))
        {
            changes.Set(id, _schema.Force, AttrValue.From(pivotForce));
        }

        if (m.EntityIdentifier is { } entityIdentifier)
        {
            BindEntityIdentifier(ctx, id, entityIdentifier);
        }

        bool spatial = false;
        if (m.WorldLocation is { } location)
        {
            changes.Set(id, _schema.Position, AttrValue.From(NedRprConversions.ToVec3(location)));
            spatial = true;
        }

        if (m.Orientation is { } orientation)
        {
            changes.Set(id, _schema.Orientation, AttrValue.From(NedRprConversions.ToVec3(orientation)));
            spatial = true;
        }

        if (m.VelocityVector is { } velocity)
        {
            changes.Set(id, _schema.Velocity, AttrValue.From(NedRprConversions.ToVec3(velocity)));
            spatial = true;
        }

        if (m.AccelerationVector is { } acceleration)
        {
            changes.Set(id, _schema.Acceleration, AttrValue.From(NedRprConversions.ToVec3(acceleration)));
            spatial = true;
        }

        if (m.AngularVelocity is { } angular)
        {
            changes.Set(id, _schema.AngularVelocity, AttrValue.From(NedRprConversions.ToVec3(angular)));
            spatial = true;
        }

        if (m.DeadReckoningAlgorithm is { } algorithm)
        {
            changes.Set(id, _schema.DrAlgorithm, AttrValue.From((long)algorithm));
            spatial = true;
        }

        if (spatial)
        {
            changes.Set(id, _schema.StateTime, AttrValue.From(ctx.SimTime));
        }

        if (damageLevel is { } damage)
        {
            changes.Set(id, _schema.Damage, AttrValue.From(damage));
        }
    }

    private void BindEntityIdentifier(IngressContext ctx, EntityId id, DisEntityId identifier)
    {
        var map = _maps.RprEntityId;
        if (map.TryResolve(ctx.Changes, identifier, out var owner))
        {
            if (owner != id)
            {
                ctx.Diagnostics.Warn(Name, $"Entity identifier {identifier} is already bound to another entity; ignored.");
            }

            return;
        }

        if (map.TryGetKey(id, out _))
        {
            return; // bound the first time only
        }

        map.Bind(ctx.Changes, id, identifier);
    }
}
