using SimNetGateway.Engine;

namespace SimNetGateway.Engine.InMemory;

/// <summary>The process-wide message counter shared by all recorders (a static of a generic class would be per type).</summary>
internal static class OutboundSequence
{
    private static int s_value;

    public static int Next() => Interlocked.Increment(ref s_value);
}

/// <summary>One message captured by <see cref="InMemoryOutbound{T}"/> with the engine times at which it was sent.</summary>
public readonly record struct Recorded<T>(T Message, long Tick, double WallTime, double SimTime, int Sequence);

/// <summary>
/// An outbound channel that records what is sent (tests, showcase). Sent from the engine thread; the stamps come from
/// <see cref="Context"/> (zeros while it is null). <see cref="Sequence"/> numbers all recorders of the process in
/// one global order, so messages of several sides can be ordered against each other.
/// </summary>
public sealed class InMemoryOutbound<T> : IOutboundChannel<T>
{
    private readonly List<Recorded<T>> _messages = new();

    /// <summary>Source of the tick / wall / sim stamps; settable after the gateway is built.</summary>
    public IGatewayContext? Context { get; set; }

    /// <summary>Called after a message was recorded, e.g. to feed it back into an inbound channel (loopback tap).</summary>
    public Action<T>? Tap { get; set; }

    public IReadOnlyList<Recorded<T>> Messages => _messages;

    public int Count => _messages.Count;

    public void Send(in T message)
    {
        var context = Context;
        int sequence = OutboundSequence.Next();
        _messages.Add(context is null
            ? new Recorded<T>(message, 0, 0, 0, sequence)
            : new Recorded<T>(message, context.TickCount, context.WallTime, context.SimTime, sequence));
        Tap?.Invoke(message);
    }

    public void Clear() => _messages.Clear();

    /// <summary>The recorded messages that are a <typeparamref name="TMessage"/> (allocates; for tests).</summary>
    public IEnumerable<TMessage> OfType<TMessage>()
    {
        var result = new List<TMessage>();
        foreach (var recorded in _messages)
        {
            object? boxed = recorded.Message;
            if (boxed is TMessage typed)
            {
                result.Add(typed);
            }
        }

        return result;
    }

    /// <summary>The records whose wall time lies in [<paramref name="fromWall"/>, <paramref name="toWall"/>] (inclusive).</summary>
    public IReadOnlyList<Recorded<T>> Between(double fromWall, double toWall)
    {
        var result = new List<Recorded<T>>();
        foreach (var recorded in _messages)
        {
            if (recorded.WallTime >= fromWall && recorded.WallTime <= toWall)
            {
                result.Add(recorded);
            }
        }

        return result;
    }
}

/// <summary>An outbound channel that discards everything (benchmarks) and counts what was sent.</summary>
public sealed class NullOutbound<T> : IOutboundChannel<T>
{
    public long Sent { get; private set; }

    public void Send(in T message) => Sent++;
}
