using System.Runtime.CompilerServices;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Transport.FakeRti;
using SimNetGateway.Transport.HlaRpr;

namespace SimNetGateway.Showcase;

/// <summary>How a <see cref="NedRprHost"/> treats the messages the gateway sends.</summary>
public sealed record NedRprHostOptions
{
    /// <summary>True: recorders keep every outbound message. False: counting channels that keep nothing (benchmarks).</summary>
    public bool Record { get; init; } = true;

    /// <summary>Engine options; null selects the sizes the demo and the tests use.</summary>
    public GatewayOptions? EngineOptions { get; init; }

    /// <summary>
    /// True: the scripted RPR federate decodes and keeps what it receives through the RTI. False: it only drains its callbacks
    /// (benchmarks); null follows <see cref="Record"/>.
    /// </summary>
    public bool? PeerDecodes { get; init; }
}

/// <summary>
/// The host assembly of the NED-RPR gateway in one process (DD-003 section 5): the configuration is built with one
/// multi-type in-memory inbound channel per input side, outbound recorders or counting channels, and the RPR side wired to a
/// <see cref="RprHlaConnection"/> on a <see cref="FakeRtiHub"/> (DD-008 sections 6 and 12b): the gateway's "rpr" port is bound to that
/// transport in both directions and the scripted RPR side is a <see cref="ScriptedRprFederate"/> on the same hub, so everything
/// the RPR side says and hears crosses the RTI as encoded attribute and parameter values. Every port is bound and the gateway
/// built. Shared by the demo, the benchmark, the showcase, the viewer and the tests.
/// <para>
/// One host tick (DD-008 section 6): <see cref="PumpRti"/> (the transport evokes the RTI's callbacks and answers the registrations
/// of the previous tick), <c>Engine.Tick()</c> (drain, rules, outbound sends into the RTI), <see cref="EvokePeer"/> (the scripted
/// federate receives and decodes what the gateway sent). No threads, no sleeping: with a <see cref="ManualClock"/> a run is a
/// deterministic sequence of <see cref="Step"/>s.
/// </para>
/// </summary>
public sealed class NedRprHost
{
    /// <summary>One engine tick: 60 Hz.</summary>
    public const double Dt = 1.0 / 60.0;

    private readonly ForwardingOutbound<IRprMessage>? _rprCounter;
    private readonly NullOutbound<INedMessage>? _nedNull;
    private readonly NullOutbound<IDisplayMessage>? _displayNull;
    private readonly RprInboundMux _rprMux;
    private readonly HashSet<RprObjectHandle> _registered = new();
    private readonly List<RprObjectHandle> _registeredOrder = new();
    private readonly Queue<Guid> _scriptedEvents = new();
    private readonly Dictionary<Guid, Guid> _seenEventIds = new();
    private long _eventCounter;

