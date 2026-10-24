using System.Globalization;
using System.Text;

namespace SimNetGateway.Showcase;

/// <summary>One line of the timeline: an input, an output of the gateway, an operator action, a diagnostic, ...</summary>
public sealed class TimelineEntry
{
    public TimelineEntry(long tick, double runnerTime, double simTime, int act, string kind, string side, string text, string? groupKey = null)
    {
        Tick = tick;
        RunnerTime = runnerTime;
        SimTime = simTime;
        Act = act;
        Kind = kind;
        Side = side;
        Text = text;
        GroupKey = groupKey;
    }

    public long Tick { get; }

    /// <summary>ManualClock wall time of the tick (the script's clock).</summary>
    public double RunnerTime { get; }

    /// <summary>Engine sim time of the tick (resets at Load, frozen while paused).</summary>
    public double SimTime { get; internal set; }

    public int Act { get; }

    /// <summary>"act", "in", "out", "loop", "op", "action", "diag", "check", "violation".</summary>
    public string Kind { get; }

    /// <summary>"NED", "RPR", "DISPLAY" or "GW".</summary>
    public string Side { get; }

    public string Text { get; }

    /// <summary>Entries with the same non-null key within one runner second are printed as one line with a count.</summary>
    public string? GroupKey { get; }
}

/// <summary>The outcome of one success condition: every evaluation of it (it may be checked at several times).</summary>
public sealed class ConditionResult
{
    private readonly List<(double Time, bool Passed, string Details)> _evaluations = new();
    private readonly List<string> _questions = new();

    public ConditionResult(string id, string text, int act)
    {
        Id = id;
        Text = text;
        Act = act;
    }

    public string Id { get; }

    public string Text { get; }

    public int Act { get; }

    public IReadOnlyList<(double Time, bool Passed, string Details)> Evaluations => _evaluations;

    /// <summary>Design questions: the condition cannot be checked exactly as written (the closest faithful check was made).</summary>
    public IReadOnlyList<string> DesignQuestions => _questions;

    public bool Evaluated => _evaluations.Count > 0;

    public bool Passed
    {
        get
        {
            if (_evaluations.Count == 0)
            {
                return false;
            }

            foreach (var evaluation in _evaluations)
            {
                if (!evaluation.Passed)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal void Add(double time, CheckResult result)
    {
        _evaluations.Add((time, result.Passed, result.Details));
        if (result.DesignQuestion is { } question && !_questions.Contains(question))
        {
            _questions.Add(question);
        }
    }
}

/// <summary>A violation of a global invariant (G-1 .. G-8) at a tick.</summary>
public sealed record GViolation(string Id, long Tick, double RunnerTime, double SimTime, string Detail);

/// <summary>
/// What a scenario run produced: the timeline (inputs, outputs per side, diagnostics, each with runner time and sim time), the
/// result per success condition with its details, the violations of the global invariants with tick and detail, and the overall
/// verdict. <see cref="Render"/> and <see cref="RenderSummary"/> are deterministic text: two runs of the same script give the same
/// bytes.
/// </summary>
public sealed class ScenarioReport
{
    internal ScenarioReport(string name, IReadOnlyList<TimelineEntry> timeline, IReadOnlyList<ConditionResult> conditions,
        IReadOnlyList<GViolation> violations, long ticks, double endTime, IReadOnlyList<string> finalState,
        IReadOnlyList<string> stepFailures)
    {
        Name = name;
        Timeline = timeline;
        Conditions = conditions;
        Violations = violations;
        Ticks = ticks;
        EndTime = endTime;
        FinalState = finalState;
        StepFailures = stepFailures;
    }

    public string Name { get; }

    public IReadOnlyList<TimelineEntry> Timeline { get; }

    /// <summary>The declared conditions in script order, then any extra condition the script evaluated.</summary>
    public IReadOnlyList<ConditionResult> Conditions { get; }

    public IReadOnlyList<GViolation> Violations { get; }

    public long Ticks { get; }

    public double EndTime { get; }

    /// <summary>A few lines about the state at the end of the run (empty repository, allocators, session).</summary>
    public IReadOnlyList<string> FinalState { get; }

    /// <summary>Steps (injections, operator and action steps) that threw; a run with such a step does not pass.</summary>
    public IReadOnlyList<string> StepFailures { get; }

    public bool Passed
    {
        get
        {
            if (Violations.Count > 0 || StepFailures.Count > 0)
            {
                return false;
            }

            foreach (var condition in Conditions)
            {
                if (!condition.Passed)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public ConditionResult? Find(string id)
    {
        foreach (var condition in Conditions)
        {
            if (string.Equals(condition.Id, id, StringComparison.Ordinal))
            {
                return condition;
            }
        }

        return null;
    }

    /// <summary>The timeline followed by the summary.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("Showcase \"").Append(Name).Append("\" - timeline (runner time and sim time in seconds; <- input, -> output of the gateway)\n");
        RenderTimeline(sb);
        sb.Append('\n').Append(RenderSummary());
        return sb.ToString();
    }

    /// <summary>The PASS/FAIL table of the success conditions, the invariant violations and the overall verdict.</summary>
    public string RenderSummary()
    {
        var sb = new StringBuilder();
        sb.Append("Success conditions\n");
        var questions = new List<(string Id, string Question)>();
        foreach (var c in Conditions)
        {
            string verdict = !c.Evaluated ? "FAIL" : c.Passed ? "PASS" : "FAIL";
            sb.Append("  ").Append(c.Id.PadRight(7)).Append(verdict).Append(c.DesignQuestions.Count > 0 ? "* " : "  ").Append(c.Text).Append('\n');
            if (!c.Evaluated)
            {
                sb.Append("          not evaluated\n");
            }

            foreach (var evaluation in c.Evaluations)
            {
                if (!evaluation.Passed || evaluation.Details.Length > 0)
                {
                    sb.Append(string.Create(CultureInfo.InvariantCulture, $"          r={evaluation.Time:F3} {(evaluation.Passed ? "ok" : "FAILED")}: {evaluation.Details}\n"));
                }
            }

            foreach (string question in c.DesignQuestions)
            {
                questions.Add((c.Id, question));
            }
        }

        sb.Append('\n').Append("Global invariants G-1 .. G-8 (checked after every tick)\n");
        if (Violations.Count == 0)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"  no violation in {Ticks} ticks\n"));
        }
        else
        {
            foreach (var v in Violations)
            {
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"  {v.Id} at tick {v.Tick} (r={v.RunnerTime:F3}, sim={v.SimTime:F3}): {v.Detail}\n"));
            }
        }

        if (StepFailures.Count > 0)
        {
            sb.Append('\n').Append("Steps that threw\n");
            foreach (string failure in StepFailures)
            {
                sb.Append("  ").Append(failure).Append('\n');
            }
        }

        if (questions.Count > 0)
        {
            sb.Append('\n').Append("Design questions (* above): the condition could not be checked exactly as written\n");
            foreach (var (id, question) in questions)
            {
                sb.Append("  ").Append(id).Append(": ").Append(question).Append('\n');
            }
        }

        if (FinalState.Count > 0)
        {
            sb.Append('\n').Append("Final state\n");
            foreach (string line in FinalState)
            {
                sb.Append("  ").Append(line).Append('\n');
            }
        }

        int passed = Conditions.Count(c => c.Passed);
        sb.Append('\n').Append(string.Create(CultureInfo.InvariantCulture,
            $"{passed} of {Conditions.Count} success conditions passed, {Violations.Count} invariant violations, {Ticks} ticks, {EndTime:F2} s\n"));
        sb.Append("Result: ").Append(Passed ? "PASS" : "FAIL").Append('\n');
        return sb.ToString();
    }

