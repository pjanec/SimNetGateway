namespace SimNetGateway.Engine;

/// <summary>A declared inbound port: a named source plus the channel the host binds to it.</summary>
public sealed class InboundPort
{
    internal InboundPort(string name, SourceId source)
    {
        Name = name;
        Source = source;
    }

    public string Name { get; }

    /// <summary>The source registered under the port name; the bound channel must report the same source.</summary>
    public SourceId Source { get; }

    /// <summary>The channel bound by the host; null until <see cref="GatewayBuilder.BindInbound"/>.</summary>
    public IInboundChannel? Channel { get; internal set; }
}

/// <summary>Non-generic view of an outbound port.</summary>
public interface IOutboundPortInfo
{
    string Name { get; }

    Type MessageType { get; }

    bool IsBound { get; }
}

/// <summary>
/// A forwarding outbound channel: modules are constructed with the port before the host binds the real channel.
/// Sending before binding throws <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class OutboundPort<T> : IOutboundChannel<T>, IOutboundPortInfo
{
    private IOutboundChannel<T>? _target;

    internal OutboundPort(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public Type MessageType => typeof(T);

    public bool IsBound => _target is not null;

    internal void Bind(IOutboundChannel<T> channel) => _target = channel;

    public void Send(in T message)
    {
        var target = _target ?? throw new InvalidOperationException($"Outbound port '{Name}' ({typeof(T).Name}) is not bound.");
        target.Send(in message);
    }
}
