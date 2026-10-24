namespace SimNetGateway.Engine;

/// <summary>Where an inbound channel pushes the messages it received (the engine's ingress entry).</summary>
public interface IIngressSink
{
    /// <summary>Delivers one message for synchronous, complete processing (no boxing of struct messages).</summary>
    void Deliver<T>(in T message, double? receivedAt = null);
}

/// <summary>A source of messages polled by <see cref="GatewayEngine.Tick"/>. Network threads only enqueue into it.</summary>
public interface IInboundChannel
{
    SourceId Source { get; }

    string Name { get; }

    /// <summary>Delivers at most <paramref name="budget"/> messages into the sink; returns how many.</summary>
    int Drain(IIngressSink sink, int budget);
}

/// <summary>Where egress modules send outgoing messages (invariant: a by-reference "in" parameter cannot be contravariant).</summary>
public interface IOutboundChannel<T>
{
    void Send(in T message);
}
