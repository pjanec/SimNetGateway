using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>RprObjectRemoved: destroys the RPR entity of the handle; an unknown handle, or one of our own objects, is ignored.</summary>
public sealed class RprObjectRemovedIngress : IGatewayModule
{
    private readonly NedRprMaps _maps;

    public RprObjectRemovedIngress(NedRprMaps maps)
    {
        _maps = maps;
    }

    public string Name => nameof(RprObjectRemovedIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<RprObjectRemoved>(Handle);

    private void Handle(in RprObjectRemoved m, IngressContext ctx)
    {
        var resolution = IngressEntities.Resolve(ctx, _maps.RprHandle, m.Handle, create: false, out var id);
        if (resolution == EntityResolution.Existing)
        {
            ctx.Changes.DestroyEntity(id);
        }
    }
}
