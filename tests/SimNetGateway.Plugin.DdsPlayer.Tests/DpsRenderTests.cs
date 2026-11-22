using System.Numerics;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>
/// DD-022 part D, the render half in the plugin (T-050): a fake render client over real DDS drives the gateway's film gate - the frames in order with the exact camera, the barrier that holds the
/// counted world writes and counts them for <c>ClockStep.Samples</c> (D-B4 rev 6), the faults and timeouts on an injected wall clock, and the steady state outside a job.
/// </summary>
public class DpsRenderTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "dps-render-root");

    /// <summary>The station and its fake client: one render seat, a manual wall clock, the render root.</summary>
    private sealed class RenderRig : IDisposable
    {
        public RenderRig(int seats = 1, int renderSeat = 1, bool connect = true, Action<D.RenderClient>? announce = null, string? root = null)
        {
            Clock = new ManualClock();
            Plugin = new DdsPlayerPlugin { WallClock = Clock };
            uint domain = Watch.NewDomain();
            Station = new Rig(domain, plugin: Plugin, seats: seats, renderSeat: renderSeat, renderRoot: root ?? Root, stations: seats > 1 ? Enumerable.Range(0, seats).Select(_ => (IPlayerStation)new Watch.FakeStation()).ToList() : null);
            Service = (DpsHostService)Station.Service!;
            Gate = Service;
            Client = new FakeRenderClient(domain, seats > 1 ? $"station-1-{renderSeat}" : "station-1");
            if (announce is not null)
            {
                var sample = Client.Sample;
                announce(sample);
            }

            if (connect)
            {
                Assert.True(Connect(), "the render client was never announced");
            }
        }

        public ManualClock Clock { get; }

        public DdsPlayerPlugin Plugin { get; }

        public Rig Station { get; }

        public DpsHostService Service { get; }

        public IRenderGate Gate { get; }

        public FakeRenderClient Client { get; }

        public bool Connect() => Client.AnnounceUntil(() => Gate.CanFilm, Station.Step);

        public RenderJobSpec Job(bool audio = false, int width = 1920, int height = 1080, int supersample = 2, string? dir = null, bool reel = true) =>
            new(Guid.NewGuid(), reel, "test reel", 30, width, height, supersample, dir ?? Path.Combine(Root, "job1"), audio);

        /// <summary>Steps the station and polls the client until the task is done (or the time is up).</summary>
        public bool Complete(Task task, Action? each = null, int timeoutMs = 6000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && sw.ElapsedMilliseconds < timeoutMs)
            {
                Station.Step();
                Client.Poll();
                each?.Invoke();
                Thread.Sleep(2);
            }

            return task.IsCompleted;
        }

        /// <summary>Begins the job and answers its warm-up: the client says it is ready for it.</summary>
        public string Begin(RenderJobSpec spec)
        {
            var begin = Gate.BeginJob(spec);
            Assert.True(Client.Until(() => Client.Jobs.Any(j => j.JobId == spec.JobId.ToString("N")), Station.Step), "the RenderJob never reached the client");
            string id = spec.JobId.ToString("N");
            Assert.True(Complete(begin, () => Client.Announce(id)), "the job never became ready");
            Assert.True(begin.IsCompletedSuccessfully);
            Client.Reset();   // the client counts since it sent ReadyJobId
            return id;
        }

        public void Dispose()
        {
            Client.Dispose();
            Station.Dispose();
        }
    }

    /// <summary>The result of a task known to be complete (kept out of the test methods: they must not block).</summary>
    private static T ResultOf<T>(Task<T> task) => task.Result;

    private static RenderFrameSpec Frame(RenderJobSpec job, int index, long step, RenderFrameKind kind = RenderFrameKind.Scene, Vector3? forward = null, Vector3? up = null, float fade = 0f, string? path = null) =>
        new(job.JobId, index, kind, 10.0 + index * 0.05, 0.5, step, new Vector3(100 + index, 200, 30), forward ?? Vector3.UnitX, up ?? Vector3.UnitZ, 47.5f,
            kind == RenderFrameKind.Scene ? 1920 : 0, kind == RenderFrameKind.Scene ? 1080 : 0, kind == RenderFrameKind.Scene ? 2 : 1,
            path ?? (kind == RenderFrameKind.Scene ? Path.Combine(Root, "job1", $"{index:D6}.png") : string.Empty), null, Array.Empty<RenderGraphic>(), fade, Array.Empty<string>(), Array.Empty<string>());

    // ---- (a) a reel reaches the client in order with the exact camera ---------------------------------------------------------------------

    [Fact]
    public void A_reel_of_mixed_frames_reaches_the_client_in_index_order_with_the_exact_camera_fade_and_kind_and_every_frame_task_completes_after_its_FrameDone()
    {
        using var rig = new RenderRig();
        var spec = rig.Job(audio: true);
        string id = rig.Begin(spec);
        var rnd = new Random(7);
        var kinds = new[] { RenderFrameKind.Scene, RenderFrameKind.Scene, RenderFrameKind.Repeat, RenderFrameKind.Scene, RenderFrameKind.Silence, RenderFrameKind.Scene, RenderFrameKind.Repeat, RenderFrameKind.Repeat };
        var steps = new long[] { 0, 1, 1, 2, 2, 3, 3, 3 };
        var cameras = new List<(Vector3 Forward, Vector3 Up)>();
        long clockSeq = 500;
        for (int i = 0; i < kinds.Length; i++)
        {
            // a banked camera: forward anywhere on the sphere, up rolled about it
            var forward = Vector3.Normalize(new Vector3((float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5)) + Vector3.UnitX * 0.2f);
            var worldUp = Vector3.UnitZ - Vector3.Dot(Vector3.UnitZ, forward) * forward;
            var side = Vector3.Cross(Vector3.Normalize(worldUp), forward);
            float roll = (float)(rnd.NextDouble() * 1.2 - 0.6);
            var up = Vector3.Normalize(Vector3.Normalize(worldUp) * MathF.Cos(roll) + side * MathF.Sin(roll));
            cameras.Add((forward, up));
            if (i == 0 || steps[i] != steps[i - 1])
            {
                if (i > 0)
                {
                    rig.Station.Send(new HrotStep(++clockSeq, 0.05f, 10.0 + steps[i] * 0.05, 1f, 1));
                }
            }

            float fade = i == 3 ? 0.75f : 0f;
            var task = rig.Gate.Frame(Frame(spec, i, steps[i], kinds[i], forward, up, fade));
            Assert.True(rig.Client.Until(() => rig.Client.Requests.Count == i + 1, rig.Station.Step), $"frame {i} never reached the client");
            // the frame is outstanding until the client answers: its task must not be done yet, however long the station runs
            for (int t = 0; t < 5; t++)
            {
                rig.Station.Step();
                Thread.Sleep(3);
            }

            Assert.False(task.IsCompleted, $"frame {i} completed before its FrameDone");
            rig.Client.FrameDone(rig.Client.Requests[i]);
            Assert.True(rig.Complete(task), $"frame {i} never completed");
            Assert.True(task.IsCompletedSuccessfully);
        }

        Assert.Equal(Enumerable.Range(0, kinds.Length), rig.Client.Requests.Select(r => r.FrameIndex));
        for (int i = 0; i < kinds.Length; i++)
        {
            var r = rig.Client.Requests[i];
            Assert.Equal(id, r.JobId);
            Assert.Equal(kinds[i] switch { RenderFrameKind.Scene => D.FrameKind.FrameKindScene, RenderFrameKind.Repeat => D.FrameKind.FrameKindRepeat, _ => D.FrameKind.FrameKindSilence }, r.Kind);
            Assert.Equal(i == 3 ? 0.75f : 0f, r.Fade);
            Assert.Equal(10.0 + i * 0.05, r.SimTime, 9);
            Assert.Equal(0.5f, r.FilmRate);
            Assert.Equal(47.5f, r.FovDeg);
            Assert.Equal((100.0 + i, 200.0, 30.0), (r.Position.X, r.Position.Y, r.Position.Z));
            Assert.Equal(kinds[i] == RenderFrameKind.Scene ? $"job1/{i:D6}.png" : string.Empty, r.File);
            // the camera round trip: the wire's quaternion takes the body's +x to the forward and +z to the up of the frame
            var q = new Quaternion(r.Orientation.X, r.Orientation.Y, r.Orientation.Z, r.Orientation.W);
            Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitX, q), cameras[i].Forward), 0, 1e-5f);
            Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, q), cameras[i].Up), 0, 1e-5f);
        }

        // frames 1 and 3 and 5 follow a step each: their ClockStep was released with the frame, the later frames of the same step name the same one
        Assert.Equal(new long[] { 501, 502, 503 }, rig.Client.Steps.Select(s => s.StepSeq).ToArray());
        Assert.Equal(new long[] { 0, 501, 501, 502, 502, 503, 503, 503 }, rig.Client.Requests.Select(r => r.StepSeq).ToArray());
        Assert.All(rig.Client.Steps, s => Assert.True(s.AckRequired));

        // the end: the client's soundtrack comes back with the samples it reported
        var end = rig.Gate.EndJob(spec, RenderEnd.Ok, null);
        Assert.True(rig.Client.Until(() => rig.Client.Jobs.Any(j => j.JobId == id && j.State == D.RenderState.RenderStateDone), rig.Station.Step));
        rig.Client.JobResult(id, D.FrameStatus.FrameStatusWritten, "job1/" + id + ".wav", 72_000);
        Assert.True(rig.Complete(end));
        var track = ResultOf(end);
        Assert.NotNull(track);
        Assert.Equal((72_000L, 48000, 2), (track!.Samples, track.SampleRate, track.Channels));
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, "job1", id + ".wav")), track.Path);
        Assert.False(rig.Service.JobActive());
    }

    [Fact]
    public void Orientation_takes_forward_and_up_exactly_and_survives_a_degenerate_pair()
    {
        var rnd = new Random(3);
        for (int i = 0; i < 400; i++)
        {
            var f = Vector3.Normalize(new Vector3((float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5)));
            var any = Vector3.Normalize(new Vector3((float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5)));
            var up = Vector3.Normalize(any - Vector3.Dot(any, f) * f);
            var q = DpsRenderService.OrientationOf(f, up);
            Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitX, q), f), 0, 1e-5f);
            Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, q), up), 0, 1e-5f);
            Assert.InRange(q.Length(), 0.99999f, 1.00001f);
        }

        // up parallel to forward, straight up, zero vectors: still a unit rotation taking +x to the forward
        foreach (var (f, up) in new[] { (Vector3.UnitZ, Vector3.UnitZ), (-Vector3.UnitZ, Vector3.UnitZ), (Vector3.UnitX, Vector3.Zero), (Vector3.Zero, Vector3.Zero) })
        {
            var q = DpsRenderService.OrientationOf(f, up);
            Assert.InRange(q.Length(), 0.99999f, 1.00001f);
            var expected = f.LengthSquared() > 0 ? Vector3.Normalize(f) : Vector3.UnitX;
            Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitX, q), expected), 0, 1e-5f);
        }
    }

    // ---- (b) the barrier: Samples equals what the client counts, nothing counted lands between a step and its FrameDone ------------------------

    private static void WriteWorld(DpsTransport t, Random rnd, HashSet<int> alive)
    {
        int ops = rnd.Next(1, 14);
        for (int i = 0; i < ops; i++)
        {
            switch (rnd.Next(12))
            {
                case 0:
                case 1:
                case 2:
                {
                    int id = rnd.Next(1, 25);
                    int times = rnd.Next(1, 4);   // several writes of one instance in a gather count once
                    for (int k = 0; k < times; k++)
                    {
                        t.EntityState.Send(new DpsItem<D.EntityState>(new D.EntityState { EntityId = id, SimTime = rnd.NextDouble() }));
                    }

                    break;
                }

                case 3:
                {
                    int id = rnd.Next(1, 12);
                    t.EntityTopic.Send(new DpsItem<D.Entity>(new D.Entity { EntityId = id, Name = "e" + id }));
                    alive.Add(id);
                    break;
                }

                case 4:
                {
                    if (alive.Count > 0)
                    {
                        int id = alive.ElementAt(rnd.Next(alive.Count));
                        t.EntityTopic.Send(new DpsItem<D.Entity>(new D.Entity { EntityId = id, Name = string.Empty }, Dispose: true));
                        alive.Remove(id);
                    }

                    break;
                }

                case 5:
                    t.Session.Send(new DpsItem<D.Session>(new D.Session { StationName = "s", Terrain = string.Empty, Vocabulary = string.Empty, TerrainFormat = string.Empty, TerrainBaseHash = string.Empty, TerrainOverlayHash = string.Empty, SimTime = rnd.NextDouble() }));
                    break;
                case 6:
                    t.Status.Send(new DpsItem<D.Status>(new D.Status { StatusId = rnd.Next(1, 5), Reason = string.Empty, Value = (float)rnd.NextDouble() }));
                    break;
                case 7:
                {
                    int n = rnd.Next(1, 6);
                    for (int k = 0; k < n; k++)
                    {
                        t.Fire.Send(new DpsItem<D.Fire>(new D.Fire { ShooterId = rnd.Next(1, 9), SimTime = rnd.NextDouble() }));
                    }

                    break;
                }

                case 8:
                    t.ManDowned.Send(new DpsItem<D.ManDowned>(new D.ManDowned { }));
                    break;
                case 9:
                    t.Exercise.Send(new DpsItem<D.Exercise>(new D.Exercise { Reason = string.Empty }));
                    break;
                case 10:
                    t.EntityStatus.Send(new DpsItem<D.EntityStatus>(new D.EntityStatus { EntityId = rnd.Next(1, 25), Symbol = string.Empty, HealthCurrent = (float)rnd.NextDouble(), HealthMax = 1f }));
                    break;
                default:
                    if (rnd.Next(2) == 0)
                    {
                        t.PlayerSlot.Send(new DpsItem<D.PlayerSlot>(new D.PlayerSlot { Reason = string.Empty, Offers = new List<D.Offer>() }));
                    }
                    else
                    {
                        t.OwnUnit.Send(new DpsItem<D.OwnUnit>(new D.OwnUnit { UseLabel = string.Empty, Ammo = new List<D.AmmoEntry>() }));
                    }

                    break;
            }
        }
    }

    [Fact]
    public void ClockStep_Samples_equals_the_instances_and_events_the_client_receives_since_its_last_presentation_and_nothing_counted_lands_between_a_step_and_its_FrameDone()
    {
        int checkedSteps = 0, nonEmpty = 0, midRenderWrites = 0;
        for (int seed = 1; seed <= 5; seed++)
        {
            var rnd = new Random(seed);
            var alive = new HashSet<int>();
            using var rig = new RenderRig();
            var spec = rig.Job();
            rig.Begin(spec);
            var t = rig.Station.Transport;
            long clockSeq = 9000;

            // frame 0: no step in the director yet; its gather and step 1 are written while the client renders it (the director steps once a frame is asked)
            var pending = rig.Gate.Frame(Frame(spec, 0, 0));
            Assert.True(rig.Client.Until(() => rig.Client.Requests.Count == 1, rig.Station.Step));
            Assert.Empty(rig.Client.Steps);   // a frame with no step of its own gets no ClockStep
            for (int index = 1; index <= 8; index++)
            {
                // while the previous frame is being rendered the world moves and the next step arrives: none of it may reach the client yet
                WriteWorld(t, rnd, alive);
                rig.Station.Send(new HrotStep(++clockSeq, 0.05f, index * 0.05, 1f, 1));
                midRenderWrites++;
                Assert.Equal(0, rig.Client.ArrivalsDuring(15));
                Assert.Equal(index - 1, rig.Client.Steps.Count);
                rig.Client.FrameDone(rig.Client.Requests[index - 1]);
                Assert.True(rig.Complete(pending));
                rig.Client.Reset();   // the client presented

                // the next frame owns that step
                pending = rig.Gate.Frame(Frame(spec, index, index));
                Assert.True(rig.Client.Until(() => rig.Client.Requests.Count == index + 1, rig.Station.Step), $"seed {seed}: frame {index} never reached the client");
                var step = rig.Client.Steps[^1];
                Assert.True(step.AckRequired);
                Assert.Equal(clockSeq, step.StepSeq);
                Assert.Equal(clockSeq, rig.Client.Requests[index].StepSeq);
                // the client counts until it has Samples, and the count never goes beyond it
                Assert.True(rig.Client.Until(() => rig.Client.Counted >= (int)step.Samples), $"seed {seed} step {index}: only {rig.Client.Counted} of {step.Samples} arrived ({rig.Client.CountedDetail()})");
                Assert.Equal((int)step.Samples, rig.Client.Counted);
                Assert.Equal(0, rig.Client.ArrivalsDuring(15));
                checkedSteps++;
                if (step.Samples > 0)
                {
                    nonEmpty++;
                }
            }

            rig.Client.FrameDone(rig.Client.Requests[^1]);
            Assert.True(rig.Complete(pending));
        }

        Assert.True(checkedSteps >= 40);
        Assert.True(nonEmpty >= 30, $"the random worlds were too quiet: {nonEmpty} of {checkedSteps} steps wrote something");
        Assert.True(midRenderWrites >= 40);
    }

    [Fact]
    public void Writes_outside_a_job_go_out_at_once_and_a_job_ending_flushes_what_it_held_uncounted()
    {
        using var rig = new RenderRig();
        var t = rig.Station.Transport;
        Thread.Sleep(300);   // discovery of the client's event readers (a volatile event written before the match is not delivered)
        rig.Client.Poll();
        rig.Client.Reset();   // what the station wrote when it started (the slot, the unit) is not under test
        t.EntityState.Send(new DpsItem<D.EntityState>(new D.EntityState { EntityId = 5 }));
        t.Fire.Send(new DpsItem<D.Fire>(new D.Fire { ShooterId = 1 }));
        Assert.True(rig.Client.Until(() => rig.Client.Counted == 2));   // no job: straight out

        var spec = rig.Job();
        rig.Begin(spec);
        t.EntityState.Send(new DpsItem<D.EntityState>(new D.EntityState { EntityId = 6 }));
        t.Fire.Send(new DpsItem<D.Fire>(new D.Fire { ShooterId = 2 }));
        Assert.Equal(0, rig.Client.ArrivalsDuring(30));   // held

        var end = rig.Gate.EndJob(spec, RenderEnd.Stopped, "user stopped");
        Assert.True(rig.Complete(end));
        Assert.Null(ResultOf(end));
        Assert.True(rig.Client.Until(() => rig.Client.Counted == 2, rig.Station.Step), "the held writes were not flushed when the job ended");
        Assert.Contains(rig.Client.Jobs, j => j.State == D.RenderState.RenderStateCancelled && j.Reason == "user stopped");
        t.EntityState.Send(new DpsItem<D.EntityState>(new D.EntityState { EntityId = 7 }));
        Assert.True(rig.Client.Until(() => rig.Client.Counted == 3));   // and the gate is open again
    }

    [Fact]
    public void A_step_that_arrives_while_a_frame_is_rendering_waits_for_the_next_frame_and_a_frame_may_own_several_steps()
    {
        using var rig = new RenderRig();
        var spec = rig.Job();
        rig.Begin(spec);
        var f0 = rig.Gate.Frame(Frame(spec, 0, 0));
        Assert.True(rig.Client.Until(() => rig.Client.Requests.Count == 1, rig.Station.Step));
        rig.Station.Send(new HrotStep(11, 0.05f, 0.05, 1f, 1));
        rig.Station.Send(new HrotStep(12, 0.05f, 0.10, 1f, 1));
        rig.Station.Send(new HrotStep(13, 0.05f, 0.15, 1f, 1));
        Assert.Empty(rig.Client.Steps);   // three steps queued, none written
        rig.Client.FrameDone(rig.Client.Requests[0]);
        Assert.True(rig.Complete(f0));

        // the director went three steps (fast forward between two frames): the frame owns all three, the last carries the ack
        var f1 = rig.Gate.Frame(Frame(spec, 1, 3));
        Assert.True(rig.Client.Until(() => rig.Client.Requests.Count == 2, rig.Station.Step));
        Assert.Equal(new long[] { 11, 12, 13 }, rig.Client.Steps.Select(s => s.StepSeq).ToArray());
        Assert.Equal(13, rig.Client.Requests[1].StepSeq);
        Assert.All(rig.Client.Steps, s => Assert.True(s.AckRequired));
        rig.Client.FrameDone(rig.Client.Requests[1]);
        Assert.True(rig.Complete(f1));
    }

    [Fact]
    public void A_frame_waits_for_the_steps_it_owns_when_the_director_is_ahead_of_the_gateway_and_faults_after_30_seconds()
    {
        using var rig = new RenderRig();
        var spec = rig.Job();
        rig.Begin(spec);
        var f0 = rig.Gate.Frame(Frame(spec, 0, 0));
        Assert.True(rig.Client.Until(() => rig.Client.Requests.Count == 1, rig.Station.Step));
        rig.Client.FrameDone(rig.Client.Requests[0]);
        Assert.True(rig.Complete(f0));

        var f1 = rig.Gate.Frame(Frame(spec, 1, 1));   // the director says one step; the gateway has seen none
        for (int i = 0; i < 5; i++)
        {
            rig.Station.Step();
        }

        rig.Client.Poll();
        Assert.False(f1.IsCompleted);
        Assert.Single(rig.Client.Requests);   // not sent: its step has not arrived

        rig.Station.Send(new HrotStep(21, 0.05f, 0.05, 1f, 1));   // it arrives
        Assert.True(rig.Client.Until(() => rig.Client.Requests.Count == 2, rig.Station.Step));
        Assert.Equal(21, rig.Client.Requests[1].StepSeq);

        // and when it never arrives
        rig.Client.FrameDone(rig.Client.Requests[1]);
        Assert.True(rig.Complete(f1));
        var f2 = rig.Gate.Frame(Frame(spec, 2, 2));
        rig.Station.Step();
        rig.Clock.Advance(31);
        Assert.True(rig.Complete(f2));
        Assert.True(f2.IsFaulted);
        Assert.Contains("waited", f2.Exception!.GetBaseException().Message);
    }

    // ---- (c) faults ------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5000, 1080, 2, false, "5000 px wide")]
    [InlineData(1920, 3000, 2, false, "3000 px high")]
    [InlineData(1920, 1080, 8, false, "supersamples 8x")]
    [InlineData(1920, 1080, 2, true, "soundtrack")]
    public void A_job_that_does_not_fit_the_client_faults_at_once_with_the_limit_named(int width, int height, int supersample, bool audio, string limit)
    {
        using var rig = new RenderRig(connect: false);
        var sample = rig.Client.Sample;
        rig.Client.Sample = sample with { MaxWidth = 3840, MaxHeight = 2160, MaxSupersample = 4, AudioFormats = audio ? 0u : 3u };
        Assert.True(rig.Connect());
        var task = rig.Gate.BeginJob(rig.Job(audio, width, height, supersample));
        Assert.True(rig.Complete(task));
        Assert.True(task.IsFaulted);
        Assert.Contains(limit, task.Exception!.GetBaseException().Message);
        Assert.False(rig.Service.JobActive());   // nothing started: no RenderJob, no hold
        Assert.Empty(rig.Client.Jobs);
    }

    [Fact]
    public void A_folder_outside_the_render_root_a_missing_root_and_a_client_that_is_not_ready_fault_the_job_at_once()
    {
        using (var rig = new RenderRig())
        {
            var outside = rig.Gate.BeginJob(rig.Job(dir: Path.Combine(Path.GetTempPath(), "elsewhere", "job")));
            Assert.True(rig.Complete(outside));
            Assert.Contains("outside the render root", outside.Exception!.GetBaseException().Message);
            var dotdot = rig.Gate.BeginJob(rig.Job(dir: Path.Combine(Root, "..", "escape")));
            Assert.True(rig.Complete(dotdot));
            Assert.Contains("outside the render root", dotdot.Exception!.GetBaseException().Message);
        }

        using (var rig = new RenderRig(root: " "))
        {
            var task = rig.Gate.BeginJob(rig.Job());
            Assert.True(rig.Complete(task));
            Assert.Contains("dps.renderRoot", task.Exception!.GetBaseException().Message);
        }

        using (var rig = new RenderRig(connect: false))
        {
            rig.Client.Sample = rig.Client.Sample with { Ready = false };
            rig.Client.AnnounceUntil(() => false, rig.Station.Step, timeoutMs: 300);
            Assert.False(rig.Gate.CanFilm);   // announced but not ready (loading its world)
        }
    }

    [Fact]
    public void Without_a_ready_signal_the_job_faults_after_180_seconds_of_wall_time_and_never_before()
    {
        using var rig = new RenderRig();
        var spec = rig.Job();
        var begin = rig.Gate.BeginJob(spec);
        Assert.True(rig.Client.Until(() => rig.Client.Jobs.Count > 0, rig.Station.Step));
        Assert.Equal(D.RenderState.RenderStateStarting, rig.Client.Jobs[0].State);
        rig.Clock.Advance(179);
        rig.Station.Step();
        Assert.False(begin.IsCompleted);
        rig.Clock.Advance(2);
        Assert.True(rig.Complete(begin));
        Assert.True(begin.IsFaulted);
        Assert.Contains("180", begin.Exception!.GetBaseException().Message);
        // the director ends the failed job: the barrier opens
        Assert.True(rig.Service.JobActive());
        var end = rig.Gate.EndJob(spec, RenderEnd.Failed, begin.Exception!.GetBaseException().Message);
        Assert.True(rig.Complete(end));
        Assert.False(rig.Service.JobActive());
        Assert.True(rig.Client.Until(() => rig.Client.Jobs.Any(j => j.State == D.RenderState.RenderStateFailed && j.Reason.Contains("180")), rig.Station.Step));
    }

    [Fact]
    public void A_frame_done_that_failed_faults_the_frame_with_its_reason_and_the_job_stays_ended_for_the_next_frames()
    {
        using var rig = new RenderRig();
        var spec = rig.Job();
        rig.Begin(spec);
        var f0 = rig.Gate.Frame(Frame(spec, 0, 0));
        Assert.True(rig.Client.Until(() => rig.Client.Requests.Count == 1, rig.Station.Step));
        rig.Client.FrameDone(rig.Client.Requests[0], D.FrameStatus.FrameStatusFailed, "out of video memory");
        Assert.True(rig.Complete(f0));
        Assert.True(f0.IsFaulted);
        Assert.Equal("out of video memory", f0.Exception!.GetBaseException().Message);

        var f1 = rig.Gate.Frame(Frame(spec, 1, 1));
        Assert.True(rig.Complete(f1));
        Assert.True(f1.IsFaulted);   // a failed job takes no more frames
        Assert.Equal("out of video memory", f1.Exception!.GetBaseException().Message);
    }

    [Fact]
    public void Thirty_seconds_without_a_frame_done_faults_the_frame_and_a_frame_done_for_another_frame_or_job_is_ignored()
    {
        using var rig = new RenderRig();
        var spec = rig.Job();
        rig.Begin(spec);
        var f0 = rig.Gate.Frame(Frame(spec, 0, 0));
        Assert.True(rig.Client.Until(() => rig.Client.Requests.Count == 1, rig.Station.Step));
        var request = rig.Client.Requests[0];
        rig.Client.FrameDone(request with { FrameIndex = 5 });
        rig.Client.FrameDone(request with { JobId = "00000000000000000000000000000000" });
        rig.Clock.Advance(29);
        for (int i = 0; i < 20; i++)
        {
            rig.Station.Step();
            Thread.Sleep(3);
        }

        Assert.False(f0.IsCompleted);   // neither answer was for this frame
        rig.Clock.Advance(2);
        Assert.True(rig.Complete(f0));
        Assert.True(f0.IsFaulted);
        Assert.Contains("30", f0.Exception!.GetBaseException().Message);
    }

    [Fact]
    public void Lost_liveliness_faults_the_pending_frame_and_the_job_that_follows()
    {
        using var rig = new RenderRig();
        var spec = rig.Job();
        rig.Begin(spec);
        var f0 = rig.Gate.Frame(Frame(spec, 0, 0));
        Assert.True(rig.Client.Until(() => rig.Client.Requests.Count == 1, rig.Station.Step));
        rig.Client.Vanish();
        Assert.True(rig.Complete(f0), "the lost client never faulted the pending frame");
        Assert.True(f0.IsFaulted);
        Assert.Contains("lost", f0.Exception!.GetBaseException().Message);
        Assert.False(rig.Gate.CanFilm);
    }

    [Fact]
    public void Lost_liveliness_during_the_warm_up_faults_the_begin()
    {
        using var rig = new RenderRig();
        var begin = rig.Gate.BeginJob(rig.Job());
        Assert.True(rig.Client.Until(() => rig.Client.Jobs.Count > 0, rig.Station.Step));
        rig.Client.Vanish();
        Assert.True(rig.Complete(begin));
        Assert.True(begin.IsFaulted);
        Assert.Contains("lost", begin.Exception!.GetBaseException().Message);
    }

    [Fact]
    public void A_failed_job_result_at_the_end_faults_the_end_with_its_reason_and_a_silent_client_faults_it_after_30_seconds()
    {
        using var rig = new RenderRig();
        var spec = rig.Job(audio: true);
        string id = rig.Begin(spec);
        var end = rig.Gate.EndJob(spec, RenderEnd.Ok, null);
        Assert.True(rig.Client.Until(() => rig.Client.Jobs.Any(j => j.State == D.RenderState.RenderStateDone), rig.Station.Step));
        rig.Client.JobResult(id, D.FrameStatus.FrameStatusFailed, reason: "the mixer ran dry");
        Assert.True(rig.Complete(end));
        Assert.True(end.IsFaulted);
        Assert.Equal("the mixer ran dry", end.Exception!.GetBaseException().Message);

        using var quiet = new RenderRig();
        var spec2 = quiet.Job(audio: true);
        quiet.Begin(spec2);
        var end2 = quiet.Gate.EndJob(spec2, RenderEnd.Ok, null);
        quiet.Station.Step();
        quiet.Clock.Advance(29);
        quiet.Station.Step();
        Assert.False(end2.IsCompleted);
        quiet.Clock.Advance(2);
        Assert.True(quiet.Complete(end2));
        Assert.True(end2.IsFaulted);
        Assert.Contains("RenderJobResult", end2.Exception!.GetBaseException().Message);
    }

    [Fact]
    public void A_job_with_no_client_soundtrack_ends_with_no_track_and_a_job_that_never_started_ends_quietly()
    {
        using var rig = new RenderRig();
        var spec = rig.Job(audio: false);
        rig.Begin(spec);
        var end = rig.Gate.EndJob(spec, RenderEnd.Ok, null);
        Assert.True(rig.Complete(end));
        Assert.Null(ResultOf(end));

        var never = rig.Gate.EndJob(rig.Job(), RenderEnd.Failed, "refused at begin");
        Assert.True(rig.Complete(never));
        Assert.Null(ResultOf(never));
    }

    [Fact]
    public void A_frame_out_of_index_order_is_refused_and_a_second_job_while_one_runs_is_refused()
    {
        using var rig = new RenderRig();
        var spec = rig.Job();
        rig.Begin(spec);
        var second = rig.Gate.BeginJob(rig.Job());
        Assert.True(rig.Complete(second));
        Assert.Contains("already running", second.Exception!.GetBaseException().Message);

        var skipped = rig.Gate.Frame(Frame(spec, 3, 0));
        Assert.True(rig.Complete(skipped));
        Assert.Contains("out of order", skipped.Exception!.GetBaseException().Message);
    }

    // ---- (d) no render client, and the steady state outside a job --------------------------------------------------------------------------

    [Fact]
    public void With_no_render_client_nothing_can_film_and_normal_stepping_is_unchanged()
    {
        using var rig = new RenderRig(connect: false);
        var steps = new List<D.ClockStep>();
        rig.Station.Transport.ClockStep.Tap = steps.Add;
        Assert.False(rig.Gate.CanFilm);
        rig.Station.Send(new HrotStep(31, 0.05f, 1.0, 1f, 1));
        rig.Station.Send(new HrotStep(32, 0.05f, 1.05, 1f, 1));
        var written = steps.ToArray();
        Assert.Equal(new long[] { 31, 32 }, written.Select(s => s.StepSeq).ToArray());   // written at once, in order
        Assert.All(written, s => Assert.Equal((false, 0u), (s.AckRequired, s.Samples)));

        var begin = rig.Gate.BeginJob(rig.Job());
        Assert.True(rig.Complete(begin));
        Assert.True(begin.IsFaulted);   // no client: a refusal with the reason, nothing started
        Assert.Contains("no render client", begin.Exception!.GetBaseException().Message);
        Assert.False(rig.Service.JobActive());
    }

    [Fact]
    public void Outside_a_job_a_frame_costs_the_render_machinery_zero_bytes()
    {
        using var rig = new RenderRig();
        var t = rig.Station.Transport;
        var render = t.Seats[0].Render!;
        using var participant = new CycloneDDS.Runtime.DdsParticipant(rig.Station.Domain, defaultPartition: "zero-bytes-baseline");
        using var plain = new DpsChannel<D.EntityState>(participant);   // the same write with no barrier behind it
        var item = new DpsItem<D.EntityState>(new D.EntityState { EntityId = 3 });
        for (int i = 0; i < 100; i++)
        {
            rig.Service.Pump();
            render.Pump();
            t.EntityState.Send(item);
            plain.Send(item);
        }

        long A(Action body)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 300; i++)
            {
                body();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, A(() => rig.Service.Pump()));   // the queued-call loop, the client table, the deadlines: nothing
        long held = A(() => t.EntityState.Send(item));
        long baseline = A(() => plain.Send(item));
        Assert.True(held <= baseline, $"the barrier's hold check allocates: {held} B against {baseline} B for the same write with none");
        // the endpoint's three reads are the library's takes; they are measured against the seat's existing client-world read
        long endpoint = A(() => render.Pump());
        Assert.Equal(0, endpoint);
    }

    [Fact]
    public void The_render_seat_and_root_are_configuration_validated_and_read_from_the_file()
    {
        Assert.Equal(1, new DpsSettings().RenderSeat);
        Assert.Equal(string.Empty, new DpsSettings().RenderRoot);
        foreach (var bad in new[] { new DpsSettings { RenderSeat = 0 }, new DpsSettings { Seats = 2, RenderSeat = 3 }, new DpsSettings { RenderSeat = 2 } })
        {
            var ex = Assert.Throws<ArgumentException>(bad.Validate);
            Assert.Contains("dps.renderSeat", ex.Message);
            Assert.Contains("dps.renderSeat", new DdsPlayerPlugin().Check(bad, new PluginHostContext(_ => { })));
        }

        new DpsSettings { Seats = 4, RenderSeat = 4 }.Validate();
        var parsed = GatewayConfigurationFile.Parse("""{ "dps": { "seats": 3, "renderSeat": 2, "renderRoot": "/data/render" } }""", "test", SideRegistry.Empty.With(new DpsSide()));
        Assert.Empty(parsed.Errors);
        var settings = (DpsSettings)parsed.Configuration!.SettingsOf("dps")!;
        Assert.Equal((3, 2, "/data/render"), (settings.Seats, settings.RenderSeat, settings.RenderRoot));
    }

    [Fact]
    public void Ending_the_host_fails_whatever_the_director_waits_for_and_opens_the_barrier()
    {
        var rig = new RenderRig();
        var spec = rig.Job();
        var begin = rig.Gate.BeginJob(spec);
        Assert.True(rig.Client.Until(() => rig.Client.Jobs.Count > 0, rig.Station.Step));
        Assert.True(rig.Service.JobActive());
        rig.Service.Dispose();
        Assert.True(begin.IsFaulted);
        Assert.Contains("ended", begin.Exception!.GetBaseException().Message);
        Assert.False(rig.Service.JobActive());
        rig.Dispose();
    }

    // ---- D-12: the render seat's counted topics -------------------------------------------------------------------------------------------

    [Fact]
    public void Samples_count_the_world_plus_the_render_seats_own_topics_only()
    {
        foreach (int renderSeat in new[] { 1, 2 })
        {
            using var rig = new RenderRig(seats: 2, renderSeat: renderSeat);
            var spec = rig.Job();
            rig.Begin(spec);
            var t = rig.Station.Transport;
            var f0 = rig.Gate.Frame(Frame(spec, 0, 0));
            Assert.True(rig.Client.Until(() => rig.Client.Requests.Count == 1, rig.Station.Step));
            t.EntityState.Send(new DpsItem<D.EntityState>(new D.EntityState { EntityId = 1 }));   // the world: counted
            t.Seats[0].PlayerSlot.Send(new DpsItem<D.PlayerSlot>(new D.PlayerSlot { Reason = "seat 1", Offers = new List<D.Offer>() }));
            t.Seats[1].PlayerSlot.Send(new DpsItem<D.PlayerSlot>(new D.PlayerSlot { Reason = "seat 2", Offers = new List<D.Offer>() }));
            t.Seats[1].OwnUnit.Send(new DpsItem<D.OwnUnit>(new D.OwnUnit { UseLabel = string.Empty, Ammo = new List<D.AmmoEntry>() }));
            rig.Station.Send(new HrotStep(41, 0.05f, 0.05, 1f, 1));
            Assert.Equal(0, rig.Client.ArrivalsDuring(20));   // every seat's writes are held in the job
            rig.Client.FrameDone(rig.Client.Requests[0]);
            Assert.True(rig.Complete(f0));
            rig.Client.Reset();

            var f1 = rig.Gate.Frame(Frame(spec, 1, 1));
            Assert.True(rig.Client.Until(() => rig.Client.Steps.Count == 1, rig.Station.Step));
            uint samples = rig.Client.Steps[0].Samples;
            // the world instance + the render seat's slot (+ its unit when that is seat 2): the other seat's slot is not a topic of this client's partition
            Assert.Equal(renderSeat == 1 ? 2u : 3u, samples);
            rig.Client.FrameDone(rig.Client.Requests[1]);
            Assert.True(rig.Complete(f1));
        }
    }
}

internal static class DpsRenderTestExtensions
{
    /// <summary>True while the service has a job between Starting and its end.</summary>
    public static bool JobActive(this DpsHostService service) => service.Render.JobActive;
}
