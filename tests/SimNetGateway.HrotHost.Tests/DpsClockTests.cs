using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Time.Controllers;
using SimNetGateway.Plugin.DdsPlayer;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// DD-022 3b, 3c through the host and the dps plugin: HROT's REAL <see cref="MasterSyncController"/> (on a controlled tick source) publishes its time events on the node's bus, the host turns them into
/// pivot messages, the plugin writes <c>SimClock</c> / <c>ClockStep</c>, and a client model fed only those samples must compute the simulation time of the slave formula.
/// </summary>
public class DpsClockTests
{
    private const int Domain = 98;   // HrotHost 96..99 (see DpsIntentRateTests)
    private const long BaseTicks = 639_000_000_000_000_000L;

    private sealed class Rig : IDisposable
    {
        private readonly PackHarness _h;
        private long _ticks = BaseTicks;
        private long _frames;

        public Rig(string partition, bool startPaused = false, bool discardBoot = false)
        {
            var plugin = new CountingDdsPlugin { OnBound = t => { t.SimClock.Tap = Clocks.Add; t.ClockStep.Tap = Steps.Add; } };
            _h = new PackHarness(dis: false, plugins: [plugin], configure: c => c.WithSide(DpsSide.SideKey, new DpsSettings { Domain = Domain, Partition = partition }));
            _h.WallTicksSource = () => _ticks;
            Controller = new MasterSyncController(_h.World.Bus, tickSource: () => _ticks, startPaused: startPaused);
            _h.TimeController = Controller;
            if (discardBoot)
            {
                _h.World.Bus.SwapBuffers();
                _h.World.Bus.SwapBuffers();   // the boot anchor was published before this gateway looked: it is gone
            }

            Plugin = plugin;
            _h.Step();   // the runtime starts
            Transport = plugin.Transport!;
        }

        public PackHarness Harness => _h;

        public MasterSyncController Controller { get; }

        public CountingDdsPlugin Plugin { get; }

        public DpsTransport Transport { get; }

        public List<D.SimClock> Clocks { get; } = new();

        public List<D.ClockStep> Steps { get; } = new();

        public long Ticks => _ticks;

        /// <summary>One host frame: time passes (the controller updates, publishing its events), the bus is swapped as the kernel does, then the gateway's frame runs.</summary>
        public void Frame(long advanceTicks)
        {
            _ticks += advanceTicks;
            Controller.Update();
            _h.World.Bus.SwapBuffers();
            _h.Step();
            _frames++;
        }

        public void Dispose() => _h.Dispose();
    }

    private const long Frame60 = TimeSpan.TicksPerSecond / 60;

    /// <summary>The client's model of the clock: what a client computes from the samples alone.</summary>
    private static double ClientSim(Rig rig) => DpsClockFormula.SimTime(rig.Clocks[^1], rig.Ticks);

