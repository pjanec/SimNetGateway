using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.HlaRpr;

/// <summary>
/// The HLA side of the RPR gateway (DD-008 sections 3.4 - 3.6, 5): the gateway's inbound and outbound channels of
/// <see cref="IRprMessage"/> on top of an <see cref="IRtiAmbassador"/> (the fake RTI now, a real binding later), and the federate
/// ambassador that receives the RTI's callbacks.
/// <para>
/// Threading and order: everything runs on the host thread. The host calls <see cref="Pump"/> before the engine tick; it
/// evokes the RTI's callbacks (which only enqueue, R-018), processes them in order (decoding into inbound messages, asking
/// for attribute values of newly discovered objects) and delivers the answers to registrations made since the previous pump.
/// The engine's <see cref="Drain"/> then hands the messages over. Outbound messages are sent through the RTI synchronously inside
/// <see cref="Send"/>; a registration is answered by <see cref="RprObjectRegistered"/> on the NEXT <see cref="Pump"/> (the gateway
/// models the registration as asynchronous). Nothing here throws into the engine or the pump: RTI errors and undecodable values
/// are counted (see the counters) and the affected unit (one attribute, one parameter, one message) is dropped.
/// </para>
/// </summary>
public sealed partial class RprHlaConnection : IInboundChannel, IOutboundChannel<IRprMessage>, IFederateAmbassador, IDisposable
{
    private static readonly byte[] NoTag = Array.Empty<byte>();
    private const int MaxWarnings = 100;

    private readonly IRtiAmbassador _rti;
    private readonly RprHlaSettings _settings;
    private readonly IClock _clock;
    private readonly RprHlaHandles _h = new();
    private readonly EventCorrelation _correlation;
    private readonly Func<Guid> _newEventId;

    // inbound side: callbacks enqueue into _events, Pump turns them into messages in _inbound, Drain hands those to the engine
    private readonly Queue<CallbackEvent> _events = new();
    private readonly Queue<IRprMessage> _inbound = new();
    private readonly List<RprObjectRegistered> _answers = new();
    private readonly HashSet<uint> _pendingRequests = new();

    // objects: ours (registered by us) and the peers' (discovered); names are the RTI object instance names (R-104)
    private readonly Dictionary<uint, OwnObject> _own = new();
    private readonly Dictionary<int, RemoteObject> _remote = new();
    private readonly Dictionary<uint, string> _handleToName = new();
    private readonly Dictionary<string, uint> _nameToHandle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DisEntityId> _entityIdByName = new(StringComparer.Ordinal);

    private readonly List<string> _warnings = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private bool _started;
    private bool _resigned;

    /// <param name="rti">The RTI ambassador; not connected yet (<see cref="Start"/> connects it with this object as the callback target).</param>
    /// <param name="settings">Federation and federate names, FOM modules.</param>
    /// <param name="clock">The monotonic clock of the host (only used to age the event correlations).</param>
    /// <param name="source">The engine source of the inbound port this channel is bound to.</param>
    /// <param name="name">The channel name.</param>
    /// <param name="newEventId">Source of the Guids for inbound events (random by default; tests pass a deterministic one).</param>
    public RprHlaConnection(IRtiAmbassador rti, RprHlaSettings settings, IClock clock, SourceId source, string name = "rpr",
        Func<Guid>? newEventId = null)
    {
        ArgumentNullException.ThrowIfNull(rti);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrEmpty(name);
        _rti = rti;
        _settings = settings;
        _clock = clock;
        Source = source;
        Name = name;
        _newEventId = newEventId ?? Guid.NewGuid;
        _correlation = new EventCorrelation(settings.EventCorrelationSeconds, settings.EventCorrelationCapacity, _newEventId);
    }

    public SourceId Source { get; }

    public string Name { get; }

    public bool IsStarted => _started && !_resigned;

    // ---- counters: nothing is thrown at the engine, everything is counted ------------------------------------------------

    /// <summary>Attribute or parameter values from the RTI that could not be decoded (the value was dropped, the rest delivered).</summary>
    public int DecodeErrors { get; private set; }

