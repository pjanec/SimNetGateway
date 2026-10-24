using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Engine.Monitoring.Tests;

internal readonly record struct CreateMsg(int Key, string Name, EntityId Parent, double X);

internal readonly record struct SetMsg(EntityId Entity, int Which, long N);

internal readonly record struct DestroyMsg(EntityId Entity);

internal readonly record struct EventMsg(long N);

internal readonly record struct AbortMsg(int N);

internal readonly record struct ThrowMsg(int N);

internal readonly record struct SessionMsg(SessionCommandKind Kind);

internal readonly record struct UnknownMsg(int N);

/// <summary>Per-entity state of the "w.slots" table (every entity has one).</summary>
internal readonly record struct Slot(int N, bool Flag);

/// <summary>Per-entity state of the "w.pending" table (entities with an even id have one).</summary>
internal readonly record struct Pend(string Text);

/// <summary>A channel whose messages are scripted by the test.</summary>
internal sealed class ScriptChannel : IInboundChannel
{
    private readonly Queue<Action<IIngressSink>> _queue = new();

    public ScriptChannel(string name, SourceId source)
    {
        Name = name;
        Source = source;
    }

    public SourceId Source { get; }

    public string Name { get; }

    public void Send<T>(T message) => _queue.Enqueue(sink => sink.Deliver(in message));

    public int Drain(IIngressSink sink, int budget)
    {
        int n = 0;
        while (n < budget && _queue.Count > 0)
        {
            _queue.Dequeue()(sink);
            n++;
        }

        return n;
    }
}

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

/// <summary>One trigger as the reference probe module saw it.</summary>
internal readonly record struct Trig(long Corr, DispatchTriggerKind Kind, EntityId Entity, EventTypeId EventType);

/// <summary>An ingress message as the handlers saw it: what arrived, with the correlation of its transaction (0 when unhandled).</summary>
internal readonly record struct Arrival(string Type, long Corr);

/// <summary>
/// A small gateway built for the monitoring tests: two sides, a processor, a handful of attributes (one with reference index),
/// two identity maps, two entity tables, a probe module that records every trigger the engine dispatches, and rules with
/// statically known behaviour (fire, reject, fail, disabled, cascade). Driven by seeded random traffic.
/// </summary>
internal sealed class World
{
    // attribute bits of the masks
    public const int CountBit = 1, PosBit = 2, LabelBit = 4, NameBit = 8, SpeedBit = 16, ParentBit = 32;

    public readonly ManualClock Clock = new();
    public readonly GatewayEngine Engine;
    public readonly AttrId Name, Pos, Count, Label, Speed, Parent;
    public readonly SourceId SideA, SideB, Proc;
    public readonly EventTypeId Fired, Other;
    public readonly IdentityMap<int> MapA;
    public readonly IdentityMap<string> MapB;
    public readonly EntityTable<Slot> Slots;
    public readonly EntityTable<Pend> Pending;
    public readonly ScriptChannel ChannelA, ChannelB;
    public readonly InMemoryOutbound<string> Out;
    public readonly RuleInfo OffRule;
    public readonly Random Rnd;
    public readonly JournalMirror Mirror = new();

    // reference logs of the current tick (cleared by BeginTick)
    public readonly List<Trig> TriggerLog = new();
    public readonly Dictionary<(long, int), (int Changed, int Written)> Masks = new();
    public readonly List<Arrival> Arrivals = new();
    public readonly List<(long Parent, long Child)> CommitCascades = new();
    public readonly List<(long Child, long CausedBy)> CausedByLog = new();
    public readonly List<(long Corr, string Text)> Sent = new();
    public readonly List<string> HostOps = new();
    public readonly List<EntityId> Live = new();

    public bool VetoPause;
    public bool OffEnabled = true;
    public DispatchTracer? Tracer;
    private int _nextKey = 1;
    private readonly List<int> _usedKeys = new();

