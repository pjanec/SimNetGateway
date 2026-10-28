using System.Text;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Protocol.Rpr.Fom;

/// <summary>An attribute of an object class; the data type is the FOM data type name (informational, the RTI never decodes values).</summary>
public sealed class FomAttribute
{
    internal FomAttribute(string name, string dataType, FomObjectClass declaringClass)
    {
        Name = name;
        DataType = dataType;
        DeclaringClass = declaringClass;
    }

    public string Name { get; }

    public string DataType { get; }

    /// <summary>The class that declares the attribute; subclasses inherit it.</summary>
    public FomObjectClass DeclaringClass { get; }

    public override string ToString() => DeclaringClass.FullName + "." + Name;
}

/// <summary>A node of the object class tree. Fully qualified names are dotted and start with <c>HLAobjectRoot</c>.</summary>
public sealed class FomObjectClass
{
    private readonly List<FomObjectClass> _children = new();
    private readonly List<FomAttribute> _declared = new();
    private IReadOnlyList<FomAttribute>? _all;

    internal FomObjectClass(string name, FomObjectClass? parent)
    {
        Name = name;
        Parent = parent;
        FullName = parent is null ? name : parent.FullName + "." + name;
    }

    public string Name { get; }

    public string FullName { get; }

    public FomObjectClass? Parent { get; }

    public IReadOnlyList<FomObjectClass> Children => _children;

    /// <summary>Attributes this class declares itself, in declaration order.</summary>
    public IReadOnlyList<FomAttribute> DeclaredAttributes => _declared;

    /// <summary>All attributes, inherited ones (root first) before the declared ones.</summary>
    public IReadOnlyList<FomAttribute> AllAttributes
    {
        get
        {
            if (_all is null)
            {
                var list = new List<FomAttribute>();
                if (Parent is not null)
                {
                    list.AddRange(Parent.AllAttributes);
                }
                list.AddRange(_declared);
                _all = list;
            }
            return _all;
        }
    }

    /// <summary>This class followed by its ancestors up to the root.</summary>
    public IEnumerable<FomObjectClass> SelfAndAncestors
    {
        get
        {
            for (var c = this; c is not null; c = c.Parent)
            {
                yield return c;
            }
        }
    }

    /// <summary>The attribute of that simple name, declared here or inherited; null when there is none.</summary>
    public FomAttribute? FindAttribute(string name) => AllAttributes.FirstOrDefault(a => a.Name == name);

    /// <summary>True when this class is <paramref name="other"/> or one of its subclasses.</summary>
    public bool IsSameOrSubclassOf(FomObjectClass other) => SelfAndAncestors.Contains(other);

    internal void AddChild(FomObjectClass child) => _children.Add(child);

    internal FomAttribute AddAttribute(string name, string dataType)
    {
        var attribute = new FomAttribute(name, dataType, this);
        _declared.Add(attribute);
        return attribute;
    }

    public override string ToString() => FullName;
}

/// <summary>A parameter of an interaction class.</summary>
public sealed class FomParameter
{
    internal FomParameter(string name, string dataType, FomInteractionClass declaringClass)
    {
        Name = name;
        DataType = dataType;
        DeclaringClass = declaringClass;
    }

    public string Name { get; }

    public string DataType { get; }

    public FomInteractionClass DeclaringClass { get; }

    public override string ToString() => DeclaringClass.FullName + "." + Name;
}

/// <summary>A node of the interaction class tree. Fully qualified names are dotted and start with <c>HLAinteractionRoot</c>.</summary>
public sealed class FomInteractionClass
{
    private readonly List<FomInteractionClass> _children = new();
    private readonly List<FomParameter> _declared = new();
    private IReadOnlyList<FomParameter>? _all;

    internal FomInteractionClass(string name, FomInteractionClass? parent)
    {
        Name = name;
        Parent = parent;
        FullName = parent is null ? name : parent.FullName + "." + name;
    }

    public string Name { get; }

    public string FullName { get; }

    public FomInteractionClass? Parent { get; }

    public IReadOnlyList<FomInteractionClass> Children => _children;

    public IReadOnlyList<FomParameter> DeclaredParameters => _declared;

    /// <summary>All parameters, inherited ones (root first) before the declared ones.</summary>
    public IReadOnlyList<FomParameter> AllParameters
    {
        get
        {
            if (_all is null)
            {
                var list = new List<FomParameter>();
                if (Parent is not null)
                {
                    list.AddRange(Parent.AllParameters);
                }
                list.AddRange(_declared);
                _all = list;
            }
            return _all;
        }
    }

