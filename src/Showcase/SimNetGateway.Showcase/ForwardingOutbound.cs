using SimNetGateway.Engine;

namespace SimNetGateway.Showcase;

/// <summary>
/// An outbound channel that counts what is sent and hands every message to an optional observer, without recording it
/// (benchmarks: the RPR side must still reach the simulated RTI, but nothing is kept).
/// </summary>
public sealed class ForwardingOutbound<T> : IOutboundChannel<T>
{
    /// <summary>Called for every message after it was counted.</summary>
    public Action<T>? Observer { get; set; }

    public long Sent { get; private set; }

    public void Send(in T message)
    {
        Sent++;
        Observer?.Invoke(message);
    }
}