    public NedRprHost(GatewayConfiguration configuration, IClock clock, NedRprHostOptions? options = null,
        Action<GatewayBuilder>? beforeBuild = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(clock);
        options ??= new NedRprHostOptions();
        Clock = clock;
        Configuration = configuration;
        var engineOptions = options.EngineOptions
            ?? new GatewayOptions { JournalCapacity = 16384, DiagnosticsCapacity = 8192, EventLogCapacity = 4096 };
        Builder = configuration.CreateBuilder(clock, engineOptions);
        var ned = Builder.RequireInbound("ned");
        var rpr = Builder.RequireInbound("rpr");
        NedIn = new InMemoryInbound(ned.Source, "ned");
        RprIn = new InMemoryInbound(rpr.Source, "rpr");

        // the RPR side: the transport on a fake RTI, and the scripted federate that plays the rest of the federation
        Hub = new FakeRtiHub();
        var settings = new RprHlaSettings { FederationName = "RPR-Showcase", FederateName = "SimNetGateway" };
        RprConnection = new RprHlaConnection(Hub.CreateAmbassador(), settings, clock, rpr.Source, "rpr", NextEventId);
        RprConnection.Start();
        RprPeer = new ScriptedRprFederate(Hub, settings.FederationName, "RprPeer")
        {
            Decode = options.PeerDecodes ?? options.Record,
        };
        RprPeer.DeclareForGateway();
        _rprMux = new RprInboundMux(rpr.Source, "rpr", RprIn, RprConnection, this);

        Builder.BindInbound("ned", NedIn);
        Builder.BindInbound("rpr", _rprMux);
        if (options.Record)
        {
            NedOut = new InMemoryOutbound<INedMessage>();
            RprOut = new InMemoryOutbound<IRprMessage>();
            DisplayOut = new InMemoryOutbound<IDisplayMessage>();
            Builder.BindOutbound<INedMessage>("ned", NedOut);
            Builder.BindOutbound<IRprMessage>("rpr", new RprOutboundTee(RprOut, RprConnection));
            Builder.BindOutbound<IDisplayMessage>("display", DisplayOut);
        }
        else
        {
            _nedNull = new NullOutbound<INedMessage>();
            _displayNull = new NullOutbound<IDisplayMessage>();
            _rprCounter = new ForwardingOutbound<IRprMessage>();
            Builder.BindOutbound<INedMessage>("ned", _nedNull);
            Builder.BindOutbound<IRprMessage>("rpr", new RprOutboundTee(_rprCounter, RprConnection));
            Builder.BindOutbound<IDisplayMessage>("display", _displayNull);
        }

        beforeBuild?.Invoke(Builder);
        Binding = Builder.Build();
        Engine = Binding.Engine;
        if (NedOut is not null)
        {
            NedOut.Context = Engine;
            RprOut!.Context = Engine;
            DisplayOut!.Context = Engine;
        }

        RprPeer.TickSource = () => Engine.TickCount;
        Schema = new PivotSchema(Engine.Attributes, Engine.EventTypes); // idempotent: the same ids as the gateway's
        NedSource = ned.Source;
        RprSource = rpr.Source;
        NedIds = Map<ulong>("ned.entity");
        RprHandles = Map<RprObjectHandle>("rpr.handle");
        RprEntityIds = Map<DisEntityId>("rpr.entityId");
        DisplayLabels = Map<string>("display.label");
        NedMembers = Map<NedMemberKey>("ned.member");
        NedFires = Map<Guid>("ned.fire");
    }

    public IClock Clock { get; }

    public GatewayConfiguration Configuration { get; }

    public GatewayBuilder Builder { get; }

    public GatewayHostBinding Binding { get; }

    public GatewayEngine Engine { get; }

    public PivotSchema Schema { get; }

    public InMemoryInbound NedIn { get; }

    /// <summary>
    /// The direct inbound queue of the gateway's "rpr" port: a message enqueued here reaches the engine WITHOUT crossing the RTI.
    /// It exists for tests of the gateway's modules that need a message with an exact content (an RPR handle of their own
    /// choosing, a deliberately odd sequence). Everything that stands for the real RPR side (the showcase, the demo, the viewer)
    /// uses <see cref="SendRpr{T}"/>, which goes through <see cref="RprPeer"/> and the RTI. The engine drains this queue before
    /// the transport's messages.
    /// </summary>
    public InMemoryInbound RprIn { get; }

    /// <summary>The fake RTI the RPR side runs on.</summary>
    public FakeRtiHub Hub { get; }

    /// <summary>The gateway's HLA transport (the "rpr" port, both directions).</summary>
    public RprHlaConnection RprConnection { get; }

    /// <summary>The scripted RPR federate on <see cref="Hub"/>: it sends what <see cref="SendRpr{T}"/> is given and decodes what the gateway sends.</summary>
    public ScriptedRprFederate RprPeer { get; }

    /// <summary>The recorders; null when the host does not record.</summary>
    public InMemoryOutbound<INedMessage>? NedOut { get; }

    public InMemoryOutbound<IRprMessage>? RprOut { get; }

    public InMemoryOutbound<IDisplayMessage>? DisplayOut { get; }

    /// <summary>The RTI object handles the transport answered registrations with, in answer order, so far (they are never forgotten).</summary>
    public IReadOnlyList<RprObjectHandle> RegisteredHandles => _registeredOrder;

    /// <summary>True when the transport answered a registration with this handle (G-5: the RTI delivered it).</summary>
    public bool IsRegisteredHandle(RprObjectHandle handle) => _registered.Contains(handle);

    /// <summary>How many registrations the transport answered so far.</summary>
    public int RegisteredCount => _registered.Count;

