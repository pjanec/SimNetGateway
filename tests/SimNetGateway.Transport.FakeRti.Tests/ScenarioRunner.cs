using System.Text;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.FakeRti.Tests;

internal enum K
{
    Connect, Create, Destroy, Join, Resign,
    GetClass, GetAttribute, GetInteraction, GetParameter,
    Publish, Unpublish, Subscribe, Unsubscribe, PublishI, UnpublishI, SubscribeI, UnsubscribeI,
    Reserve, Register, Update, Delete, Send, Request, RequestClass, GetName,
    EvokeOne, EvokeAll,
}

/// <summary>One operation of a scenario, independent of model and RTI; both interpret it.</summary>
internal sealed class Op
{
    public K Kind;
    public int Amb;
    public string? Federation;
    public string[] Modules = [];
    public string? Name;

    /// <summary>Object class full name; "BOGUS" stands for a handle the RTI never issued.</summary>
    public string? Cls;

    public string? Icls;

    /// <summary>Attributes or parameters: declaring class full name (null = invalid handle), name, value.</summary>
    public List<(string? Declaring, string Name, byte[] Value)> Items = new();

    public int Obj;
    public byte[] Tag = [];
    public ResignAction Action;

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Kind).Append(" amb=").Append(Amb);
        void Add(string label, object? value)
        {
            if (value is not null)
            {
                sb.Append(' ').Append(label).Append('=').Append(value);
            }
        }
        Add("fed", Federation);
        Add("modules", Modules.Length == 0 ? null : string.Join("+", Modules));
        Add("name", Name);
        Add("cls", Cls);
        Add("icls", Icls);
        Add("obj", Kind is K.Update or K.Delete or K.Request or K.GetName ? Obj : null);
        Add("items", Items.Count == 0 ? null : string.Join(",", Items.Select(i => (i.Declaring ?? "<bogus>") + "#" + i.Name)));
        Add("action", Kind == K.Resign ? Action : null);
        return sb.ToString();
    }
}

/// <summary>The scenario world: the FOM, the extra modules and the handle oracle.</summary>
internal static class World
{
    public const string RprModule = RprFom2.ModuleName;
    public const string InteractionModule = "IA2";
    public const string ConflictModule = "CONFLICT";
    public const string Bogus = "BOGUS";

    private static readonly FomModel InteractionFom = new FomModelBuilder()
        .InteractionClass("HLAinteractionRoot.I1", ("a", "T"))
        .InteractionClass("HLAinteractionRoot.I1.I2", ("b", "T"))
        .InteractionClass("HLAinteractionRoot.I1.I2.I3", ("c", "T"))
        .Build();

    private static readonly FomModel ConflictFom = new FomModelBuilder()
        .ObjectClass(RprFom2.ObjectClasses.BaseEntity, ("EntityType", "SomethingElseEntirely"))
        .Build();

    public static readonly FomModel Fom = RprFom2.Model().Merge(InteractionFom);

    public static void Configure(FakeRtiHub hub)
    {
        hub.RegisterModule(InteractionModule, InteractionFom);
        hub.RegisterModule(ConflictModule, ConflictFom);
    }

    public static readonly string[] ValidModules = [RprModule, InteractionModule];
}

/// <summary>Handle numbers of the scenario FOM as the RTI issues them, asked once from a private hub; plus the reverse maps.</summary>
internal sealed class Oracle
{
    public readonly Dictionary<string, ObjectClassHandle> ClassHandle = new();
    public readonly Dictionary<ObjectClassHandle, string> ClassName = new();
    public readonly Dictionary<(string, string), AttributeHandle> AttributeHandle = new();
    public readonly Dictionary<AttributeHandle, string> AttributeKey = new();
    public readonly Dictionary<string, InteractionClassHandle> InteractionHandle = new();
    public readonly Dictionary<InteractionClassHandle, string> InteractionName = new();
    public readonly Dictionary<(string, string), ParameterHandle> ParameterHandle = new();
    public readonly Dictionary<ParameterHandle, string> ParameterKey = new();