    public World(GatewayOptions? options = null, int seed = 1, DispatchTracer? tracer = null)
    {
        Rnd = new Random(seed);
        Tracer = tracer;
        var opts = options ?? new GatewayOptions();
        // sources are registered in this order: engine = 1, a = 2, b = 3, proc = 4
        var policy = new SingleWriterPolicy(new SourceId(4));
        Engine = new GatewayEngine(Clock, new GatewayOptions
        {
            JournalCapacity = opts.JournalCapacity,
            EventLogCapacity = opts.EventLogCapacity,
            DiagnosticsCapacity = opts.DiagnosticsCapacity,
            MaxMessagesPerTick = opts.MaxMessagesPerTick,
            MaxCascadeDepth = opts.MaxCascadeDepth,
            WriteAuthority = policy,
            DispatchObserver = tracer,
        });
        SideA = Engine.Sources.Register("a");
        SideB = Engine.Sources.Register("b");
        Proc = Engine.Sources.Register("proc");
        Assert.Equal(4, Proc.Value);
        tracer?.UseNamesOf(Engine);
        Name = Engine.Attributes.Register("Name", AttrKind.String);
        Pos = Engine.Attributes.Register("Pos", AttrKind.Vec3);
        Count = Engine.Attributes.Register("Count", AttrKind.Int);
        Label = Engine.Attributes.Register("Label", AttrKind.String);
        Speed = Engine.Attributes.Register("Speed", AttrKind.Double);
        Parent = Engine.Attributes.Register("Parent", AttrKind.EntityRef, null, AttrOptions.IndexReferences);
        Fired = Engine.EventTypes.Register("Fired");
        Other = Engine.EventTypes.Register("Other");
        ChannelA = new ScriptChannel("a", SideA);
        ChannelB = new ScriptChannel("b", SideB);
        Engine.AddInbound(ChannelA);
        Engine.AddInbound(ChannelB);
        Out = new InMemoryOutbound<string> { Context = Engine };
        Out.Tap = text => Tracer?.OutboundSent(Engine.CurrentCorrelation, "out", text);

        IdentityMap<int> mapA = null!;
        IdentityMap<string> mapB = null!;
        EntityTable<Slot> slots = null!;
        EntityTable<Pend> pending = null!;
        Engine.AddModule(new LambdaModule("Ingress", s =>
        {
            mapA = s.Identities.Create<int>(SideA, "a.id");
            mapB = s.Identities.Create<string>(SideB, "b.name");
            slots = s.CreateEntityTable<Slot>("w.slots");
            pending = s.CreateEntityTable<Pend>("w.pending");
            s.OnMessage<CreateMsg>(OnCreate);
            s.OnMessage<SetMsg>(OnSet);
            s.OnMessage<DestroyMsg>((in DestroyMsg m, IngressContext ctx) =>
            {
                Arrivals.Add(new Arrival(nameof(DestroyMsg), ctx.Correlation.Value));
                ctx.Changes.DestroyEntity(m.Entity);
            });
            s.OnMessage<EventMsg>((in EventMsg m, IngressContext ctx) =>
            {
                Arrivals.Add(new Arrival(nameof(EventMsg), ctx.Correlation.Value));
                ctx.Publish(Fired, ctx.RentPayload().Set(Count, AttrValue.From(m.N)));
            });
            s.OnMessage<AbortMsg>((in AbortMsg m, IngressContext ctx) =>
            {
                Arrivals.Add(new Arrival(nameof(AbortMsg), ctx.Correlation.Value));
                ctx.Abort("abort " + m.N);
            });
            s.OnMessage<ThrowMsg>((in ThrowMsg m, IngressContext ctx) =>
            {
                Arrivals.Add(new Arrival(nameof(ThrowMsg), ctx.Correlation.Value));
                throw new InvalidOperationException("boom " + m.N);
            });
            s.OnMessage<SessionMsg>((in SessionMsg m, IngressContext ctx) =>
            {
                Arrivals.Add(new Arrival(nameof(SessionMsg), ctx.Correlation.Value));
                ctx.Command(m.Kind == SessionCommandKind.Load ? SessionCommand.Load("s") : new SessionCommand(m.Kind));
            });
        }));
        MapA = mapA;
        MapB = mapB;
        Slots = slots;
        Pending = pending;

        Engine.AddModule(new LambdaModule("Probe", s =>
        {
            s.OnEntityCreated().Named("Created").Do(t =>
                TriggerLog.Add(new Trig(t.Correlation.Value, DispatchTriggerKind.EntityCreated, t.Id, default)));
            s.OnEntityRemoved().Named("Removed").Do(t =>
                TriggerLog.Add(new Trig(t.Correlation.Value, DispatchTriggerKind.EntityRemoved, t.Id, default)));
            s.OnAttributesWritten().Named("Written").Do(t =>
            {
                Masks[(t.Correlation.Value, t.Id.Value)] = (Mask(t, changed: true), Mask(t, changed: false));
                if (t.Kind == EntityTriggerKind.Updated)
                {
                    TriggerLog.Add(new Trig(t.Correlation.Value, DispatchTriggerKind.EntityUpdated, t.Id, default));
                }
            });
            s.OnAnyEvent().Named("AnyEvent").Do(t =>
            {
                TriggerLog.Add(new Trig(t.Correlation.Value, DispatchTriggerKind.Event, default, t.Type));
                Mirror.OnEvent(t.Event);
                if (!t.Event.CausedBy.IsNone)
                {
                    CausedByLog.Add((t.Correlation.Value, t.Event.CausedBy.Value));
                }
            });
            s.OnChangeSetEnd().Named("End").Do(t =>
            {
                TriggerLog.Add(new Trig(t.ChangeSet.Correlation.Value, DispatchTriggerKind.ChangeSetEnd, default, default));
                Mirror.OnRecord(t.ChangeSet);
                if (!t.ChangeSet.CausedBy.IsNone)
                {
                    CausedByLog.Add((t.ChangeSet.Correlation.Value, t.ChangeSet.CausedBy.Value));
                }
            });
            s.OnSessionChanged().Named("Session").Do(_ =>
                TriggerLog.Add(new Trig(Engine.CurrentCorrelation.Value, DispatchTriggerKind.SessionChanged, default, default)));
            s.OnSessionRequest().Named("Request").Do(_ =>
                TriggerLog.Add(new Trig(Engine.CurrentCorrelation.Value, DispatchTriggerKind.SessionRequest, default, default)));
        }));

        RuleInfo off = null!;
        Engine.AddModule(new LambdaModule("W", s =>
        {
            s.OnEntityCreated().Named("Table").Do(t =>
            {
                Slots.GetOrAdd(t.Id) = new Slot(0, true);
                if (t.Id.Value % 2 == 0)
                {
                    Pending.GetOrAdd(t.Id) = new Pend("p" + t.Id.Value);
                }
            });
            s.OnEntityCreated().Named("Bind").Do(t =>
            {
                if (t.Entity.Origin == SideA && t.Id.Value % 3 == 0)
                {
                    MapB.Bind(t.Id, "x" + t.Id.Value);
                }
            });
            s.OnAttributesChanged(Count).Named("Fire").Do(t =>
            {
                ref var slot = ref Slots.GetOrAdd(t.Id);
                slot = slot with { N = slot.N + 1 };
                string text = "fire:" + t.Correlation.Value + ":" + t.Id.Value;
                Sent.Add((t.Correlation.Value, text));
                Out.Send(text);
            });
            s.OnAttributesChanged(Pos).Named("Reject").When(_ => false).Do(_ => { });
            s.OnAttributesChanged(Label).Named("Fail").Do(_ => throw new InvalidOperationException("label failure"));
            off = s.OnAttributesChanged(Count, Pos).Named("Off").Do(_ => { });
            s.OnAttributesChanged(Pos).Named("ProcPos").Do(OnProcPos);
            s.OnEvent(Fired).Named("EvFire").Do(_ => { });
            s.OnEvent(Fired).Named("EvReject").When(_ => false).Do(_ => { });
            s.OnEvent(Fired).Named("EvFail").Do(_ => throw new InvalidOperationException("event failure"));
            s.OnEvent(Fired).Named("EvOff").Disabled().Do(_ => { });
            s.OnEvent(Other).Named("OtherSub").Do(_ => { });
            s.OnSessionChanged().Named("SessionOk").Do(_ => { });
            s.OnSessionChanged().Named("SessionReject").When(_ => false).Do(_ => { });
            s.OnSessionRequest().Named("Veto").Do(t =>
            {
                if (VetoPause && t.Command.Kind == SessionCommandKind.Pause)
                {
                    t.Veto("no pause");
                }
            });
            s.Every(TimeSpan.FromMilliseconds(250)).Named("Timer").Do(_ =>
            {
                if (Live.Count > 0)
                {
                    var cs = Engine.BeginChange(Proc);
                    cs.Set(Live[0], Speed, AttrValue.From(Clock.Now));
                    var r = cs.Commit();
                    if (r.Status == CommitStatus.Committed)
                    {
                        CommitCascades.Add((0, r.Record!.Correlation.Value));
                    }
                }
            });
        }));
        OffRule = off;
    }

