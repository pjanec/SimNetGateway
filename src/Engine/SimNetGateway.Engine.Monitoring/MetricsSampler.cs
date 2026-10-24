namespace SimNetGateway.Engine.Monitoring;

/// <summary>A copy of the engine's counters at the end of one tick (plain numbers; nothing of the engine is kept).</summary>
public readonly record struct EngineCounters(
    long Tick,
    double WallTime,
    double SimTime,
    int Entities,
    long MessagesProcessed,
    long UnhandledMessages,
    long TransactionFailures,
    long RuleFailures,
    long CascadeOverflows,
    long SessionRejections,
    long EventsWithoutSubscriber)
{
    public static EngineCounters Of(GatewayEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var d = engine.Diagnostics;
        return new EngineCounters(engine.TickCount, engine.WallTime, engine.SimTime, engine.State.Count, d.MessagesProcessed,
            d.UnhandledMessages, d.TransactionFailures, d.RuleFailures, d.CascadeOverflows, d.SessionRejections,
            d.EventsWithoutSubscriber);
    }
}

/// <summary>One sample of the ring: when (seconds, the clock the host chose), how long the tick took, and the engine counters.</summary>
public readonly record struct TickSample(double Time, double TickMilliseconds, EngineCounters Counters);

/// <summary>
/// Per-tick ring of metrics (DD-005 section 3, Dashboard): tick time, the messages in and out per side as the host
/// reports them (cumulative totals, so the sampler needs no hooks in the channels) and the engine counters. Percentiles
/// and rates are computed over the most recent samples. Recording and querying allocate nothing.
/// <para>
/// Percentile: nearest rank on the sorted tick times of the window, rank = ceil(p / 100 * n) clamped to 1..n (p = 100 is the
/// maximum, p = 0 the minimum). Rate over a window of w samples: (total now - total w samples ago) / (time now - time
/// w samples ago), using w + 1 samples; fewer samples use what exists; fewer than two give 0.
/// </para>
/// </summary>
public sealed class MetricsSampler
{
    private readonly TickSample[] _ring;
    private readonly long[] _in;
    private readonly long[] _out;
    private readonly string[] _sides;
    private readonly double[] _scratch;
    private int _head;   // index of the oldest sample
    private int _count;