    public Oracle()
    {
        var hub = new FakeRtiHub();
        World.Configure(hub);
        var rti = hub.CreateAmbassador();
        rti.Connect(new Recorder());
        rti.CreateFederationExecution("oracle", World.ValidModules);
        rti.JoinFederationExecution("oracle", "oracle", "oracle");
        foreach (var c in World.Fom.ObjectClasses)
        {
            var handle = rti.GetObjectClassHandle(c.FullName);
            ClassHandle[c.FullName] = handle;
            ClassName[handle] = c.FullName;
            foreach (var a in c.DeclaredAttributes)
            {
                var ah = rti.GetAttributeHandle(handle, a.Name);
                AttributeHandle[(c.FullName, a.Name)] = ah;
                AttributeKey[ah] = RefModel.Key(a);
            }
        }
        foreach (var c in World.Fom.InteractionClasses)
        {
            var handle = rti.GetInteractionClassHandle(c.FullName);
            InteractionHandle[c.FullName] = handle;
            InteractionName[handle] = c.FullName;
            foreach (var p in c.DeclaredParameters)
            {
                var ph = rti.GetParameterHandle(handle, p.Name);
                ParameterHandle[(c.FullName, p.Name)] = ph;
                ParameterKey[ph] = RefModel.Key(p);
            }
        }
    }
}

/// <summary>Totals over many scenarios, to show the generator exercised what it claims to.</summary>
internal sealed class Coverage
{
    public readonly SortedSet<string> Exceptions = new(StringComparer.Ordinal);
    public readonly SortedSet<K> SucceededKinds = new();
    public readonly SortedDictionary<string, int> Outcomes = new(StringComparer.Ordinal);
    public int Scenarios, Operations, Discoveries, Promotions, Reflections, Removals, Interactions, Provides, Reservations, Callbacks;

    public string Summary() =>
        $"scenarios {Scenarios}, operations {Operations}, discoveries {Discoveries} (promoted {Promotions}), reflections {Reflections}, " +
        $"removals {Removals}, interactions {Interactions}, provides {Provides}, reservations {Reservations}, callbacks {Callbacks}; " +
        $"exceptions {Exceptions.Count}: {string.Join(",", Exceptions)}; succeeded kinds {SucceededKinds.Count}/{Enum.GetValues<K>().Length}\n" +
        string.Join("\n", Outcomes.Select(o => o.Key + " " + o.Value));
}

/// <summary>Random operation generator; reads the model only to aim operations at things that exist.</summary>
internal sealed class Generator
{
    private static readonly string[] CoreClasses =
    [
        RprFom2.ObjectClasses.GroundVehicle, RprFom2.ObjectClasses.Platform, RprFom2.ObjectClasses.PhysicalEntity,
        RprFom2.ObjectClasses.BaseEntity, RprFom2.ObjectClasses.Aircraft, RprFom2.ObjectClasses.Human,
        RprFom2.ObjectClasses.Lifeform, RprFom2.ObjectClasses.Munition, RprFom2.ObjectClasses.AggregateEntity, "HLAobjectRoot",
    ];

    private static readonly string[] Interactions =
    [
        "HLAinteractionRoot", RprFom2.InteractionClasses.WeaponFire, RprFom2.InteractionClasses.MunitionDetonation,
        "HLAinteractionRoot.I1", "HLAinteractionRoot.I1.I2", "HLAinteractionRoot.I1.I2.I3",
    ];

    private static readonly string[] GoodNames = ["n1", "n2", "n3", "n4", "n5", "n6"];
    private static readonly string[] BadNames = ["", "HLAreserved", "bad\tname", "caf\u00e9", new string('x', 256)];

    private readonly Random _r;
    private readonly RefModel _m;
    private readonly int _ambs;

    public Generator(Random random, RefModel model, int ambassadors)
    {
        _r = random;
        _m = model;
        _ambs = ambassadors;
    }

    private static readonly (K Kind, int Weight)[] Weights =
    [
        (K.Publish, 8), (K.Unpublish, 1), (K.Subscribe, 11), (K.Unsubscribe, 2),
        (K.PublishI, 4), (K.UnpublishI, 1), (K.SubscribeI, 5), (K.UnsubscribeI, 1),
        (K.Reserve, 3), (K.Register, 12), (K.Update, 22), (K.Delete, 2), (K.Send, 8), (K.Request, 4), (K.RequestClass, 2),
        (K.GetName, 1), (K.GetClass, 1), (K.GetAttribute, 1), (K.GetInteraction, 1), (K.GetParameter, 1),
        (K.EvokeOne, 5), (K.EvokeAll, 8), (K.Resign, 1), (K.Join, 3), (K.Connect, 1), (K.Create, 1), (K.Destroy, 1),
    ];