    private void OnCreate(in CreateMsg m, IngressContext ctx)
    {
        Arrivals.Add(new Arrival(nameof(CreateMsg), ctx.Correlation.Value));
        var cs = ctx.Changes;
        var id = cs.CreateEntity();
        cs.Set(id, Name, AttrValue.From(m.Name));
        cs.Set(id, Pos, AttrValue.From(new Vec3(m.X, 0, 0)));
        cs.Set(id, Count, AttrValue.From(0L));
        if (!m.Parent.IsNone)
        {
            cs.Set(id, Parent, AttrValue.From(m.Parent));
        }

        if (ctx.Source == SideA)
        {
            MapA.Bind(cs, id, m.Key);
        }
        else
        {
            MapB.Bind(cs, id, "n" + m.Key);
        }
    }

    private void OnSet(in SetMsg m, IngressContext ctx)
    {
        Arrivals.Add(new Arrival(nameof(SetMsg), ctx.Correlation.Value));
        var cs = ctx.Changes;
        switch (m.Which)
        {
            case 0: cs.Set(m.Entity, Count, AttrValue.From(m.N)); break;
            case 1: cs.Set(m.Entity, Pos, AttrValue.From(new Vec3(m.N, 1, 2))); break;
            case 2: cs.Set(m.Entity, Label, AttrValue.From("L" + m.N)); break;
            case 3: cs.Set(m.Entity, Name, AttrValue.From("N" + m.N)); break;
            default: cs.Clear(m.Entity, Label); break;
        }
    }

