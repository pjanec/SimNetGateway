namespace SimNetGateway.Engine;

/// <summary>Thrown by <see cref="GatewayBuilder.Build"/>; the message lists every problem found.</summary>
public sealed class GatewayBuildException : InvalidOperationException
{
    public GatewayBuildException(IReadOnlyList<string> problems)
        : base("The gateway could not be built (" + problems.Count + " problem(s)):\n  - " + string.Join("\n  - ", problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// Composition of one gateway (DD-003 4): a thin layer over the public API of <see cref="GatewayEngine"/>, which stays
/// unaware of it. The profile registers schema, sources, modules and required ports; the host binds channels to the
/// ports and calls <see cref="Build"/>. No file I/O, no reflection (R-054). Single-threaded, used once.
/// </summary>
public sealed class GatewayBuilder
{
    private readonly List<InboundPort> _inbound = new();
    private readonly Dictionary<string, InboundPort> _inboundByName = new(StringComparer.Ordinal);
    private readonly List<IOutboundPortInfo> _outbound = new();
    private readonly Dictionary<string, IOutboundPortInfo> _outboundByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IInboundChannel> _inboundBindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutboundBinding> _outboundBindings = new(StringComparer.Ordinal);
    private bool _built;

    private readonly record struct OutboundBinding(Type MessageType, Action Apply);

    public GatewayBuilder(IClock clock, GatewaySettings settings, GatewayOptions? engineOptions = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        Clock = clock;
        Settings = settings;
        EngineOptions = engineOptions ?? new GatewayOptions();
        Engine = new GatewayEngine(clock, EngineOptions);
    }

    public IClock Clock { get; }

    public GatewayOptions EngineOptions { get; }

    public GatewaySettings Settings { get; }

    /// <summary>The engine being configured; for registration only, before <see cref="Build"/>.</summary>
    public GatewayEngine Engine { get; }

    public AttributeRegistry Attributes => Engine.Attributes;

    public EventTypeRegistry EventTypes => Engine.EventTypes;

    public SourceRegistry Sources => Engine.Sources;

    public IdentityRegistry Identities => Engine.Identities;

    /// <summary>The profile that configured this builder (set by <see cref="GatewayConfiguration.CreateBuilder"/>).</summary>
    public IGatewayProfile? Profile { get; internal set; }

    /// <summary>The settings as the profile's own settings type; throws when the host supplied another type.</summary>
    public TSettings SettingsAs<TSettings>()
        where TSettings : GatewaySettings
    {
        EnsureOpen();
        return Settings as TSettings
            ?? throw new InvalidOperationException(
                $"The gateway settings are of type {Settings.GetType().Name}, but {typeof(TSettings).Name} was expected.");
    }

    /// <summary>Declares (idempotently) an inbound port; registers a source with the port name.</summary>
    public InboundPort RequireInbound(string name)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_inboundByName.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var port = new InboundPort(name, Sources.Register(name));
        _inbound.Add(port);
        _inboundByName.Add(name, port);
        return port;
    }

    /// <summary>Declares (idempotently) an outbound port for messages of type <typeparamref name="T"/>.</summary>
    public OutboundPort<T> RequireOutbound<T>(string name)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_outboundByName.TryGetValue(name, out var existing))
        {
            return existing as OutboundPort<T>
                ?? throw new InvalidOperationException(
                    $"Outbound port '{name}' was already required for {existing.MessageType.Name}, not {typeof(T).Name}.");
        }

        var port = new OutboundPort<T>(name);
        _outbound.Add(port);
        _outboundByName.Add(name, port);
        return port;
    }

    public void AddModule(IGatewayModule module)
    {
        EnsureOpen();
        Engine.AddModule(module);
    }