    /// <summary>Operations mostly come from federates that are in a federation, joins mostly from those that are not.</summary>
    private int PickAmbassador(K kind)
    {
        var wantMember = kind is not (K.Connect or K.Create or K.Destroy or K.Join);
        var wantOutsider = kind == K.Join;
        if (wantMember || wantOutsider)
        {
            var candidates = Enumerable.Range(0, _ambs).Where(a => _m.IsMember(a) == wantMember).ToList();
            if (candidates.Count > 0 && Chance(0.9))
            {
                return Pick(candidates);
            }
        }
        return _r.Next(_ambs);
    }

    public IEnumerable<Op> Preamble()
    {
        // most federates connect at the start; the others stay unconnected until a random Connect (or forever)
        var connected = new List<int>();
        for (var a = 0; a < _ambs; a++)
        {
            if (a == 0 || _r.NextDouble() < 0.9)
            {
                connected.Add(a);
                yield return new Op { Kind = K.Connect, Amb = a };
            }
        }
        yield return new Op { Kind = K.Create, Amb = 0, Federation = "F", Modules = World.ValidModules };
        foreach (var a in connected)
        {
            if (_r.NextDouble() < 0.75)
            {
                yield return new Op { Kind = K.Join, Amb = a, Name = "P" + a, Federation = "F" };
            }
        }
    }

    public Op Next()
    {
        var total = Weights.Sum(w => w.Weight);
        var pick = _r.Next(total);
        var kind = Weights.First(w => (pick -= w.Weight) < 0).Kind;
        var op = new Op { Kind = kind, Amb = PickAmbassador(kind), Tag = Bytes(0, 3) };
        switch (kind)
        {
            case K.Connect:
                break;
            case K.Create:
                op.Federation = Chance(0.7) ? "F" : "G";
                op.Modules = ModuleSet();
                break;
            case K.Destroy:
                op.Federation = Chance(0.7) ? "F" : Chance(0.7) ? "G" : "Nowhere";
                break;
            case K.Join:
                op.Name = Chance(0.8) ? "P" + op.Amb : "P" + _r.Next(_ambs);
                op.Federation = Chance(0.8) ? "F" : Chance(0.7) ? "G" : "Nowhere";
                break;
            case K.Resign:
                op.Action = Pick(Enum.GetValues<ResignAction>());
                break;
            case K.GetClass:
                op.Name = Chance(0.6) ? ClassName() : Pick(new[] { "HLAobjectRoot.Bogus", "PhysicalEntity", "" });
                break;
            case K.GetAttribute:
                op.Cls = ClassOrBogus(0.1);
                op.Name = op.Cls != World.Bogus && Chance(0.8) ? Pick(AttributesOf(op.Cls!)).Name : "Nope";
                break;
            case K.GetInteraction:
                op.Name = Chance(0.7) ? Pick(Interactions) : "HLAinteractionRoot.Bogus";
                break;
            case K.GetParameter:
                op.Icls = InteractionOrBogus(0.1);
                var parameters = op.Icls == World.Bogus
                    ? Array.Empty<FomParameter>()
                    : World.Fom.FindInteractionClass(op.Icls!)!.AllParameters.ToArray();
                op.Name = parameters.Length > 0 && Chance(0.8) ? Pick(parameters).Name : "Nope";
                break;
            case K.Publish:
            case K.Subscribe:
                op.Cls = ClassOrBogus(0.03);
                op.Items = Attributes(op.Cls, 0.5, noise: true, withValues: false);
                break;
            case K.Unpublish:
                op.Cls = Chance(0.7) ? PublishedOr(op.Amb) : ClassOrBogus(0.05);
                break;
            case K.Unsubscribe:
                op.Cls = ClassOrBogus(0.05);
                break;
            case K.PublishI:
            case K.UnpublishI:
            case K.SubscribeI:
            case K.UnsubscribeI:
                op.Icls = InteractionOrBogus(0.04);
                break;
            case K.Reserve:
                op.Name = Chance(0.88) ? Pick(GoodNames) : Pick(BadNames);
                break;
            case K.Register:
                op.Cls = Chance(0.75) ? PublishedOr(op.Amb) : ClassOrBogus(0.04);
                op.Name = Chance(0.35) ? (Chance(0.9) ? Pick(GoodNames) : Pick(BadNames)) : null;
                break;
            case K.Update:
                op.Obj = ObjectFor(op.Amb, ownedBias: 0.65);
                op.Items = UpdateItems(op.Amb, op.Obj);
                break;
            case K.Delete:
                op.Obj = ObjectFor(op.Amb, ownedBias: 0.6);
                break;
            case K.Send:
                op.Icls = Chance(0.7) && _m.PublishedInteractionClasses(op.Amb).Count > 0
                    ? Pick(_m.PublishedInteractionClasses(op.Amb).ToArray())
                    : InteractionOrBogus(0.04);
                op.Items = Parameters(op.Icls);
                break;
            case K.Request:
                op.Obj = ObjectFor(op.Amb, ownedBias: 0.1);
                op.Items = Attributes(_m.KnownClass(op.Amb, op.Obj)?.FullName ?? ClassOrBogus(0.2), 0.3, noise: true, withValues: false);
                break;
            case K.RequestClass:
                op.Cls = ClassOrBogus(0.04);
                op.Items = Attributes(op.Cls, 0.3, noise: true, withValues: false);
                break;
            case K.GetName:
                op.Obj = ObjectFor(op.Amb, ownedBias: 0.3);
                break;
        }
        return op;
    }