    private void RenderTimeline(StringBuilder sb)
    {
        var groups = new Dictionary<(long Second, string Key), List<TimelineEntry>>();
        foreach (var entry in Timeline)
        {
            if (entry.GroupKey is { } key)
            {
                var slot = (SecondOf(entry), key);
                if (!groups.TryGetValue(slot, out var list))
                {
                    groups[slot] = list = new List<TimelineEntry>();
                }

                list.Add(entry);
            }
        }

        var printed = new HashSet<(long, string)>();
        foreach (var entry in Timeline)
        {
            if (entry.GroupKey is { } key)
            {
                var slot = (SecondOf(entry), key);
                if (!printed.Add(slot))
                {
                    continue;
                }

                var list = groups[slot];
                sb.Append(Prefix(entry)).Append(Arrow(entry)).Append(entry.Side.PadRight(8)).Append(Summarize(list, key)).Append('\n');
                continue;
            }

            sb.Append(Prefix(entry)).Append(Arrow(entry)).Append(entry.Side.PadRight(8)).Append(entry.Text).Append('\n');
        }
    }

    private static long SecondOf(TimelineEntry entry) => (long)Math.Floor(entry.RunnerTime + 1e-9);

    private static string Prefix(TimelineEntry e) =>
        string.Create(CultureInfo.InvariantCulture, $"r={e.RunnerTime,8:F3} sim={e.SimTime,8:F3} A{e.Act,-2} ");

    private static string Arrow(TimelineEntry e) => e.Kind switch
    {
        "in" => "<- ",
        "out" => "-> ",
        "loop" => "~> ",
        "op" => "## ",
        "action" => "## ",
        "diag" => "!! ",
        "act" => "== ",
        _ => "   ",
    };

    private static string Summarize(List<TimelineEntry> list, string key)
    {
        // "<type> xN (label xK, ...)" with the labels in order of first appearance
        var counts = new List<(string Label, int Count)>();
        foreach (var e in list)
        {
            int i = counts.FindIndex(c => c.Label == e.Text);
            if (i < 0)
            {
                counts.Add((e.Text, 1));
            }
            else
            {
                counts[i] = (e.Text, counts[i].Count + 1);
            }
        }

        var sb = new StringBuilder();
        sb.Append(key).Append(string.Create(CultureInfo.InvariantCulture, $" x{list.Count} in r={SecondOf(list[0])}..{SecondOf(list[0]) + 1} ("));
        for (int i = 0; i < counts.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(counts[i].Label).Append(string.Create(CultureInfo.InvariantCulture, $" x{counts[i].Count}"));
        }

        return sb.Append(')').ToString();
    }
}