    public MetricsSampler(int capacity, IReadOnlyList<string> sides)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentNullException.ThrowIfNull(sides);
        _sides = sides.ToArray();
        _ring = new TickSample[capacity];
        _in = new long[capacity * Math.Max(1, _sides.Length)];
        _out = new long[capacity * Math.Max(1, _sides.Length)];
        _scratch = new double[capacity];
    }

    public int Capacity => _ring.Length;

    public int Count => _count;

    /// <summary>The sides whose in/out totals are recorded, in the order the totals are passed.</summary>
    public IReadOnlyList<string> Sides => _sides;

    /// <summary>The sample at an age: 0 = oldest retained, <see cref="Count"/> - 1 = newest.</summary>
    public TickSample this[int index]
    {
        get
        {
            CheckIndex(index);
            return _ring[Slot(index)];
        }
    }

    public TickSample Latest => this[_count - 1];

    /// <summary>Cumulative messages received from the side as of the sample (index as in the indexer).</summary>
    public long InTotal(int index, int side)
    {
        CheckIndex(index);
        return _in[Slot(index) * _sides.Length + side];
    }

    /// <summary>Cumulative messages sent to the side as of the sample.</summary>
    public long OutTotal(int index, int side)
    {
        CheckIndex(index);
        return _out[Slot(index) * _sides.Length + side];
    }

    /// <summary>Records the end of a tick: the counters are read from the engine, the time is the engine's wall time.</summary>
    public void Record(GatewayEngine engine, double tickMilliseconds, ReadOnlySpan<long> inTotals, ReadOnlySpan<long> outTotals)
    {
        var counters = EngineCounters.Of(engine);
        Record(new TickSample(counters.WallTime, tickMilliseconds, counters), inTotals, outTotals);
    }

    /// <summary>Records one sample; the totals hold one cumulative figure per side (a short span counts the rest as 0).</summary>
    public void Record(in TickSample sample, ReadOnlySpan<long> inTotals, ReadOnlySpan<long> outTotals)
    {
        int slot;
        if (_count == _ring.Length)
        {
            slot = _head;
            _head = (_head + 1) % _ring.Length;
        }
        else
        {
            slot = (_head + _count) % _ring.Length;
            _count++;
        }

        _ring[slot] = sample;
        int n = _sides.Length;
        for (int s = 0; s < n; s++)
        {
            _in[slot * n + s] = s < inTotals.Length ? inTotals[s] : 0;
            _out[slot * n + s] = s < outTotals.Length ? outTotals[s] : 0;
        }
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }

    // ---- tick time statistics ---------------------------------------------------------------------------------

    /// <summary>The p-th percentile (0..100, nearest rank) of the tick times of the last <paramref name="window"/> samples (0 = all).</summary>
    public double Percentile(double p, int window = 0)
    {
        int n = Window(window);
        if (n == 0)
        {
            return 0;
        }

        for (int i = 0; i < n; i++)
        {
            _scratch[i] = _ring[Slot(_count - n + i)].TickMilliseconds;
        }

        Array.Sort(_scratch, 0, n);
        int rank = (int)Math.Ceiling(p / 100.0 * n);
        rank = Math.Clamp(rank, 1, n);
        return _scratch[rank - 1];
    }

    public double Max(int window = 0) => Percentile(100, window);

    public double Min(int window = 0) => Percentile(0, window);

    public double Mean(int window = 0)
    {
        int n = Window(window);
        if (n == 0)
        {
            return 0;
        }

        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += _ring[Slot(_count - n + i)].TickMilliseconds;
        }

        return sum / n;
    }

    // ---- rates ------------------------------------------------------------------------------------------------

    /// <summary>Messages per second received from the side over the last <paramref name="window"/> intervals (0 = all retained).</summary>
    public double InRate(int side, int window = 0) => Rate(_in, side, window);

    public double OutRate(int side, int window = 0) => Rate(_out, side, window);

    /// <summary>Engine ticks per second over the window.</summary>
    public double TickRate(int window = 0)
    {
        if (!Span(window, out int first, out int last))
        {
            return 0;
        }

        double dt = _ring[Slot(last)].Time - _ring[Slot(first)].Time;
        return dt > 0 ? (_ring[Slot(last)].Counters.Tick - _ring[Slot(first)].Counters.Tick) / dt : 0;
    }

    /// <summary>Per-second rate of any engine counter over the window, e.g. <c>c =&gt; c.TransactionFailures</c>.</summary>
    public double CounterRate(Func<EngineCounters, long> counter, int window = 0)
    {
        ArgumentNullException.ThrowIfNull(counter);
        if (!Span(window, out int first, out int last))
        {
            return 0;
        }

        double dt = _ring[Slot(last)].Time - _ring[Slot(first)].Time;
        return dt > 0 ? (counter(_ring[Slot(last)].Counters) - counter(_ring[Slot(first)].Counters)) / dt : 0;
    }

    private double Rate(long[] totals, int side, int window)
    {
        if ((uint)side >= (uint)_sides.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(side));
        }

        if (!Span(window, out int first, out int last))
        {
            return 0;
        }

        double dt = _ring[Slot(last)].Time - _ring[Slot(first)].Time;
        long delta = totals[Slot(last) * _sides.Length + side] - totals[Slot(first) * _sides.Length + side];
        return dt > 0 ? delta / dt : 0;
    }

    /// <summary>The ages of the first and last sample of a rate window of <paramref name="window"/> intervals.</summary>
    private bool Span(int window, out int first, out int last)
    {
        last = _count - 1;
        first = 0;
        if (_count < 2)
        {
            return false;
        }

        int intervals = window <= 0 ? _count - 1 : Math.Min(window, _count - 1);
        first = last - intervals;
        return true;
    }

    private int Window(int window) => window <= 0 ? _count : Math.Min(window, _count);

    private int Slot(int age) => (_head + age) % _ring.Length;

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
