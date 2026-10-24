using System.Globalization;
using System.Text;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

/// <summary>Hooks of a demo run (the tests attach their invariant checker here).</summary>
public sealed record DemoOptions
{
    /// <summary>The gateway configuration; null selects <see cref="NedRprConfigurations.Default"/>.</summary>
    public GatewayConfiguration? Configuration { get; init; }

    /// <summary>Called with the host before anything happens.</summary>
    public Action<NedRprHost>? OnHostCreated { get; init; }

    /// <summary>Called after every engine tick.</summary>
    public Action<NedRprHost>? AfterTick { get; init; }

    /// <summary>Called for every message the gateway sends to NED, in order.</summary>
    public Action<INedMessage>? OnNedSent { get; init; }

    /// <summary>Called for every message the gateway sends to RPR, in order (after the RTI saw it).</summary>
    public Action<IRprMessage>? OnRprSent { get; init; }
}

/// <summary>What the scripted demo produced.</summary>
public sealed class DemoResult
{
    internal DemoResult(NedRprHost host, List<string> timeline, SortedDictionary<string, int> counts, List<string> diagnostics,
        long ticks, double seconds, double senderSeconds)
    {
        Host = host;
        Timeline = timeline;
        Counts = counts;
        Diagnostics = diagnostics;
        Ticks = ticks;
        Seconds = seconds;
        SenderSeconds = senderSeconds;
    }

    public NedRprHost Host { get; }

    /// <summary>One line per scripted input and per message the gateway sent, in order; contains no wall-clock values.</summary>
    public IReadOnlyList<string> Timeline { get; }

    /// <summary>Messages the gateway sent, keyed by "NED NedEntityMaster", "RPR RprObjectUpdate", "DISPLAY DisplayEntityPosition", ...</summary>
    public IReadOnlyDictionary<string, int> Counts { get; }

    /// <summary>Warnings and errors the engine recorded (category, origin, message).</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    public long Ticks { get; }

    /// <summary>Simulated seconds of the run.</summary>
    public double Seconds { get; }

    /// <summary>Simulated seconds the scripted senders were active: the run minus the time the session was paused.</summary>
    public double SenderSeconds { get; }

    public int Count(string side, string messageType) =>
        Counts.TryGetValue(side + " " + messageType, out int n) ? n : 0;

    public int CountOfSide(string side)
    {
        int total = 0;
        foreach (var (key, n) in Counts)
        {
            if (key.StartsWith(side + " ", StringComparison.Ordinal))
            {
                total += n;
            }
        }

        return total;
    }

    /// <summary>The timeline followed by the summary.</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append("Timeline (simulated seconds; <- input, -> output of the gateway)\n");
        foreach (string line in Timeline)
        {
            sb.Append(line).Append('\n');
        }

        sb.Append('\n').Append("Summary\n");
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"  {Ticks} ticks, {Seconds:F2} s simulated\n"));
        foreach (string side in new[] { "NED", "RPR", "DISPLAY" })
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"  sent to {side}: {CountOfSide(side)}\n"));
            foreach (var (key, n) in Counts)
            {
                if (key.StartsWith(side + " ", StringComparison.Ordinal))
                {
                    sb.Append(string.Create(CultureInfo.InvariantCulture, $"    {key.Substring(side.Length + 1),-26} {n}\n"));
                }
            }
        }

        sb.Append(string.Create(CultureInfo.InvariantCulture, $"  diagnostics warnings and errors: {Diagnostics.Count}\n"));
        foreach (string entry in Diagnostics)
        {
            sb.Append("    ").Append(entry).Append('\n');
        }

        return sb.ToString();
    }
}

/// <summary>One demo run that is advanced tick by tick (see <see cref="DemoScenario.Start"/>).</summary>
public sealed class DemoRun
{
    private readonly Func<bool> _step;
    private readonly Func<int> _ticks;
    private readonly Func<DemoResult> _finish;

    internal DemoRun(NedRprHost host, int totalTicks, Func<bool> step, Func<int> ticks, Func<DemoResult> finish)
    {
        Host = host;
        TotalTicks = totalTicks;
        _step = step;
        _ticks = ticks;
        _finish = finish;
    }

    public NedRprHost Host { get; }

    public int TotalTicks { get; }

    /// <summary>Ticks done so far.</summary>
    public int Ticks => _ticks();

    public bool Finished => Ticks >= TotalTicks;

    /// <summary>Runs the next tick (script, senders, host step); false when the demo has ended.</summary>
    public bool Step() => _step();

    /// <summary>The result of the run so far (the whole demo once <see cref="Finished"/>).</summary>
    public DemoResult Finish() => _finish();
}