    [Fact]
    public void A_client_fed_only_the_published_sim_clock_equals_the_slave_formula_through_seeded_random_play_pause_step_scale_snap_and_jump()
    {
        int totalSteps = 0, totalJumps = 0, totalReanchors = 0;
        for (int seed = 1; seed <= 12; seed++)
        {
            var rnd = new Random(seed);
            using var rig = new Rig("clock-prop-" + seed, startPaused: rnd.Next(2) == 0);
            var ctl = rig.Controller;
            var expectedSteps = new List<long>();
            long lastEpoch = 0;
            int jumps = 0;
            for (int op = 0; op < 150; op++)
            {
                int writesBefore = rig.Clocks.Count;
                bool runningBefore = rig.Clocks.Count > 0 && rig.Clocks[^1].Mode == D.ClockMode.ClockModeContinuous;
                var oldClock = rig.Clocks.Count > 0 ? rig.Clocks[^1] : default;
                string what;
                switch (rnd.Next(8))
                {
                    case 0:
                        what = "pause";
                        ctl.SwitchToDeterministic(new HashSet<int>());
                        rig.Frame(Frame60);
                        rig.Frame(15_000_000);   // past the future barrier (lookahead)
                        break;
                    case 1:
                        what = "play";
                        ctl.SwitchToContinuous(rnd.Next(3) == 0 ? 0.5f + rnd.Next(4) * 0.5f : 0f);
                        rig.Frame(Frame60);
                        break;
                    case 2:
                    case 3:
                        what = "step";
                        if (ctl.GetMode() == Fdp.ModuleHost.Time.TimeMode.Deterministic && !ctl.IsPauseBarrierPending)
                        {
                            ctl.Step(0.05f);
                            expectedSteps.Add(ctl.GetCurrentState().FrameNumber);
                        }

                        rig.Frame(Frame60);
                        break;
                    case 4:
                        what = "scale";
                        rig.Frame(Frame60 * rnd.Next(1, 5));
                        writesBefore = rig.Clocks.Count;
                        oldClock = rig.Clocks[^1];
                        runningBefore = oldClock.Mode == D.ClockMode.ClockModeContinuous;
                        float newScale = 0.25f + rnd.Next(1, 16) * 0.25f;
                        ctl.SetTimeScale(newScale == ctl.GetTimeScale() ? newScale + 0.125f : newScale);   // HROT announces nothing for this
                        rig.Frame(0);
                        break;
                    case 5:
                        what = "snap";
                        ctl.SnapAndPause(new GlobalTime { FrameNumber = rnd.Next(0, 5000), TotalTime = rnd.NextDouble() * 900, UnscaledTotalTime = rnd.NextDouble() * 900, TotalWallTicks = rig.Ticks, TimeScale = 1f });
                        rig.Frame(Frame60);
                        break;
                    case 6:
                        what = "jump";
                        ctl.JumpWhileRunning(rnd.NextDouble() * 900);
                        rig.Frame(Frame60);
                        break;
                    default:
                        what = "idle";
                        rig.Frame(Frame60 * rnd.Next(1, 12));
                        break;
                }

                // the equivalence, at the instant of this frame
                double expected = ctl.GetCurrentState().TotalTime;
                var clock = rig.Clocks[^1];
                double got = ClientSim(rig);
                if (clock.Mode == D.ClockMode.ClockModeStepped)
                {
                    Assert.True(expected == got, $"seed {seed} op {op} ({what}): stepped, expected {expected:R} got {got:R}");
                }
                else
                {
                    Assert.True(Math.Abs(expected - got) < 1e-6, $"seed {seed} op {op} ({what}): continuous, expected {expected:R} got {got:R}");
                }

                if (what == "scale" && runningBefore && ctl.GetMode() == Fdp.ModuleHost.Time.TimeMode.Continuous && !ctl.IsPauseBarrierPending)
                {
                    // a running scale change: exactly one SimClock, and the formula is continuous across it
                    totalReanchors++;
                    Assert.Equal(writesBefore + 1, rig.Clocks.Count);
                    Assert.True(Math.Abs(DpsClockFormula.SimTime(oldClock, rig.Ticks) - DpsClockFormula.SimTime(clock, rig.Ticks)) < 1e-6, $"seed {seed} op {op}: the formula jumps across the scale change");
                    Assert.Equal(ctl.GetTimeScale(), clock.TimeScale);
                }

                Assert.True(clock.TimelineEpoch == ctl.TimelineEpoch, $"seed {seed} op {op} ({what}): the published epoch {clock.TimelineEpoch} is not the controller's {ctl.TimelineEpoch}");
                if (clock.TimelineEpoch != lastEpoch)
                {
                    jumps++;
                    lastEpoch = clock.TimelineEpoch;
                }
            }

            totalSteps += rig.Steps.Count;
            totalJumps += jumps;
            Assert.Equal(ctl.TimelineEpoch, jumps);   // one jump per epoch change, none besides
            Assert.Equal(expectedSteps, rig.Steps.Select(s => s.StepSeq).ToList());   // every step, once, with its frame id
            Assert.All(rig.Steps, s => Assert.Equal((false, 0u), (s.AckRequired, s.Samples)));
            for (int i = 1; i < rig.Steps.Count; i++)
            {
                if (rig.Steps[i].TimelineEpoch == rig.Steps[i - 1].TimelineEpoch)
                {
                    Assert.True(rig.Steps[i].StepSeq > rig.Steps[i - 1].StepSeq, $"seed {seed}: step ids do not grow within an epoch");
                }
            }
        }

        // the property was not vacuous: the sequences did run steps, snaps and running scale changes
        Assert.True(totalSteps > 50 && totalJumps > 30 && totalReanchors > 30, $"steps {totalSteps}, jumps {totalJumps}, re-anchors {totalReanchors}");
    }

