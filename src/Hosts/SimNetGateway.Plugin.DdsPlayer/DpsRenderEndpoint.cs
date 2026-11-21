using System.Collections.Concurrent;
using CycloneDDS.Runtime;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The render client's topics on the render seat's participant (DD-022 3e, DD-023 D-12), for one gateway runtime: <c>RenderClient</c>, <c>FrameDone</c> and <c>RenderJobResult</c> are read,
/// <c>RenderJob</c> and <c>FrameRequest</c> written. Every sample read goes to the host-lifetime <see cref="DpsRenderService"/>, which holds the state (a world boundary rebuilds this
/// endpoint; the service keeps the clients and the job and gets the new endpoint attached). The listener only records liveliness; everything is applied in <see cref="Pump"/>. Main thread only.
/// </summary>
internal sealed class DpsRenderEndpoint : IDisposable
{
    private readonly record struct Liveliness(long Handle, int AliveChange, int NotAliveChange);

    private readonly DdsReader<D.RenderClient> _clients;
    private readonly DdsReader<D.FrameDone> _done;
    private readonly DdsReader<D.RenderJobResult> _results;
    private readonly DdsWriter<D.RenderJob> _jobs;
    private readonly DdsWriter<D.FrameRequest> _frames;
    private readonly DpsRenderService _service;
    private readonly ConcurrentQueue<Liveliness> _liveliness = new();
    private bool _disposed;

    public DpsRenderEndpoint(DdsParticipant participant, DpsSettings settings, DpsRenderService service)
    {
        _service = service;
        var options = new DdsReaderOptions { LivelinessLeaseDuration = TimeSpan.FromSeconds(settings.LeaseSeconds) };
        try
        {
            _clients = new DdsReader<D.RenderClient>(participant, null, options, null);
            _clients.LivelinessChanged += (_, st) => _liveliness.Enqueue(new Liveliness(st.LastPublicationHandle, st.AliveCountChange, st.NotAliveCountChange));
            _done = new DdsReader<D.FrameDone>(participant);
            _results = new DdsReader<D.RenderJobResult>(participant);
            _jobs = new DdsWriter<D.RenderJob>(participant);
            _frames = new DdsWriter<D.FrameRequest>(participant);
        }
        catch
        {
            Dispose();
            throw;
        }

        _service.Attach(this);
    }

    /// <summary>Frame requests written through this endpoint (tests).</summary>
    public int FramesWritten { get; private set; }

    /// <summary>Job states written through this endpoint (tests).</summary>
    public int JobsWritten { get; private set; }

    public void WriteJob(in D.RenderJob job)
    {
        _jobs.Write(job);
        JobsWritten++;
    }

    public void WriteFrame(in D.FrameRequest frame)
    {
        _frames.Write(frame);
        FramesWritten++;
    }

    /// <summary>Once per frame: the client's announcements, the frames it finished and the job's result go to the service.</summary>
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

        using (var loan = _done.Take(64))
        {
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    _service.OnFrameDone(sample.Data);
                }
            }
        }

        using (var loan = _results.Take(16))
        {
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    _service.OnJobResult(sample.Data);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.Detach(this);
        _frames?.Dispose();
        _jobs?.Dispose();
        _results?.Dispose();
        _done?.Dispose();
        _clients?.Dispose();
    }
}
