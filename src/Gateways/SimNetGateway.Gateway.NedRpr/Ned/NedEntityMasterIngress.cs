using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>NedEntityMaster: resolves or creates the entity and stores name, type and force.</summary>
public sealed class NedEntityMasterIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;

    public NedEntityMasterIngress(PivotSchema schema, NedRprMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(NedEntityMasterIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.Name, _schema.TypeTkb, _schema.Force);
        setup.OnMessage<NedEntityMaster>(Handle);
    }

    private void Handle(in NedEntityMaster m, IngressContext ctx)
    {
        if (!NedRprConversions.TryForceToPivot(m.Force, out int force))
        {
            ctx.Abort($"force {(int)m.Force} out of range");
            return;
        }

        var resolution = IngressEntities.Resolve(ctx, _maps.NedEntity, m.Id, create: true, out var id);
        if (resolution == EntityResolution.Echo)
        {
            return;
        }

        ctx.Changes.Set(id, _schema.Name, AttrValue.From(m.Name));
        ctx.Changes.Set(id, _schema.TypeTkb, AttrValue.From(m.TkbType));
        ctx.Changes.Set(id, _schema.Force, AttrValue.From(force));
    }
}
