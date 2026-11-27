using System.Collections.Concurrent;
using CycloneDDS.Runtime;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The host-lifetime listener for <c>RenderClient</c> (T-086, DD-022 9): a participant of its own on the render seat's partition, owned by the <see cref="DpsRenderService"/>, so a client
/// is known before the first world exists and across every world boundary (the <see cref="DpsRenderEndpoint"/> is per gateway runtime and reads the same topic while it lives; both feed the
/// same idempotent service calls). It reads nothing else. Main thread only, from <see cref="DpsRenderService.Pump"/>.
/// </summary>
internal sealed class DpsRenderClientWatcher : IDisposable
{
    private readonly record struct Liveliness(long Handle, int AliveChange, int NotAliveChange);

    private readonly DdsParticipant _participant;
    private readonly DdsReader<D.RenderClient> _clients;
    private readonly DpsRenderService _service;
    private readonly ConcurrentQueue<Liveliness> _liveliness = new();

    public DpsRenderClientWatcher(DpsSettings settings, string basePartition, DpsRenderService service)
    {
        _service = service;
        string partition = settings.Seats >= 2 ? $"{basePartition}-{settings.RenderSeat}" : basePartition;
        _participant = new DdsParticipant((uint)settings.Domain, defaultPartition: partition);
        try
        {
            var options = new DdsReaderOptions { LivelinessLeaseDuration = TimeSpan.FromSeconds(settings.LeaseSeconds) };
            _clients = new DdsReader<D.RenderClient>(_participant, null, options, null);
            _clients.LivelinessChanged += (_, st) => _liveliness.Enqueue(new Liveliness(st.LastPublicationHandle, st.AliveCountChange, st.NotAliveCountChange));
        }
        catch
        {
            _participant.Dispose();
            throw;
        }
    }

    public void Pump()
    {
        using (var loan = _clients.Take(64))
        {
            foreach (var sample in loan)
            {
                if (!sample.IsValid)
                {
                    if (sample.Info.InstanceState != DdsInstanceState.Alive && sample.Data.ClientId != 0)
                    {
                        _service.OnClientGone(sample.Data.ClientId);
                    }

                    continue;
                }

                if (sample.Info.InstanceState == DdsInstanceState.Alive)
                {
                    _service.OnClient(sample.Data, sample.Info.PublicationHandle);
                }
                else
                {
                    _service.OnClientGone(sample.Data.ClientId);
                }
            }
        }

        while (_liveliness.TryDequeue(out var e))
        {
            if (e.NotAliveChange > 0 || e.AliveChange < 0)
            {
                _service.OnClientLost(e.Handle);
            }
        }
    }

    public void Dispose()
    {
        _clients.Dispose();
        _participant.Dispose();
    }
}