    private void OnProcPos(EntityTrigger t)
    {
        var cs = Engine.BeginChange(Proc);
        double x = t.Entity.Get(Pos).AsVec3().X;
        cs.Set(t.Id, Speed, AttrValue.From(x * 0.5));
        var r = cs.Commit();
        if (r.Status == CommitStatus.Committed)
        {
            CommitCascades.Add((t.Correlation.Value, r.Record!.Correlation.Value));
        }

        if ((long)x % 3 == 0)
        {
            Engine.Publish(Proc, Other, Engine.RentPayload().Set(Speed, AttrValue.From(x)));
        }
    }

    private int Mask(EntityTrigger t, bool changed)
    {
        int mask = 0;
        (AttrId Attr, int Bit)[] bits = [(Count, CountBit), (Pos, PosBit), (Label, LabelBit), (Name, NameBit), (Speed, SpeedBit), (Parent, ParentBit)];
        foreach (var (attr, bit) in bits)
        {
            if (changed ? t.Changed(attr) : t.Written(attr))
            {
                mask |= bit;
            }
        }

        return mask;
    }

    // ---- driving ----------------------------------------------------------------------------------------------

    public void ClearLogs()
    {
        TriggerLog.Clear();
        Masks.Clear();
        Arrivals.Clear();
        CommitCascades.Clear();
        CausedByLog.Clear();
        Sent.Clear();
        HostOps.Clear();
    }

