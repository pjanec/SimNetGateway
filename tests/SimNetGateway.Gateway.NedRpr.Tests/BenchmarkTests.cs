using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// The 10k benchmark at a small scale (300 entities): a deterministic simulation, DR-thinned input (R-005), every entity
/// published to the other side, consistent result fields, and the invariants after every tick.
/// </summary>
public class BenchmarkTests
{
    private const int Entities = 300;

    private static BenchmarkResult Run(int entities = Entities, double seconds = 5, int seed = 1, double threshold = 1.0,
        double warmup = 5, Action<NedRprHost>? onHost = null, Action<NedRprHost>? afterTick = null, bool record = false) =>
        BenchmarkRunner.Run(new BenchmarkOptions
        {
            Entities = entities,
            Seconds = seconds,
            Seed = seed,
            ThresholdMetres = threshold,
            WarmupSeconds = warmup,
            Record = record,
            OnHostCreated = onHost,
            AfterTick = afterTick,
        });

    private static readonly Lazy<BenchmarkResult> s_default = new(() => Run());

    private static (long, long, long, long, long, long, long, long, long) Counts(BenchmarkResult r) =>
        (r.MessagesInNed, r.MessagesInRpr, r.MessagesOutNed, r.MessagesOutRpr, r.MessagesOutDisplay, r.DerivedWrites,
            r.MessagesInStaticRpr, r.MessagesInStaticNed, r.SetupTicks);

    [Fact]
    public void The_run_completes_with_the_requested_population_and_everything_published()
    {
        var r = s_default.Value;
        Assert.Equal("ned-rpr/default", r.Configuration);
        Assert.Equal(Entities, r.Entities);
        Assert.Equal(210, r.NedEntities);
        Assert.Equal(90, r.RprEntities);
        Assert.Equal(21, r.StaticNedEntities);
        Assert.Equal(9, r.StaticRprEntities);
        Assert.Equal(Entities, r.FinalEntities);
        Assert.Equal(r.NedEntities, r.PublishedToRpr);
        Assert.Equal(r.RprEntities, r.PublishedToNed);
        Assert.Equal(0, r.DiagnosticWarnings);
        Assert.Equal(0, r.DiagnosticErrors);
        Assert.True(r.SetupTicks >= 1);
    }

    [Fact]
    public void Ten_percent_of_each_side_is_static()
    {
        var r = s_default.Value;
        Assert.Equal(r.Entities / 10, r.StaticNedEntities + r.StaticRprEntities);
        Assert.Equal(0.7, (double)r.NedEntities / r.Entities, 9);
    }

    [Fact]
    public void The_measurement_covers_the_requested_simulated_time_and_excludes_setup_and_warm_up()
    {
        var r = s_default.Value;
        Assert.Equal(300, r.Ticks);
        Assert.Equal(5.0, r.MeasuredSeconds, 9);
        Assert.Equal(5.0, r.WarmupSeconds);
        // the setup alone sends 2 messages per NED entity and one per RPR entity; the measurement must not contain them
        Assert.True(r.MessagesIn < r.Entities * 5 * 2, $"{r.MessagesIn} inbound messages in 5 s look like they include the setup");
    }

    [Fact]
    public void The_simulation_is_deterministic_for_a_seed_and_differs_for_another()
    {
        var a = Run(seed: 1);
        var b = Run(seed: 1);
        var c = Run(seed: 2);
        Assert.Equal(Counts(a), Counts(b));
        Assert.NotEqual(Counts(a), Counts(c));
        Assert.NotEqual(a.MessagesInNed + a.MessagesInRpr, c.MessagesInNed + c.MessagesInRpr);
    }