    /// <summary>Values of outbound messages that the FOM cannot hold (the attribute or parameter was not sent).</summary>
    public int EncodeErrors { get; private set; }

    /// <summary>RTI calls on the outbound path that threw an RTI exception (the message was dropped).</summary>
    public int SendErrors { get; private set; }

    /// <summary>Errors of any kind while processing a callback (an unexpected exception was contained).</summary>
    public int CallbackErrors { get; private set; }

    /// <summary>Outbound updates that carried a damage percentage, which RPR FOM 2.0 has no attribute for (R-105).</summary>
    public int DroppedDamagePercent { get; private set; }

    /// <summary>Outbound <see cref="RprSimulationControl"/> messages, which are not carried over HLA (R-107).</summary>
    public int DroppedSimulationControl { get; private set; }

    /// <summary>Outbound messages that name a handle this transport did not register, or are not outbound messages at all.</summary>
    public int RejectedOutbound { get; private set; }

    /// <summary>Reflections, removals and requests for objects this transport does not know.</summary>
    public int UnknownObjectCallbacks { get; private set; }

    /// <summary>Warnings given (each kind once); also raised as <see cref="Warning"/>.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public event Action<string>? Warning;

    /// <summary>Messages waiting to be drained by the engine.</summary>
    public int PendingInbound => _inbound.Count;

    /// <summary>Event identifiers currently correlated with a Guid (for tests).</summary>
    public int EventCorrelationCount => _correlation.Count;

    /// <summary>The RTI name of one of our registered objects or of a discovered one; false when unknown.</summary>
    public bool TryGetObjectName(RprObjectHandle handle, out string name) => _handleToName.TryGetValue(handle.Value, out name!);

    /// <summary>The handle of an object instance name we know (ours or discovered); false when unknown.</summary>
    public bool TryGetObjectHandle(string name, out RprObjectHandle handle)
    {
        if (_nameToHandle.TryGetValue(name, out var value))
        {
            handle = new RprObjectHandle(value);
            return true;
        }

        handle = default;
        return false;
    }

    /// <summary>The merged spatial state the transport keeps for one of its objects, as it is encoded; false for an unknown object or while no position is known.</summary>
    public bool TryGetCachedSpatial(RprObjectHandle handle, out SpatialVariantStruct spatial)
    {
        spatial = default;
        return _own.TryGetValue(handle.Value, out var o) && o.Spatial.TryBuild(out spatial);
    }

    // ---- lifecycle (DD-008 section 5.1) -------------------------------------------------------------------------------

