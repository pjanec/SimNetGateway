namespace SimNetGateway.Engine;

internal enum RuleKind
{
    EntityCreated,
    EntityRemoved,
    AttributesChanged,
    AttributesWritten,
    Event,
    AnyEvent,
    SessionChanged,
    SessionRequest,
    ChangeSetEnd,
    Every,
    TickEnd,
}

/// <summary>What a rule subscribes to (built by <see cref="ModuleSetup"/>).</summary>
internal sealed class RuleSubscription
{
    public RuleSubscription(RuleKind kind, string kindName, string description)
    {
        Kind = kind;
        KindName = kindName;
        Description = description;
    }

    public RuleKind Kind { get; }
    public string KindName { get; }
    public string Description { get; }
    public AttrId[] Attributes { get; init; } = Array.Empty<AttrId>();
    public EventTypeId EventType { get; init; }
    public TimeSpan Period { get; init; }
}

/// <summary>Trigger + conditions + action; the rule is registered by <see cref="Do"/> (DD-001 4.3).</summary>
public sealed class RuleBuilder<TTrigger> where TTrigger : class
{
    private readonly ModuleSetup _setup;
    private readonly RuleSubscription _subscription;
    private readonly List<Func<TTrigger, bool>> _conditions = new();
    private string? _name;

    internal RuleBuilder(ModuleSetup setup, RuleSubscription subscription)
    {
        _setup = setup;
        _subscription = subscription;
    }

    public RuleBuilder<TTrigger> Named(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _name = name;
        return this;
    }

    /// <summary>Adds a condition; several are ANDed and evaluated in order.</summary>
    public RuleBuilder<TTrigger> When(Func<TTrigger, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        _conditions.Add(condition);
        return this;
    }

    /// <summary>Registers the rule and returns its description and counters.</summary>
    public RuleInfo Do(Action<TTrigger> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _setup.AddRule(_subscription, _name, _conditions.ToArray(), action);
    }
}
