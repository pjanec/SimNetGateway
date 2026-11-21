using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Time.Controllers;
using Hrot.Common.Infrastructure;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Plugin.DdsPlayer;
using SimNetGateway.Plugin.DdsPlayer.Tests;
using D = SimNetGateway.Protocol.DdsPlayer;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// T-050 through the host: HROT's linked-client film contract (<see cref="ILinkedClient"/>) answered by <see cref="DpsLinkedClient"/> over the real dps plugin, HROT's REAL
/// <see cref="MasterSyncController"/> making the steps, and a fake render client over DDS. The capability offers the client to the node's registry and withdraws it.
/// </summary>
public class DpsRenderHostTests
{
    private const int Domain = 99;   // HrotHost 96..99 (see DpsIntentRateTests)
    private const long BaseTicks = 639_000_000_000_000_000L;
    private const long Frame60 = TimeSpan.TicksPerSecond / 60;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "dps-render-host-root");

    private sealed class Rig : IDisposable
    {
        private long _ticks = BaseTicks;

        public Rig(string partition, string? root = null)
        {
            Wall = new ManualClock();
            var plugin = new CountingDdsPlugin();
            plugin.Inner.WallClock = Wall;
            H = new PackHarness(dis: false, plugins: [plugin], configure: c => c.WithSide(DpsSide.SideKey, new DpsSettings { Domain = Domain, Partition = partition, RenderRoot = root ?? Root }));
            H.WallTicksSource = () => _ticks;
            Controller = new MasterSyncController(H.World.Bus, tickSource: () => _ticks, startPaused: true);
            H.TimeController = Controller;
            Service = (DpsHostService)H.HostServices.Of("dps")!;
            Client = new DpsLinkedClient(() => H.HostServices.Of("dps") as IClientWorldGate, () => H.HostServices.Of("dps") as IRenderGate);
            Fake = new FakeRenderClient((uint)Domain, partition);
            Frame();
            Frame();   // the runtime starts, the boot anchor is read
            Assert.True(Fake.AnnounceUntil(() => Client.CanFilm, Frame), "the render client was never announced");
        }

        public ManualClock Wall { get; }

        public PackHarness H { get; }

        public MasterSyncController Controller { get; }

        public DpsHostService Service { get; }

        public DpsLinkedClient Client { get; }

        public FakeRenderClient Fake { get; }

        /// <summary>One host frame: time passes, the controller publishes, the bus is swapped as the kernel does, the gateway's frame runs.</summary>
        public void Frame()
        {
            _ticks += Frame60;
            Controller.Update();
            H.World.Bus.SwapBuffers();
            H.Step();
        }

        public bool Complete(Task task, int timeoutMs = 6000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && sw.ElapsedMilliseconds < timeoutMs)
            {
                Frame();
                Fake.Poll();
                Thread.Sleep(2);
            }

            return task.IsCompleted;
        }

        public FilmJob Job(FilmSound sound = FilmSound.Client) => new(Guid.NewGuid(), FilmJobKind.Reel, "city fight", 30, 1920, 1080, 2, Path.Combine(Root, "frames"), sound);

        public string Begin(FilmJob job)
        {
            var begin = Client.BeginFilm(job);
            string id = job.JobId.ToString("N");
            Assert.True(Fake.Until(() => Fake.Jobs.Any(j => j.JobId == id), Frame), "the RenderJob never reached the client");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!begin.IsCompleted && sw.ElapsedMilliseconds < 6000)
            {
                Fake.Announce(id);   // the client says it is warmed up for the job
                Frame();
                Fake.Poll();
                Thread.Sleep(3);
            }

            Assert.True(begin.IsCompletedSuccessfully);
            Fake.Reset();
            return id;
        }

        public void Dispose()
        {
            Fake.Dispose();
            H.Dispose();
        }
    }

    private static T ResultOf<T>(Task<T> task) => task.Result;

    private static FilmFrame Film(FilmJob job, int index, FilmFrameKind kind, long step, FilmCamera? camera = null, float fade = 0f, FilmSubtitle? subtitle = null, IReadOnlyList<FilmGraphic>? graphics = null,
        string[]? on = null, string[]? off = null) =>
        new(job.JobId, index, kind, 12.0 + step * 0.05, 0.5, step, camera ?? default, kind == FilmFrameKind.Scene ? 1920 : 0, kind == FilmFrameKind.Scene ? 1080 : 0, kind == FilmFrameKind.Scene ? 2 : 1,
            kind == FilmFrameKind.Scene ? Path.Combine(Root, "frames", $"{index:D6}.png") : string.Empty, subtitle, graphics ?? Array.Empty<FilmGraphic>(), fade, on ?? Array.Empty<string>(), off ?? Array.Empty<string>());

    private static FilmCamera Camera(Random rnd)
    {
        var forward = Vector3.Normalize(new Vector3((float)rnd.NextDouble() + 0.2f, (float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5) * 0.4f));
        var worldUp = Vector3.UnitZ - Vector3.Dot(Vector3.UnitZ, forward) * forward;
        var side = Vector3.Cross(Vector3.Normalize(worldUp), forward);
        float roll = (float)(rnd.NextDouble() * 1.0 - 0.5);
        var up = Vector3.Normalize(Vector3.Normalize(worldUp) * MathF.Cos(roll) + side * MathF.Sin(roll));
        return new FilmCamera(new Vector3(100 + (float)rnd.NextDouble(), 200, 30), forward, up, 55f);
    }

    // ---- (a) a reel through HROT's contract -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_reel_through_the_linked_client_maps_every_frame_field_and_pairs_each_frame_with_the_real_controllers_steps()
    {
        using var rig = new Rig("render-host-1");
        var rnd = new Random(11);
        // the editor has been running: the controller's frame counter is far from the director's step count (which restarts at every reel item)
        rig.Controller.SnapAndPause(new GlobalTime { FrameNumber = 4000, TotalTime = 12.0, UnscaledTotalTime = 12.0, TotalWallTicks = BaseTicks, TimeScale = 1f });
        rig.Frame();
        rig.Frame();
        var job = rig.Job();
        string id = rig.Begin(job);

        // a world of five men moving: every director step the gateway gathers them
        var men = Enumerable.Range(0, 5).Select(i => rig.H.AddHrotEntity(2000 + i, new Vector3(10 * i, 0, 0), "man" + i)).ToList();
        for (int i = 0; i < 4; i++)
        {
            rig.Frame();
        }

        rig.Fake.Poll();
        rig.Fake.Reset();   // the client has presented everything before the job's frames

        var graphic = new FilmGraphic(FilmGraphicKind.Route, [new Vector3(1, 2, 3), new Vector3(4, 5, 6)], 7.5f, "route", 0xFFD23Cu, 0.625f);
        var subtitle = new FilmSubtitle("hello", FilmSubtitlePosition.Top, 0.5f);
        // (kind, the director's step count, camera?) - Repeat and Silence frames own no step
        var plan = new (FilmFrameKind Kind, long Step)[]
        {
            (FilmFrameKind.Scene, 0), (FilmFrameKind.Scene, 1), (FilmFrameKind.Repeat, 1), (FilmFrameKind.Scene, 2), (FilmFrameKind.Silence, 2), (FilmFrameKind.Scene, 3),
        };
        var cameras = new List<FilmCamera>();
        var frames = new List<FilmFrame>();
        var controllerIds = new List<long>();
        for (int i = 0; i < plan.Length; i++)
        {
            var cam = plan[i].Kind == FilmFrameKind.Silence ? default : Camera(rnd);
            cameras.Add(cam);
            if (i > 0 && plan[i].Step != plan[i - 1].Step)
            {
                // the director takes a step: HROT's master publishes the advance order, the gateway reads it
                rig.Controller.Step(0.05f);
                controllerIds.Add(rig.Controller.GetCurrentState().FrameNumber);
                rig.Frame();
                rig.Frame();
                men.ForEach(m => rig.H.Move(m, new Vector3(10 * men.IndexOf(m) + plan[i].Step, 0, 0)));
                rig.Frame();
            }

            var film = Film(job, i, plan[i].Kind, plan[i].Step, cam, fade: i == 3 ? 0.75f : 0f, subtitle: i == 1 ? subtitle : null, graphics: i == 1 ? [graphic] : null,
                on: i == 1 ? ["Buildings"] : null, off: i == 1 ? ["Grid", "Labels"] : null);
            frames.Add(film);
            var task = rig.Client.Frame(film);
            Assert.True(rig.Fake.Until(() => rig.Fake.Requests.Count == i + 1, rig.Frame), $"frame {i} never reached the client");
            Assert.False(task.IsCompleted);
            rig.Fake.FrameDone(rig.Fake.Requests[i]);
            Assert.True(rig.Complete(task));
            Assert.True(task.IsCompletedSuccessfully);
            rig.Fake.Reset();
        }

        var seen = rig.Fake.Requests;
        Assert.Equal(Enumerable.Range(0, plan.Length), seen.Select(r => r.FrameIndex));
        for (int i = 0; i < plan.Length; i++)
        {
            var r = seen[i];
            var f = frames[i];
            Assert.Equal(id, r.JobId);
            Assert.Equal(plan[i].Kind switch { FilmFrameKind.Scene => D.FrameKind.FrameKindScene, FilmFrameKind.Repeat => D.FrameKind.FrameKindRepeat, _ => D.FrameKind.FrameKindSilence }, r.Kind);
            Assert.Equal(f.SimTime, r.SimTime, 9);
            Assert.Equal(0.5f, r.FilmRate);
            Assert.Equal(i == 3 ? 0.75f : 0f, r.Fade);
            Assert.Equal(f.Camera.FovDeg, r.FovDeg);
            Assert.Equal(f.Camera.Position.X, r.Position.X, 4);
            var q = new Quaternion(r.Orientation.X, r.Orientation.Y, r.Orientation.Z, r.Orientation.W);
            if (plan[i].Kind != FilmFrameKind.Silence)
            {
                Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitX, q), f.Camera.Forward), 0, 1e-5f);
                Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, q), f.Camera.Up), 0, 1e-5f);
            }

            Assert.Equal(plan[i].Kind == FilmFrameKind.Scene ? $"frames/{i:D6}.png" : string.Empty, r.File);
            Assert.Equal((plan[i].Kind == FilmFrameKind.Scene ? 1920 : 1920, plan[i].Kind == FilmFrameKind.Scene ? 1080 : 1080), (r.Width, r.Height));   // a frame with no size takes the job's
        }

        // the overlay of frame 1: subtitle, graphic (colour 0xRRGGBB, opacity apart), layers
        Assert.Equal(("hello", D.SubtitlePosition.SubtitlePositionTop, 0.5f), (seen[1].Subtitle, seen[1].SubtitlePosition, seen[1].SubtitleAlpha));
        var g = Assert.Single(seen[1].Graphics);
        Assert.Equal((D.FilmGraphicKind.FilmGraphicKindRoute, 7.5f, "route", 0xFFD23Cu, 0.625f, 2), (g.Kind, g.Radius, g.Text, g.ColourRgb, g.Alpha, g.Points.Count));
        Assert.Equal(new[] { "Buildings" }, seen[1].LayersOn.Select(l => l.Name));
        Assert.Equal(new[] { "Grid", "Labels" }, seen[1].LayersOff.Select(l => l.Name));
        Assert.Empty(seen[0].Graphics);

        // the pairing: HROT's director counts its own steps in the item (0, 1, 2, 3); the clock's FrameID is the controller's own frame number - a different, consecutive sequence
        Assert.Equal(new long[] { 0, controllerIds[0], controllerIds[0], controllerIds[1], controllerIds[1], controllerIds[2] }, seen.Select(r => r.StepSeq).ToArray());
        Assert.Equal(controllerIds, rig.Fake.Steps.Select(s => s.StepSeq).ToList());
        Assert.Equal(new[] { 4001L, 4002L, 4003L }, controllerIds);   // MEASURED: the controller's own frame number, consecutive per step - and not the director's 1, 2, 3
        Assert.NotEqual(new long[] { 1, 2, 3 }, controllerIds);
        Assert.All(rig.Fake.Steps, s => Assert.True(s.AckRequired));
    }

    [Fact]
    public void ClockStep_Samples_counts_what_the_real_gather_wrote_for_a_moving_world_and_the_client_counts_the_same()
    {
        using var rig = new Rig("render-host-2");
        var job = rig.Job(FilmSound.Hrot);
        rig.Begin(job);
        var men = Enumerable.Range(0, 6).Select(i => rig.H.AddHrotEntity(3000 + i, new Vector3(10 * i, 0, 0), "m" + i)).ToList();
        var rnd = new Random(5);
        var first = rig.Client.Frame(Film(job, 0, FilmFrameKind.Scene, 0, Camera(rnd)));
        Assert.True(rig.Fake.Until(() => rig.Fake.Requests.Count == 1, rig.Frame));
        rig.Fake.FrameDone(rig.Fake.Requests[0]);
        Assert.True(rig.Complete(first));
        rig.Fake.Poll();
        rig.Fake.Reset();

        int checkedSteps = 0;
        for (int step = 1; step <= 6; step++)
        {
            // the director's step: the world moves (a random subset of the men), the gateway gathers
            foreach (var m in men.Where(_ => rnd.Next(3) != 0))
            {
                rig.H.Move(m, new Vector3(10 * men.IndexOf(m) + step * 2, step, 0));
            }

            rig.Controller.Step(0.05f);
            for (int k = 0; k < 4; k++)
            {
                rig.Frame();
            }

            var task = rig.Client.Frame(Film(job, step, FilmFrameKind.Scene, step, Camera(rnd)));
            Assert.True(rig.Fake.Until(() => rig.Fake.Requests.Count == step + 1, rig.Frame), $"frame {step}");
            var clockStep = rig.Fake.Steps[^1];
            Assert.True(rig.Fake.Until(() => rig.Fake.Counted >= (int)clockStep.Samples), $"step {step}: {rig.Fake.Counted} of {clockStep.Samples} ({rig.Fake.CountedDetail()})");
            Assert.Equal((int)clockStep.Samples, rig.Fake.Counted);
            Assert.Equal(0, rig.Fake.ArrivalsDuring(15));
            rig.Fake.FrameDone(rig.Fake.Requests[step]);
            Assert.True(rig.Complete(task));
            rig.Fake.Reset();
            checkedSteps++;
        }

        Assert.Equal(6, checkedSteps);
        Assert.True(rig.Fake.Steps.Any(s => s.Samples > 0), "the real gather wrote nothing countable in any step");
    }

    [Fact]
    public void A_failed_frame_is_a_faulted_task_with_the_clients_reason_and_the_end_returns_the_track_or_null()
    {
        using var rig = new Rig("render-host-3");
        var job = rig.Job();
        string id = rig.Begin(job);
        var bad = rig.Client.Frame(Film(job, 0, FilmFrameKind.Scene, 0, Camera(new Random(1))));
        Assert.True(rig.Fake.Until(() => rig.Fake.Requests.Count == 1, rig.Frame));
        rig.Fake.FrameDone(rig.Fake.Requests[0], D.FrameStatus.FrameStatusFailed, "no GPU");
        Assert.True(rig.Complete(bad));
        Assert.Equal("no GPU", bad.Exception!.GetBaseException().Message);

        var end = rig.Client.EndFilm(job, new FilmJobOutcome(FilmJobEnd.Failed, "no GPU"));
        Assert.True(rig.Complete(end));
        Assert.Null(ResultOf(end));   // a failed job has no track to wait for
        Assert.True(rig.Fake.Until(() => rig.Fake.Jobs.Any(j => j.JobId == id && j.State == D.RenderState.RenderStateFailed && j.Reason == "no GPU"), rig.Frame));

        // a finished job's track comes back as a FilmTrack with the full path
        var job2 = rig.Job();
        string id2 = rig.Begin(job2);
        var end2 = rig.Client.EndFilm(job2, FilmJobOutcome.Ok);
        Assert.True(rig.Fake.Until(() => rig.Fake.Jobs.Any(j => j.JobId == id2 && j.State == D.RenderState.RenderStateDone), rig.Frame));
        rig.Fake.JobResult(id2, D.FrameStatus.FrameStatusWritten, "frames/" + id2 + ".wav", 144_000);
        Assert.True(rig.Complete(end2));
        Assert.Equal(new FilmTrack(Path.GetFullPath(Path.Combine(Root, "frames", id2 + ".wav")), 144_000, 48000, 2), ResultOf(end2));
    }

    [Fact]
    public void A_misfit_job_faults_the_begin_and_a_job_with_no_render_client_cannot_film()
    {
        using var rig = new Rig("render-host-4");
        var wide = rig.Client.BeginFilm(rig.Job() with { Width = 10000 });
        Assert.True(rig.Complete(wide));
        Assert.Contains("10000 px wide", wide.Exception!.GetBaseException().Message);

        var host = new DpsLinkedClient(() => null, () => null);   // the dps side does not run
        Assert.False(host.CanFilm);
        Assert.True(host.IsWorldReady(Guid.NewGuid(), out string? failure, out string? step));
        Assert.Null(failure);
        Assert.Null(step);
        Assert.True(host.BeginFilm(rig.Job()).IsFaulted);
    }

    // ---- (e) the capability offers the client and withdraws it -------------------------------------------------------------------------------

    private static HrotNodeContext ContextWith(LinkedClients? registry)
    {
        var context = (HrotNodeContext)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(HrotNodeContext));
        typeof(HrotNodeContext).GetProperty("LinkedClients")!.SetValue(context, registry);
        return context;
    }

    private static GatewayCapability Capability(bool dps, string partition = "cap-1")
    {
        var config = GatewayConfiguration.CreateDefault(1) with { Dis = null };
        if (dps)
        {
            config = config.WithSide(DpsSide.SideKey, new DpsSettings { Domain = Domain, Partition = partition });
        }

        return new GatewayCapability(config, PluginSet.Of(new DdsPlayerPlugin()), _ => { });
    }

    [Fact]
    public void The_capability_offers_its_client_to_the_nodes_registry_and_its_world_calls_reach_the_gate_and_it_is_withdrawn_at_teardown()
    {
        var registry = new LinkedClients();
        var context = ContextWith(registry);
        var capability = Capability(dps: true);
        capability.PopulateSystems(context, [], [], []);

        Assert.NotNull(capability.LinkedClient);
        Assert.Same(capability.LinkedClient, registry.Current);   // THIS client, on the node's registry
        Assert.Equal("dps", registry.Current!.Name);   // never a product name (HROT R-11009)

        // the world calls reach the T-045 gate: BeginWorld bumps the station's epoch, and nothing is ready before that epoch is published
        var service = (DpsHostService)capability.Services.Of("dps")!;
        int epoch = service.Epoch;
        var load = Guid.NewGuid();
        registry.Current.BeginWorld(load);
        Assert.Equal(epoch + 1, service.Epoch);
        Assert.False(registry.Current.IsWorldReady(load, out _, out _));
        Assert.Same(service, capability.ClientWorldGate);
        Assert.Same(service, capability.RenderGate);

        capability.Dispose();
        Assert.Null(registry.Current);   // withdrawn: the node's loads stop waiting
        Assert.Null(capability.LinkedClient);
    }

    [Fact]
    public void Teardown_leaves_a_client_another_capability_offered_since_and_a_node_whose_dps_side_does_not_run_offers_nothing()
    {
        var registry = new LinkedClients();
        var capability = Capability(dps: true, "cap-2");
        capability.PopulateSystems(ContextWith(registry), [], [], []);
        var other = new DpsLinkedClient(() => null, () => null);
        registry.Offer(other);
        capability.Dispose();
        Assert.Same(other, registry.Current);

        var none = new LinkedClients();
        var quiet = Capability(dps: false);
        quiet.PopulateSystems(ContextWith(none), [], [], []);
        Assert.Null(none.Current);
        Assert.Null(quiet.LinkedClient);
        quiet.Dispose();
    }
}
