using System.Collections.Concurrent;
using System.Numerics;
using DISnet;
using Fdp.Core;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Combat.Contracts;
using Fdp.Toolkit.Combat.Events;
using Fdp.Toolkit.Orchestration;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Replication.Services;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Gateway.HrotDis.Tests;
using SimNetGateway.Plugin.Dis;
using SimNetGateway.Protocol.Dis7;
using SimNetGateway.Protocol.Hrot;
using SimNetGateway.Transport.DisUdp;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;
using Quaternion = System.Numerics.Quaternion;
using EntityInfo = Fdp.Core.EntityInfo;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>A datagram socket in memory: what the gateway sends is queued for the test, what the test feeds goes through <see cref="DisUdpTransport.Ingest"/>.</summary>
public sealed class LoopSocket : IDatagramSocket
{
    public ConcurrentQueue<byte[]> Sent { get; } = new();

    public int Receive(byte[] buffer, int timeoutMs) => -1;

    public void Send(ReadOnlySpan<byte> datagram) => Sent.Enqueue(datagram.ToArray());

    /// <summary>True once the gateway closed the socket (teardown).</summary>
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// The tests' host of the pack: a bare <see cref="EntityRepository"/> as the HROT world, the system under test with the DIS plugin on an in-memory socket (more plugins can be
/// added) and a manual gateway clock. One <see cref="Step"/> is one editor frame (system, then the bus swap). Another test project derives from it to add its own plugins.
/// </summary>
public class PackHarness : IDisposable
{
    public const double Lat = 50.0, Lon = 14.0, Alt = 300.0;
    public const int LocalNodeId = 1;

    private long _frame;
    private int _nextId = 1000;

    /// <param name="dis">The DIS section is present.</param>
    /// <param name="realDis">Use the real UDP transport of the DIS plugin instead of the in-memory socket.</param>
    /// <param name="plugins">Plugins besides DIS, in order.</param>
    /// <param name="configure">Adds the settings of those plugins to the configuration.</param>
    /// <param name="seats">How many player seats the host offers (DD-023); station of seat n = node | n &lt;&lt; 16.</param>
    public PackHarness(bool dis = true, Action<GatewayConfiguration>? inspect = null, DisConfiguration? disConfiguration = null, bool realDis = false,
        IReadOnlyList<IGatewayPlugin>? plugins = null, Func<GatewayConfiguration, GatewayConfiguration>? configure = null, Func<long, string?>? munitionName = null, int seats = 1)
    {
        Config = GatewayConfiguration.CreateDefault(1) with { Dis = dis ? disConfiguration ?? new DisConfiguration { ExerciseId = 1, HeartbeatS = 5, TimeoutS = 12 } : null };
        if (configure is not null)
        {
            Config = configure(Config);
        }

        inspect?.Invoke(Config);
        World = new EntityRepository();
        EcsHrotAdapter.EnsureRegistered(World);
        World.SetSingleton(new GlobalTime { TimeScale = 1f });
        Map = new NetworkEntityMap();
        var disPlugin = realDis
            ? new DisPlugin { Open = (settings, source) => DisTransport = DisUdpTransport.Open(settings, source) }
            : new DisPlugin
            {
                Open = (settings, source) =>
                {
                    DisWire = new LoopSocket();
                    return DisTransport = new DisUdpTransport(settings, DisWire, source);
                },
            };
        Plugins = PluginSet.Of([disPlugin, .. plugins ?? []]);
        var services = new HrotServices
        {
            State = () => State,
            GeoReference = () => GeoRef,
            EntityMap = Map,
            LocalNodeId = LocalNodeId,
            Tkb = () => TestTkb,
            EventBus = World.Bus,
            TimeController = () => TimeController,
            WallTicks = () => WallTicksSource(),
        };
        var allocator = new NodeIdAllocator(() => _nextId++);
        HostServices = new HostServices(Plugins, Config, line => Log.Add(line));
        System = new DisGatewaySystem(services, Config, seats, stations => GatewayRuntime.Create(Config, allocator, Plugins, HostServices, GatewayClock, munitionName, line => Log.Add(line), stations[0], playerSeats: stations),
            line => Errors.Add(line), () => HostServices.Pump(line => Log.Add(line)));
    }

    /// <summary>The node's template database as the gateway reads it (empty: tests register the templates whose DIS type they need).</summary>
    public Fdp.Toolkit.Tkb.TkbDatabase TestTkb { get; } = new();

    /// <summary>Registers a template of the given TKB id with a DIS entity kind (1 platform, 3 life form).</summary>
    public void RegisterTemplate(long tkb, byte disKind)
    {
        var template = new Fdp.Interfaces.TkbTemplate("T" + tkb, tkb) { DisType = new Fdp.Core.DISEntityType { Kind = disKind, Domain = 1, Country = 225, Category = 1 } };
        TestTkb.Register(template);
    }

