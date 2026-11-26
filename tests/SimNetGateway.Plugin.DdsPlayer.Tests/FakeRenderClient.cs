using System.Diagnostics;
using CycloneDDS.Runtime;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>
/// A render client over real DDS for the tests (DD-022 3e): announces itself, takes the job and the frames, counts what the station writes the way the client does (D-B4 rev 6: instances
/// of keyed state topics deduplicated, keyless state topics once, event samples one each) and answers with <c>FrameDone</c> / <c>RenderJobResult</c> when the test says so.
/// </summary>
internal sealed class FakeRenderClient : IDisposable
{
    private interface ICounter
    {
        string Name { get; }

        int Count { get; }

        void Poll();

        void Reset();
    }

    private sealed class Counter<T> : ICounter
        where T : struct
    {
        private readonly DdsReader<T> _reader;
        private readonly Func<T, long>? _key;
        private readonly bool _isEvent;
        private readonly HashSet<long> _keys = new();
        private int _events;
        private bool _seen;

        public Counter(DdsParticipant participant, Func<T, long>? key, bool isEvent)
        {
            _reader = new DdsReader<T>(participant);
            _key = key;
            _isEvent = isEvent;
        }

        public string Name => typeof(T).Name;

        public int Count => _isEvent ? _events : _key is null ? (_seen ? 1 : 0) : _keys.Count;

        public void Poll()
        {
            using var loan = _reader.Take(2048);
            foreach (var sample in loan)
            {
                if (_isEvent)
                {
                    if (sample.IsValid)
                    {
                        _events++;
                    }
                }
                else if (_key is null)
                {
                    if (sample.IsValid)
                    {
                        _seen = true;
                    }
                }
                else
                {
                    _keys.Add(_key(sample.Data));   // a write and a dispose of one instance count once; a disposed instance carries its key
                }
            }
        }

        public void Reset()
        {
            _keys.Clear();
            _events = 0;
            _seen = false;
        }
    }

    private readonly DdsParticipant _participant;
    private DdsWriter<D.RenderClient>? _announce;
    private readonly DdsWriter<D.FrameDone> _done;
    private readonly DdsWriter<D.RenderJobResult> _result;
    private readonly DdsReader<D.RenderJob> _jobs;
    private readonly DdsReader<D.FrameRequest> _requests;
    private readonly DdsReader<D.ClockStep> _steps;
    private readonly List<ICounter> _counters = new();

    public FakeRenderClient(uint domain, string partition, ulong clientId = 77)
    {
        ClientId = clientId;
        _participant = new DdsParticipant(domain, defaultPartition: partition);
        _announce = new DdsWriter<D.RenderClient>(_participant);
        _done = new DdsWriter<D.FrameDone>(_participant);
        _result = new DdsWriter<D.RenderJobResult>(_participant);
        _jobs = new DdsReader<D.RenderJob>(_participant);
        _requests = new DdsReader<D.FrameRequest>(_participant);
        _steps = new DdsReader<D.ClockStep>(_participant);

        // every topic the gateway writes into the world, counted as the contract says (SimClock, ClockStep and GizmoFrame are not)
        Keyless<D.Session>();
        Keyed<D.Entity>(e => e.EntityId);
        Keyed<D.EntityState>(e => e.EntityId);
        Keyed<D.EntityStatus>(e => e.EntityId);
        Keyed<D.Articulation>(a => ((long)a.EntityId << 32) | (uint)a.PartId);
        Keyed<D.PersonState>(e => e.EntityId);
        Keyed<D.PersonAim>(e => e.EntityId);
        Keyed<D.Appearance>(e => e.EntityId);
        Event<D.Fire>();
        Event<D.Detonation>();
        Event<D.Effect>();
        Event<D.Impact>();
        Keyed<D.VehicleLook>(e => e.EntityId);
        Keyed<D.Round>(e => e.EntityId);
        Keyed<D.MunitionLook>(e => e.MunitionTypeId);
        Keyed<D.FlameJet>(j => ((long)j.EntityId << 32) | (uint)j.Mount);
        Keyed<D.FireSpots>(e => e.EntityId);
        Keyed<D.GroundMarks>(e => e.EntityId);
        Keyed<D.Animal>(e => e.EntityId);
        Keyed<D.BuildingDamage>(b => DpsTransport.KeyOf(b.Building));
        Keyed<D.GroundDamage>(g => DpsTransport.KeyOf(g.Cell));
        Keyed<D.Carried>(c => c.EntityId);
        Keyed<D.Cloud>(c => c.CloudId);
        Keyed<D.Deck>(d => d.EntityId);
        Keyed<D.Mover>(m => m.MoverId);
        Event<D.MoverTracks>();
        Keyed<D.TrafficLights>(t => t.SignalId);
        Keyed<D.TkbType>(t => t.TypeId);
        Event<D.ManDowned>();
        Event<D.PlayerMessage>();
        Keyed<D.Status>(s => s.StatusId);
        Keyless<D.Exercise>();
        Keyless<D.View>();
        Keyless<D.PlayerSlot>();
        Keyless<D.OwnUnit>();
    }

