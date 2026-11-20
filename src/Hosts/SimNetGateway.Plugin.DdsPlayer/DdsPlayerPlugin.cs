using SimNetGateway.Gateway.HrotDis;
using D = SimNetGateway.Protocol.DdsPlayer;

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
        if (Side is DpsSide dps)
        {
            dps.Log = binding.Log;
        }

        string partition = settings.Partition.Length > 0
            ? settings.Partition
            : $"station-{binding.Config.Shared.Site}-{binding.Config.Shared.Application}";
        int epoch = binding.Service is DpsHostService service ? service.NextEpoch() : 1;
        var transport = new DpsTransport(settings, partition, binding.PlayerStation, epoch, binding.Log, binding.GizmoSource);
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
        if (settings.LeaseSeconds * 1000.0 < D.DpsProtocol.DefaultLeaseMs)
        {
            // a reader's lease shorter than the writer's fails the DDS match silently: a client with the contract's default lease would never be heard
            binding.Log($"dps: warning: leaseSeconds {settings.LeaseSeconds} is shorter than the contract's default player lease ({D.DpsProtocol.DefaultLeaseMs / 1000.0} s); "
                + "a client whose player writers offer the default lease is not matched (DD-017 3.11)");
        }

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