    private bool Chance(double p) => _r.NextDouble() < p;

    private T Pick<T>(IReadOnlyList<T> values) => values[_r.Next(values.Count)];

    private byte[] Bytes(int min, int max)
    {
        var bytes = new byte[_r.Next(min, max + 1)];
        _r.NextBytes(bytes);
        return bytes;
    }

    private string[] ModuleSet()
    {
        var p = _r.NextDouble();
        return p switch
        {
            < 0.78 => World.ValidModules,
            < 0.84 => [World.RprModule, World.InteractionModule, World.InteractionModule],
            < 0.90 => [World.RprModule, World.Bogus],
            < 0.95 => [World.RprModule, World.ConflictModule],
            _ => [World.Bogus, World.ConflictModule, World.RprModule],
        };
    }

    private string ClassName() => Chance(0.7) ? Pick(CoreClasses) : Pick(World.Fom.ObjectClasses.Select(c => c.FullName).ToArray());

    private string ClassOrBogus(double bogus) => Chance(bogus) ? World.Bogus : ClassName();

    private string InteractionOrBogus(double bogus) => Chance(bogus) ? World.Bogus : Pick(Interactions);

    private string PublishedOr(int amb)
    {
        var published = _m.PublishedClasses(amb);
        return published.Count > 0 && Chance(0.85) ? Pick(published) : ClassName();
    }

    private static IReadOnlyList<FomAttribute> AttributesOf(string cls) => World.Fom.FindObjectClass(cls)!.AllAttributes;

    private List<(string? Declaring, string Name, byte[] Value)> Attributes(string? cls, double p, bool noise, bool withValues)
    {
        var items = new List<(string?, string, byte[])>();
        if (cls is null || cls == World.Bogus)
        {
            return Chance(0.5) ? items : [(RprFom2.ObjectClasses.BaseEntity, "EntityType", [])];
        }
        foreach (var a in AttributesOf(cls))
        {
            if (Chance(p))
            {
                items.Add((a.DeclaringClass.FullName, a.Name, withValues ? Bytes(0, 3) : []));
            }
        }
        if (items.Count == 0 && Chance(0.85))
        {
            var a = Pick(AttributesOf(cls));
            items.Add((a.DeclaringClass.FullName, a.Name, []));
        }
        if (noise && Chance(0.06))
        {
            var any = Pick(World.Fom.ObjectClasses.SelectMany(c => c.DeclaredAttributes).ToArray());
            items.Add((any.DeclaringClass.FullName, any.Name, []));
        }
        if (noise && Chance(0.02))
        {
            items.Add((null, "bogus", []));
        }
        return items;
    }

    private List<(string? Declaring, string Name, byte[] Value)> UpdateItems(int amb, int handle)
    {
        var owned = _m.OwnedAttributes(amb, handle);
        var items = new List<(string?, string, byte[])>();
        if (owned.Count > 0 && Chance(0.75))
        {
            foreach (var (declaring, name) in owned)
            {
                if (Chance(0.5))
                {
                    items.Add((declaring, name, Bytes(0, 3)));
                }
            }
            if (items.Count == 0)
            {
                var (d, n) = Pick(owned);
                items.Add((d, n, Bytes(0, 3)));
            }
            if (Chance(0.05))
            {
                items.Add((null, "bogus", []));
            }
            return items;
        }
        var cls = _m.KnownClass(amb, handle)?.FullName ?? ClassOrBogus(0.1);
        return Attributes(cls, 0.35, noise: true, withValues: true);
    }