/// <summary>
/// The default demo: a deterministic, scripted run of about 20 simulated seconds at 60 Hz on a manual clock with a handful of
/// entities - a NED vehicle that moves and turns, a static NED checkpoint, a second NED vehicle that is disposed, an RPR tank
/// that appears, fires, is hit and then falls silent so that it is reaped - plus a pause and a resume. Every message the
/// gateway sends is put on a timeline (simulated time only, no wall-clock values).
/// </summary>
public static class DemoScenario
{
    /// <summary>Length of the run in simulated seconds.</summary>
    public const double Duration = 20.0;

    private const double Threshold = 1.0;
    private static readonly TypeCatalog s_catalog = new();

    public static DemoResult Run(DemoOptions? options = null)
    {
        var run = Start(options);
        while (run.Step())
        {
        }

        return run.Finish();
    }

    /// <summary>
    /// Builds the host and the script and returns the run before its first tick: the caller advances it tick by tick with
    /// <see cref="DemoRun.Step"/> (the viewer does, from its frame loop). <see cref="Run"/> is this plus a loop.
    /// </summary>
    public static DemoRun Start(DemoOptions? options = null)
    {
        options ??= new DemoOptions();
        var configuration = options.Configuration ?? NedRprConfigurations.Default;
        var clock = new ManualClock();
        var host = new NedRprHost(configuration, clock);
        var timeline = new List<string>();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        host.NedOut!.Tap = message =>
        {
            Record(host, timeline, counts, "NED", "-> NED", message.GetType().Name, Describe(message));
            options.OnNedSent?.Invoke(message);
        };
        host.RprOut!.Tap = message =>
        {
            host.Rti.Observe(message);
            Record(host, timeline, counts, "RPR", "-> RPR", message.GetType().Name, Describe(message));
            options.OnRprSent?.Invoke(message);
        };
        host.DisplayOut!.Tap = message =>
            Record(host, timeline, counts, "DISPLAY", "-> DISPLAY", message.GetType().Name, Describe(message));
        options.OnHostCreated?.Invoke(host);
        host.AfterTick = options.AfterTick;

        var random = new DeterministicRandom(1);
        var alpha = new SimulatedVehicle(VehicleSide.Ned, 0, "Alpha-1", 100, NedForce.Friendly, 50.0, 14.0, 300.0, 90.0, 12.0,
            isStatic: false, random.Fork(), autoTurn: false);
        var checkpoint = new SimulatedVehicle(VehicleSide.Ned, 1, "Checkpoint", 300, NedForce.Neutral, 50.001, 14.002, 300.0, 0.0,
            0.0, isStatic: true, random.Fork(), autoTurn: false);
        var bravo = new SimulatedVehicle(VehicleSide.Ned, 2, "Bravo-2", 101, NedForce.Friendly, 49.999, 14.001, 300.0, 0.0, 8.0,
            isStatic: false, random.Fork(), autoTurn: false);
        var tank = new SimulatedVehicle(VehicleSide.Rpr, 3, "Enemy-T72", 400, NedForce.Opposing, 50.002, 14.003, 300.0, 270.0, 6.0,
            isStatic: false, random.Fork(), autoTurn: false);

        bool paused = false;
        bool bravoActive = false;
        bool alphaActive = false;
        bool checkpointActive = false;
        bool tankActive = false;
        double senderTime = 0;

        var script = new List<(int Tick, Action Action)>();
        void At(double seconds, Action action) => script.Add(((int)Math.Round(seconds / NedRprHost.Dt), action));
        void Input(string side, string text) =>
            timeline.Add(string.Create(CultureInfo.InvariantCulture, $"{host.Engine.SimTime,8:F3}  <- {side,-8}{text}"));
        void InjectNed<T>(T message, string text)
            where T : INedMessage
        {
            Input("NED", text);
            host.NedIn.Enqueue(in message);
        }

        void InjectRpr<T>(T message, string text)
            where T : IRprMessage
        {
            Input("RPR", text);
            host.RprIn.Enqueue(in message);
        }

        At(1 * NedRprHost.Dt, () => InjectNed(new NedSimControl(NedControlKind.Load, "demo"), "NedSimControl Load demo"));
        At(2 * NedRprHost.Dt, () => InjectNed(new NedSimControl(NedControlKind.Start, null), "NedSimControl Start"));
        At(0.1, () =>
        {
            Input("NED", "start of Alpha-1 (moving, 12 m/s east), Checkpoint (static), Bravo-2 (moving, 8 m/s north)");
            alpha.SendInitial(host, senderTime);
            checkpoint.SendInitial(host, senderTime);
            bravo.SendInitial(host, senderTime);
            alphaActive = checkpointActive = bravoActive = true;
        });
        At(1.0, () =>
        {
            Input("RPR", "Enemy-T72 appears (RprObjectUpdate, full)");
            tank.SendInitial(host, senderTime);
            tankActive = true;
        });
        At(2.0, () =>
        {
            Input("RPR", "Enemy-T72 starts a turn to the left (8 deg/s)");
            tank.TurnRate = -8.0;
        });
        At(3.0, () =>
        {
            Input("NED", "Alpha-1 starts a turn to the right (12 deg/s)");
            alpha.TurnRate = 12.0;
        });
        At(3.5, () => InjectRpr(
            new RprWeaponFire(DemoGuid(1), tank.Handle, null, new WorldLocation(tank.TruePosition.X, tank.TruePosition.Y, tank.TruePosition.Z),
                Munition(502), 1),
            "RprWeaponFire by Enemy-T72"));
        At(5.0, () =>
        {
            Input("NED", "Alpha-1 ends its turn");
            alpha.TurnRate = 0.0;
            Input("RPR", "Enemy-T72 falls silent (no more updates)");
            tank.TurnRate = 0.0;
            tankActive = false;
        });
        At(6.0, () => InjectNed(new NedWeaponStatus(alpha.NedId, 501, 20), "NedWeaponStatus Alpha-1 munition=501 ammo=20"));
        At(7.0, () =>
        {
            ulong target = NedIdOf(host, tank.Handle);
            InjectNed(new NedWeaponFire(DemoGuid(2), alpha.NedId, target, 501, alpha.Latitude, alpha.Longitude, alpha.Altitude, 1),
                "NedWeaponFire Alpha-1 -> Enemy-T72");
        });
        At(7.3, () =>
        {
            ulong target = NedIdOf(host, tank.Handle);
            var location = tank.TruePosition;
            NedRprConversions.EcefToGeodetic(location, out double lat, out double lon, out double alt);
            InjectNed(new NedDetonation(DemoGuid(3), target, 501, lat, lon, alt, NedDetonationResult.EntityImpact, -1),
                "NedDetonation on Enemy-T72");
        });
        At(9.0, () =>
        {
            InjectNed(new NedSimControl(NedControlKind.Pause, null), "NedSimControl Pause");
            paused = true;
        });
        At(10.0, () =>
        {
            InjectNed(new NedSimControl(NedControlKind.Resume, null), "NedSimControl Resume");
            paused = false;
        });
        At(12.0, () =>
        {
            InjectNed(new NedEntityDisposed(bravo.NedId), "NedEntityDisposed Bravo-2");
            bravoActive = false;
        });

        int totalTicks = (int)Math.Round(Duration / NedRprHost.Dt);
        int next = 0;
        int tick = 0;
        script.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        bool StepOne()
        {
            if (tick >= totalTicks)
            {
                return false;
            }

            tick++;
            while (next < script.Count && script[next].Tick <= tick)
            {
                script[next++].Action();
            }

            if (!paused)
            {
                senderTime += NedRprHost.Dt;
                if (alphaActive)
                {
                    alpha.Update(host, senderTime, NedRprHost.Dt, Threshold);
                }

                if (checkpointActive)
                {
                    checkpoint.Update(host, senderTime, NedRprHost.Dt, Threshold);
                }

                if (bravoActive)
                {
                    bravo.Update(host, senderTime, NedRprHost.Dt, Threshold);
                }

                if (tankActive)
                {
                    tank.Update(host, senderTime, NedRprHost.Dt, Threshold);
                }
            }

            host.Step(NedRprHost.Dt);
            return true;
        }

        DemoResult Finish()
        {
            var diagnostics = new List<string>();
            foreach (var entry in host.Engine.Diagnostics.Recent)
            {
                if (entry.Severity != DiagnosticSeverity.Info)
                {
                    diagnostics.Add($"{entry.Severity} [{entry.Category}] {entry.Origin}: {entry.Message}");
                }
            }

            return new DemoResult(host, timeline, counts, diagnostics, tick, tick * NedRprHost.Dt, senderTime);
        }

        return new DemoRun(host, totalTicks, StepOne, () => tick, Finish);
    }