    [Fact]
    public void A_running_scale_change_that_HROT_does_not_announce_is_one_new_anchor_from_the_controllers_own_state()
    {
        using var rig = new Rig("clock-scale");
        rig.Controller.SwitchToContinuous();
        rig.Frame(Frame60);
        rig.Frame(Frame60 * 30);   // half a second at scale 1
        int before = rig.Clocks.Count;

        rig.Controller.SetTimeScale(2f);
        rig.Frame(0);

        Assert.Equal(before + 1, rig.Clocks.Count);
        var anchor = rig.Clocks[^1];
        Assert.Equal((D.ClockMode.ClockModeContinuous, 2f), (anchor.Mode, anchor.TimeScale));
        Assert.Equal(rig.Controller.GetCurrentState().TotalTime, anchor.AnchorSimTime);
        rig.Frame(Frame60 * 6);
        Assert.Equal(before + 1, rig.Clocks.Count);   // no further sample while nothing changes: none is periodic
        Assert.Equal(rig.Controller.GetCurrentState().TotalTime, ClientSim(rig), 6);
    }

    [Fact]
    public void A_gateway_that_starts_after_the_clock_booted_anchors_from_the_controllers_state()
    {
        using var rig = new Rig("clock-late", discardBoot: true);   // the boot anchor was missed
        rig.Controller.SwitchToContinuous();
        rig.Frame(Frame60 * 12);

        Assert.NotEmpty(rig.Clocks);
        Assert.Equal(rig.Controller.GetCurrentState().TotalTime, ClientSim(rig), 6);
    }

    [Fact]
    public void A_steps_world_state_is_written_before_its_clock_step_in_the_same_tick()
    {
        using var rig = new Rig("clock-order", startPaused: true);
        rig.Harness.RegisterTemplate(100, 1);
        var e = rig.Harness.AddHrotEntity(2001, new Vector3(0, 0, 0), tkb: 100);
        rig.Frame(Frame60);
        rig.Frame(15_000_000);   // paused, barrier crossed
        var order = new List<string>();
        rig.Transport.EntityState.Tap = _ => order.Add("state");
        rig.Transport.ClockStep.Tap = _ => order.Add("step");

        // run until a frame gathers the entity: the next frame is then in the middle of the gather period (an ordinary frame would not gather)
        float x = 0;
        for (int i = 0; i < 20 && !order.Contains("state"); i++)
        {
            rig.Harness.Move(e, new Vector3(x += 1, 0, 0));
            rig.Frame(Frame60);
        }

        Assert.Contains("state", order);
        order.Clear();

        // the entity moves a metre and the step is taken in that mid-period frame: its state must still go out before the step does
        rig.Harness.Move(e, new Vector3(x + 1, 0, 0));
        rig.Controller.Step(0.05f);
        rig.Frame(Frame60);

        Assert.Contains("step", order);
        Assert.Contains("state", order);
        Assert.True(order.LastIndexOf("state") < order.IndexOf("step"), "ClockStep written before the state of its step: " + string.Join(",", order));
    }