    public SourceId NedSource { get; }

    public SourceId RprSource { get; }

    public IdentityMap<ulong> NedIds { get; }

    public IdentityMap<RprObjectHandle> RprHandles { get; }

    public IdentityMap<DisEntityId> RprEntityIds { get; }

    public IdentityMap<string> DisplayLabels { get; }

    /// <summary>"ned.member": (formation id, member index) of the members of NED formations.</summary>
    public IdentityMap<NedMemberKey> NedMembers { get; }

    /// <summary>"ned.fire": the fire ids of the split fires under assembly.</summary>
    public IdentityMap<Guid> NedFires { get; }

    /// <summary>When false the transport is not pumped: no RTI callback is processed and no registration is answered (the handle stays pending).</summary>
    public bool DeliverRti { get; set; } = true;

    /// <summary>Called at the end of every <see cref="Step"/>.</summary>
    public Action<NedRprHost>? AfterTick { get; set; }

    /// <summary>Called by <see cref="SendNed{T}"/> with every message injected on the NED side (the showcase runner logs and inspects it).</summary>
    public Action<INedMessage>? OnNedInput { get; set; }

    /// <summary>Called by <see cref="SendRpr{T}"/> with every message injected on the RPR side (as the script wrote it).</summary>
    public Action<IRprMessage>? OnRprInput { get; set; }

    /// <summary>
    /// Called with every message the transport hands to the engine, as the engine sees it (the event ids and handles are the
    /// transport's, not the script's). Set before the first tick; null costs nothing.
    /// </summary>
    public Action<IRprMessage>? OnRprDelivered { get; set; }

    /// <summary>Enqueues a message on the NED inbound channel (after telling <see cref="OnNedInput"/>).</summary>
    public void SendNed<T>(T message)
        where T : INedMessage
    {
        OnNedInput?.Invoke(message);
        NedIn.Enqueue(in message);
    }

    /// <summary>
    /// The RPR side says something: the scripted federate does what a federate does for this message (registers the object on its
    /// first message, updates the attributes the message carries, sends the interaction, deletes) through the RTI, after telling
    /// <see cref="OnRprInput"/>. The handles in the message are SCRIPT handles for the objects of the RPR side (the RTI assigns the
    /// real ones, see <see cref="ActualHandle"/>); a handle of an object the gateway registered is its real RTI handle.
    /// </summary>
    public void SendRpr<T>(T message)
        where T : IRprMessage
    {
        OnRprInput?.Invoke(message);
        if (message is RprWeaponFire fire)
        {
            _scriptedEvents.Enqueue(fire.EventId);
        }
        else if (message is RprMunitionDetonation detonation)
        {
            _scriptedEvents.Enqueue(detonation.EventId);
        }

        RprPeer.Inject(message);
    }

    /// <summary>The RTI handle of the RPR-side object that was announced under a script handle (the script handle itself while the RTI has not seen the object).</summary>
    public RprObjectHandle ActualHandle(uint scriptHandle) => RprPeer.ActualHandle(scriptHandle) ?? new RprObjectHandle(scriptHandle);

    /// <summary>
    /// The event id the gateway saw for an event the script sent with <paramref name="scriptedId"/>: HLA carries no event guid, the
    /// transport correlates the interaction's event identifier with a guid of its own (DD-008 section 3.4). The script's id itself
    /// until the event arrived.
    /// </summary>
    public Guid SeenEventId(Guid scriptedId) => _seenEventIds.TryGetValue(scriptedId, out var seen) ? seen : scriptedId;

    /// <summary>Messages the gateway has sent to NED so far (recorded or counted).</summary>
    public long NedSentCount => NedOut?.Count ?? _nedNull!.Sent;

    public long RprSentCount => RprOut?.Count ?? _rprCounter!.Sent;

    public long DisplaySentCount => DisplayOut?.Count ?? _displayNull!.Sent;

    public double SimTime => Engine.SimTime;

    /// <summary>
    /// One host tick (DD-008 section 6): advances the clock by <paramref name="dt"/> when it is a <see cref="ManualClock"/>,
    /// <see cref="PumpRti"/>, ticks the engine, <see cref="EvokePeer"/>, then calls <see cref="AfterTick"/>.
    /// </summary>
    public void Step(double dt = Dt)
    {
        if (Clock is ManualClock manual)
        {
            manual.Advance(dt);
        }

        PumpRti();
        Engine.Tick();
        EvokePeer();
        AfterTick?.Invoke(this);
    }

