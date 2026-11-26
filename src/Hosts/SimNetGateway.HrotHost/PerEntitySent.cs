using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The per-entity bookkeeping of a state gather that sends a message when an entity's inputs change and an "ended" message once when the entity is no longer seen (T-084 follow-up; shared by
/// <see cref="DeckGather"/> and <see cref="ObstacleGather"/>): the last inputs sent per entity, whether the entity was seen in this frame, and the ended-once sweep. Call <see cref="Begin"/> before the frame's
/// entities, <see cref="Changed"/> for each one, <see cref="End"/> after them. A steady frame allocates nothing (the inputs are a value-equal struct; <see cref="End"/> takes a cached, non-capturing factory).
/// Main thread only.
/// </summary>
internal sealed class PerEntitySent<TInputs>
    where TInputs : struct, IEquatable<TInputs>
{
    private sealed class Sent(TInputs inputs)
    {
        public TInputs Inputs = inputs;
        public bool Seen = true;
    }

    private readonly Dictionary<int, Sent> _sent = new();
    private readonly List<int> _gone = new();

    public void Clear() => _sent.Clear();

    /// <summary>A new frame: nothing has been seen yet.</summary>
    public void Begin()
    {
        foreach (var s in _sent.Values)
        {
            s.Seen = false;
        }
    }

    /// <summary>Marks the entity seen and says whether to send it: it is new, or its inputs differ from the last sent (which are then replaced).</summary>
    public bool Changed(int id, in TInputs now)
    {
        if (_sent.TryGetValue(id, out var sent))
        {
            sent.Seen = true;
            if (sent.Inputs.Equals(now))
            {
                return false;
            }

            sent.Inputs = now;
            return true;
        }

        _sent[id] = new Sent(now);
        return true;
    }

    /// <summary>Entities not seen this frame are gone: forgotten and <paramref name="ended"/> sent once for each.</summary>
    public void End<TEnded>(InMemoryInbound inbound, Func<int, TEnded> ended)
        where TEnded : struct, SimNetGateway.Protocol.Hrot.IHrotToGatewayMessage
    {
        _gone.Clear();
        foreach (var (id, s) in _sent)
        {
            if (!s.Seen)
            {
                _gone.Add(id);
            }
        }

        foreach (int id in _gone)
        {
            _sent.Remove(id);
            inbound.Enqueue(ended(id));
        }
    }
}
