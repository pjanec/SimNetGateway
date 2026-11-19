using SimNetGateway.Gateway.HrotDis;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The DDS Player Station side as a plugin (DD-017): publishes the world the gateway sees to DDS topics in the station's partition. One DDS participant per
/// gateway runtime, so a world boundary (which rebuilds the runtime) ends every instance; the world epoch that tells clients to drop their caches lives in
/// the host service, which survives the boundary.
/// </summary>
public sealed class DdsPlayerPlugin : IGatewayPlugin
{
    public string Name => DpsSide.SideKey;

    public int InterfaceVersion => PluginInterface.Version;

    public IProtocolSide Side { get; } = new DpsSide();

    /// <summary>The station shows effects of a recording as well as of a live run.</summary>
    public bool WantsEventsInReplay(object settings) => true;

    public IHostService? CreateHostService(object settings, PluginHostContext context) => new DpsHostService();

    public PluginBound Bind(SideBinding binding)
    {
        var settings = (DpsSettings)binding.Settings;
        string partition = settings.Partition.Length > 0
            ? settings.Partition
            : $"station-{binding.Config.Shared.Site}-{binding.Config.Shared.Application}";
        int epoch = binding.Service is DpsHostService service ? service.NextEpoch() : 1;
        var transport = new DpsTransport(settings, partition, binding.PlayerStation, epoch, binding.Log);
        try
        {
            DpsPorts.Bind(binding.Builder, transport);
        }
        catch
        {
            transport.Dispose();
            throw;
        }

        binding.Log($"dps: station partition '{partition}' on DDS domain {settings.Domain}, world epoch {epoch}");
        return new PluginBound(transport, transport.Pump);
    }
}

/// <summary>Lives as long as the host: counts the worlds (gateway runtimes) the station has published.</summary>
internal sealed class DpsHostService : IHostService
{
    private int _epoch;

    public int NextEpoch() => ++_epoch;

    public void Pump()
    {
    }

    public void Dispose()
    {
    }
}
