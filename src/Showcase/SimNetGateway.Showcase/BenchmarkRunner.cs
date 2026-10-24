using System.Diagnostics;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Showcase;

/// <summary>The parameters of a benchmark run (DD-001 sections 8 and 12).</summary>
public sealed record BenchmarkOptions
{
    /// <summary>The gateway configuration; null selects <see cref="NedRprConfigurations.Default"/>.</summary>
    public GatewayConfiguration? Configuration { get; init; }

    public int Entities { get; init; } = 10_000;

    /// <summary>Simulated seconds of the measurement phase.</summary>
    public double Seconds { get; init; } = 30;

    public int Seed { get; init; } = 1;

    /// <summary>The senders' dead-reckoning threshold in metres.</summary>
    public double ThresholdMetres { get; init; } = 1.0;

    /// <summary>Simulated seconds between the setup and the measurement; excluded from the result.</summary>
    public double WarmupSeconds { get; init; } = 5;

    /// <summary>True: the host records the gateway's output (tests that inspect it); false: counting channels.</summary>
    public bool Record { get; init; }

    /// <summary>Called with the host before anything is sent (tests install checkers and taps here).</summary>
    public Action<NedRprHost>? OnHostCreated { get; init; }

    /// <summary>Called after every engine tick of every phase (it runs inside the timed step).</summary>
    public Action<NedRprHost>? AfterTick { get; init; }

    /// <summary>Receives short status lines at the start of each phase.</summary>
    public Action<string>? Progress { get; init; }
}

/// <summary>
/// The 10k-entity benchmark of the NED-RPR gateway: a deterministic simulation (manual clock, 1/60 s per tick, seeded random
/// numbers) of 70 % NED-origin and 30 % RPR-origin entities, 10 % of them static, whose senders thin their updates by dead
/// reckoning (<see cref="SimulatedVehicle"/>), driven through the in-memory channels with counting outbound channels. The
/// real time of every engine step is measured with a <see cref="Stopwatch"/>. Phases: setup (until every entity is published
/// and registered), warm-up (excluded), measurement.
/// </summary>
public static class BenchmarkRunner
{
    /// <summary>The largest entity count (DIS entity numbers are 16 bit).</summary>
    public const int MaxEntities = 60_000;

    private const double NedShare = 0.7;
    private const double StaticShare = 0.1;
    private const double MaxSetupSeconds = 120;
    private static readonly int[] s_tkbTypes = [100, 101, 102, 200];

    /// <summary>The simulated population for an entity count and seed: sides shuffled, 10 % of each side static.</summary>
    public static List<SimulatedVehicle> CreatePopulation(int entities, int seed)
    {
        if (entities < 1 || entities > MaxEntities)
        {
            throw new ArgumentOutOfRangeException(nameof(entities), $"The entity count must be 1..{MaxEntities}.");
        }

        var random = new DeterministicRandom(seed);
        int nedCount = (int)Math.Round(entities * NedShare);
        int rprCount = entities - nedCount;
        int staticNed = (int)Math.Round(nedCount * StaticShare);
        int staticRpr = (int)Math.Round(rprCount * StaticShare);

        var sides = new VehicleSide[entities];
        for (int i = 0; i < entities; i++)
        {
            sides[i] = i < nedCount ? VehicleSide.Ned : VehicleSide.Rpr;
        }

        for (int i = entities - 1; i > 0; i--)
        {
            int j = random.NextInt(i + 1);
            (sides[i], sides[j]) = (sides[j], sides[i]);
        }

        var vehicles = new List<SimulatedVehicle>(entities);
        int nedSeen = 0, rprSeen = 0;
        for (int i = 0; i < entities; i++)
        {
            var side = sides[i];
            bool isStatic = side == VehicleSide.Ned ? nedSeen++ < staticNed : rprSeen++ < staticRpr;
            string name = (side == VehicleSide.Ned ? "N" : "R") + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            int tkb = s_tkbTypes[random.NextInt(s_tkbTypes.Length)];
            var force = (NedForce)(1 + random.NextInt(3));
            double lat = random.NextDouble(49.5, 50.5);
            double lon = random.NextDouble(13.5, 14.5);
            double heading = random.NextDouble(0, 360);
            double speed = random.NextDouble(0, 40);
            vehicles.Add(new SimulatedVehicle(side, i, name, tkb, force, lat, lon, 300.0, heading, speed, isStatic, random.Fork()));
        }

        return vehicles;
    }

