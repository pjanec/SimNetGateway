using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>HrotEntityRemoved: destroys the pivot entity; an unknown id is ignored, and so is the id of a DIS replica (echo).</summary>
public sealed class HrotEntityRemovedIngress : IGatewayModule
{
    private readonly HrotDisMaps _maps;

    public HrotEntityRemovedIngress(HrotDisMaps maps) => _maps = maps;

    public string Name => nameof(HrotEntityRemovedIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotEntityRemoved>(Handle);

    private void Handle(in HrotEntityRemoved m, IngressContext ctx)
    {
        if (HrotIngress.Resolve(ctx, _maps, m.Id, create: false, out var id) == EntityResolution.Existing)
        {
            ctx.Changes.DestroyEntity(id);
        }
    }
}
