using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

/// <summary>How a <see cref="NedRprHost"/> treats the messages the gateway sends.</summary>
public sealed record NedRprHostOptions
{
    /// <summary>True: recorders keep every outbound message. False: counting channels that keep nothing (benchmarks).</summary>
    public bool Record { get; init; } = true;

    /// <summary>Engine options; null selects the sizes the demo and the tests use.</summary>
    public GatewayOptions? EngineOptions { get; init; }
}

/// <summary>
/// The host assembly of the NED-RPR gateway in one process (DD-003 section 5): the configuration is built with one
/// multi-type in-memory inbound channel per input side, outbound recorders or counting channels, and the simulated RTI
/// wired to the RPR outbound path; every port is bound and the gateway built. Shared by the demo, the benchmark, the tests
/// and, later, the showcase and the viewer.
/// </summary>
public sealed class NedRprHost
{
    /// <summary>One engine tick: 60 Hz.</summary>
    public const double Dt = 1.0 / 60.0;

    private readonly ForwardingOutbound<IRprMessage>? _rprCounter;
    private readonly NullOutbound<INedMessage>? _nedNull;
    private readonly NullOutbound<IDisplayMessage>? _displayNull;

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
        Rti = new SimulatedRti();
        Builder.BindInbound("ned", NedIn);
        Builder.BindInbound("rpr", RprIn);
        if (options.Record)
        {
            NedOut = new InMemoryOutbound<INedMessage>();
            RprOut = new InMemoryOutbound<IRprMessage> { Tap = Rti.Observe };
            DisplayOut = new InMemoryOutbound<IDisplayMessage>();
            Builder.BindOutbound<INedMessage>("ned", NedOut);
            Builder.BindOutbound<IRprMessage>("rpr", RprOut);
            Builder.BindOutbound<IDisplayMessage>("display", DisplayOut);
        }
        else
        {
            _nedNull = new NullOutbound<INedMessage>();
            _displayNull = new NullOutbound<IDisplayMessage>();
            _rprCounter = new ForwardingOutbound<IRprMessage> { Observer = Rti.Observe };
            Builder.BindOutbound<INedMessage>("ned", _nedNull);
            Builder.BindOutbound<IRprMessage>("rpr", _rprCounter);
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

    public InMemoryInbound RprIn { get; }

    /// <summary>The recorders; null when the host does not record.</summary>
    public InMemoryOutbound<INedMessage>? NedOut { get; }

    public InMemoryOutbound<IRprMessage>? RprOut { get; }

    public InMemoryOutbound<IDisplayMessage>? DisplayOut { get; }

    public SimulatedRti Rti { get; }

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

    /// <summary>When false the RTI does not answer registrations (the handle stays pending).</summary>
    public bool DeliverRti { get; set; } = true;

    /// <summary>Called at the end of every <see cref="Step"/>.</summary>
    public Action<NedRprHost>? AfterTick { get; set; }

    /// <summary>Messages the gateway has sent to NED so far (recorded or counted).</summary>
    public long NedSentCount => NedOut?.Count ?? _nedNull!.Sent;

    public long RprSentCount => RprOut?.Count ?? _rprCounter!.Sent;

    public long DisplaySentCount => DisplayOut?.Count ?? _displayNull!.Sent;

    public double SimTime => Engine.SimTime;

    /// <summary>
    /// One engine tick: advances the clock by <paramref name="dt"/> when it is a <see cref="ManualClock"/>, lets the RTI
    /// answer the registrations requested earlier, ticks the engine, then calls <see cref="AfterTick"/>.
    /// </summary>
    public void Step(double dt = Dt)
    {
        if (Clock is ManualClock manual)
        {
            manual.Advance(dt);
        }

        if (DeliverRti)
        {
            Rti.DeliverPending(registered => RprIn.Enqueue(registered));
        }

        Engine.Tick();
        AfterTick?.Invoke(this);
    }

    private IdentityMap<TKey> Map<TKey>(string name)
        where TKey : notnull, IEquatable<TKey> =>
        (IdentityMap<TKey>)(Engine.Identities.Find(name) ?? throw new InvalidOperationException($"No identity map '{name}'."));
}