    public static BenchmarkResult Run(BenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Seconds <= 0 || options.WarmupSeconds < 0 || options.ThresholdMetres < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Seconds must be positive; warm-up and threshold must not be negative.");
        }

        var configuration = options.Configuration ?? NedRprConfigurations.Default;
        var progress = options.Progress;
        var engineOptions = new GatewayOptions
        {
            MaxMessagesPerTick = Math.Max(10_000, options.Entities * 4),
            JournalCapacity = 32768,
            DiagnosticsCapacity = 8192,
            EventLogCapacity = 4096,
        };
        var clock = new ManualClock();
        var host = new NedRprHost(configuration, clock, new NedRprHostOptions { Record = options.Record, EngineOptions = engineOptions });
        options.OnHostCreated?.Invoke(host);
        host.AfterTick = options.AfterTick;
        var vehicles = CreatePopulation(options.Entities, options.Seed);
        const double dt = NedRprHost.Dt;
        double threshold = options.ThresholdMetres;
        long tick = 0;

        // ---- setup: create every entity, run until all are published and registered ----------------------------------
        progress?.Invoke($"Setup: creating {vehicles.Count} entities");
        foreach (var vehicle in vehicles)
        {
            vehicle.SendInitial(host, 0.0);
        }

        long maxSetupTicks = (long)Math.Round(MaxSetupSeconds / dt);
        while (!AllPublished(host, vehicles.Count))
        {
            if (tick >= maxSetupTicks)
            {
                throw new InvalidOperationException($"The setup did not publish all {vehicles.Count} entities within {MaxSetupSeconds} s.");
            }

            tick++;
            SendUpdates(host, vehicles, tick * dt, dt, threshold, null);
            host.Step(dt);
        }

        long setupTicks = tick;

        // ---- warm-up: excluded ----------------------------------------------------------------------------------------
        progress?.Invoke($"Warm-up: {options.WarmupSeconds:F1} s simulated");
        long warmupTicks = (long)Math.Round(options.WarmupSeconds / dt);
        for (long i = 0; i < warmupTicks; i++)
        {
            tick++;
            SendUpdates(host, vehicles, tick * dt, dt, threshold, null);
            host.Step(dt);
        }

        // ---- measurement ----------------------------------------------------------------------------------------------
        progress?.Invoke($"Measurement: {options.Seconds:F1} s simulated");
        long ticks = Math.Max(1, (long)Math.Round(options.Seconds / dt));
        var counters = new SendCounters();
        var samples = new double[ticks];
        long nedOut0 = host.NedSentCount, rprOut0 = host.RprSentCount, displayOut0 = host.DisplaySentCount;
        int registered0 = host.Rti.Registered.Count;
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long allocated = 0;
        long derivedWrites = 0;
        double msPerStopwatchTick = 1000.0 / Stopwatch.Frequency;
        var predicted = host.Schema.PredictedPosition;
        for (long i = 0; i < ticks; i++)
        {
            tick++;
            SendUpdates(host, vehicles, tick * dt, dt, threshold, counters);
            long bytes0 = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            host.Step(dt);
            long end = Stopwatch.GetTimestamp();
            allocated += GC.GetAllocatedBytesForCurrentThread() - bytes0;
            samples[i] = (end - start) * msPerStopwatchTick;
            derivedWrites += CountPredictedPositionChanges(host.Engine, predicted, clock.Now);
        }

        int gen0End = GC.CollectionCount(0), gen1End = GC.CollectionCount(1), gen2End = GC.CollectionCount(2);
        long registrations = host.Rti.Registered.Count - registered0;
        var summary = Percentiles.Summarize(samples);

        int nedEntities = 0, rprEntities = 0, staticNed = 0, staticRpr = 0;
        foreach (var vehicle in vehicles)
        {
            if (vehicle.Side == VehicleSide.Ned)
            {
                nedEntities++;
                staticNed += vehicle.IsStatic ? 1 : 0;
            }
            else
            {
                rprEntities++;
                staticRpr += vehicle.IsStatic ? 1 : 0;
            }
        }