    private List<(string? Declaring, string Name, byte[] Value)> Parameters(string? icls)
    {
        var items = new List<(string?, string, byte[])>();
        if (icls is null || icls == World.Bogus)
        {
            return items;
        }
        foreach (var p in World.Fom.FindInteractionClass(icls)!.AllParameters)
        {
            if (Chance(0.5))
            {
                items.Add((p.DeclaringClass.FullName, p.Name, Bytes(0, 3)));
            }
        }
        if (Chance(0.06))
        {
            var any = Pick(World.Fom.InteractionClasses.SelectMany(c => c.DeclaredParameters).ToArray());
            items.Add((any.DeclaringClass.FullName, any.Name, Bytes(0, 3)));
        }
        return items;
    }

    private int ObjectFor(int amb, double ownedBias)
    {
        var owned = _m.OwnedObjects(amb);
        if (owned.Count > 0 && Chance(ownedBias))
        {
            return Pick(owned);
        }
        var known = _m.KnownObjects(amb);
        if (known.Count > 0 && Chance(0.75))
        {
            return Pick(known);
        }
        var count = _m.ObjectCount(amb);
        return _r.Next(0, count + 3); // includes 0, deleted instances and handles never issued
    }
}

/// <summary>Runs one scenario against the model and the fake RTI and compares them after every operation.</summary>
internal sealed class ScenarioRun
{
    private readonly Oracle _oracle;
    private readonly FakeRtiHub _hub = new();
    private readonly FakeRtiAmbassador[] _ambs;
    private readonly Recorder[] _recorders;
    private readonly int[] _compared;
    private readonly int[] _probes;
    private readonly List<string> _history = new();
    private int _opNumber;

    public ScenarioRun(Oracle oracle, int ambassadors, int seed)
    {
        _oracle = oracle;
        Seed = seed;
        World.Configure(_hub);
        Model = new RefModel(World.Fom, ambassadors, World.ValidModules, World.ConflictModule);
        _ambs = Enumerable.Range(0, ambassadors).Select(_ => _hub.CreateAmbassador()).ToArray();
        _recorders = Enumerable.Range(0, ambassadors).Select(_ => new Recorder()).ToArray();
        _compared = new int[ambassadors];
        _probes = new int[ambassadors];
        for (var i = 0; i < ambassadors; i++)
        {
            var index = i;
            _recorders[i].InsideCallback = () => Probe(index);
        }
    }

    public int Seed { get; }

    public RefModel Model { get; }

    public int Probes => _probes.Sum();

    public IReadOnlyList<string> Trace => _hub.Trace;

    /// <summary>Inside every callback the RTI must refuse every service (CallNotAllowedFromWithinCallback) and stay unchanged.</summary>
    private void Probe(int i)
    {
        var rti = _ambs[i];
        var attempt = _probes[i] % 4;
        try
        {
            switch (attempt)
            {
                case 0: rti.GetObjectClassHandle("HLAobjectRoot"); break;
                case 1: rti.ReserveObjectInstanceName("probe-name"); break;
                case 2: rti.EvokeCallback(0); break;
                default: rti.PublishInteractionClass(new InteractionClassHandle(1)); break;
            }
        }
        catch (CallNotAllowedFromWithinCallback)
        {
            _probes[i]++;
            return;
        }
        throw new Xunit.Sdk.XunitException("a service was allowed from inside a callback");
    }