    /// <summary>Binds the host's inbound channel to a declared port (checked by <see cref="Build"/>).</summary>
    public void BindInbound(string name, IInboundChannel channel)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(channel);
        if (!_inboundBindings.TryAdd(name, channel))
        {
            throw new InvalidOperationException($"Inbound port '{name}' is already bound.");
        }
    }

    /// <summary>Binds the host's outbound channel to a declared port (checked by <see cref="Build"/>).</summary>
    public void BindOutbound<T>(string name, IOutboundChannel<T> channel)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(channel);
        var binding = new OutboundBinding(typeof(T), () =>
        {
            ((OutboundPort<T>)_outboundByName[name]).Bind(channel);
        });
        if (!_outboundBindings.TryAdd(name, binding))
        {
            throw new InvalidOperationException($"Outbound port '{name}' is already bound.");
        }
    }

    /// <summary>
    /// Checks the whole composition, collecting every problem into one <see cref="GatewayBuildException"/>; on success adds
    /// the inbound channels to the engine, applies the operator rule overrides and returns the binding. Callable once.
    /// </summary>
    public GatewayHostBinding Build()
    {
        EnsureOpen();
        _built = true;
        var problems = new List<string>();

        foreach (var port in _inbound)
        {
            if (!_inboundBindings.TryGetValue(port.Name, out var channel))
            {
                problems.Add($"Inbound port '{port.Name}' is declared but not bound.");
            }
            else if (channel.Source != port.Source)
            {
                problems.Add($"Inbound port '{port.Name}': the bound channel '{channel.Name}' has source " +
                    $"'{SourceName(channel.Source)}', expected '{port.Name}'.");
            }
        }

        foreach (var name in _inboundBindings.Keys)
        {
            if (!_inboundByName.ContainsKey(name))
            {
                problems.Add($"Inbound channel bound to '{name}', but no inbound port of that name is declared.");
            }
        }

        foreach (var port in _outbound)
        {
            if (!_outboundBindings.TryGetValue(port.Name, out var binding))
            {
                problems.Add($"Outbound port '{port.Name}' ({port.MessageType.Name}) is declared but not bound.");
            }
            else if (binding.MessageType != port.MessageType)
            {
                problems.Add($"Outbound port '{port.Name}': expected a channel for {port.MessageType.Name}, " +
                    $"but one for {binding.MessageType.Name} was bound.");
            }
        }

        foreach (var name in _outboundBindings.Keys)
        {
            if (!_outboundByName.ContainsKey(name))
            {
                problems.Add($"Outbound channel bound to '{name}', but no outbound port of that name is declared.");
            }
        }

        CheckRuleNames(Settings.DisabledRules, nameof(GatewaySettings.DisabledRules), problems);
        CheckRuleNames(Settings.EnabledRules, nameof(GatewaySettings.EnabledRules), problems);

        var validation = Engine.Validate();
        foreach (var error in validation.Errors)
        {
            problems.Add("Validation: " + error);
        }

        if (problems.Count > 0)
        {
            throw new GatewayBuildException(problems);
        }

        foreach (var port in _inbound)
        {
            var channel = _inboundBindings[port.Name];
            port.Channel = channel;
            Engine.AddInbound(channel);
        }

        foreach (var port in _outbound)
        {
            _outboundBindings[port.Name].Apply();
        }

        foreach (var name in Settings.DisabledRules)
        {
            Engine.FindRule(name)!.Enabled = false;
        }

        foreach (var name in Settings.EnabledRules)
        {
            Engine.FindRule(name)!.Enabled = true;
        }

        return new GatewayHostBinding(Engine, validation, _inbound, _outbound, Settings, Profile);
    }

    private void CheckRuleNames(IReadOnlyList<string> names, string setting, List<string> problems)
    {
        foreach (var name in names)
        {
            if (Engine.FindRule(name) is null)
            {
                problems.Add($"Settings.{setting} names the rule '{name}', which does not exist.");
            }
        }
    }

    private string SourceName(SourceId id)
    {
        try
        {
            return Sources.NameOf(id);
        }
        catch (KeyNotFoundException)
        {
            return "#" + id.Value;
        }
    }

    private void EnsureOpen()
    {
        if (_built)
        {
            throw new InvalidOperationException("The gateway has already been built.");
        }
    }
}

/// <summary>What <see cref="GatewayBuilder.Build"/> hands the host: the engine, the validation report and the ports.</summary>
public sealed class GatewayHostBinding
{
    private readonly Dictionary<string, InboundPort> _inboundByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IOutboundPortInfo> _outboundByName = new(StringComparer.Ordinal);

    internal GatewayHostBinding(GatewayEngine engine, ValidationReport validation, IReadOnlyList<InboundPort> inbound,
        IReadOnlyList<IOutboundPortInfo> outbound, GatewaySettings settings, IGatewayProfile? profile)
    {
        Engine = engine;
        Validation = validation;
        InboundPorts = inbound.ToArray();
        OutboundPorts = outbound.ToArray();
        Settings = settings;
        Profile = profile;
        foreach (var port in InboundPorts)
        {
            _inboundByName.Add(port.Name, port);
        }

        foreach (var port in OutboundPorts)
        {
            _outboundByName.Add(port.Name, port);
        }
    }

    public GatewayEngine Engine { get; }

    /// <summary>The vocabulary validation at build time (no errors, or the build would have failed; warnings remain).</summary>
    public ValidationReport Validation { get; }

    public IReadOnlyList<InboundPort> InboundPorts { get; }

    public IReadOnlyList<IOutboundPortInfo> OutboundPorts { get; }

    public GatewaySettings Settings { get; }

    public IGatewayProfile? Profile { get; }

    public InboundPort Inbound(string name) =>
        _inboundByName.TryGetValue(name, out var port) ? port : throw new KeyNotFoundException($"No inbound port '{name}'.");

    public OutboundPort<T> Outbound<T>(string name)
    {
        if (!_outboundByName.TryGetValue(name, out var port))
        {
            throw new KeyNotFoundException($"No outbound port '{name}'.");
        }

        return port as OutboundPort<T>
            ?? throw new InvalidOperationException(
                $"Outbound port '{name}' carries {port.MessageType.Name}, not {typeof(T).Name}.");
    }
}