    /// <summary>Step 1 of a host tick: the transport evokes the RTI's callbacks and queues the answers for the engine (nothing when <see cref="DeliverRti"/> is false).</summary>
    public void PumpRti()
    {
        if (DeliverRti)
        {
            RprConnection.Pump();
        }
    }

    /// <summary>Step 3 of a host tick: the scripted federate receives (and decodes) what the gateway sent into the RTI during the engine tick.</summary>
    public void EvokePeer() => RprPeer.Evoke();

    private Guid NextEventId() => new((int)++_eventCounter, 0x0E, 0x71, 0x4E, 0, 0, 0, 0, 0, 0, 1);

    private void Delivered<T>(in T message)
    {
        // by static type: no boxing of the struct messages on the hot path (the observer, when set, gets a boxed copy)
        if (typeof(T) == typeof(RprObjectRegistered))
        {
            var handle = Unsafe.As<T, RprObjectRegistered>(ref Unsafe.AsRef(in message)).Handle;
            if (_registered.Add(handle))
            {
                _registeredOrder.Add(handle);
            }
        }
        else if (typeof(T) == typeof(RprWeaponFire))
        {
            MapScriptedEvent(Unsafe.As<T, RprWeaponFire>(ref Unsafe.AsRef(in message)).EventId);
        }
        else if (typeof(T) == typeof(RprMunitionDetonation))
        {
            MapScriptedEvent(Unsafe.As<T, RprMunitionDetonation>(ref Unsafe.AsRef(in message)).EventId);
        }

        if (OnRprDelivered is { } observer && message is IRprMessage boxed)
        {
            observer(boxed);
        }
    }

    private void MapScriptedEvent(Guid seen)
    {
        if (_scriptedEvents.Count > 0)
        {
            _seenEventIds.TryAdd(_scriptedEvents.Dequeue(), seen);
        }
    }

    private IdentityMap<TKey> Map<TKey>(string name)
        where TKey : notnull, IEquatable<TKey> =>
        (IdentityMap<TKey>)(Engine.Identities.Find(name) ?? throw new InvalidOperationException($"No identity map '{name}'."));

    /// <summary>The engine's "rpr" inbound channel: the direct queue first, then the transport (whose messages are reported to the host).</summary>
    private sealed class RprInboundMux : IInboundChannel
    {
        private readonly IInboundChannel _direct;
        private readonly RprHlaConnection _transport;
        private readonly NedRprHost _host;

        public RprInboundMux(SourceId source, string name, IInboundChannel direct, RprHlaConnection transport, NedRprHost host)
        {
            Source = source;
            Name = name;
            _direct = direct;
            _transport = transport;
            _host = host;
        }

        public SourceId Source { get; }

        public string Name { get; }

        public int Drain(IIngressSink sink, int budget)
        {
            int n = _direct.Drain(sink, budget);
            if (n < budget)
            {
                n += _transport.Drain(new ReportingSink(sink, _host), budget - n);
            }

            return n;
        }
    }

    private sealed class ReportingSink : IIngressSink
    {
        private readonly IIngressSink _inner;
        private readonly NedRprHost _host;

        public ReportingSink(IIngressSink inner, NedRprHost host)
        {
            _inner = inner;
            _host = host;
        }

        public void Deliver<T>(in T message, double? receivedAt = null)
        {
            _host.Delivered(in message);
            _inner.Deliver(in message, receivedAt);
        }
    }

    /// <summary>The engine's "rpr" outbound channel: the recorder (or counter) first, so taps see a message before the RTI does, then the transport.</summary>
    private sealed class RprOutboundTee : IOutboundChannel<IRprMessage>
    {
        private readonly IOutboundChannel<IRprMessage> _first;
        private readonly RprHlaConnection _transport;

        public RprOutboundTee(IOutboundChannel<IRprMessage> first, RprHlaConnection transport)
        {
            _first = first;
            _transport = transport;
        }

        public void Send(in IRprMessage message)
        {
            _first.Send(in message);
            _transport.Send(in message);
        }
    }
}