    public IEnumerable<FomInteractionClass> SelfAndAncestors
    {
        get
        {
            for (var c = this; c is not null; c = c.Parent)
            {
                yield return c;
            }
        }
    }

    public FomParameter? FindParameter(string name) => AllParameters.FirstOrDefault(p => p.Name == name);

    public bool IsSameOrSubclassOf(FomInteractionClass other) => SelfAndAncestors.Contains(other);

    internal void AddChild(FomInteractionClass child) => _children.Add(child);

    internal FomParameter AddParameter(string name, string dataType)
    {
        var parameter = new FomParameter(name, dataType, this);
        _declared.Add(parameter);
        return parameter;
    }

    public override string ToString() => FullName;
}

/// <summary>
/// A FOM in code (R-054, DD-008 section 3.2): the object class tree and the interaction class tree with fully qualified
/// dotted names, <c>HLAobjectRoot</c> carrying <c>HLAprivilegeToDeleteObject</c>. Immutable; built by
/// <see cref="FomModelBuilder"/> or by <see cref="Merge"/>. Class order is the order of first declaration (parents before
/// children), which is also the order the RTI hands out handles in.
/// </summary>
public sealed class FomModel : IEquatable<FomModel>
{
    public const string ObjectRootName = "HLAobjectRoot";

    public const string InteractionRootName = "HLAinteractionRoot";

    /// <summary>Attribute of the object root every instance owner holds; the right to delete the instance.</summary>
    public const string PrivilegeToDeleteObjectName = "HLAprivilegeToDeleteObject";

    private readonly Dictionary<string, FomObjectClass> _objectsByName = new();
    private readonly Dictionary<string, FomInteractionClass> _interactionsByName = new();

    internal FomModel(FomObjectClass objectRoot, FomInteractionClass interactionRoot)
    {
        ObjectRoot = objectRoot;
        InteractionRoot = interactionRoot;
        ObjectClasses = Flatten(objectRoot, c => c.Children);
        InteractionClasses = Flatten(interactionRoot, c => c.Children);
        foreach (var c in ObjectClasses)
        {
            _objectsByName[c.FullName] = c;
        }
        foreach (var c in InteractionClasses)
        {
            _interactionsByName[c.FullName] = c;
        }
    }

    public FomObjectClass ObjectRoot { get; }

    public FomInteractionClass InteractionRoot { get; }

    /// <summary>All object classes, parents before children, siblings in declaration order (pre-order).</summary>
    public IReadOnlyList<FomObjectClass> ObjectClasses { get; }

    /// <summary>All interaction classes in the same order.</summary>
    public IReadOnlyList<FomInteractionClass> InteractionClasses { get; }

    public FomObjectClass? FindObjectClass(string fullName) =>
        _objectsByName.TryGetValue(fullName, out var c) ? c : null;

    public FomInteractionClass? FindInteractionClass(string fullName) =>
        _interactionsByName.TryGetValue(fullName, out var c) ? c : null;

    /// <summary>
    /// The union of this model and <paramref name="other"/>. A class or member defined in both must agree (same data type),
    /// otherwise <see cref="InconsistentFdd"/>. Merging a model with itself gives an equal model.
    /// </summary>
    public FomModel Merge(FomModel other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var builder = new FomModelBuilder();
        builder.Add(this);
        builder.Add(other);
        return builder.Build();
    }

