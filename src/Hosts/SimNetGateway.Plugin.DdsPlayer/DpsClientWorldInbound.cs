using System.Collections.Concurrent;
using CycloneDDS.Runtime;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Reads <c>ClientWorld</c> (DD-022 3g) into the host service's table: the newest sample of each client; a client whose writer's liveliness is lost, or whose instance goes away,
/// is dropped. The listener only records; the table is touched in <see cref="Pump"/> on the host's main thread.
/// </summary>
internal sealed class DpsClientWorldInbound : IDisposable
{
    private readonly record struct Liveliness(long Handle, int AliveChange, int NotAliveChange);

    private readonly DdsReader<D.ClientWorld> _reader;
    private readonly DpsClientWorlds _worlds;
    private readonly ConcurrentQueue<Liveliness> _liveliness = new();

    public DpsClientWorldInbound(DdsParticipant participant, DpsSettings settings, DpsClientWorlds worlds)
    {
        _worlds = worlds;
        var options = new DdsReaderOptions { LivelinessLeaseDuration = TimeSpan.FromSeconds(settings.LeaseSeconds) };
        _reader = new DdsReader<D.ClientWorld>(participant, null, options, null);
        _reader.LivelinessChanged += (_, st) => _liveliness.Enqueue(new Liveliness(st.LastPublicationHandle, st.AliveCountChange, st.NotAliveCountChange));
    }

    public void Pump()
    {
        using (var loan = _reader.Take(64))
        {
            foreach (var sample in loan)
            {
                if (!sample.IsValid)
                {
                    // a disposed instance carries only its key
                    if (sample.Info.InstanceState != DdsInstanceState.Alive && sample.Data.ClientId != 0)
                    {
                        _worlds.Remove(sample.Data.ClientId);
                    }

                    continue;
                }

                if (sample.Info.InstanceState == DdsInstanceState.Alive)
                {
                    _worlds.Update(sample.Data, sample.Info.PublicationHandle);
                }
                else
                {
                    _worlds.Remove(sample.Data.ClientId);
                }
            }
        }

        while (_liveliness.TryDequeue(out var e))
        {
            if (e.NotAliveChange > 0 || e.AliveChange < 0)
            {
                _worlds.WriterLost(e.Handle);
            }
        }
    }

    public void Dispose() => _reader.Dispose();
}
