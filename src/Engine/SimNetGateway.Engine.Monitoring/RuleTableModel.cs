namespace SimNetGateway.Engine.Monitoring;

/// <summary>One rule with its counters and their rates per second (a copy of <see cref="RuleInfo"/> at the refresh).</summary>
public readonly record struct RuleRow(
    int RegistrationIndex,
    string Module,
    string Name,
    string FullName,
    string Trigger,
    bool Enabled,
    long FireCount,
    long ConditionRejects,
    long Failures,
    double FireRate,
    double RejectRate,
    double FailureRate);

/// <summary>Which rules the table shows; every set criterion must hold.</summary>
public sealed record RuleFilter
{
    /// <summary>Only rules that failed at least once.</summary>
    public bool Failed { get; init; }

    /// <summary>Only rules whose condition rejected at least once.</summary>
    public bool Rejecting { get; init; }

    /// <summary>Only rules that never ran to completion.</summary>
    public bool NeverFired { get; init; }

    /// <summary>Case-insensitive text the rule's full name or trigger contains.</summary>
    public string? Text { get; init; }
}

/// <summary>
/// The Rules panel's model (DD-005 section 3): every rule with its counters and the rates of fires, condition rejects and
/// failures per second of wall time. Each refresh remembers the counters; the rate compares them with the oldest remembered
/// refresh that is not older than <see cref="RateWindow"/> seconds (a rate is 0 when that is the refresh itself or
/// no time passed). Rows are copies and are in registration order.
/// </summary>
public sealed class RuleTableModel
{
    private readonly GatewayEngine _engine;
    private readonly Snapshot[] _history;
    private int _head;
    private int _count;
    private readonly List<RuleRow> _rows = new();

    private sealed class Snapshot
    {
        public double Time;
        public long[] Fires = Array.Empty<long>();
        public long[] Rejects = Array.Empty<long>();
        public long[] Failures = Array.Empty<long>();
    }

    public RuleTableModel(GatewayEngine engine, double rateWindowSeconds = 1.0, int historySize = 128)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historySize);
        _engine = engine;
        RateWindow = rateWindowSeconds;
        _history = new Snapshot[historySize];
        for (int i = 0; i < historySize; i++)
        {
            _history[i] = new Snapshot();
        }
    }

    public double RateWindow { get; set; }

    public RuleFilter Filter { get; set; } = new();

    public IReadOnlyList<RuleRow> Rows => _rows;

    /// <summary>Rules registered at the last refresh (before filtering).</summary>
    public int TotalRules { get; private set; }

    public void Refresh()
    {
        var rules = _engine.Rules;
        int n = rules.Count;
        TotalRules = n;
        double now = _engine.WallTime;

        // Push a snapshot of the counters.
        Snapshot current;
        if (_count > 0 && _history[Slot(_count - 1)].Fires.Length != n)
        {
            _count = 0; // the rule set changed: the old counters do not line up
            _head = 0;
        }

        if (_count == _history.Length)
        {
            current = _history[_head];
            _head = (_head + 1) % _history.Length;
        }
        else
        {
            current = _history[(_head + _count) % _history.Length];
            _count++;
        }

        if (current.Fires.Length != n)
        {
            current.Fires = new long[n];
            current.Rejects = new long[n];
            current.Failures = new long[n];
        }

        current.Time = now;
        for (int i = 0; i < n; i++)
        {
            current.Fires[i] = rules[i].FireCount;
            current.Rejects[i] = rules[i].ConditionRejects;
            current.Failures[i] = rules[i].Failures;
        }

        // The baseline: the oldest remembered snapshot not older than the window.
        Snapshot? baseline = null;
        for (int age = 0; age < _count; age++)
        {
            var candidate = _history[Slot(age)];
            if (candidate.Time >= now - RateWindow)
            {
                baseline = candidate;
                break;
            }
        }

        double dt = baseline is null ? 0 : now - baseline.Time;
        var filter = Filter;
        string? text = string.IsNullOrEmpty(filter.Text) ? null : filter.Text;
        _rows.Clear();
        for (int i = 0; i < n; i++)
        {
            var rule = rules[i];
            if (filter.Failed && rule.Failures == 0)
            {
                continue;
            }

            if (filter.Rejecting && rule.ConditionRejects == 0)
            {
                continue;
            }

            if (filter.NeverFired && rule.FireCount != 0)
            {
                continue;
            }

            if (text is not null && rule.FullName.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0
                && rule.Trigger.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            double fire = 0, reject = 0, failure = 0;
            if (dt > 0 && baseline is not null)
            {
                fire = (rule.FireCount - baseline.Fires[i]) / dt;
                reject = (rule.ConditionRejects - baseline.Rejects[i]) / dt;
                failure = (rule.Failures - baseline.Failures[i]) / dt;
            }

            _rows.Add(new RuleRow(rule.RegistrationIndex, rule.Module, rule.Name, rule.FullName, rule.Trigger, rule.Enabled,
                rule.FireCount, rule.ConditionRejects, rule.Failures, fire, reject, failure));
        }
    }

    private int Slot(int age) => (_head + age) % _history.Length;
}
