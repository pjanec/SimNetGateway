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
        };
        var allocator = services.AllocateId is { } allocate ? new NodeIdAllocator(allocate) : null;
        System = new DisGatewaySystem(
            services,
            _config,
            () => GatewayRuntime.Create(_config, allocator, _plugins, Services, munitionName: tkb => services.Tkb?.Invoke() is { } db && db.TryGetByType(tkb, out var template) ? template.Name : null, log: _log),
            beforeFrame: () => Services.Pump(_log));
        // NOT in `input`: that list ends in a togglable group HROT disables for a whole replay, and the gateway runs in a replay. It is a node global
        // system instead (Register), executed every frame on the main thread in the Input phase.
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
            System?.Dispose();
        }
        finally
        {
            Services.Dispose();
        }
    }
}