    private static void Record(NedRprHost host, List<string> timeline, SortedDictionary<string, int> counts, string side,
        string arrow, string type, string text)
    {
        string key = side + " " + type;
        counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        timeline.Add(string.Create(CultureInfo.InvariantCulture, $"{host.Engine.SimTime,8:F3}  {arrow,-11}{text}"));
    }

    private static Guid DemoGuid(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    private static DisEntityType Munition(int tkb) =>
        s_catalog.TryToDis(tkb, out var dis) ? dis : throw new InvalidOperationException($"Unknown munition TKB {tkb}.");

    private static ulong NedIdOf(NedRprHost host, RprObjectHandle handle) =>
        host.RprHandles.TryResolve(handle, out var entity) && host.NedIds.TryGetKey(entity, out ulong id) ? id : 0UL;

    // ---- message summaries --------------------------------------------------------------------------------------------

    private static string Describe(INedMessage message)
    {
        var c = CultureInfo.InvariantCulture;
        return message switch
        {
            NedEntityMaster m => string.Create(c, $"NedEntityMaster id={m.Id} name={m.Name} tkb={m.TkbType} force={m.Force}"),
            NedGeoSpatial m => string.Create(c,
                $"NedGeoSpatial id={m.Id} lat={m.LatitudeDeg:F5} lon={m.LongitudeDeg:F5} alt={m.Altitude:F1} heading={m.HeadingDeg:F1} speed={m.Speed:F1}"),
            NedEntityDamage m => string.Create(c, $"NedEntityDamage id={m.Id} level={m.Level}"),
            NedWeaponStatus m => string.Create(c, $"NedWeaponStatus id={m.Id} munition={m.SelectedMunitionTkb} ammo={m.Ammo}"),
            NedEntityDisposed m => string.Create(c, $"NedEntityDisposed id={m.Id}"),
            NedWeaponFire m => string.Create(c,
                $"NedWeaponFire shooter={m.Shooter} target={m.Target} munition={m.MunitionTkb} lat={m.LatitudeDeg:F5} lon={m.LongitudeDeg:F5} qty={m.Quantity}"),
            NedDetonation m => string.Create(c,
                $"NedDetonation target={m.Target} munition={m.MunitionTkb} result={m.Result} damage={m.TargetDamage}"),
            NedSimControl m => string.Create(c, $"NedSimControl {m.Kind}"),
            _ => message.GetType().Name,
        };
    }

    private static string Describe(IRprMessage message)
    {
        var c = CultureInfo.InvariantCulture;
        return message switch
        {
            RprRegisterObject m => string.Create(c, $"RprRegisterObject token={m.Token}"),
            RprObjectRegistered m => string.Create(c, $"RprObjectRegistered token={m.Token} handle=0x{m.Handle.Value:X}"),
            RprObjectUpdate m => DescribeUpdate(m),
            RprObjectRemoved m => string.Create(c, $"RprObjectRemoved handle=0x{m.Handle.Value:X}"),
            RprWeaponFire m => string.Create(c,
                $"RprWeaponFire firing={Handle(m.FiringObject)} target={Handle(m.TargetObject)} munition={m.Munition.Category}/{m.Munition.Subcategory} qty={m.Quantity}"),
            RprMunitionDetonation m => string.Create(c,
                $"RprMunitionDetonation firing={Handle(m.FiringObject)} target={Handle(m.TargetObject)} result={m.Result}"),
            RprSimulationControl m => string.Create(c, $"RprSimulationControl {m.Kind}"),
            _ => message.GetType().Name,
        };
    }

    private static string DescribeUpdate(RprObjectUpdate m)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(string.Create(c, $"RprObjectUpdate handle=0x{m.Handle.Value:X}"));
        sb.Append(m.Marking is not null ? " (full)" : " (partial)");
        if (m.Marking is not null)
        {
            sb.Append(" marking=").Append(m.Marking);
        }

        if (m.EntityType is { } type)
        {
            sb.Append(string.Create(c, $" type={type.Kind}.{type.Domain}.{type.Country}.{type.Category}.{type.Subcategory}"));
        }

        if (m.WorldLocation is { } p)
        {
            sb.Append(string.Create(c, $" pos=({p.X:F1},{p.Y:F1},{p.Z:F1})"));
        }

        if (m.VelocityVector is { } v)
        {
            sb.Append(string.Create(c, $" vel=({v.X:F1},{v.Y:F1},{v.Z:F1})"));
        }

        if (m.DeadReckoningAlgorithm is { } dr)
        {
            sb.Append(string.Create(c, $" dr={dr}"));
        }

        if (m.DamageState is { } damage)
        {
            sb.Append(" damage=").Append(damage);
        }

        return sb.ToString();
    }

    private static string Handle(RprObjectHandle? handle) =>
        handle is { } h ? "0x" + h.Value.ToString("X", CultureInfo.InvariantCulture) : "none";

    private static string Describe(IDisplayMessage message)
    {
        var c = CultureInfo.InvariantCulture;
        return message switch
        {
            DisplayEntityPosition m => string.Create(c,
                $"DisplayEntityPosition {m.Label} lat={m.LatitudeDeg:F5} lon={m.LongitudeDeg:F5} alt={m.Altitude:F1} heading={m.HeadingDeg:F1}"),
            DisplayEntityRemoved m => string.Create(c, $"DisplayEntityRemoved {m.Label}"),
            _ => message.GetType().Name,
        };
    }
}