    [Fact]
    public void The_population_is_deterministic_and_the_static_vehicles_do_not_move()
    {
        var a = BenchmarkRunner.CreatePopulation(100, 4);
        var b = BenchmarkRunner.CreatePopulation(100, 4);
        var c = BenchmarkRunner.CreatePopulation(100, 5);
        Assert.Equal(a.Select(v => (v.Side, v.IsStatic, v.Latitude, v.Speed)), b.Select(v => (v.Side, v.IsStatic, v.Latitude, v.Speed)));
        Assert.NotEqual(a.Select(v => v.Latitude), c.Select(v => v.Latitude));
        Assert.Equal(10, a.Count(v => v.IsStatic));
        Assert.All(a.Where(v => v.IsStatic), v => Assert.Equal(0.0, v.Speed));
        Assert.All(a.Where(v => !v.IsStatic), v => Assert.InRange(v.Speed, 0.0, 40.0));
        Assert.Equal(100, a.Select(v => v.Name).Distinct().Count());
        Assert.Equal(100, a.Select(v => v.Index).Distinct().Count());
    }

    [Fact]
    public void Static_vehicles_neither_move_nor_send_except_the_rpr_heartbeat()
    {
        var host = new NedRprHost(NedRprConfigurations.Default, new SimNetGateway.Engine.InMemory.ManualClock(),
            new NedRprHostOptions { Record = false });
        var random = new DeterministicRandom(1);
        var ned = new SimulatedVehicle(VehicleSide.Ned, 0, "S1", 100, SimNetGateway.Protocol.Ned.NedForce.Friendly, 50, 14, 300, 45, 20,
            isStatic: true, random.Fork());
        var rpr = new SimulatedVehicle(VehicleSide.Rpr, 1, "S2", 100, SimNetGateway.Protocol.Ned.NedForce.Friendly, 50, 14, 300, 45, 20,
            isStatic: true, random.Fork());
        var nedStart = ned.TruePosition;
        ned.SendInitial(host, 0);
        rpr.SendInitial(host, 0);
        int nedSent = 0, rprSent = 0;
        for (int tick = 1; tick <= 60 * 12; tick++)
        {
            double t = tick * NedRprHost.Dt;
            nedSent += ned.Update(host, t, NedRprHost.Dt, 0.0);
            rprSent += rpr.Update(host, t, NedRprHost.Dt, 0.0);
        }

        Assert.Equal(0, nedSent);
        Assert.Equal(nedStart, ned.TruePosition);
        Assert.Equal(0.0, ned.Speed);
        Assert.Equal(2, rprSent); // at 5 s and at about 10 s
    }

    [Fact]
    public void A_moving_vehicle_sends_when_the_prediction_error_exceeds_the_threshold_and_not_before()
    {
        var host = new NedRprHost(NedRprConfigurations.Default, new SimNetGateway.Engine.InMemory.ManualClock(),
            new NedRprHostOptions { Record = false });
        var random = new DeterministicRandom(1);
        var straight = new SimulatedVehicle(VehicleSide.Ned, 0, "M1", 100, SimNetGateway.Protocol.Ned.NedForce.Friendly, 50, 14, 300, 90, 30,
            isStatic: false, random.Fork(), autoTurn: false);
        straight.SendInitial(host, 0);
        int straightSent = 0;
        for (int tick = 1; tick <= 600; tick++)
        {
            straightSent += straight.Update(host, tick * NedRprHost.Dt, NedRprHost.Dt, 1.0);
        }

        // constant heading: dead reckoning is exact apart from the curvature of the earth, so (almost) nothing is sent
        Assert.True(straightSent <= 2, $"a vehicle on a straight line sent {straightSent} updates in 10 s");

        var turning = new SimulatedVehicle(VehicleSide.Ned, 1, "M2", 100, SimNetGateway.Protocol.Ned.NedForce.Friendly, 50, 14, 300, 90, 30,
            isStatic: false, random.Fork(), autoTurn: false) { TurnRate = 3.0 };
        turning.SendInitial(host, 0);
        int turningSent = 0;
        double worst = 0;
        for (int tick = 1; tick <= 600; tick++)
        {
            double t = tick * NedRprHost.Dt;
            int sent = turning.Update(host, t, NedRprHost.Dt, 1.0);
            turningSent += sent;
            var truth = turning.TruePosition;
            var predicted = turning.PredictedPosition(t);
            worst = Math.Max(worst, Math.Sqrt(Math.Pow(truth.X - predicted.X, 2) + Math.Pow(truth.Y - predicted.Y, 2) + Math.Pow(truth.Z - predicted.Z, 2)));
        }

        Assert.InRange(turningSent, 5, 100);
        // after every tick the error is back within the threshold (plus the movement of the one tick that exceeded it)
        Assert.True(worst <= 1.0 + 1e-9, $"the error after a tick was {worst} m");
    }