    public void Execute(Op op)
    {
        _opNumber++;
        _history.Add($"#{_opNumber} {op}");
        var a = op.Amb;
        var rti = _ambs[a];
        string? expected;
        string? actual;
        switch (op.Kind)
        {
            case K.Connect:
                expected = Model.Connect(a);
                actual = Real(() => rti.Connect(_recorders[a]));
                break;
            case K.Create:
                expected = Model.Create(a, op.Federation!, op.Modules);
                actual = Real(() => rti.CreateFederationExecution(op.Federation!, op.Modules));
                break;
            case K.Destroy:
                expected = Model.Destroy(a, op.Federation!);
                actual = Real(() => rti.DestroyFederationExecution(op.Federation!));
                break;
            case K.Join:
            {
                expected = Model.Join(a, op.Name!, op.Federation!, out var handle);
                var returned = 0;
                actual = Real(() => returned = rti.JoinFederationExecution(op.Name!, "scenario", op.Federation!).Value);
                if (expected is null && actual is null)
                {
                    Check(handle == returned, $"join returned federate handle {returned}, model {handle}");
                }
                break;
            }
            case K.Resign:
                expected = Model.Resign(a, op.Action);
                actual = Real(() => rti.ResignFederationExecution(op.Action));
                break;
            case K.GetClass:
                expected = Model.GetClass(a, op.Name!);
                actual = Real(() => rti.GetObjectClassHandle(op.Name!));
                break;
            case K.GetAttribute:
                expected = Model.GetAttribute(a, op.Cls == World.Bogus ? null : op.Cls, op.Name!);
                actual = Real(() => rti.GetAttributeHandle(ClassHandle(op.Cls!), op.Name!));
                break;
            case K.GetInteraction:
                expected = Model.GetInteraction(a, op.Name!);
                actual = Real(() => rti.GetInteractionClassHandle(op.Name!));
                break;
            case K.GetParameter:
                expected = Model.GetParameter(a, op.Icls == World.Bogus ? null : op.Icls, op.Name!);
                actual = Real(() => rti.GetParameterHandle(InteractionHandle(op.Icls!), op.Name!));
                break;
            case K.Publish:
            case K.Subscribe:
            {
                var subscribe = op.Kind == K.Subscribe;
                expected = Model.Declare(a, subscribe, op.Cls == World.Bogus ? null : op.Cls, op.Items.Select(i => (i.Declaring, i.Name)).ToList());
                actual = Real(() =>
                {
                    if (subscribe)
                    {
                        rti.SubscribeObjectClassAttributes(ClassHandle(op.Cls!), AttributeSet(op.Items));
                    }
                    else
                    {
                        rti.PublishObjectClassAttributes(ClassHandle(op.Cls!), AttributeSet(op.Items));
                    }
                });
                break;
            }
            case K.Unpublish:
            case K.Unsubscribe:
            {
                var subscribe = op.Kind == K.Unsubscribe;
                expected = Model.Undeclare(a, subscribe, op.Cls == World.Bogus ? null : op.Cls);
                actual = Real(() =>
                {
                    if (subscribe)
                    {
                        rti.UnsubscribeObjectClass(ClassHandle(op.Cls!));
                    }
                    else
                    {
                        rti.UnpublishObjectClass(ClassHandle(op.Cls!));
                    }
                });
                break;
            }
            case K.PublishI:
            case K.UnpublishI:
            case K.SubscribeI:
            case K.UnsubscribeI:
            {
                var subscribe = op.Kind is K.SubscribeI or K.UnsubscribeI;
                var add = op.Kind is K.PublishI or K.SubscribeI;
                expected = Model.DeclareInteraction(a, subscribe, add, op.Icls == World.Bogus ? null : op.Icls);
                actual = Real(() =>
                {
                    var h = InteractionHandle(op.Icls!);
                    switch (op.Kind)
                    {
                        case K.PublishI: rti.PublishInteractionClass(h); break;
                        case K.UnpublishI: rti.UnpublishInteractionClass(h); break;
                        case K.SubscribeI: rti.SubscribeInteractionClass(h); break;
                        default: rti.UnsubscribeInteractionClass(h); break;
                    }
                });
                break;
            }
            case K.Reserve:
                expected = Model.Reserve(a, op.Name!);
                actual = Real(() => rti.ReserveObjectInstanceName(op.Name!));
                break;
            case K.Register:
            {
                expected = Model.Register(a, op.Cls == World.Bogus ? null : op.Cls, op.Name, out var id);
                var returned = 0;
                actual = Real(() =>
                {
                    var h = op.Name is null
                        ? rti.RegisterObjectInstance(ClassHandle(op.Cls!))
                        : rti.RegisterObjectInstance(ClassHandle(op.Cls!), op.Name);
                    returned = h.Value;
                });
                if (expected is null && actual is null)
                {
                    Check(id == returned, $"register returned instance handle {returned}, model {id}");
                }
                break;
            }
            case K.GetName:
            {
                expected = Model.GetObjectName(a, op.Obj, out var name);
                string? returned = null;
                actual = Real(() => returned = rti.GetObjectInstanceName(new ObjectInstanceHandle(op.Obj)));
                if (expected is null && actual is null)
                {
                    Check(name == returned, $"instance name '{returned}', model '{name}'");
                }
                break;
            }
            case K.Update:
            {
                expected = Model.Update(a, op.Obj, op.Items, op.Tag);
                actual = Real(() =>
                {
                    var map = new AttributeHandleValueMap();
                    foreach (var (declaring, name, value) in op.Items)
                    {
                        map.Set(AttributeHandleOf(declaring, name), value);
                    }
                    rti.UpdateAttributeValues(new ObjectInstanceHandle(op.Obj), map, op.Tag);
                });
                break;
            }
            case K.Delete:
                expected = Model.Delete(a, op.Obj, op.Tag);
                actual = Real(() => rti.DeleteObjectInstance(new ObjectInstanceHandle(op.Obj), op.Tag));
                break;
            case K.Send:
            {
                expected = Model.Send(a, op.Icls == World.Bogus ? null : op.Icls, op.Items, op.Tag);
                actual = Real(() =>
                {
                    var map = new ParameterHandleValueMap();
                    foreach (var (declaring, name, value) in op.Items)
                    {
                        map.Set(_oracle.ParameterHandle[(declaring!, name)], value);
                    }
                    rti.SendInteraction(InteractionHandle(op.Icls!), map, op.Tag);
                });
                break;
            }
            case K.Request:
                expected = Model.Request(a, op.Obj, op.Items.Select(i => (i.Declaring, i.Name)).ToList(), op.Tag);
                actual = Real(() => rti.RequestAttributeValueUpdate(new ObjectInstanceHandle(op.Obj), AttributeSet(op.Items), op.Tag));
                break;
            case K.RequestClass:
                expected = Model.RequestClass(a, op.Cls == World.Bogus ? null : op.Cls, op.Items.Select(i => (i.Declaring, i.Name)).ToList(), op.Tag);
                actual = Real(() => rti.RequestAttributeValueUpdate(ClassHandle(op.Cls!), AttributeSet(op.Items), op.Tag));
                break;
            case K.EvokeOne:
            case K.EvokeAll:
            {
                var all = op.Kind == K.EvokeAll;
                expected = Model.Evoke(a, all, out var more);
                var returned = false;
                actual = Real(() => returned = all ? rti.EvokeMultipleCallbacks(0, 0) : rti.EvokeCallback(0));
                if (expected is null && actual is null)
                {
                    Check(more == returned, $"evoke returned {returned}, model {more}");
                }
                break;
            }
            default:
                throw new InvalidOperationException(op.Kind.ToString());
        }

        Check(expected == actual, $"exception: model expects {expected ?? "none"}, RTI threw {actual ?? "none"}");
        LastThrown = actual;
        Compare();
    }