    /// <summary>
    /// Connects, creates the federation execution (an existing one is fine) when <see cref="RprHlaSettings.CreateIfMissing"/>,
    /// joins, resolves every handle once, publishes the attributes of the classes of section 3.5 and both interactions and
    /// subscribes PhysicalEntity, AggregateEntity and both interactions. RTI exceptions of the setup propagate (a federate that
    /// cannot join has nothing to run).
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("The HLA connection is already started.");
        }

        _rti.Connect(this);
        if (_settings.CreateIfMissing)
        {
            try
            {
                _rti.CreateFederationExecution(_settings.FederationName, _settings.FomModules);
            }
            catch (FederationExecutionAlreadyExists)
            {
                // another federate was first: join it
            }
        }

        _rti.JoinFederationExecution(_settings.FederateName, _settings.FederateType, _settings.FederationName);
        _h.Resolve(_rti);
        foreach (var cls in _h.PublishedClasses.Values)
        {
            _rti.PublishObjectClassAttributes(cls, _h.PhysicalSet.Clone());
        }

        _rti.PublishInteractionClass(_h.WeaponFire);
        _rti.PublishInteractionClass(_h.Detonation);
        _rti.SubscribeObjectClassAttributes(_h.PhysicalEntity, _h.PhysicalSet.Clone());
        _rti.SubscribeObjectClassAttributes(_h.AggregateEntity, _h.AggregateSet.Clone());
        _rti.SubscribeInteractionClass(_h.WeaponFire);
        _rti.SubscribeInteractionClass(_h.Detonation);
        _started = true;
    }

    /// <summary>Resigns, deleting the objects this federate registered (their peers get removals). Safe to call twice.</summary>
    public void Dispose()
    {
        if (!_started || _resigned)
        {
            return;
        }

        _resigned = true;
        try
        {
            _rti.ResignFederationExecution(ResignAction.DeleteObjects);
        }
        catch (RtiException)
        {
            // already gone
        }

        _own.Clear();
    }

    // ---- the pump (DD-008 section 6) ----------------------------------------------------------------------------------

    /// <summary>
    /// Called by the host before the engine tick: delivers the answers of the registrations made since the last pump, evokes the
    /// RTI's callbacks, processes them in order, and ages the event correlations.
    /// </summary>
    public void Pump()
    {
        if (!IsStarted)
        {
            return;
        }

        for (int i = 0; i < _answers.Count; i++)
        {
            _inbound.Enqueue(_answers[i]);
        }

        _answers.Clear();
        try
        {
            _rti.EvokeMultipleCallbacks(0, 0);
        }
        catch (RtiException)
        {
            CallbackErrors++;
        }

        while (_events.Count > 0)
        {
            var e = _events.Dequeue();
            try
            {
                Process(e);
            }
            catch (Exception ex)
            {
                // never an exception out of Pump: the callback is dropped
                CallbackErrors++;
                WarnOnce("callback:" + ex.GetType().Name, $"A {e.GetType().Name} callback failed and was dropped: {ex.Message}");
            }
        }

        _correlation.Expire(_clock.Now);
    }

    /// <inheritdoc />
    public int Drain(IIngressSink sink, int budget)
    {
        ArgumentNullException.ThrowIfNull(sink);
        int n = 0;
        while (n < budget && _inbound.Count > 0)
        {
            var message = _inbound.Dequeue();
            if (message is RprUpdateRequested requested)
            {
                _pendingRequests.Remove(requested.Handle.Value);
            }

            Deliver(sink, message);
            n++;
        }

        return n;
    }

    private static void Deliver(IIngressSink sink, IRprMessage message)
    {
        // the engine routes by the concrete type: hand each message over with its static type
        switch (message)
        {
            case RprObjectUpdate m: sink.Deliver(in m); break;
            case RprAggregateEntity m: sink.Deliver(in m); break;
            case RprObjectRemoved m: sink.Deliver(in m); break;
            case RprWeaponFire m: sink.Deliver(in m); break;
            case RprMunitionDetonation m: sink.Deliver(in m); break;
            case RprObjectRegistered m: sink.Deliver(in m); break;
            case RprUpdateRequested m: sink.Deliver(in m); break;
            default: throw new InvalidOperationException("Unexpected inbound message " + message.GetType().Name);
        }
    }

    private void WarnOnce(string kind, string message)
    {
        if (!_warned.Add(kind))
        {
            return;
        }

        if (_warnings.Count < MaxWarnings)
        {
            _warnings.Add(message);
        }

        Warning?.Invoke(message);
    }

    // ---- state records ----------------------------------------------------------------------------------------------

    private sealed class OwnObject
    {
        public OwnObject(ObjectInstanceHandle instance, string name)
        {
            Instance = instance;
            Name = name;
        }

        public ObjectInstanceHandle Instance { get; }

        public string Name { get; }

        public SpatialCache Spatial { get; } = new();
    }

    private sealed class RemoteObject
    {
        public RemoteObject(ObjectInstanceHandle instance, string name, bool isAggregate)
        {
            Instance = instance;
            Name = name;
            IsAggregate = isAggregate;
        }

        public ObjectInstanceHandle Instance { get; }

        public string Name { get; }

        public bool IsAggregate { get; }

        public RprObjectHandle Handle => new((uint)Instance.Value);

        public DisEntityId? EntityId { get; set; }

        // aggregate state, kept across partial reflections
        public string? Marking { get; set; }

        public DisEntityType? EntityType { get; set; }

        public RprForceId? Force { get; set; }

        public string[]? MemberNames { get; set; }
    }
}