    [Fact]
    public void Input_is_dead_reckoned_thinned_far_below_the_tick_rate()
    {
        var r = s_default.Value;
        double perEntityPerSecond = r.MessagesInPerSecond / r.Entities;
        Assert.True(perEntityPerSecond < 3.0, $"{perEntityPerSecond:F2} messages per entity per second is not thinned");
        Assert.True(perEntityPerSecond > 0.0);
    }

    [Fact]
    public void A_larger_threshold_means_fewer_messages_in()
    {
        var fine = Run(seconds: 10, threshold: 0.25);
        var normal = Run(seconds: 10, threshold: 1.0);
        var coarse = Run(seconds: 10, threshold: 5.0);
        Assert.True(fine.MessagesIn > normal.MessagesIn, $"{fine.MessagesIn} vs {normal.MessagesIn}");
        Assert.True(normal.MessagesIn > coarse.MessagesIn, $"{normal.MessagesIn} vs {coarse.MessagesIn}");
        Assert.True(fine.MessagesIn > 2 * coarse.MessagesIn, $"{fine.MessagesIn} vs {coarse.MessagesIn}");
        Assert.True(fine.DerivedWrites > 0);
    }

    [Fact]
    public void Without_updates_only_the_rpr_heartbeats_remain_and_keep_those_entities_alive()
    {
        // a threshold nobody reaches: moving NED entities send nothing after the setup, RPR entities only their 5 s refresh
        var r = Run(seconds: 30, threshold: 1e9);
        Assert.Equal(0, r.MessagesInNed);
        Assert.Equal(0, r.MessagesInStaticNed);
        int rprEntities = r.RprEntities;
        // every RPR entity sends once per 5 s: 90 entities * 6 refreshes in 30 s (one more or less at the window edges)
        Assert.InRange(r.MessagesInRpr, rprEntities * 5, rprEntities * 7);
        Assert.InRange(r.MessagesInStaticRpr, r.StaticRprEntities * 5, r.StaticRprEntities * 7);
        // the refreshes keep the reaper (12 s) away: nobody is removed in 40 s of simulated time
        Assert.Equal(Entities, r.FinalEntities);
        Assert.Equal(0, r.DiagnosticWarnings);
    }

    [Fact]
    public void Static_entities_send_nothing_after_the_setup_except_the_rpr_refreshes()
    {
        var r = Run(seconds: 10);
        Assert.Equal(0, r.MessagesInStaticNed);
        Assert.InRange(r.MessagesInStaticRpr, r.StaticRprEntities * 1, r.StaticRprEntities * 2);
    }

    [Fact]
    public void Derived_writes_are_the_prediction_changes_of_the_moving_entities_at_the_processor_rate()
    {
        var r = Run(seconds: 10);
        int moving = r.Entities - r.StaticNedEntities - r.StaticRprEntities;
        double expected = moving * 10.0 * r.MeasuredSeconds; // 10 Hz processor
        Assert.InRange(r.DerivedWrites, expected * 0.95, expected * 1.05);
        // each prediction change becomes one display position
        Assert.Equal(r.DerivedWrites, r.MessagesOutDisplay);
        Assert.InRange(r.DerivedWritesPerSecond, moving * 9.5, moving * 10.5);
    }