    /// <summary>The node's time controller as the host reads it (null = none; a test sets HROT's real master controller).</summary>
    public Fdp.ModuleHost.Time.ITimeController? TimeController { get; set; }

    /// <summary>The wall tick source of the host (a test's controlled clock).</summary>
    public Func<long> WallTicksSource { get; set; } = Fdp.Toolkit.Time.HighResUtcClock.GetTicks;

    public GatewayConfiguration Config { get; }

    public PluginSet Plugins { get; }

    /// <summary>The host services of the plugins, as <see cref="GatewayCapability"/> holds them.</summary>
    public HostServices HostServices { get; }

    /// <summary>What the plugins and the runtime logged.</summary>
    public List<string> Log { get; } = new();

    public EntityRepository World { get; }

    public NetworkEntityMap Map { get; }

    public ManualClock GatewayClock { get; } = new();

    public DisGatewaySystem System { get; }

    public ClusterState State { get; set; } = ClusterState.OperatingLive;

    public HrotGeoRef? GeoRef { get; set; } = new HrotGeoRef(true, Lat, Lon, Alt, 0, false);

    public bool Advancing { get; set; } = true;

    public double SimTime { get; set; }

    public List<string> Errors { get; } = new();

    public LoopSocket DisWire { get; private set; } = null!;

    public DisUdpTransport DisTransport { get; private set; } = null!;

    public List<Pdu> DisSent { get; } = new();

    public TestFrame Frame { get; } = new(Lat, Lon, Alt);

    public GatewayEngine Engine => System.Runtime!.Engine;

    public EcsHrotAdapter Adapter => System.Adapter;

    // ---- stepping -----------------------------------------------------------------------------------------------------------

    public void Step(float dt = 1f / 60f)
    {
        GatewayClock.Advance(dt);
        double advance = Advancing ? dt : 0.0;
        SimTime += advance;
        World.SetSingleton(new GlobalTime { TotalTime = SimTime, DeltaTime = (float)advance, TimeScale = 1f, FrameNumber = ++_frame });
        System.Execute(World, dt);
        World.Bus.SwapBuffers();
        Drain();
    }

    public void Steps(int count, float dt = 1f / 60f)
    {
        for (int i = 0; i < count; i++)
        {
            Step(dt);
        }
    }

    private sealed class EffectSink : IIngressSink
    {
        public readonly List<HrotEffect> Effects = new();

        public void Deliver<T>(in T message, double? receivedAt = null)
        {
            if (message is HrotEffect effect)
            {
                Effects.Add(effect);
            }
        }
    }

    /// <summary>The effect-state gather, called the way the gateway does (one path for live and replay): the effects of the effect entities not sent before.</summary>
    public List<HrotEffect> GatherEffects(double simNow, bool replaying = false)
    {
        var inbound = new InMemoryInbound(new SourceId(99), "test");
        Adapter.GatherEffects(World, inbound, simNow, replaying);
        var sink = new EffectSink();
        inbound.Drain(sink, int.MaxValue);
        return sink.Effects;
    }

    /// <summary>
    /// Makes an effect entity for every spec, as HROT's <c>EffectSpawnSystem.SpawnAll</c> does (transform, effect type, lifespan, anchor, cause); the specs come from HROT's own
    /// rules (<c>EffectRules.ForShot</c> ...), so the entities are what the map would have made.
    /// </summary>
    public List<Entity> SpawnEffects(double now, Action<List<Hrot.Look.Effects.EffectSpec>> rule)
    {
        foreach (var register in new Action[]
        {
            () => { if (!World.IsComponentTypeRegistered<Hrot.Map.Common.Components.EffectCause>()) { World.RegisterComponent<Hrot.Map.Common.Components.EffectCause>(); } },
        })
        {
            register();
        }

        var specs = new List<Hrot.Look.Effects.EffectSpec>();
        rule(specs);
        var made = new List<Entity>();
        foreach (var spec in specs)
        {
            var e = World.CreateEntity();
            World.AddComponent(e, new SimTransform { Position = spec.At, Rotation = spec.Rotation });
            World.AddComponent(e, new TkbIdentity { TkbType = (Fdp.Core.TkbTypeId)spec.EffectType });
            World.AddComponent(e, Fdp.Toolkit.Time.Lifespan.Starting(now, spec.Lifetime));
            if (spec.Anchor is { } anchor)
            {
                World.AddComponent(e, anchor);
            }

            if (spec.Cause is { } cause)
            {
                World.AddComponent(e, cause);
            }

            made.Add(e);
        }

        return made;
    }