    private ObjectClassHandle ClassHandle(string cls) => cls == World.Bogus ? new ObjectClassHandle(9999) : _oracle.ClassHandle[cls];

    private InteractionClassHandle InteractionHandle(string cls) =>
        cls == World.Bogus ? new InteractionClassHandle(9999) : _oracle.InteractionHandle[cls];

    private AttributeHandle AttributeHandleOf(string? declaring, string name) =>
        declaring is null ? new AttributeHandle(9999) : _oracle.AttributeHandle[(declaring, name)];

    private AttributeHandleSet AttributeSet(IEnumerable<(string? Declaring, string Name, byte[] Value)> items) =>
        new(items.Select(i => AttributeHandleOf(i.Declaring, i.Name)));

    private string? Real(Action call)
    {
        try
        {
            call();
            return null;
        }
        catch (RtiException e)
        {
            return e.GetType().Name;
        }
    }

    private string? Real<T>(Func<T> call) => Real(() => { call(); });

    private void Check(bool ok, string message)
    {
        if (!ok)
        {
            var recent = string.Join(Environment.NewLine, _history.TakeLast(12));
            throw new Xunit.Sdk.XunitException(
                $"seed {Seed}, operation #{_opNumber} ({_history[^1]}): {message}{Environment.NewLine}last operations:{Environment.NewLine}{recent}");
        }
    }