    /// <summary>One random tick: scripted messages on both sides, a few host operations, the clock, the engine tick.</summary>
    public void RandomTick(bool withHostOps = true)
    {
        ClearLogs();
        int n = Rnd.Next(0, 7);
        for (int i = 0; i < n; i++)
        {
            EnqueueRandomMessage();
        }

        if (withHostOps)
        {
            RandomHostOp();
        }

        Clock.Advance(1.0 / 60.0);
        Engine.Tick();
        UpdateLive();
    }

    /// <summary>One scripted tick: whatever the test enqueued, the clock advanced by <paramref name="dt"/>, the engine tick.</summary>
    public void Step(double dt = 1.0 / 60.0)
    {
        ClearLogs();
        Clock.Advance(dt);
        Engine.Tick();
        UpdateLive();
    }

    private void UpdateLive()
    {
        Live.Clear();
        foreach (var e in Engine.State.Entities)
        {
            Live.Add(e.Id);
        }

        Live.Sort((a, b) => a.Value.CompareTo(b.Value));
    }

    private EntityId PickLive() => Live.Count == 0 ? new EntityId(9999) : Live[Rnd.Next(Live.Count)];

    private void EnqueueRandomMessage()
    {
        var channel = Rnd.Next(2) == 0 ? ChannelA : ChannelB;
        int roll = Rnd.Next(100);
        if (roll < 28)
        {
            int key = _nextKey++;
            if (_usedKeys.Count > 0 && Rnd.Next(20) == 0)
            {
                key = _usedKeys[Rnd.Next(_usedKeys.Count)]; // a duplicate key: the bind conflicts and the commit is rejected
            }

            _usedKeys.Add(key);
            var parent = Live.Count > 0 && Rnd.Next(5) < 2 ? Live[Rnd.Next(Live.Count)] : default;
            channel.Send(new CreateMsg(key, "unit-" + key + (Rnd.Next(4) == 0 ? "-Alpha" : ""), parent, Rnd.Next(0, 1000)));
        }
        else if (roll < 66)
        {
            int which = Rnd.Next(10) switch { < 4 => 0, < 7 => 1, 7 => 2, 8 => 3, _ => 4 };
            channel.Send(new SetMsg(PickLive(), which, Rnd.Next(0, 50)));
        }
        else if (roll < 76)
        {
            channel.Send(new DestroyMsg(PickLive()));
        }
        else if (roll < 84)
        {
            channel.Send(new EventMsg(Rnd.Next(100)));
        }
        else if (roll < 89)
        {
            channel.Send(new AbortMsg(Rnd.Next(100)));
        }
        else if (roll < 92)
        {
            channel.Send(new ThrowMsg(Rnd.Next(100)));
        }
        else if (roll < 97)
        {
            var kind = (SessionCommandKind)Rnd.Next(5);
            channel.Send(new SessionMsg(kind));
        }
        else
        {
            channel.Send(new UnknownMsg(Rnd.Next(100)));
        }
    }

    private void RandomHostOp()
    {
        switch (Rnd.Next(14))
        {
            case 0:
                HostOps.Add("Publish");
                Engine.Publish(SideA, Fired, Engine.RentPayload().Set(Count, AttrValue.From((long)Rnd.Next(10))));
                break;
            case 1:
                HostOps.Add("Execute");
                Engine.Execute(new SessionCommand((SessionCommandKind)Rnd.Next(5)), SideB);
                break;
            case 2:
            {
                HostOps.Add("Commit");
                var cs = Engine.BeginChange(SideA);
                cs.Set(PickLive(), Count, AttrValue.From((long)Rnd.Next(100)));
                cs.Commit();
                break;
            }

            case 3:
                OffEnabled = !OffEnabled;
                OffRule.Enabled = OffEnabled;
                break;
            case 4:
                VetoPause = !VetoPause;
                break;
        }
    }

    public void Run(int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            RandomTick();
        }
    }
}