    /// <summary>Decodes what the DIS socket sent since the last step into <see cref="DisSent"/> (a step does it; a test calls it after a teardown).</summary>
    public void Drain()
    {
        if (DisWire is null)
        {
            return;
        }

        while (DisWire.Sent.TryDequeue(out var bytes))
        {
            var decoded = PduCodec.Decode(bytes);
            foreach (var item in decoded.Items)
            {
                if (item.Pdu is not null)
                {
                    DisSent.Add(item.Pdu);
                }
            }
        }
    }

    /// <summary>Publishes an event as if a HROT system had in the previous frame (readable in the next <see cref="Step"/>).</summary>
    public void PublishForNextFrame<T>(T evt)
        where T : unmanaged
    {
        World.Bus.Publish(evt);
        World.Bus.SwapBuffers();
    }

    // ---- HROT entities --------------------------------------------------------------------------------------------------------

    public Entity AddHrotEntity(int id, Vector3 position, string name = "HROT-1", long tkb = 100, ForceId force = ForceId.Friend, float health = 100f)
    {
        var e = World.CreateEntity();
        World.AddComponent(e, new NetworkIdentity(id));
        World.AddComponent(e, new TkbIdentity { TkbType = new TkbTypeId(tkb) });
        World.AddComponent(e, new SimTransform { Position = position, Rotation = Quaternion.Identity });
        World.AddComponent(e, new SimVelocity());
        World.AddComponent(e, new EntityInfo { Name = new FixedString64(name), ForceId = force });
        World.AddComponent(e, new Health { Current = health, Max = 100f });
        World.AddComponent(e, new NetworkAuthority(LocalNodeId, LocalNodeId));
        Map.Register(id, e);
        return e;
    }

    public void Move(Entity e, Vector3 position) => World.SetComponent(e, new SimTransform { Position = position, Rotation = Quaternion.Identity });

    public void RemoveHrotEntity(Entity e)
    {
        if (World.TryGetComponent(e, out NetworkIdentity id))
        {
            Map.Unregister(id.Value, 0);
        }

        World.DestroyEntity(e);
    }

    // ---- DIS peer --------------------------------------------------------------------------------------------------------------

    public Vec3 Ecef(double east, double north, double up) => Frame.PointToEcef(new Vec3(east, north, up));

    public Vec3 EcefVector(Vec3 at, double east, double north, double up) => Frame.VectorToEcef(at, new Vec3(east, north, up));

    /// <summary>Sends a PDU from the scripted peer; with no gateway running (idle, between world boundary and restart) nobody listens and it is lost, like on a real network.</summary>
    public void DisIngest(Pdu pdu)
    {
        if (System.Runtime is not null)
        {
            DisTransport.Ingest(PduCodec.Encode(pdu));
        }
    }

    /// <summary>An Entity State from the scripted DIS peer (site 5, application 77) at a local ENU position, fixed orientation or moving at <paramref name="velocityEnu"/> (FPW).</summary>
    public void DisEntity(ushort number, double east, double north, double up = 0, Vec3 velocityEnu = default, string marking = "DIS", byte force = 2, int algorithm = 2)
    {
        var euler = default(Vec3);
        DisIngest(DisPdus.EntityState(number, Ecef(east, north, up), euler, EcefVector(new Vec3(east, north, up), velocityEnu.X, velocityEnu.Y, velocityEnu.Z), marking: marking, force: force, algorithm: algorithm));
    }

    public static DisEntityKey DisKey(ushort number) => new(DisPdus.PeerSite, DisPdus.PeerApplication, number);

    public IEnumerable<T> Sent<T>()
        where T : Pdu => DisSent.OfType<T>();

    // ---- world queries ---------------------------------------------------------------------------------------------------------

    /// <summary>Every entity on the gateway route (the replicas, whatever the book says).</summary>
    public List<Entity> RoutedEntities()
    {
        var list = new List<Entity>();
        foreach (var e in World.Query().With<ReplicationRoute>().Build())
        {
            if (World.GetComponentRO<ReplicationRoute>(e).RouteId == ReplicationRoute.ExternalGateway)
            {
                list.Add(e);
            }
        }

        return list;
    }

    public Entity ReplicaNamed(string name)
    {
        foreach (var e in RoutedEntities())
        {
            if (World.GetComponentRO<EntityInfo>(e).Name.ToString() == name)
            {
                return e;
            }
        }

        return Entity.Null;
    }

    public virtual void Dispose()
    {
        System.Dispose();
        HostServices.Dispose();
        World.Dispose();
    }
}