    [Fact]
    public void Outbound_traffic_is_counted_per_side()
    {
        var r = s_default.Value;
        Assert.True(r.MessagesOutNed > 0);
        Assert.True(r.MessagesOutRpr > 0);
        Assert.True(r.MessagesOutDisplay > 0);
        Assert.Equal(r.MessagesOutNed + r.MessagesOutRpr + r.MessagesOutDisplay, r.MessagesOut);
        Assert.Equal(r.MessagesOutNed / r.MeasuredSeconds, r.MessagesOutNedPerSecond, 9);
        Assert.Equal(r.MessagesOutDisplay / r.MeasuredSeconds, r.MessagesOutDisplayPerSecond, 9);
        Assert.Equal(r.MessagesIn / r.MeasuredSeconds, r.MessagesInPerSecond, 9);
    }

    [Fact]
    public void The_result_fields_are_consistent()
    {
        var r = s_default.Value;
        Assert.True(r.TickMeanMs > 0);
        Assert.True(r.TickP50Ms <= r.TickP95Ms);
        Assert.True(r.TickP95Ms <= r.TickP99Ms);
        Assert.True(r.TickP99Ms <= r.TickMaxMs);
        Assert.True(r.TickMeanMs <= r.TickMaxMs);
        Assert.True(r.TickP50Ms > 0);
        Assert.Equal((double)r.AllocatedBytes / r.MessagesIn, r.AllocatedBytesPerMessageIn, 9);
        Assert.True(r.AllocatedBytes > 0);
        Assert.Equal(r.MeasuredSeconds / (r.TickMeanMs * r.Ticks / 1000.0), r.RealTimeFactor, 6);
        Assert.True(r.RealTimeFactor > 1.0, "300 entities must run faster than real time");
        Assert.True(r.Gen0Collections >= 0 && r.Gen1Collections >= 0 && r.Gen2Collections >= 0);
        Assert.True(r.Gen0Collections >= r.Gen1Collections && r.Gen1Collections >= r.Gen2Collections);
        // the processor ticks (every 6th) cost far more than the idle ones: the times really are measured per tick
        Assert.True(r.TickMaxMs > r.TickP50Ms, $"max {r.TickMaxMs} ms is not above the median {r.TickP50Ms} ms");
        Assert.True(r.TickP95Ms > r.TickP50Ms, $"p95 {r.TickP95Ms} ms is not above the median {r.TickP50Ms} ms");
        Assert.Contains("ned-rpr/default", r.Format());
        Assert.Contains("300 (210 NED-origin, 90 RPR-origin; static 21 + 9)", r.Format());
        Assert.Contains("p99", r.Format());
    }

    [Fact]
    public void Invariants_hold_after_every_tick_of_a_small_run()
    {
        InvariantChecker? checker = null;
        var r = Run(entities: 120, seconds: 6, warmup: 3, record: true,
            onHost: host =>
            {
                checker = new InvariantChecker(host);
                host.NedOut!.Tap = checker.OnNedSent;
                host.RprOut!.Tap = checker.OnRprSent;
            },
            afterTick: host =>
            {
                checker!.Check();
                host.NedOut!.Clear();
                host.RprOut!.Clear();
                host.DisplayOut!.Clear();
            });
        Assert.NotNull(checker);
        Assert.Equal(r.SetupTicks + 3 * 60 + 6 * 60, checker!.Checks);
        Assert.Equal(120, r.FinalEntities);
        Assert.Equal(0, r.DiagnosticWarnings);
    }

    [Fact]
    public void The_measured_tick_times_fit_into_the_real_time_the_run_took()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = Run(seconds: 5);
        watch.Stop();
        double summed = r.TickMeanMs * r.Ticks / 1000.0;
        Assert.True(summed > 0);
        Assert.True(summed < watch.Elapsed.TotalSeconds, $"{summed} s of engine steps in a run of {watch.Elapsed.TotalSeconds} s");
    }

    [Fact]
    public void Bad_options_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(entities: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(entities: BenchmarkRunner.MaxEntities + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(seconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(threshold: -1));
    }
}