    /// <summary>A canonical text of the whole model (declaration order); two models are equal when it is.</summary>
    public string ToCanonicalString()
    {
        var sb = new StringBuilder();
        foreach (var c in ObjectClasses)
        {
            sb.Append("O ").Append(c.FullName);
            foreach (var a in c.DeclaredAttributes)
            {
                sb.Append(' ').Append(a.Name).Append(':').Append(a.DataType);
            }
            sb.Append('\n');
        }
        foreach (var c in InteractionClasses)
        {
            sb.Append("I ").Append(c.FullName);
            foreach (var p in c.DeclaredParameters)
            {
                sb.Append(' ').Append(p.Name).Append(':').Append(p.DataType);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public bool Equals(FomModel? other) => other is not null && ToCanonicalString() == other.ToCanonicalString();

    public override bool Equals(object? obj) => obj is FomModel other && Equals(other);

    public override int GetHashCode() => ToCanonicalString().GetHashCode();

    private static IReadOnlyList<T> Flatten<T>(T root, Func<T, IReadOnlyList<T>> children)
    {
        var list = new List<T>();
        void Visit(T node)
        {
            list.Add(node);
            foreach (var child in children(node))
            {
                Visit(child);
            }
        }
        Visit(root);
        return list;
    }
}

/// <summary>
/// Builds a <see cref="FomModel"/>. Declaring a class creates its missing ancestors; declaring a class again adds members to
/// it (a FOM module extends the classes of earlier modules); a member declared twice must have the same data type, else
/// <see cref="InconsistentFdd"/>.
/// </summary>
public sealed class FomModelBuilder
{
    private sealed class ObjectNode
    {
        public ObjectNode(string name) => Name = name;

        public string Name { get; }

        public List<ObjectNode> Children { get; } = new();

        public List<(string Name, string DataType)> Members { get; } = new();
    }

    private readonly ObjectNode _objectRoot = new(FomModel.ObjectRootName);
    private readonly ObjectNode _interactionRoot = new(FomModel.InteractionRootName);

    public FomModelBuilder()
    {
        _objectRoot.Members.Add((FomModel.PrivilegeToDeleteObjectName, "HLAtoken"));
    }

    /// <summary>Declares an object class by fully qualified name with attributes (name, data type name).</summary>
    public FomModelBuilder ObjectClass(string fullName, params (string Name, string DataType)[] attributes)
    {
        var node = Locate(_objectRoot, fullName);
        foreach (var attribute in attributes)
        {
            AddMember(node, fullName, attribute.Name, attribute.DataType);
        }
        return this;
    }

    /// <summary>Declares an interaction class by fully qualified name with parameters (name, data type name).</summary>
    public FomModelBuilder InteractionClass(string fullName, params (string Name, string DataType)[] parameters)
    {
        var node = Locate(_interactionRoot, fullName);
        foreach (var parameter in parameters)
        {
            AddMember(node, fullName, parameter.Name, parameter.DataType);
        }
        return this;
    }

    /// <summary>Adds everything an existing model declares.</summary>
    public FomModelBuilder Add(FomModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        foreach (var c in model.ObjectClasses)
        {
            ObjectClass(c.FullName, c.DeclaredAttributes.Select(a => (a.Name, a.DataType)).ToArray());
        }
        foreach (var c in model.InteractionClasses)
        {
            InteractionClass(c.FullName, c.DeclaredParameters.Select(p => (p.Name, p.DataType)).ToArray());
        }
        return this;
    }

    public FomModel Build()
    {
        var objectRoot = BuildObjects(_objectRoot, null);
        var interactionRoot = BuildInteractions(_interactionRoot, null);
        return new FomModel(objectRoot, interactionRoot);
    }

    private static ObjectNode Locate(ObjectNode root, string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        var parts = fullName.Split('.');
        if (parts[0] != root.Name || parts.Any(p => p.Length == 0))
        {
            throw new ArgumentException($"'{fullName}' is not a dotted name starting with {root.Name}", nameof(fullName));
        }
        var node = root;
        for (var i = 1; i < parts.Length; i++)
        {
            var child = node.Children.FirstOrDefault(c => c.Name == parts[i]);
            if (child is null)
            {
                child = new ObjectNode(parts[i]);
                node.Children.Add(child);
            }
            node = child;
        }
        return node;
    }

    private static void AddMember(ObjectNode node, string className, string name, string dataType)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var existing = node.Members.FindIndex(m => m.Name == name);
        if (existing < 0)
        {
            node.Members.Add((name, dataType));
        }
        else if (node.Members[existing].DataType != dataType)
        {
            throw new InconsistentFdd(
                $"{className}.{name} is declared as {node.Members[existing].DataType} and as {dataType}");
        }
    }

    private static FomObjectClass BuildObjects(ObjectNode node, FomObjectClass? parent)
    {
        var built = new FomObjectClass(node.Name, parent);
        foreach (var member in node.Members)
        {
            built.AddAttribute(member.Name, member.DataType);
        }
        foreach (var child in node.Children)
        {
            built.AddChild(BuildObjects(child, built));
        }
        return built;
    }

    private static FomInteractionClass BuildInteractions(ObjectNode node, FomInteractionClass? parent)
    {
        var built = new FomInteractionClass(node.Name, parent);
        foreach (var member in node.Members)
        {
            built.AddParameter(member.Name, member.DataType);
        }
        foreach (var child in node.Children)
        {
            built.AddChild(BuildInteractions(child, built));
        }
        return built;
    }
}
