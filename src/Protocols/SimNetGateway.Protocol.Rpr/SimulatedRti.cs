namespace SimNetGateway.Protocol.Rpr;

/// <summary>
/// A stand-in for the RTI (PoC). It observes the outbound RPR messages (the host wires <see cref="Observe"/> as the tap
/// of the outbound recorder), remembers every <see cref="RprRegisterObject"/> and answers each one with an
/// <see cref="RprObjectRegistered"/> on the next <see cref="DeliverPending"/> call, so handles arrive one tick later.
/// Handles are allocated sequentially from <see cref="FirstHandle"/>.
/// </summary>
public sealed class SimulatedRti
{
    public const uint FirstHandle = 0x2A01;

    private readonly List<long> _pending = new();
    private readonly List<RprObjectHandle> _registered = new();
    private uint _next = FirstHandle;

    /// <summary>Handles allocated so far, in allocation order.</summary>
    public IReadOnlyCollection<RprObjectHandle> Registered => _registered;

    /// <summary>Reacts to <see cref="RprRegisterObject"/>; every other message is ignored.</summary>
    public void Observe(IRprMessage outbound)
    {
        if (outbound is RprRegisterObject register)
        {
            _pending.Add(register.Token);
        }
    }

    /// <summary>
    /// Delivers the answer to every registration requested before this call, in request order, and returns how many.
    /// A registration observed from inside <paramref name="deliver"/> waits for the next call.
    /// </summary>
    public int DeliverPending(Action<RprObjectRegistered> deliver)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        int count = _pending.Count;
        if (count == 0)
        {
            return 0;
        }

        var tokens = _pending.GetRange(0, count);
        _pending.RemoveRange(0, count);
        foreach (long token in tokens)
        {
            var handle = new RprObjectHandle(_next++);
            _registered.Add(handle);
            deliver(new RprObjectRegistered(token, handle));
        }

        return count;
    }
}
