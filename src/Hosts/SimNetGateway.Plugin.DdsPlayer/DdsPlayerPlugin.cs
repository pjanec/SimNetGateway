using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The DDS Player Station side as a plugin (DD-017, DD-023): publishes the world the gateway sees to DDS topics in the station's partition (the world participant, plus one
/// participant per seat when there are several). The participants live per gateway runtime, so a world boundary (which rebuilds the runtime) ends every instance; the world epoch that tells clients to drop their caches lives in
/// the host service, which survives the boundary.
/// </summary>
public sealed class DdsPlayerPlugin : IGatewayPlugin
{
    public string Name => DpsSide.SideKey;

    public int InterfaceVersion => PluginInterface.Version;

    public IProtocolSide Side { get; } = new DpsSide();

    /// <summary>The station shows effects of a recording as well as of a live run.</summary>
    public bool WantsEventsInReplay(object settings) => true;

    /// <summary>The wall clock the render timeouts (180 s warm-up, 30 s a frame) run on: the host's own monotonic clock unless a test sets one (never simulation time).</summary>
    public IClock WallClock { get; set; } = new StopwatchClock();

    public IHostService? CreateHostService(object settings, PluginHostContext context) => new DpsHostService((DpsSettings)settings, WallClock, context.Log);

    /// <summary>One seat per configured player (DD-023): the host offers that many player stations.</summary>
    public int PlayerSeats(object settings) => ((DpsSettings)settings).Seats;

    /// <summary>A configuration this plugin cannot run: <c>dps.seats</c> outside 1 .. 1024.</summary>
    public string? Check(object settings, PluginHostContext context)
    {
        try
        {
            ((DpsSettings)settings).Validate();
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    public PluginBound Bind(SideBinding binding)
    {
        var settings = (DpsSettings)binding.Settings;
        settings.Validate();
        if (Side is DpsSide dps)
        {
            dps.Log = binding.Log;
        }

        string partition = settings.Partition.Length > 0
            ? settings.Partition
            : $"station-{binding.Config.Shared.Site}-{binding.Config.Shared.Application}";
        int epoch = binding.Service is DpsHostService service ? service.NextEpoch() : 1;
        // seat 1 keeps the host's explicit gizmo source (if any); the others use their own station as the source
        var seats = binding.PlayerSeats;
        var gizmoSources = new IGizmoSource?[Math.Min(settings.Seats, Math.Max(seats.Count, 1))];
        for (int i = 0; i < gizmoSources.Length; i++)
        {
            gizmoSources[i] = i == 0 ? binding.GizmoSource ?? (seats.Count > 0 ? seats[0] as IGizmoSource : null) : seats[i] as IGizmoSource;
        }

        if (seats.Count > 0 && seats.Count < settings.Seats)
        {
            binding.Log($"dps: warning: dps.seats is {settings.Seats} but the host offers {seats.Count} player station(s); the seats beyond it only observe");
        }

        var transport = new DpsTransport(settings, partition, seats, epoch, binding.Log, gizmoSources, binding.Service as DpsHostService, binding.StationTicks);
        try
        {
            DpsPorts.Bind(binding.Builder, transport);
        }
        catch
        {
            transport.Dispose();
            throw;
        }

        binding.Log(settings.Seats == 1
            ? $"dps: station partition '{partition}' on DDS domain {settings.Domain}, world epoch {epoch}"
            : $"dps: station partition '{partition}': world on '{transport.WorldPartition}', {settings.Seats} seats on '{partition}-1' .. '{partition}-{settings.Seats}', DDS domain {settings.Domain}, world epoch {epoch}");
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
public sealed class DpsHostService : IHostService, IClientWorldGate, IRenderGate
{
    private const int MaxLoads = 64;

    private readonly Dictionary<Guid, uint> _loads = new();
    private readonly Queue<Guid> _loadOrder = new();
    private int _epoch;
    private bool _forced;

    /// <param name="settings">The "dps" settings (the render root and seat).</param>
    /// <param name="clock">The wall clock of the render timeouts.</param>
    public DpsHostService(DpsSettings? settings = null, IClock? clock = null, Action<string>? log = null)
    {
        Render = new DpsRenderService(clock ?? new StopwatchClock(), settings ?? new DpsSettings(), log);
    }

    /// <summary>What the clients said about their world.</summary>
    internal DpsClientWorlds Worlds { get; } = new();

    /// <summary>The render client's film state and the barrier of a job (DD-022 3e, D-B4).</summary>
    internal DpsRenderService Render { get; }

    /// <inheritdoc />
    public bool CanFilm => Render.CanFilm;

    /// <inheritdoc />
    public Task BeginJob(RenderJobSpec job) => Render.BeginJob(job);

    /// <inheritdoc />
    public Task Frame(RenderFrameSpec frame) => Render.Frame(frame);

    /// <inheritdoc />
    public Task<RenderTrackSpec?> EndJob(RenderJobSpec job, RenderEnd end, string? reason) => Render.EndJob(job, end, reason);

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

        // the NEWEST published world is the one the clients show: a boundary after the commit moves them on to a newer epoch and they never say Ready for the load's own
        return Worlds.Evaluate(Published, out failure, out step);
    }

    public void Pump() => Render.Pump();

    public void Dispose() => Render.Shutdown();
}
