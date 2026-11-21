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
        var transport = new DpsTransport(settings, partition, binding.PlayerStation, epoch, binding.Log, binding.GizmoSource, binding.Service as DpsHostService, binding.StationTicks);
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

/// <summary>
/// Lives as long as the host: counts the worlds (gateway runtimes) the station has published, holds what the clients said about their world (DD-022 3g) and answers the host's
/// world-readiness gate (<see cref="IClientWorldGate"/>). A forced bump (<see cref="BeginWorld"/>) is taken by the live transport on its next pump, or by the next runtime, whichever
/// comes first; <see cref="Published"/> says which epoch a Session was written with last.
/// </summary>
public sealed class DpsHostService : IHostService, IClientWorldGate
{
    private const int MaxLoads = 64;

    private readonly Dictionary<Guid, uint> _loads = new();
    private readonly Queue<Guid> _loadOrder = new();
    private int _epoch;
    private bool _forced;

    /// <summary>What the clients said about their world.</summary>
    internal DpsClientWorlds Worlds { get; } = new();

    /// <summary>The newest world epoch the station has counted.</summary>
    public int Epoch => _epoch;

    /// <summary>The newest epoch a Session sample was written with (0 = none yet).</summary>
    public uint Published { get; private set; }

    /// <summary>The epoch for a new gateway runtime: the forced one when a <see cref="BeginWorld"/> bump is still untaken (that runtime is the new world), else the next.</summary>
    public int NextEpoch()
    {
        if (_forced)
        {
            _forced = false;
            return _epoch;
        }

        return ++_epoch;
    }

    /// <summary>The live transport at epoch <paramref name="current"/> asks whether a forced bump is waiting: it adopts it (once).</summary>
    public bool TryTakeForced(int current, out int epoch)
    {
        epoch = _epoch;
        if (!_forced || current == _epoch)
        {
            return false;
        }

        _forced = false;
        return true;
    }

    /// <summary>A Session sample was written with this epoch.</summary>
    public void MarkPublished(uint epoch) => Published = Math.Max(Published, epoch);

    public void BeginWorld(Guid loadId)
    {
        if (_loads.ContainsKey(loadId))
        {
            return;
        }

        _epoch++;
        _forced = true;
        _loads[loadId] = (uint)_epoch;
        _loadOrder.Enqueue(loadId);
        while (_loadOrder.Count > MaxLoads)
        {
            _loads.Remove(_loadOrder.Dequeue());
        }
    }

    public bool IsWorldReady(Guid loadId, out string? failure, out string? step)
    {
        failure = null;
        step = null;
        if (!_loads.TryGetValue(loadId, out uint epoch))
        {
            return true;   // nothing was asked
        }

        // the race this closes: the load committed but the station has not told the clients about the new world yet - they all still say Ready for the old one
        if (Published < epoch)
        {
            return false;
        }

        return Worlds.Evaluate(epoch, out failure, out step);
    }

    public void Pump()
    {
    }

    public void Dispose()
    {
    }
}
