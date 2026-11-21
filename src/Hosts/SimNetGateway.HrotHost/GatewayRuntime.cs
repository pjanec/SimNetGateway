using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.HrotHost;

/// <summary>An outbound channel that queues what the engine sends, for the adapter to apply after the tick.</summary>
public sealed class QueueOutbound<T> : IOutboundChannel<T>
{
    private readonly Queue<T> _queue = new();

    public int Count => _queue.Count;

    public void Send(in T message) => _queue.Enqueue(message);

    public bool TryDequeue(out T message) => _queue.TryDequeue(out message!);

    public void Clear() => _queue.Clear();
}

/// <summary>
/// One running gateway engine with its channels: built from a configuration, every running side bound through its plugin, ticked by the system once per frame,
/// disposed with the world boundary or the node. Owns everything the plugins handed back.
/// </summary>
public sealed class GatewayRuntime : IDisposable
{
    private readonly List<Action> _pumps = new();
    private readonly List<IDisposable> _owned = new();
    private bool _disposed;

    private GatewayRuntime(GatewayConfiguration config, IClock clock, InMemoryInbound hrotIn, QueueOutbound<IGatewayToHrotMessage> hrotOut)
    {
        Config = config;
        Clock = clock;
        HrotIn = hrotIn;
        HrotOut = hrotOut;
        Engine = null!;
    }

    public GatewayConfiguration Config { get; }

    public IClock Clock { get; }

    public GatewayEngine Engine { get; private set; }

    /// <summary>What the adapter writes: the messages HROT sends to the gateway.</summary>
    public InMemoryInbound HrotIn { get; }

    /// <summary>What the adapter reads: the messages the gateway sends to HROT.</summary>
    public QueueOutbound<IGatewayToHrotMessage> HrotOut { get; }

    /// <summary>True when events are gathered while HROT replays: the shared flag, or a running side that shows them.</summary>
    public bool EventsInReplay { get; private set; }

    public static GatewayRuntime Create(GatewayConfiguration config, IIdAllocator<int>? hrotIds, PluginSet plugins, HostServices services, IClock? clock = null,
        Func<long, string?>? munitionName = null, Action<string>? log = null, IPlayerStation? playerStation = null, IGizmoSource? gizmoSource = null, Func<long>? stationTicks = null,
        IReadOnlyList<IPlayerStation>? playerSeats = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(services);
        clock ??= new StopwatchClock();
        log ??= line => Console.Error.WriteLine("[SimNetGateway] " + line);
        munitionName ??= _ => null;
        var configuration = HrotDisConfigurations.For(config, "hrot-extension", hrotIds, null, plugins.Registry);
        var builder = configuration.CreateBuilder(clock);
        var hrot = builder.RequireInbound("hrot");
        var runtime = new GatewayRuntime(config, clock, new InMemoryInbound(hrot.Source, "hrot"), new QueueOutbound<IGatewayToHrotMessage>())
        {
            EventsInReplay = config.Shared.SendEventsInReplay,
        };
        try
        {
            builder.BindInbound("hrot", runtime.HrotIn);
            builder.BindOutbound<IGatewayToHrotMessage>("hrot", runtime.HrotOut);
            foreach (var found in plugins.Plugins)
            {
                string key = found.Plugin.Side.Key;
                if (config.SettingsOf(key) is not { } settings || !found.Plugin.Side.Runs(settings))
                {
                    continue;
                }

                if (found.Plugin.WantsEventsInReplay(settings))
                {
                    runtime.EventsInReplay = true;
                }

                var bound = found.Plugin.Bind(new SideBinding(builder, config, settings, clock, services.Of(key), munitionName, log,
                    string.IsNullOrEmpty(found.Folder) ? null : found.Folder, playerStation, gizmoSource ?? playerStation as IGizmoSource, stationTicks, playerSeats));
                if (bound.Lease is not null)
                {
                    runtime._owned.Add(bound.Lease);
                }

                if (bound.Pump is not null)
                {
                    runtime._pumps.Add(bound.Pump);
                }
            }

            runtime.Engine = builder.Build().Engine;
            return runtime;
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    /// <summary>One engine tick: the plugins' pumps run (transports are polled), then the engine drains its inbound channels, runs its rules and sends.</summary>
    public void Tick()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var pump in _pumps)
        {
            pump();
        }

        Engine.Tick();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // reverse order of opening; a failing dispose must not leave the others open
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            try
            {
                _owned[i].Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[SimNetGateway] closing a transport failed: " + ex.Message);
            }
        }
    }
}
