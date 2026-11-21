using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// What every client last said about the world it presents (DD-022 3g): the latest <c>ClientWorld</c> per client id, in order of first appearance. A client is dropped when its
/// writer's liveliness is lost or its instance goes away. <see cref="Evaluate"/> answers whether every live client that wants to be waited for is Ready for a world epoch.
/// Lives in the host service, so it survives a world boundary (a new reader does not replay a volatile sample). Main thread only.
/// </summary>
internal sealed class DpsClientWorlds
{
    internal readonly record struct Entry(ulong ClientId, uint WorldEpoch, D.ClientWorldState State, string Step, string Reason, bool WaitForMe);

    private readonly List<Entry> _clients = new();
    private readonly Dictionary<long, ulong> _byWriter = new();

    /// <summary>The clients now, in order of first appearance.</summary>
    internal IReadOnlyList<Entry> Clients => _clients;

    /// <summary>A sample of a client: it replaces that client's earlier one. <paramref name="writer"/> is the publication handle that wrote it (liveliness is per writer).</summary>
    public void Update(in D.ClientWorld sample, long writer)
    {
        if (sample.ClientId == 0)
        {
            return;   // a client puts a nonzero id on every player sample
        }

        var entry = new Entry(sample.ClientId, sample.WorldEpoch, sample.State, sample.Step ?? string.Empty, sample.Reason ?? string.Empty, sample.WaitForMe);
        _byWriter[writer] = sample.ClientId;
        int i = _clients.FindIndex(c => c.ClientId == entry.ClientId);
        if (i >= 0)
        {
            _clients[i] = entry;
        }
        else
        {
            _clients.Add(entry);
        }
    }

    /// <summary>A writer's liveliness is lost (or it is gone): its client is no longer waited for.</summary>
    public void WriterLost(long writer)
    {
        if (_byWriter.Remove(writer, out ulong client))
        {
            // another live writer of the same client keeps it
            if (!_byWriter.ContainsValue(client))
            {
                _clients.RemoveAll(c => c.ClientId == client);
            }
        }
    }

    /// <summary>The instance of a client was disposed.</summary>
    public void Remove(ulong clientId)
    {
        _clients.RemoveAll(c => c.ClientId == clientId);
        foreach (var key in _byWriter.Where(kv => kv.Value == clientId).Select(kv => kv.Key).ToList())
        {
            _byWriter.Remove(key);
        }
    }

    /// <summary>
    /// True when every client with <c>WaitForMe</c> is Ready for <paramref name="worldEpoch"/> (true when there is none). A client Loading, or still on another epoch, is not ready.
    /// <paramref name="failure"/> is the reason of the first client Failed for that epoch; <paramref name="step"/> the step of the first client that is Loading.
    /// </summary>
    public bool Evaluate(uint worldEpoch, out string? failure, out string? step)
    {
        failure = null;
        step = null;
        bool ready = true;
        foreach (var c in _clients)
        {
            if (!c.WaitForMe)
            {
                continue;
            }

            if (c.WorldEpoch == worldEpoch && c.State == D.ClientWorldState.ClientWorldStateReady)
            {
                continue;
            }

            ready = false;
            if (failure is null && c.WorldEpoch == worldEpoch && c.State == D.ClientWorldState.ClientWorldStateFailed)
            {
                failure = c.Reason;
            }

            if (step is null && c.State == D.ClientWorldState.ClientWorldStateLoading)
            {
                step = c.Step;
            }
        }

        return ready;
    }
}
