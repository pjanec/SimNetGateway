using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Showcase;

namespace SimNetGateway.Viewer;

/// <summary>The scenarios the viewer can host (DD-005 P-48, R-071).</summary>
public enum ViewerScenario
{
    /// <summary>The DD-002 showcase with its live S-* conditions and G-* invariants (8400 ticks).</summary>
    Convoy,

    /// <summary>The scripted default demo (1200 ticks).</summary>
    Demo,

    /// <summary>The benchmark traffic generator with an adjustable population (endless).</summary>
    Traffic,

    /// <summary>A gateway with nothing in it (endless); the operator drives it with session commands.</summary>
    Empty,
}

/// <summary>
/// One hosted gateway of the viewer: the host, its manual clock and a function that runs the next 60 Hz tick of the scenario
/// (clock, scripted or generated input, RTI answers, <c>Engine.Tick()</c>). Built by the factories, thrown away on restart.
/// </summary>
internal sealed class ScenarioInstance
{
    /// <summary>The senders' dead-reckoning threshold of the traffic generator (the benchmark's default).</summary>
    public const double TrafficThresholdMetres = 1.0;

    private readonly Func<bool> _step;
    private readonly Func<long> _ticks;

    private ScenarioInstance(ViewerScenario kind, string name, NedRprHost host, ManualClock clock, long totalTicks, Func<bool> step,
        Func<long> ticks)
    {
        Kind = kind;
        Name = name;
        Host = host;
        Clock = clock;
        TotalTicks = totalTicks;
        _step = step;
        _ticks = ticks;
    }

    public ViewerScenario Kind { get; }

    public string Name { get; }

    public NedRprHost Host { get; }

    public ManualClock Clock { get; }

    /// <summary>The tick the scenario ends at; 0 for an endless one.</summary>
    public long TotalTicks { get; }

    /// <summary>Entities of the traffic generator; 0 otherwise.</summary>
    public int Entities { get; private init; }

    /// <summary>The Convoy Ambush run (live conditions and violations); null for the others.</summary>
    public ScenarioRun? Convoy { get; private init; }

    /// <summary>Ticks done.</summary>
    public long Ticks => _ticks();

    public bool Finished => TotalTicks > 0 && Ticks >= TotalTicks;

    /// <summary>Runs the next tick; false when the scenario has ended.</summary>
    public bool Step() => _step();

    // ---- factories ------------------------------------------------------------------------------------------------

    public static ScenarioInstance CreateConvoy()
    {
        var run = new ScenarioRunner().Start(ConvoyAmbushScript.Create());
        return new ScenarioInstance(ViewerScenario.Convoy, "Convoy Ambush", run.Context.Host, run.Context.Clock, run.TotalTicks,
            run.Step, () => run.Ticks)
        {
            Convoy = run,
        };
    }

    public static ScenarioInstance CreateDemo()
    {
        var run = DemoScenario.Start();
        return new ScenarioInstance(ViewerScenario.Demo, "Default demo", run.Host, (ManualClock)run.Host.Clock, run.TotalTicks,
            run.Step, () => run.Ticks);
    }

    public static ScenarioInstance CreateEmpty()
    {
        var clock = new ManualClock();
        var host = new NedRprHost(NedRprConfigurations.Default, clock, new NedRprHostOptions { EngineOptions = EngineOptionsFor(0) });
        long tick = 0;
        return new ScenarioInstance(ViewerScenario.Empty, "Empty gateway", host, clock, 0, () => StepHost(host, clock, ++tick), () => tick);
    }

    public static ScenarioInstance CreateTraffic(int entities, int seed = 1)
    {
        var clock = new ManualClock();
        var host = new NedRprHost(NedRprConfigurations.Default, clock,
            new NedRprHostOptions { Record = false, EngineOptions = EngineOptionsFor(entities) });
        var vehicles = BenchmarkRunner.CreatePopulation(entities, seed);
        foreach (var vehicle in vehicles)
        {
            vehicle.SendInitial(host, 0.0);
        }

        long tick = 0;
        bool Step()
        {
            long next = ++tick;
            double t = next * NedRprHost.Dt;
            foreach (var vehicle in vehicles)
            {
                vehicle.Update(host, t, NedRprHost.Dt, TrafficThresholdMetres);
            }

            return StepHost(host, clock, next);
        }

        return new ScenarioInstance(ViewerScenario.Traffic, $"Traffic generator ({entities} entities)", host, clock, 0, Step, () => tick)
        {
            Entities = entities,
        };
    }

    private static bool StepHost(NedRprHost host, ManualClock clock, long tick)
    {
        // exact 60 Hz: tick k is at k / 60 s (no accumulated rounding)
        clock.Now = tick / 60.0;
        if (host.DeliverRti)
        {
            host.Rti.DeliverPending(registered => host.RprIn.Enqueue(registered));
        }

        host.Engine.Tick();
        host.AfterTick?.Invoke(host);
        return true;
    }

    private static GatewayOptions EngineOptionsFor(int entities) => new()
    {
        MaxMessagesPerTick = Math.Max(10_000, entities * 4),
        JournalCapacity = 8192,
        DiagnosticsCapacity = 8192,
        EventLogCapacity = 2048,
    };
}
