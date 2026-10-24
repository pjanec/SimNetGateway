using SimNetGateway.Engine;
using SimNetGateway.Engine.Monitoring;

namespace SimNetGateway.Viewer;

/// <summary>Per-side figures of the dashboard.</summary>
public readonly record struct SideRates(string Side, double InPerSecond, double OutPerSecond, long InTotal, long OutTotal);

/// <summary>Entities of one origin.</summary>
public readonly record struct OriginCount(string Origin, int Count);

/// <summary>
/// The Dashboard panel's model (DD-005 section 3): session, times, tick time statistics and sparkline data from the
/// <see cref="MetricsSampler"/>, messages per second per side, entities per origin, the engine's counters and the fill of the
/// journal and event log (or "history off", R-070). Refreshed every frame (R-075); everything is a copy.
/// </summary>
public sealed class DashboardModel
{
    /// <summary>Samples that the rates and percentiles look at (about two seconds at 60 Hz).</summary>
    public const int Window = 120;

    private readonly GatewayEngine _engine;
    private readonly MetricsSampler _sampler;
    private readonly List<SideRates> _sides = new();
    private readonly List<OriginCount> _origins = new();
    private readonly Dictionary<SourceId, int> _originCounts = new();
    private float[] _tickTimes = new float[Window];
    private int _tickTimeCount;

    public DashboardModel(GatewayEngine engine, MetricsSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(sampler);
        _engine = engine;
        _sampler = sampler;
    }

    public SessionState SessionState { get; private set; }

    public SessionMode SessionMode { get; private set; }

    public string? SessionScenario { get; private set; }

    public double SimTime { get; private set; }

    /// <summary>The engine's wall time: the manual clock, 60 ticks per second.</summary>
    public double WallTime { get; private set; }

    public long Ticks { get; private set; }

    public int Entities { get; private set; }

    public EngineCounters Counters { get; private set; }

    public double LatestTickMs { get; private set; }

    public double TickP50 { get; private set; }

    public double TickP95 { get; private set; }

    public double TickMax { get; private set; }

    /// <summary>Engine ticks per second of the manual clock (60 at x1).</summary>
    public double TickRate { get; private set; }

    public IReadOnlyList<SideRates> Sides => _sides;

    public IReadOnlyList<OriginCount> Origins => _origins;

    /// <summary>The most recent tick times, oldest first (for the sparkline).</summary>
    public ReadOnlySpan<float> TickTimes => new(_tickTimes, 0, _tickTimeCount);

    /// <summary>The array behind <see cref="TickTimes"/> and its used length (for plotting without a copy).</summary>
    public float[] TickTimeBuffer => _tickTimes;

    public int TickTimeCount => _tickTimeCount;

    public int JournalRecords { get; private set; }

    public int JournalCapacity { get; private set; }

    public int EventsRetained { get; private set; }

    public int EventCapacity { get; private set; }

    /// <summary>The journal has capacity 0 (R-070).</summary>
    public bool HistoryOff => JournalCapacity == 0;

    public bool EventsOff => EventCapacity == 0;

    /// <summary>Warnings and errors in the retained diagnostics (updated with the tables, 4 Hz).</summary>
    public int Warnings { get; private set; }

    public int Errors { get; private set; }

    public void Refresh()
    {
        var engine = _engine;
        var session = engine.Session;
        SessionState = session.State;
        SessionMode = session.Mode;
        SessionScenario = session.Scenario;
        SimTime = engine.SimTime;
        WallTime = engine.WallTime;
        Ticks = engine.TickCount;
        Entities = engine.State.Count;
        Counters = EngineCounters.Of(engine);

        var sampler = _sampler;
        int n = sampler.Count;
        if (n > 0)
        {
            LatestTickMs = sampler.Latest.TickMilliseconds;
            TickP50 = sampler.Percentile(50, Window);
            TickP95 = sampler.Percentile(95, Window);
            TickMax = sampler.Max(Window);
            TickRate = sampler.TickRate(Window);
        }
        else
        {
            LatestTickMs = TickP50 = TickP95 = TickMax = TickRate = 0;
        }

        int count = Math.Min(n, Window);
        if (_tickTimes.Length < count)
        {
            _tickTimes = new float[count];
        }

        for (int i = 0; i < count; i++)
        {
            _tickTimes[i] = (float)sampler[n - count + i].TickMilliseconds;
        }

        _tickTimeCount = count;

        _sides.Clear();
        for (int s = 0; s < sampler.Sides.Count; s++)
        {
            _sides.Add(new SideRates(sampler.Sides[s], sampler.InRate(s, Window), sampler.OutRate(s, Window),
                n > 0 ? sampler.InTotal(n - 1, s) : 0, n > 0 ? sampler.OutTotal(n - 1, s) : 0));
        }

        _originCounts.Clear();
        foreach (var entity in engine.State.Entities)
        {
            _originCounts[entity.Origin] = _originCounts.TryGetValue(entity.Origin, out int c) ? c + 1 : 1;
        }

        _origins.Clear();
        foreach (var (origin, c) in _originCounts)
        {
            _origins.Add(new OriginCount(engine.Sources.NameOf(origin), c));
        }

        _origins.Sort((a, b) => string.CompareOrdinal(a.Origin, b.Origin));

        var journal = engine.State.Journal;
        JournalRecords = journal.Count;
        JournalCapacity = journal.Capacity;
        EventsRetained = journal.EventCount;
        EventCapacity = journal.EventCapacity;
    }

    /// <summary>Counts the warnings and errors in the retained diagnostics (called with the tables' refresh).</summary>
    public void UpdateDiagnostics()
    {
        int warnings = 0, errors = 0;
        foreach (var entry in _engine.Diagnostics.Recent)
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

        Warnings = warnings;
        Errors = errors;
    }
}