    public ulong ClientId { get; }

    public List<D.RenderJob> Jobs { get; } = new();

    public List<D.FrameRequest> Requests { get; } = new();

    public List<D.ClockStep> Steps { get; } = new();

    /// <summary>The announcement a test may change before <see cref="Announce"/>: limits, readiness, formats.</summary>
    public D.RenderClient Sample { get; set; } = new()
    {
        Name = "fake", Ready = true, MaxWidth = 3840, MaxHeight = 2160, MaxSupersample = 4, Formats = 1, AudioFormats = 3, MaxSampleRate = 48000, ReadyJobId = string.Empty,
    };

    /// <summary>The instances and events counted since <see cref="Reset"/> (what the client would compare with <c>ClockStep.Samples</c>).</summary>
    public int Counted => _counters.Sum(c => c.Count);

    public string CountedDetail() => string.Join(", ", _counters.Where(c => c.Count > 0).Select(c => c.Name + "=" + c.Count));

    private void Keyed<T>(Func<T, long> key)
        where T : struct => _counters.Add(new Counter<T>(_participant, key, isEvent: false));

    private void Keyless<T>()
        where T : struct => _counters.Add(new Counter<T>(_participant, null, isEvent: false));

    private void Event<T>()
        where T : struct => _counters.Add(new Counter<T>(_participant, null, isEvent: true));

    /// <summary>Writes the announcement (the client repeats it; the first ones may find no matched reader yet).</summary>
    public void Announce(string readyJobId = "")
    {
        var s = Sample;
        s.ClientId = ClientId;
        s.ReadyJobId = readyJobId;
        Sample = s with { ReadyJobId = readyJobId };
        _announce!.Write(s);
    }

    /// <summary>Announces until <paramref name="seen"/> says the station has the client (a volatile sample may be lost before the match).</summary>
    public bool AnnounceUntil(Func<bool> seen, Action step, int timeoutMs = 6000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Announce();
            step();
            if (seen())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return seen();
    }

    /// <summary>Takes everything that arrived: job states, frame requests, clock steps, counted topics.</summary>
    public void Poll()
    {
        using (var loan = _jobs.Take(64))
        {
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    Jobs.Add(sample.Data);
                }
            }
        }

        using (var loan = _requests.Take(2048))
        {
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    Requests.Add(sample.Data);
                }
            }
        }

        using (var loan = _steps.Take(2048))
        {
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    Steps.Add(sample.Data);
                }
            }
        }

        foreach (var counter in _counters)
        {
            counter.Poll();
        }
    }

    /// <summary>Starts counting afresh (the client presented, or sent <c>ReadyJobId</c>).</summary>
    public void Reset()
    {
        foreach (var counter in _counters)
        {
            counter.Reset();
        }
    }

    /// <summary>Polls until <paramref name="condition"/> holds.</summary>
    public bool Until(Func<bool> condition, Action? step = null, int timeoutMs = 6000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Poll();
            if (condition())
            {
                return true;
            }

            step?.Invoke();
            Thread.Sleep(2);
        }

        Poll();
        return condition();
    }

    /// <summary>Polls for <paramref name="ms"/> and returns how many counted instances and events arrived in that time (the "nothing lands" check).</summary>
    public int ArrivalsDuring(int ms)
    {
        int before = Counted;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Poll();
            Thread.Sleep(2);
        }

        return Counted - before;
    }

    public void FrameDone(in D.FrameRequest request, D.FrameStatus status = D.FrameStatus.FrameStatusWritten, string reason = "") =>
        _done.Write(new D.FrameDone
        {
            ClientId = ClientId, JobId = request.JobId, FrameIndex = request.FrameIndex, StepSeq = request.StepSeq, Status = status, File = request.File ?? string.Empty, AudioSamples = 0, Reason = reason, RenderMs = 1f,
        });

    public void JobResult(string jobId, D.FrameStatus status, string trackFile = "", long samples = 0, string reason = "") =>
        _result.Write(new D.RenderJobResult { ClientId = ClientId, JobId = jobId, Status = status, TrackFile = trackFile, TrackSamples = samples, Reason = reason });

    /// <summary>The client's announcement goes away (its process ended): the station's reader sees its liveliness end.</summary>
    public void Vanish()
    {
        _announce?.Dispose();
        _announce = null;
    }

    public void Dispose()
    {
        _announce?.Dispose();
        _participant.Dispose();
    }
}
