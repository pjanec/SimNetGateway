using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>The version of the plugin contract in this assembly: a host skips a plugin that declares another one (DD-016).</summary>
public static class PluginInterface
{
    public const int Version = 1;
}

/// <summary>
/// A protocol side as a plugin of a gateway host (DD-016): one <see cref="IProtocolSide"/> (the configuration section, the ports, the modules) plus what a host
/// does for it: opening its transports for one gateway runtime, an optional object that lives as long as the host, and whether it wants events while HROT replays.
/// DIS, HLA and any other side are plugins of this one shape; a plugin object is stateless apart from its own test seams.
/// </summary>
public interface IGatewayPlugin
{
    /// <summary>The plugin's name (the key of its configuration section by convention).</summary>
    string Name { get; }

    /// <summary>The contract version the plugin was built for: <see cref="PluginInterface.Version"/>.</summary>
    int InterfaceVersion { get; }

    /// <summary>The side the plugin contributes.</summary>
    IProtocolSide Side { get; }

    /// <summary>
    /// An object that lives as long as the host and survives world boundaries (a connection to something that should not be torn down with every world), or null.
    /// Called once, when the side runs; the host pumps it every frame and disposes it after every gateway runtime is closed.
    /// </summary>
    IHostService? CreateHostService(object settings, PluginHostContext context) => null;

    /// <summary>Opens the side's transports for one gateway runtime: binds the ports the side declared, returns what ends them (at the world boundary) and an optional per-frame pump.</summary>
    PluginBound Bind(SideBinding binding);

    /// <summary>A reason this configuration cannot be run (one line, naming the key), or null; checked by the host before it composes anything. The context carries the plugin folder, where add-ons are looked up.</summary>
    string? Check(object settings, PluginHostContext context) => null;

    /// <summary>True when the side shows events while HROT replays a recording (the host then gathers them even when the shared flag is off).</summary>
    bool WantsEventsInReplay(object settings) => false;
}

/// <summary>What a plugin knows about its host when it creates its host service.</summary>
/// <param name="Log">One line to the host's log (stderr by default).</param>
/// <param name="Folder">The folder the plugin was loaded from; null when it was not loaded from a folder (tests).</param>
public sealed record PluginHostContext(Action<string> Log, string? Folder = null);

/// <summary>An object owned by the host for its whole life; <see cref="IDisposable.Dispose"/> ends it (after every gateway runtime is closed).</summary>
public interface IHostService : IDisposable
{
    /// <summary>Services it once; called every frame by the host's system, also while no gateway runtime exists (Idle).</summary>
    void Pump();
}

/// <summary>What a plugin gets to bind its side to one gateway runtime.</summary>
public sealed class SideBinding
{
    public SideBinding(GatewayBuilder builder, GatewayConfiguration config, object settings, IClock clock, IHostService? service, Func<long, string?> munitionName,
        Action<string> log, string? pluginFolder = null, IPlayerStation? playerStation = null, IGizmoSource? gizmoSource = null)
    {
        Builder = builder;
        Config = config;
        Settings = settings;
        Clock = clock;
        Service = service;
        MunitionName = munitionName;
        Log = log;
        PluginFolder = pluginFolder;
        PlayerStation = playerStation;
        GizmoSource = gizmoSource;
    }

    /// <summary>The gateway being built: bind the ports the side declared (<c>BindInbound</c>, <c>BindOutbound</c>).</summary>
    public GatewayBuilder Builder { get; }

    public GatewayConfiguration Config { get; }

    /// <summary>The side's own settings (the object <see cref="IProtocolSide.Parse"/> returned).</summary>
    public object Settings { get; }

    public IClock Clock { get; }

    /// <summary>The plugin's host service, or null when it has none.</summary>
    public IHostService? Service { get; }

    /// <summary>HROT's name of a munition by its template type (null = unknown): the node's template database, read when needed.</summary>
    public Func<long, string?> MunitionName { get; }

    public Action<string> Log { get; }

    /// <summary>The folder the plugin was loaded from (null in tests).</summary>
    public string? PluginFolder { get; }

    /// <summary>The host's player station (DD-017 3.6) for a side that plays a unit; null when the host has none. Additive: the contract version stays.</summary>
    public IPlayerStation? PlayerStation { get; }

    /// <summary>The host's gizmo source (DD-017 3.7) for a side that shows gizmos; null when the host has none. Additive: the contract version stays.</summary>
    public IGizmoSource? GizmoSource { get; }
}

/// <summary>What <see cref="IGatewayPlugin.Bind"/> hands back: the lease (disposed at the world boundary or the end of the node) and a pump called once per frame before the tick.</summary>
public sealed class PluginBound
{
    public PluginBound(IDisposable? lease = null, Action? pump = null)
    {
        Lease = lease;
        Pump = pump;
    }

    public static PluginBound None { get; } = new();

    public IDisposable? Lease { get; }

    public Action? Pump { get; }
}