    /// <summary>After every operation: delivered callbacks, queue sizes and the callback probe agree with the model.</summary>
    private void Compare()
    {
        for (var i = 0; i < _ambs.Length; i++)
        {
            var events = _recorders[i].Events;
            var delivered = Model.Delivered(i);
            Check(events.Count == delivered.Count, $"federate {i}: {events.Count} callbacks delivered, model {delivered.Count}");
            while (_compared[i] < events.Count)
            {
                var real = Canonical(i, events[_compared[i]]);
                Check(real == delivered[_compared[i]], $"federate {i} callback {_compared[i]}: RTI '{real}', model '{delivered[_compared[i]]}'");
                _compared[i]++;
            }
            Check(_ambs[i].PendingCallbackCount == Model.Pending(i),
                $"federate {i}: {_ambs[i].PendingCallbackCount} callbacks pending, model {Model.Pending(i)}");
            Check(_probes[i] == events.Count, $"federate {i}: {_probes[i]} callbacks probed, {events.Count} delivered");
            Check(_ambs[i].FederateHandle?.Value == Model.FederateHandle(i), $"federate {i}: membership differs");
        }
        Check(_hub.FederationNames.SequenceEqual(Model.FederationNames), "set of federations differs");
    }

    private string Canonical(int amb, RtiEvent e)
    {
        var federation = Model.FederationOf(amb) ?? "?";
        string Name(ObjectInstanceHandle h) => Model.ObjectName(federation, h.Value) ?? "?" + h.Value;
        string Pairs<T>(IEnumerable<(T Handle, byte[] Value)> values, Dictionary<T, string> keys) where T : notnull =>
            string.Join(",", values.Select(v => keys[v.Handle] + "=" + RefModel.Hex(v.Value)).Order(StringComparer.Ordinal));
        return e switch
        {
            NameReserved n => "RES+ " + n.Name,
            NameReservationFailed n => "RES- " + n.Name,
            Discovered d => $"DISC {d.Name} as {_oracle.ClassName[d.Class]}",
            Reflected r => $"REFL {Name(r.Object)} {Pairs(r.Values, _oracle.AttributeKey)} tag={RefModel.Hex(r.Tag)}",
            Removed r => $"REM {Name(r.Object)} tag={RefModel.Hex(r.Tag)}",
            Interaction i => $"INT {_oracle.InteractionName[i.Class]} {Pairs(i.Values, _oracle.ParameterKey)} tag={RefModel.Hex(i.Tag)}",
            Provide p => $"PROV {Name(p.Object)} {string.Join(",", p.Attributes.Select(h => _oracle.AttributeKey[h]).Order(StringComparer.Ordinal))} tag={RefModel.Hex(p.Tag)}",
            _ => throw new InvalidOperationException(e.ToString()),
        };
    }

    /// <summary>Runs the whole scenario of a seed; returns what it exercised.</summary>
    public static ScenarioRun Play(Oracle oracle, int seed, Coverage coverage)
    {
        var random = new Random(seed);
        var ambassadors = random.Next(2, 6);
        var operations = random.Next(30, 201);
        var run = new ScenarioRun(oracle, ambassadors, seed);
        var generator = new Generator(random, run.Model, ambassadors);
        foreach (var op in generator.Preamble())
        {
            run.Execute(op);
            run.Note(op, coverage);
        }
        for (var i = 0; i < operations; i++)
        {
            var op = generator.Next();
            run.Execute(op);
            run.Note(op, coverage);
        }
        // drain everything: no callback may remain that the model did not predict
        for (var a = 0; a < ambassadors; a++)
        {
            run.Execute(new Op { Kind = K.EvokeAll, Amb = a });
        }
        coverage.Scenarios++;
        coverage.Operations += operations + ambassadors;
        coverage.Discoveries += run.Model.Discoveries;
        coverage.Promotions += run.Model.Promotions;
        coverage.Reflections += run.Model.Reflections;
        coverage.Removals += run.Model.Removals;
        coverage.Interactions += run.Model.Interactions;
        coverage.Provides += run.Model.Provides;
        coverage.Reservations += run.Model.Reservations;
        coverage.Callbacks += run.Probes;
        return run;
    }

    /// <summary>The exception type the last executed operation threw (model and RTI agreed on it), or null.</summary>
    public string? LastThrown { get; private set; }

    private void Note(Op op, Coverage coverage)
    {
        var outcome = op.Kind + ":" + (LastThrown ?? "ok");
        coverage.Outcomes[outcome] = coverage.Outcomes.GetValueOrDefault(outcome) + 1;
        if (LastThrown is not null)
        {
            coverage.Exceptions.Add(LastThrown);
        }
        else
        {
            coverage.SucceededKinds.Add(op.Kind);
        }
    }
}
