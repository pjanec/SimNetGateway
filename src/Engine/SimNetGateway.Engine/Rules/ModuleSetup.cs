namespace SimNetGateway.Engine;

/// <summary>What a module declares through <see cref="ModuleProduction"/> (kept for the vocabulary validation of stage 3).</summary>
public sealed record ModuleProduction(string Module, IReadOnlyList<AttrId> Attributes, EventTypeId EventType);

/// <summary>The registration surface handed to <see cref="IGatewayModule.Register"/> (one per AddModule call).</summary>
public sealed class ModuleSetup
{
    private readonly GatewayEngine _engine;
    private readonly string _module;
    private readonly double _startTime;
    private int _ruleCounter;

    internal ModuleSetup(GatewayEngine engine, string module, double startTime)
    {
        _engine = engine;
        _module = module;
        _startTime = startTime;
    }

    public IGatewayContext Gateway => _engine;
    public AttributeRegistry Attributes => _engine.Attributes;
    public EventTypeRegistry EventTypes => _engine.EventTypes;
    public SourceRegistry Sources => _engine.Sources;

    /// <summary>Declares the attributes this module writes (recorded for vocabulary validation).</summary>
    public void Produces(params AttrId[] attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        _engine.ProductionList.Add(new ModuleProduction(_module, (AttrId[])attributes.Clone(), default));
    }

    /// <summary>Declares the event type this module publishes (recorded for vocabulary validation).</summary>
    public void Produces(EventTypeId type) =>
        _engine.ProductionList.Add(new ModuleProduction(_module, Array.Empty<AttrId>(), type));

    public void OnMessage<T>(IngressHandler<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _engine.Router.Add(_module, handler);
    }

    public RuleBuilder<EntityTrigger> OnEntityCreated() =>
        new(this, new RuleSubscription(RuleKind.EntityCreated, "OnEntityCreated", "OnEntityCreated()"));

    public RuleBuilder<EntityTrigger> OnEntityRemoved() =>
        new(this, new RuleSubscription(RuleKind.EntityRemoved, "OnEntityRemoved", "OnEntityRemoved()"));

    /// <summary>Fires when any listed attribute changed its value (empty list = any attribute).</summary>
    public RuleBuilder<EntityTrigger> OnAttributesChanged(params AttrId[] attributes) =>
        new(this, new RuleSubscription(RuleKind.AttributesChanged, "OnAttributesChanged", Describe("OnAttributesChanged", attributes))
        {
            Attributes = (AttrId[])attributes.Clone(),
        });

    /// <summary>Fires when any listed attribute was written, even with the same value (empty list = any).</summary>
    public RuleBuilder<EntityTrigger> OnAttributesWritten(params AttrId[] attributes) =>
        new(this, new RuleSubscription(RuleKind.AttributesWritten, "OnAttributesWritten", Describe("OnAttributesWritten", attributes))
        {
            Attributes = (AttrId[])attributes.Clone(),
        });

    public RuleBuilder<EventTrigger> OnEvent(EventTypeId type) =>
        new(this, new RuleSubscription(RuleKind.Event, "OnEvent", "OnEvent(" + EventName(type) + ")") { EventType = type });

    public RuleBuilder<EventTrigger> OnAnyEvent() =>
        new(this, new RuleSubscription(RuleKind.AnyEvent, "OnAnyEvent", "OnAnyEvent()"));

    public RuleBuilder<SessionTrigger> OnSessionChanged() =>
        new(this, new RuleSubscription(RuleKind.SessionChanged, "OnSessionChanged", "OnSessionChanged()"));

    /// <summary>A session command is about to be applied; the rule may veto it (DD-004 4.2).</summary>
    public RuleBuilder<SessionRequestTrigger> OnSessionRequest() =>
        new(this, new RuleSubscription(RuleKind.SessionRequest, "OnSessionRequest", "OnSessionRequest()"));

    public RuleBuilder<ChangeSetTrigger> OnChangeSetEnd() =>
        new(this, new RuleSubscription(RuleKind.ChangeSetEnd, "OnChangeSetEnd", "OnChangeSetEnd()"));

    public RuleBuilder<TimerTrigger> Every(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "The period must be positive.");
        }

        return new(this, new RuleSubscription(RuleKind.Every, "Every", "Every(" + period + ")") { Period = period });
    }

    public RuleBuilder<TimerTrigger> OnTickEnd() =>
        new(this, new RuleSubscription(RuleKind.TickEnd, "OnTickEnd", "OnTickEnd()"));

    internal RuleInfo AddRule<TTrigger>(RuleSubscription subscription, string? name, Func<TTrigger, bool>[] conditions,
        Action<TTrigger> action)
        where TTrigger : class
    {
        _ruleCounter++;
        name ??= subscription.KindName + "#" + _ruleCounter;
        return _engine.RegisterRule(_module, subscription, name, conditions, action, _startTime);
    }

    private string Describe(string kind, AttrId[] attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var names = new string[attributes.Length];
        for (int i = 0; i < attributes.Length; i++)
        {
            names[i] = Attributes.TryGet(attributes[i], out var def) ? def.Name : "#" + attributes[i].Value;
        }

        return kind + "(" + string.Join(", ", names) + ")";
    }

    private string EventName(EventTypeId type)
    {
        try
        {
            return EventTypes.NameOf(type);
        }
        catch (KeyNotFoundException)
        {
            return "#" + type.Value;
        }
    }
}
