using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>NedEntityDisposed: destroys the entity; an unknown id is ignored.</summary>
public sealed class NedDisposeIngress : IGatewayModule
{
    private readonly NedRprMaps _maps;

    public NedDisposeIngress(NedRprMaps maps)
    {
        _maps = maps;
    }

    public string Name => nameof(NedDisposeIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<NedEntityDisposed>(Handle);

    private void Handle(in NedEntityDisposed m, IngressContext ctx)
    {
        var resolution = IngressEntities.Resolve(ctx, _maps.NedEntity, m.Id, create: false, out var id);
        if (resolution == EntityResolution.Existing)
        {
            ctx.Changes.DestroyEntity(id);
        }
    }
}
