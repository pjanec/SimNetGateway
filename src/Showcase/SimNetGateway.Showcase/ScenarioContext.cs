using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

/// <summary>An input the script injected: when (runner time, sim time of the tick that processed it), where and what.</summary>
public sealed class InputRecord
{
    public InputRecord(long tick, double runnerTime, int act, string side, object message)
    {
        Tick = tick;
        RunnerTime = runnerTime;
        Act = act;
        Side = side;
        Message = message;
    }

    public long Tick { get; }

    public double RunnerTime { get; }

    /// <summary>Sim time of the tick that processed the input (the time the gateway stored as the state time).</summary>
    public double SimTime { get; internal set; }

    public int Act { get; }

    /// <summary>"NED" or "RPR".</summary>
    public string Side { get; }

    public object Message { get; }
}

/// <summary>
/// What a <see cref="CheckStep"/>, <see cref="ActionStep"/> and the harness tests see of a running scenario: the host with its
/// recorders, the probe and the test-only modules, the checker, the inputs injected so far, the current act and times, and a
/// scratch store for values that a later check needs (e.g. a counter noted at an operator action).
/// Time windows are in runner seconds and inclusive at both ends, resolved to the 60 Hz ticks (a message sent in the tick of the
/// step that caused it belongs to the window that starts at that step).
/// </summary>
public sealed class ScenarioContext
{
    private readonly Dictionary<string, object> _store = new(StringComparer.Ordinal);
    private readonly List<InputRecord> _inputs = new();

    internal ScenarioContext(NedRprHost host, ManualClock clock, InvariantChecker checker, ProbeModule probe, FaultyModule faulty,
        RogueProcessor rogue)
    {
        Host = host;
        Clock = clock;
        Checker = checker;
        Probe = probe;
        Faulty = faulty;
        Rogue = rogue;
    }

    public NedRprHost Host { get; }

    public GatewayEngine Engine => Host.Engine;

    public ManualClock Clock { get; }

    public InvariantChecker Checker { get; }

    public ProbeModule Probe { get; }

    public FaultyModule Faulty { get; }

    public RogueProcessor Rogue { get; }

    public PivotSchema Schema => Host.Schema;

    public InMemoryOutbound<INedMessage> Ned => Host.NedOut!;

    public InMemoryOutbound<IRprMessage> Rpr => Host.RprOut!;

    public InMemoryOutbound<IDisplayMessage> Display => Host.DisplayOut!;

    /// <summary>The number of the act that is running.</summary>
    public int Act { get; internal set; }

    /// <summary>Runner time: the manual clock, in seconds.</summary>
    public double RunnerTime => Clock.Now;

    /// <summary>Engine sim time (resets at Load, frozen while loaded or paused).</summary>
    public double SimTime => Engine.SimTime;

    /// <summary>The tick that just ended (or is running).</summary>
    public long Tick => Engine.TickCount;

    /// <summary>NED outbound messages are fed back into the NED inbound channel (a DDS loopback).</summary>
    public bool LoopbackNed { get; set; }

    /// <summary>How many NED outbound messages were looped back so far.</summary>
    public int LoopedNed { get; internal set; }

    /// <summary>The inputs injected so far, in order (not the looped-back messages and not the RTI's answers).</summary>
    public IReadOnlyList<InputRecord> Inputs => _inputs;

    internal void AddInput(InputRecord record) => _inputs.Add(record);

    /// <summary>The tick whose time is the nearest to the runner time (ticks are at k / 60 s).</summary>
    public static long TickOf(double runnerTime) => (long)Math.Round(runnerTime * 60.0);

    // ---- recorders ------------------------------------------------------------------------------------------------------

    public List<Recorded<INedMessage>> NedBetween(double from, double to) => Between(Ned, from, to);

    public List<Recorded<IRprMessage>> RprBetween(double from, double to) => Between(Rpr, from, to);

    public List<Recorded<IDisplayMessage>> DisplayBetween(double from, double to) => Between(Display, from, to);

    public List<Recorded<T>> NedOf<T>(double from, double to)
        where T : INedMessage => OfType<INedMessage, T>(NedBetween(from, to));

    public List<Recorded<T>> RprOf<T>(double from, double to)
        where T : IRprMessage => OfType<IRprMessage, T>(RprBetween(from, to));

    public List<Recorded<T>> DisplayOf<T>(double from, double to)
        where T : IDisplayMessage => OfType<IDisplayMessage, T>(DisplayBetween(from, to));

    /// <summary>The RPR messages of the window without the registration traffic (<see cref="RprRegisterObject"/>).</summary>
    public List<Recorded<IRprMessage>> RprPayloadBetween(double from, double to) =>
        RprBetween(from, to).Where(r => r.Message is not RprRegisterObject).ToList();