        CountPublished(host, vehicles, out int publishedToRpr, out int publishedToNed);
        int warnings = 0, errors = 0;
        foreach (var entry in host.Engine.Diagnostics.Recent)
        {
            if (entry.Severity == DiagnosticSeverity.Warning)
            {
                warnings++;
            }
            else if (entry.Severity == DiagnosticSeverity.Error)
            {
                errors++;
            }
        }

        return new BenchmarkResult
        {
            Configuration = configuration.Name,
            Entities = vehicles.Count,
            NedEntities = nedEntities,
            RprEntities = rprEntities,
            StaticNedEntities = staticNed,
            StaticRprEntities = staticRpr,
            Seed = options.Seed,
            ThresholdMetres = threshold,
            MeasuredSeconds = ticks * dt,
            WarmupSeconds = options.WarmupSeconds,
            Ticks = ticks,
            SetupTicks = setupTicks,
            MessagesInNed = counters.Ned,
            MessagesInRpr = counters.Rpr + registrations,
            MessagesInStaticNed = counters.StaticNed,
            MessagesInStaticRpr = counters.StaticRpr,
            MessagesOutNed = host.NedSentCount - nedOut0,
            MessagesOutRpr = host.RprSentCount - rprOut0,
            MessagesOutDisplay = host.DisplaySentCount - displayOut0,
            DerivedWrites = derivedWrites,
            TickMeanMs = summary.Mean,
            TickP50Ms = summary.P50,
            TickP95Ms = summary.P95,
            TickP99Ms = summary.P99,
            TickMaxMs = summary.Max,
            AllocatedBytes = allocated,
            Gen0Collections = gen0End - gen0,
            Gen1Collections = gen1End - gen1,
            Gen2Collections = gen2End - gen2,
            PublishedToRpr = publishedToRpr,
            PublishedToNed = publishedToNed,
            FinalEntities = host.Engine.State.Count,
            DiagnosticWarnings = warnings,
            DiagnosticErrors = errors,
        };
    }

    private sealed class SendCounters
    {
        public long Ned;
        public long Rpr;
        public long StaticNed;
        public long StaticRpr;
    }

    private static void SendUpdates(NedRprHost host, List<SimulatedVehicle> vehicles, double t, double dt, double threshold,
        SendCounters? counters)
    {
        foreach (var vehicle in vehicles)
        {
            int sent = vehicle.Update(host, t, dt, threshold);
            if (sent == 0 || counters is null)
            {
                continue;
            }

            if (vehicle.Side == VehicleSide.Ned)
            {
                counters.Ned += sent;
                if (vehicle.IsStatic)
                {
                    counters.StaticNed += sent;
                }
            }
            else
            {
                counters.Rpr += sent;
                if (vehicle.IsStatic)
                {
                    counters.StaticRpr += sent;
                }
            }
        }
    }

    private static bool AllPublished(NedRprHost host, int entities) =>
        host.RprHandles.Count >= entities && host.NedIds.Count >= entities;

    private static void CountPublished(NedRprHost host, List<SimulatedVehicle> vehicles, out int toRpr, out int toNed)
    {
        toRpr = 0;
        toNed = 0;
        foreach (var vehicle in vehicles)
        {
            if (vehicle.Side == VehicleSide.Ned)
            {
                if (host.NedIds.TryResolve(vehicle.NedId, out var entity) && host.RprHandles.Contains(entity))
                {
                    toRpr++;
                }
            }
            else
            {
                var handle = vehicle.Handle;
                if (host.RprHandles.TryResolve(handle, out var entity) && host.NedIds.Contains(entity))
                {
                    toNed++;
                }
            }
        }
    }

    /// <summary>
    /// The number of <c>Derived.PredictedPosition</c> changes committed by the change sets of the tick that ran at
    /// <paramref name="wallNow"/> (the processor writes one such change per entity whose prediction moved).
    /// </summary>
    private static long CountPredictedPositionChanges(GatewayEngine engine, AttrId predictedPosition, double wallNow)
    {
        long count = 0;
        foreach (var record in engine.State.Journal.Since(wallNow))
        {
            foreach (var delta in record.Entities)
            {
                if (delta.TryGetChange(predictedPosition, out var change) && change.Changed)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
