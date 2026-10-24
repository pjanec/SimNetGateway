using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Engine.Tests;

/// <summary>A module built from a lambda.</summary>
internal sealed class LambdaModule : IGatewayModule
{
    private readonly Action<ModuleSetup> _register;

    public LambdaModule(string name, Action<ModuleSetup> register)
    {
        Name = name;
        _register = register;
    }

    public string Name { get; }

    public void Register(ModuleSetup setup) => _register(setup);
}

/// <summary>A message with an entity and a position.</summary>
internal readonly record struct MoveMsg(EntityId Entity, Vec3 Pos);

/// <summary>A message without content.</summary>
internal readonly record struct PingMsg(int N);

/// <summary>An inbound channel over a pre-filled ring of struct messages (the channel itself does not allocate).</summary>
internal sealed class TestChannel<T> : IInboundChannel
    where T : struct
{
    private T[] _ring;
    private int _head;
    private int _count;

    public TestChannel(string name, SourceId source, int capacity = 4096)
    {
        Name = name;
        Source = source;
        _ring = new T[capacity];
    }

    public SourceId Source { get; }
    public string Name { get; }
    public int Pending => _count;

    public void Enqueue(in T message)
    {
        if (_count == _ring.Length)
        {
            throw new InvalidOperationException("test channel full");
        }

        _ring[(_head + _count) % _ring.Length] = message;
        _count++;
    }

    public int Drain(IIngressSink sink, int budget)
    {
        int n = 0;
        while (n < budget && _count > 0)
        {
            var message = _ring[_head];
            _head = (_head + 1) % _ring.Length;
            _count--;
            sink.Deliver(in message);
            n++;
        }

        return n;
    }
}

/// <summary>An engine on a manual clock with a few attributes, sources and event types.</summary>
internal sealed class EngineFixture
{
    public readonly ManualClock Clock = new();
    public readonly GatewayEngine Engine;
    public readonly AttrId Pos;
    public readonly AttrId Speed;
    public readonly AttrId Count;
    public readonly AttrId Label;
    public readonly SourceId Net;
    public readonly SourceId Proc;
    public readonly EventTypeId Fired;
    public readonly EventTypeId Other;

    public EngineFixture(GatewayOptions? options = null)
    {
        Engine = new GatewayEngine(Clock, options);
        Pos = Engine.Attributes.Register("pos", AttrKind.Vec3);
        Speed = Engine.Attributes.Register("speed", AttrKind.Double);
        Count = Engine.Attributes.Register("count", AttrKind.Int);
        Label = Engine.Attributes.Register("label", AttrKind.String);
        Net = Engine.Sources.Register("net");
        Proc = Engine.Sources.Register("proc");
        Fired = Engine.EventTypes.Register("Fired");
        Other = Engine.EventTypes.Register("Other");
    }

    public void Module(string name, Action<ModuleSetup> register) =>
        Engine.AddModule(new LambdaModule(name, register));

    /// <summary>Creates an entity in its own host-side transaction.</summary>
    public EntityId Create(SourceId? source = null)
    {
        var cs = Engine.BeginChange(source ?? Net);
        var id = cs.CreateEntity();
        Assert.Equal(CommitStatus.Committed, cs.Commit().Status);
        return id;
    }

    public CommitResult Write(EntityId id, AttrId attr, AttrValue value, SourceId? source = null)
    {
        var cs = Engine.BeginChange(source ?? Net);
        cs.Set(id, attr, value);
        return cs.Commit();
    }

    public static AttrValue V(double x) => AttrValue.From(new Vec3(x, 0, 0));

    public static AttrValue D(double x) => AttrValue.From(x);

    public static AttrValue I(long x) => AttrValue.From(x);

    public static IEnumerable<DiagnosticEntry> Entries(Diagnostics d)
    {
        var list = new List<DiagnosticEntry>();
        foreach (var e in d.Recent)
        {
            list.Add(e);
        }

        return list;
    }
}
