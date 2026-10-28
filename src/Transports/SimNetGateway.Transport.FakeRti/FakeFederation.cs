using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.FakeRti;

/// <summary>
/// Deterministic handle numbering of a FOM: 1-based indexes in model order (object classes parents first, attributes by
/// declaring class in declaration order, interaction classes and parameters likewise). The same model always gives the
/// same handles, in every federation.
/// </summary>
internal sealed class HandleTables
{
    private readonly Dictionary<FomObjectClass, int> _classIndex = new();
    private readonly Dictionary<FomInteractionClass, int> _interactionIndex = new();
    private readonly Dictionary<FomAttribute, int> _attributeIndex = new();
    private readonly Dictionary<FomParameter, int> _parameterIndex = new();
    private readonly List<FomAttribute> _attributes = new();
    private readonly List<FomParameter> _parameters = new();

    public HandleTables(FomModel model)
    {
        Model = model;
        foreach (var c in model.ObjectClasses)
        {
            _classIndex[c] = _classIndex.Count + 1;
            foreach (var a in c.DeclaredAttributes)
            {
                _attributes.Add(a);
                _attributeIndex[a] = _attributes.Count;
            }
        }
        foreach (var c in model.InteractionClasses)
        {
            _interactionIndex[c] = _interactionIndex.Count + 1;
            foreach (var p in c.DeclaredParameters)
            {
                _parameters.Add(p);
                _parameterIndex[p] = _parameters.Count;
            }
        }
    }

    public FomModel Model { get; }

    public FomObjectClass? Class(ObjectClassHandle h) =>
        h.Value >= 1 && h.Value <= Model.ObjectClasses.Count ? Model.ObjectClasses[h.Value - 1] : null;

    public FomInteractionClass? Interaction(InteractionClassHandle h) =>
        h.Value >= 1 && h.Value <= Model.InteractionClasses.Count ? Model.InteractionClasses[h.Value - 1] : null;

    public FomAttribute? Attribute(AttributeHandle h) =>
        h.Value >= 1 && h.Value <= _attributes.Count ? _attributes[h.Value - 1] : null;

    public FomParameter? Parameter(ParameterHandle h) =>
        h.Value >= 1 && h.Value <= _parameters.Count ? _parameters[h.Value - 1] : null;

    public ObjectClassHandle Handle(FomObjectClass c) => new(_classIndex[c]);

    public InteractionClassHandle Handle(FomInteractionClass c) => new(_interactionIndex[c]);

    public AttributeHandle Handle(FomAttribute a) => new(_attributeIndex[a]);

    public ParameterHandle Handle(FomParameter p) => new(_parameterIndex[p]);
}

/// <summary>One joined federate of a federation: its declarations. Callback queue and connection state live on its ambassador.</summary>
internal sealed class FakeFederate
{
    public FakeFederate(int handle, string name, string type, FakeRtiAmbassador ambassador)
    {
        Handle = handle;
        Name = name;
        Type = type;
        Ambassador = ambassador;
    }

    public int Handle { get; }

    public string Name { get; }

    public string Type { get; }

    public FakeRtiAmbassador Ambassador { get; }

    /// <summary>Published attributes per object class (replace semantics).</summary>
    public Dictionary<FomObjectClass, HashSet<FomAttribute>> Published { get; } = new();

    /// <summary>Subscribed attributes per object class (replace semantics).</summary>
    public Dictionary<FomObjectClass, HashSet<FomAttribute>> Subscribed { get; } = new();

    public HashSet<FomInteractionClass> PublishedInteractions { get; } = new();

    public HashSet<FomInteractionClass> SubscribedInteractions { get; } = new();
}

/// <summary>An object instance of a federation. Deleted instances stay in the list (handles are never reused).</summary>
internal sealed class FakeObject
{
    public FakeObject(int handle, string name, FomObjectClass registeredClass, FakeFederate registrant, HashSet<FomAttribute> owned)
    {
        Handle = handle;
        Name = name;
        Class = registeredClass;
        Registrant = registrant;
        Owner = registrant;
        Owned = owned;
    }

    public int Handle { get; }

    public string Name { get; }

    public FomObjectClass Class { get; }

    /// <summary>The federate that registered it; null after it divested. Never discovers the instance.</summary>
    public FakeFederate? Registrant { get; set; }

    /// <summary>The federate that owns the <see cref="Owned"/> attributes and the delete privilege; null after a divest.</summary>
    public FakeFederate? Owner { get; set; }

    public HashSet<FomAttribute> Owned { get; }

    public bool Alive { get; set; } = true;

    /// <summary>Federates that discovered the instance, by federate handle, with the class they know it as.</summary>
    public SortedDictionary<int, (FakeFederate Federate, FomObjectClass KnownClass)> Known { get; } = new();
}

/// <summary>An entry of the object instance name table: reserved by a federate, or bound to an instance.</summary>
internal sealed class NameEntry
{
    public FakeFederate? ReservedBy { get; set; }

    public FakeObject? Object { get; set; }
}

/// <summary>A federation execution: its merged FOM, handle tables, joined federates, instances and names.</summary>
internal sealed class FakeFederation
{
    public FakeFederation(string name, FomModel model)
    {
        Name = name;
        Tables = new HandleTables(model);
        PrivilegeToDelete = model.ObjectRoot.FindAttribute(FomModel.PrivilegeToDeleteObjectName)!;
    }

    public string Name { get; }

    public HandleTables Tables { get; }

    public FomModel Model => Tables.Model;

    /// <summary>The HLAprivilegeToDeleteObject attribute of the object root.</summary>
    public FomAttribute PrivilegeToDelete { get; }

    /// <summary>Joined federates by federate handle (join order).</summary>
    public SortedDictionary<int, FakeFederate> Federates { get; } = new();

    public int NextFederateHandle { get; set; } = 1;

    /// <summary>All instances ever registered; the index is the handle minus one.</summary>
    public List<FakeObject> Objects { get; } = new();

    public Dictionary<string, NameEntry> Names { get; } = new(StringComparer.Ordinal);

    public FakeObject? FindObject(ObjectInstanceHandle h) =>
        h.Value >= 1 && h.Value <= Objects.Count ? Objects[h.Value - 1] : null;
}
