using Fdp.ModuleHost.Abstractions;
using Hrot.Common.Infrastructure;
using SimNetGateway.Engine;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The capability the extension contributes: one <see cref="DisGatewaySystem"/>, registered as a global system of the node. It needs no resource
/// (a role declares none for a foreign extension). It owns the host services of the plugins, which outlive every gateway runtime.
/// </summary>
public sealed class GatewayCapability : INodeCapability, IDisposable
{
    /// <summary>"cap:ext:simnetgateway".</summary>
    public const string CapabilityKey = "cap:ext:simnetgateway";

    private readonly GatewayConfiguration _config;
    private readonly PluginSet _plugins;
    private readonly Action<string> _log;
    private LinkedClients? _registry;

    public GatewayCapability(GatewayConfiguration config, PluginSet plugins, Action<string>? log = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _log = log ?? (line => Console.Error.WriteLine("[SimNetGateway] " + line));
        Services = new HostServices(_plugins, _config, _log);
    }

    /// <summary>The host services of the plugins (tests).</summary>
    public HostServices Services { get; }

    public string Key => CapabilityKey;

    public IReadOnlyList<string> Needs => Array.Empty<string>();

    /// <summary>The world-readiness gate of the linked clients (DD-022 3g), from the dps plugin's host service; null when the dps side does not run. HROT's load step calls it.</summary>
    public SimNetGateway.Gateway.HrotDis.IClientWorldGate? ClientWorldGate => Services.Of("dps") as SimNetGateway.Gateway.HrotDis.IClientWorldGate;

    /// <summary>The film gate of the render client (DD-022 3e), from the dps plugin's host service; null when the dps side does not run.</summary>
    public SimNetGateway.Gateway.HrotDis.IRenderGate? RenderGate => Services.Of("dps") as SimNetGateway.Gateway.HrotDis.IRenderGate;

    /// <summary>The linked client this capability offered to the node (null before composition, when the dps side does not run, and after teardown).</summary>
    public DpsLinkedClient? LinkedClient { get; private set; }

    /// <summary>The system built by <see cref="PopulateSystems"/> (null before; a capability is composed once per node).</summary>
    public DisGatewaySystem? System { get; private set; }

    public void PopulateSystems(HrotNodeContext context, List<IEcsModuleSystem> input, List<IEcsModuleSystem> simulation, List<IEcsModuleSystem> postSimulation)
    {
        // a null context is the shape probe several host rails make: nothing to compose from
        if (context is null)
        {
            return;
        }

        if (System is not null)
        {
            throw new InvalidOperationException("The SimNetGateway capability is composed once per node.");
        }

        OfferLinkedClient(context);
        var services = ServicesFrom(context);
        var allocator = services.AllocateId is { } allocate ? new NodeIdAllocator(allocate) : null;
        System = new DisGatewaySystem(
            services,
            _config,
            _plugins.SeatsFor(_config),
            seats => GatewayRuntime.Create(_config, allocator, _plugins, Services, playerStation: seats[0], playerSeats: seats, stationTicks: services.WallTicks, munitionName: tkb => services.Tkb?.Invoke() is { } db && db.TryGetByType(tkb, out var template) ? template.Name : null, log: _log),
            beforeFrame: () => Services.Pump(_log),
            log: _log);
        // NOT in `input`: that list ends in a togglable group HROT disables for a whole replay, and the gateway runs in a replay. It is a node global
        // system instead (Register), executed every frame on the main thread in the Input phase.
    }

    /// <summary>
    /// HROT's linked-client seam (CE-11026): the gateway offers itself as the node's linked client so that every load waits for the connected clients' worlds and the director may film
    /// through the render client. Offered only when the dps side runs (it holds both gates); withdrawn at teardown.
    /// </summary>
    private void OfferLinkedClient(HrotNodeContext context)
    {
        if (context.LinkedClients is not { } registry || (ClientWorldGate is null && RenderGate is null))
        {
            return;
        }

        LinkedClient = new DpsLinkedClient(() => ClientWorldGate, () => RenderGate);
        _registry = registry;
        registry.Offer(LinkedClient);
    }

    /// <summary>What the pack reads from the node: every dependency the context holds is passed on here (a production caller that has a dependency passes it).</summary>
    internal static HrotServices ServicesFrom(HrotNodeContext context)
    {
        var ids = context.IdAllocator;
        var geo = context.GeoTransform;
        var services = new HrotServices
        {
            State = () => context.ClusterSlave.LocalClusterState,
            GeoReference = () => HrotGeoRef.From(geo),
            AllocateId = ids is null ? null : ids.AllocateId,
            EntityMap = context.EntityMap,
            Tkb = () => context.TkbDb ?? (context.World.HasSingletonManaged<Fdp.Interfaces.ITkbDatabase>() ? context.World.GetSingletonManaged<Fdp.Interfaces.ITkbDatabase>() : null),
            LocalNodeId = context.NodeId.Value,
            EventBus = context.EventBus,
            TimeController = () => context.TimeController,
            WallTicks = Fdp.Toolkit.Time.HighResUtcClock.GetTicks,
        };
        return services;
    }

    /// <summary>Registers the system as a global system of the node kernel (Input phase, main thread, every frame, also in a replay).</summary>
    public void Register(HrotNodeContext context, NodeBootValues values)
    {
        if (context is null || System is null)
        {
            return;
        }

        context.Kernel.RegisterGlobalSystem(System);
    }

    /// <summary>
    /// What HROT calls at node teardown (it disposes the capabilities an extension added, before the world): the system closes the gateway it created, then the
    /// host services of the plugins end (what a plugin leased to the runtime is removed first). Idempotent.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (LinkedClient is { } client)
            {
                _registry?.Withdraw(client);   // the node's loads stop waiting for a client that is going away
                LinkedClient = null;
            }

            System?.Dispose();
        }
        finally
        {
            Services.Dispose();
        }
    }
}
