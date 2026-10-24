using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>NedEntityDamage: the damage level (0..3); an out-of-range level aborts the transaction.</summary>
public sealed class NedDamageIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;

    public NedDamageIngress(PivotSchema schema, NedRprMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(NedDamageIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.Damage);
        setup.OnMessage<NedEntityDamage>(Handle);
    }

    private void Handle(in NedEntityDamage m, IngressContext ctx)
    {
        if (!NedRprConversions.IsValidDamage(m.Level))
        {
            ctx.Abort($"damage level {m.Level} out of range");
            return;
        }

        var resolution = IngressEntities.Resolve(ctx, _maps.NedEntity, m.Id, create: false, out var id);
        if (resolution != EntityResolution.Existing)
        {
            return; // unknown id (ignored) or our own publication looped back
        }

        ctx.Changes.Set(id, _schema.Damage, AttrValue.From(m.Level));
    }
}