    private static List<Recorded<TMessage>> Between<TMessage>(InMemoryOutbound<TMessage> outbound, double from, double to)
    {
        long first = TickOf(from);
        long last = TickOf(to);
        var result = new List<Recorded<TMessage>>();
        foreach (var recorded in outbound.Messages)
        {
            if (recorded.Tick >= first && recorded.Tick <= last)
            {
                result.Add(recorded);
            }
        }

        return result;
    }

    private static List<Recorded<TSpecific>> OfType<TMessage, TSpecific>(List<Recorded<TMessage>> source)
        where TSpecific : TMessage
    {
        var result = new List<Recorded<TSpecific>>();
        foreach (var recorded in source)
        {
            if (recorded.Message is TSpecific specific)
            {
                result.Add(new Recorded<TSpecific>(specific, recorded.Tick, recorded.WallTime, recorded.SimTime, recorded.Sequence));
            }
        }

        return result;
    }

    /// <summary>The inputs of a side that are of the given type.</summary>
    public List<InputRecord> InputsOf<T>(string? side = null)
    {
        var result = new List<InputRecord>();
        foreach (var input in _inputs)
        {
            if (input.Message is T && (side is null || input.Side == side))
            {
                result.Add(input);
            }
        }

        return result;
    }

    // ---- entities ---------------------------------------------------------------------------------------------------

    /// <summary>The live entities (a copy).</summary>
    public List<EntityRecord> Entities()
    {
        var list = new List<EntityRecord>();
        foreach (var entity in Engine.State.Entities)
        {
            list.Add(entity);
        }

        return list;
    }

    /// <summary>The first live entity with this name (<c>Identity.Name</c>), or null.</summary>
    public EntityRecord? FindByName(string name)
    {
        foreach (var entity in Engine.State.Entities)
        {
            if (entity.TryGet(Schema.Name, out var value) && string.Equals(value.AsString(), name, StringComparison.Ordinal))
            {
                return entity;
            }
        }

        return null;
    }

    /// <summary>The live entity of a NED id (an individual or a formation unit), or null.</summary>
    public EntityRecord? NedEntity(ulong id) =>
        Host.NedIds.TryResolve(id, out var entity) ? Engine.State.Find(entity) : null;

    /// <summary>The live member of a NED formation, or null.</summary>
    public EntityRecord? NedMember(ulong formation, int index) =>
        Host.NedMembers.TryResolve(new NedMemberKey(formation, index), out var entity) ? Engine.State.Find(entity) : null;

    /// <summary>The live entity of an RPR object handle, or null.</summary>
    public EntityRecord? RprEntity(uint handle) =>
        Host.RprHandles.TryResolve(new RprObjectHandle(handle), out var entity) ? Engine.State.Find(entity) : null;

    /// <summary>The RPR handle of an entity, or null.</summary>
    public RprObjectHandle? HandleOf(EntityId entity) => Host.RprHandles.TryGetKey(entity, out var handle) ? handle : null;

    /// <summary>The NED id of an entity (individual or unit), or 0.</summary>
    public ulong NedIdOf(EntityId entity) => Host.NedIds.TryGetKey(entity, out ulong id) ? id : 0;

    /// <summary>The DIS entity identifier of an entity, or null.</summary>
    public DisEntityId? DisIdOf(EntityId entity) => Host.RprEntityIds.TryGetKey(entity, out var id) ? id : null;

    // ---- allocators -------------------------------------------------------------------------------------------------

    /// <summary>The allocator of the gateway's NED id range.</summary>
    public RangeAllocator NedRange => (RangeAllocator)(Host.NedIds.Allocator ?? throw new InvalidOperationException("ned.entity has no allocator."));

    /// <summary>The pool of DIS entity numbers (with quarantine) behind <c>rpr.entityId</c>.</summary>
    public NumberPool DisPool =>
        (NumberPool)((MappedAllocator<DisEntityId>)(Host.RprEntityIds.Allocator ?? throw new InvalidOperationException("rpr.entityId has no allocator."))).Numbers;

    // ---- scratch store ----------------------------------------------------------------------------------------------

    /// <summary>Notes a value for a later step.</summary>
    public void Remember(string key, object value) => _store[key] = value;

    public T Recall<T>(string key) => (T)_store[key];

    public bool TryRecall<T>(string key, out T value)
    {
        if (_store.TryGetValue(key, out var stored) && stored is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    // ---- rules ------------------------------------------------------------------------------------------------------

    public RuleInfo Rule(string fullName) =>
        Engine.FindRule(fullName) ?? throw new KeyNotFoundException("No rule named " + fullName);
}