    [Fact]
    public void The_capability_passes_the_nodes_bus_and_time_controller_and_the_high_resolution_clock_to_the_pack()
    {
        // HROT's silent-default rule: a production caller that HAS a dependency must pass it. A context with only what the services read (the rest of it is not needed to compose them).
        var context = (Hrot.Common.Infrastructure.HrotNodeContext)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Hrot.Common.Infrastructure.HrotNodeContext));
        var bus = new FdpEventBus();
        var controller = new MasterSyncController(bus, tickSource: () => BaseTicks);
        typeof(Hrot.Common.Infrastructure.HrotNodeContext).GetProperty("EventBus")!.SetValue(context, bus);
        typeof(Hrot.Common.Infrastructure.HrotNodeContext).GetProperty("TimeController")!.SetValue(context, controller);

        var services = GatewayCapability.ServicesFrom(context);

        Assert.Same(bus, services.EventBus);
        Assert.Same(controller, services.TimeController());
        Assert.Equal(Fdp.Toolkit.Time.HighResUtcClock.GetTicks, services.WallTicks);
    }

    // ---- T-048: the resident terrain as Session.Terrain ---------------------------------------------------------------------------------

    /// <summary>The host reads HROT's managed TerrainDefinition singleton (ResolvedName, ResolvedBaseHash, ResolvedOverlayHash), sends it on change and again at a world boundary.</summary>
    [Fact]
    public void The_resident_terrain_reaches_session_on_change_and_again_at_a_world_boundary()
    {
        var sessions = new List<D.Session>();
        var plugin = new CountingDdsPlugin { OnBound = t => t.Session.Tap = sessions.Add };
        using var h = new PackHarness(dis: false, plugins: [plugin], configure: c => c.WithSide(DpsSide.SideKey, new DpsSettings { Domain = Domain, Partition = "terrain-1" }));
        h.Steps(3);
        Assert.NotEmpty(sessions);
        Assert.Equal((string.Empty, string.Empty, string.Empty), (sessions[^1].Terrain, sessions[^1].TerrainBaseHash, sessions[^1].TerrainOverlayHash));   // no terrain loaded

        var terrain = new Fdp.Toolkit.Terrain.TerrainDefinition { ResolvedName = "city-1", ResolvedBaseHash = "base-1", ResolvedOverlayHash = "overlay-1" };
        h.World.SetSingletonManaged(terrain);
        h.Steps(2);
        Assert.Equal(("city-1", "base-1", "overlay-1"), (sessions[^1].Terrain, sessions[^1].TerrainBaseHash, sessions[^1].TerrainOverlayHash));

        int count = sessions.Count;
        h.Steps(3);
        Assert.Equal(count, sessions.Count);   // nothing changed: nothing sent

        terrain.ResolvedOverlayHash = "overlay-2";   // the loader commits another overlay
        h.Steps(2);
        Assert.Equal(("city-1", "base-1", "overlay-2"), (sessions[^1].Terrain, sessions[^1].TerrainBaseHash, sessions[^1].TerrainOverlayHash));

        // a world boundary rebuilds the runtime: its first Session already names the terrain
        sessions.Clear();
        Fdp.Toolkit.Replication.Services.WorldEpoch.Restored(h.World);
        h.Steps(3);
        Assert.NotEmpty(sessions);
        Assert.Equal(("city-1", "base-1", "overlay-2"), (sessions[^1].Terrain, sessions[^1].TerrainBaseHash, sessions[^1].TerrainOverlayHash));
    }

    [Fact]
    public void A_terrain_that_resolved_no_name_is_no_terrain()
    {
        var sessions = new List<D.Session>();
        var plugin = new CountingDdsPlugin { OnBound = t => t.Session.Tap = sessions.Add };
        using var h = new PackHarness(dis: false, plugins: [plugin], configure: c => c.WithSide(DpsSide.SideKey, new DpsSettings { Domain = Domain, Partition = "terrain-2" }));
        h.World.SetSingletonManaged(new Fdp.Toolkit.Terrain.TerrainDefinition { ResolvedBaseHash = "orphan" });   // nothing committed it by name
        h.Steps(3);

        Assert.Equal((string.Empty, string.Empty, string.Empty), (sessions[^1].Terrain, sessions[^1].TerrainBaseHash, sessions[^1].TerrainOverlayHash));
    }

    // ---- T-050 finding A: no Session of a new world epoch before the runtime knows the terrain -----------------------------------------------

    [Fact]
    public void The_first_session_of_a_runtime_already_names_the_resident_terrain_on_the_first_frame_and_at_a_world_boundary()
    {
        var sessions = new List<D.Session>();
        var plugin = new CountingDdsPlugin { OnBound = t => t.Session.Tap = sessions.Add };
        using var h = new PackHarness(dis: false, plugins: [plugin], configure: c => c.WithSide(DpsSide.SideKey, new DpsSettings { Domain = Domain, Partition = "terrain-first-1" }));
        h.World.SetSingletonManaged(new Fdp.Toolkit.Terrain.TerrainDefinition { ResolvedName = "farm", ResolvedBaseHash = "b1", ResolvedOverlayHash = "o1" });   // resident before the gateway's first frame
        h.Steps(3);
        Assert.NotEmpty(sessions);
        Assert.All(sessions, s => Assert.Equal(("farm", "b1", "o1"), (s.Terrain, s.TerrainBaseHash, s.TerrainOverlayHash)));   // not one Session without it

        sessions.Clear();
        Fdp.Toolkit.Replication.Services.WorldEpoch.Restored(h.World);   // a world boundary: a new runtime, a new epoch
        h.Steps(3);
        Assert.NotEmpty(sessions);
        Assert.All(sessions, s => Assert.Equal("farm", s.Terrain));
        Assert.Equal(1, sessions.Select(s => s.WorldEpoch).Distinct().Count());   // all of one epoch, the first sample included
    }

    [Fact]
    public void With_no_terrain_resident_every_session_of_the_epoch_says_so_and_none_adds_a_name_later()
    {
        var sessions = new List<D.Session>();
        var plugin = new CountingDdsPlugin { OnBound = t => t.Session.Tap = sessions.Add };
        using var h = new PackHarness(dis: false, plugins: [plugin], configure: c => c.WithSide(DpsSide.SideKey, new DpsSettings { Domain = Domain, Partition = "terrain-first-2" }));
        h.Steps(5);
        Assert.NotEmpty(sessions);
        Assert.All(sessions, s => Assert.Equal((string.Empty, string.Empty, string.Empty), (s.Terrain, s.TerrainBaseHash, s.TerrainOverlayHash)));
    }

    [Fact]
    public void A_forced_epoch_after_a_load_publishes_its_first_session_with_the_terrain_the_load_made_resident()
    {
        var sessions = new List<D.Session>();
        var plugin = new CountingDdsPlugin { OnBound = t => t.Session.Tap = sessions.Add };
        using var h = new PackHarness(dis: false, plugins: [plugin], configure: c => c.WithSide(DpsSide.SideKey, new DpsSettings { Domain = Domain, Partition = "terrain-first-3" }));
        h.Steps(3);   // a world with no terrain, epoch 1
        var service = (DpsHostService)h.HostServices.Of("dps")!;
        Assert.All(sessions, s => Assert.Equal(1u, s.WorldEpoch));

        // the load commits: the terrain is made resident, then the linked client's world gate is begun (HROT's chain order), both in one frame
        h.World.SetSingletonManaged(new Fdp.Toolkit.Terrain.TerrainDefinition { ResolvedName = "berlin", ResolvedBaseHash = "bb", ResolvedOverlayHash = string.Empty });
        service.BeginWorld(Guid.NewGuid());
        sessions.Clear();
        h.Steps(4);

        var epoch2 = sessions.Where(s => s.WorldEpoch == 2u).ToList();
        Assert.NotEmpty(epoch2);
        Assert.All(epoch2, s => Assert.Equal(("berlin", "bb"), (s.Terrain, s.TerrainBaseHash)));   // the first sample of the new epoch already names it
        Assert.DoesNotContain(sessions, s => s.WorldEpoch == 2u && s.Terrain.Length == 0);

        // the same terrain again (a reload of the same map): the epoch is announced with it, and no later sample of the epoch changes it
        sessions.Clear();
        service.BeginWorld(Guid.NewGuid());
        h.Steps(4);
        var epoch3 = sessions.Where(s => s.WorldEpoch == 3u).ToList();
        Assert.NotEmpty(epoch3);
        Assert.All(epoch3, s => Assert.Equal("berlin", s.Terrain));
    }
}
