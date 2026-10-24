using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>Rule "Dispose": an entity that NED knows was removed.</summary>
public sealed class NedLifecycleEgress : IGatewayModule
{
    private readonly NedRprMaps _maps;
    private readonly SourceId _ned;
    private readonly IOutboundChannel<INedMessage> _output;

    public NedLifecycleEgress(NedRprMaps maps, NedRprSources sources, IOutboundChannel<INedMessage> output)
    {
        _maps = maps;
        _ned = sources.Ned;
        _output = output;
    }

    public string Name => nameof(NedLifecycleEgress);

    public void Register(ModuleSetup setup) =>
        setup.OnEntityRemoved()
            .Named("Dispose")
            .When(t => t.Entity.Origin != _ned)
            .Do(t =>
            {
                // The key is still resolvable during the removal dispatch (released at the end of the drain).
                if (_maps.NedEntity.TryGetKey(t.Id, out ulong id))
                {
                    _output.Send(new NedEntityDisposed(id));
                }
            });
}
