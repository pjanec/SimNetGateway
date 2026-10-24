using System.Globalization;
using System.Text;
using SimNetGateway.Engine;

namespace SimNetGateway.Showcase;

/// <summary>
/// The verdict of one <see cref="CheckStep"/>: passed or not, what was found, and - when a condition of the design cannot be
/// checked as written - the design question (the closest faithful check is made and the question is listed in the report).
/// </summary>
public readonly record struct CheckResult(bool Passed, string Details, string? DesignQuestion = null)
{
    public static CheckResult Pass(string details = "") => new(true, details);

    public static CheckResult Fail(string details) => new(false, details);
}

/// <summary>
/// Collects the findings of a check: <see cref="That(bool, string)"/> records a failed assertion, <see cref="Note(string)"/> a
/// fact worth showing in the report (shown for PASS and FAIL alike), <see cref="Question(string)"/> a design question.
/// </summary>
public sealed class Verdict
{
    private readonly List<string> _failures = new();
    private readonly List<string> _notes = new();
    private readonly List<string> _questions = new();

    /// <summary>Asserts a condition; the message says what is wrong when it does not hold.</summary>
    public Verdict That(bool condition, string failure)
    {
        if (!condition)
        {
            _failures.Add(failure);
        }

        return this;
    }

    public Verdict Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            _failures.Add(string.Create(CultureInfo.InvariantCulture, $"{what}: expected {expected}, got {actual}"));
        }

        return this;
    }

    public Verdict Note(string text)
    {
        _notes.Add(text);
        return this;
    }

    /// <summary>Records a design question: the condition cannot be checked as written (the reason says why).</summary>
    public Verdict Question(string text)
    {
        _questions.Add(text);
        return this;
    }

    public bool Failed => _failures.Count > 0;

    public CheckResult Result()
    {
        var details = new StringBuilder();
        foreach (string failure in _failures)
        {
            details.Append("FAILED: ").Append(failure).Append("; ");
        }

        foreach (string note in _notes)
        {
            details.Append(note).Append("; ");
        }

        string text = details.ToString().TrimEnd(';', ' ');
        string? question = _questions.Count == 0 ? null : string.Join(" ", _questions);
        return new CheckResult(_failures.Count == 0, text, question);
    }
}

/// <summary>One step of an act. <see cref="Time"/> is on the runner clock (ManualClock wall time from 0), not sim time.</summary>
public abstract class Step
{
    protected Step(double time)
    {
        if (time < 0 || double.IsNaN(time))
        {
            throw new ArgumentOutOfRangeException(nameof(time));
        }

        Time = time;
    }

    /// <summary>Runner time in seconds; a step is executed in the tick whose time is the nearest to it (60 Hz).</summary>
    public double Time { get; }
}

/// <summary>Enqueues a typed message on an input side (through <see cref="NedRprHost.SendNed{T}"/> / <see cref="NedRprHost.SendRpr{T}"/>).</summary>
public sealed class InjectStep : Step
{
    public InjectStep(double time, string description, Action<NedRprHost> enqueue, bool quiet = false)
        : base(time)
    {
        ArgumentNullException.ThrowIfNull(enqueue);
        Description = description;
        Enqueue = enqueue;
        Quiet = quiet;
    }

    public string Description { get; }

    public Action<NedRprHost> Enqueue { get; }

    /// <summary>True: the timeline groups this input per second instead of listing it (keep-alive traffic).</summary>
    public bool Quiet { get; }
}

/// <summary>An operator action: enable or disable a rule by its full name ("Module/Name").</summary>
public sealed class OperatorStep : Step
{
    public OperatorStep(double time, string ruleFullName, bool enable)
        : base(time)
    {
        ArgumentException.ThrowIfNullOrEmpty(ruleFullName);
        RuleFullName = ruleFullName;
        Enable = enable;
    }

    public string RuleFullName { get; }

    public bool Enable { get; }
}

/// <summary>A harness switch: the loopback tap, arming the faulty module, kicking the rogue processor, noting a value for a later check.</summary>
public sealed class ActionStep : Step
{
    public ActionStep(double time, string description, Action<ScenarioContext> run)
        : base(time)
    {
        ArgumentNullException.ThrowIfNull(run);
        Description = description;
        Run = run;
    }

    public string Description { get; }

    public Action<ScenarioContext> Run { get; }
}

/// <summary>
/// A success condition checked after the tick of its time. Several steps may carry the same id (a condition that is checked at
/// different times): the condition passes when all of them pass.
/// </summary>
public sealed class CheckStep : Step
{
    public CheckStep(double time, string id, Func<ScenarioContext, CheckResult> check)
        : base(time)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(check);
        Id = id;
        Check = check;
    }

    public string Id { get; }

    public Func<ScenarioContext, CheckResult> Check { get; }
}

/// <summary>An act of the script: a number, a title, its steps and the diagnostics it expects (G-7).</summary>
public sealed class Act
{
    public Act(int number, string title, IReadOnlyList<Step> steps, IReadOnlyList<ExpectedDiagnostic> expectedDiagnostics)
    {
        Number = number;
        Title = title;
        Steps = steps;
        ExpectedDiagnostics = expectedDiagnostics;
    }

    public int Number { get; }

    public string Title { get; }

    public IReadOnlyList<Step> Steps { get; }

    /// <summary>The warnings and errors that may appear while this act runs (from its first step to the first step of the next act).</summary>
    public IReadOnlyList<ExpectedDiagnostic> ExpectedDiagnostics { get; }
}

/// <summary>A success condition of the design: its id (<c>S-a.n</c>) and its text.</summary>
public sealed record SuccessCondition(string Id, string Text);

/// <summary>
/// A scripted scenario: acts with steps at runner times, the success conditions it must evaluate and the runner time at which it
/// ends. Acts start at their <see cref="ActStart"/> times; the diagnostics whitelist of an act is active from then until the
/// next act starts.
/// </summary>
public sealed class ScenarioScript
{
    public ScenarioScript(string name, IReadOnlyList<Act> acts, IReadOnlyList<double> actStarts, IReadOnlyList<SuccessCondition> conditions,
        double endTime)
    {
        if (acts.Count != actStarts.Count)
        {
            throw new ArgumentException("One start time per act.", nameof(actStarts));
        }

        Name = name;
        Acts = acts;
        ActStarts = actStarts;
        Conditions = conditions;
        EndTime = endTime;
    }

    public string Name { get; }

    public IReadOnlyList<Act> Acts { get; }

    /// <summary>The runner time at which each act (and its diagnostics whitelist) begins.</summary>
    public IReadOnlyList<double> ActStarts { get; }

    public double ActStart(int index) => ActStarts[index];

    /// <summary>Every success condition the script has to evaluate; one that is never evaluated fails.</summary>
    public IReadOnlyList<SuccessCondition> Conditions { get; }

    /// <summary>Runner time at which the run stops.</summary>
    public double EndTime { get; }
}

/// <summary>Builds an <see cref="Act"/> step by step (the script reads like the table of the design).</summary>
public sealed class ActBuilder
{
    private readonly List<Step> _steps = new();
    private readonly List<ExpectedDiagnostic> _expected = new();

    public ActBuilder(int number, string title, double start)
    {
        Number = number;
        Title = title;
        Start = start;
    }

    public int Number { get; }

    public string Title { get; }

    public double Start { get; }

    public ActBuilder Inject(double time, string description, Action<NedRprHost> enqueue, bool quiet = false)
    {
        _steps.Add(new InjectStep(time, description, enqueue, quiet));
        return this;
    }

    public ActBuilder Operator(double time, string ruleFullName, bool enable)
    {
        _steps.Add(new OperatorStep(time, ruleFullName, enable));
        return this;
    }

    public ActBuilder Do(double time, string description, Action<ScenarioContext> run)
    {
        _steps.Add(new ActionStep(time, description, run));
        return this;
    }

    public ActBuilder Check(double time, string id, Func<ScenarioContext, CheckResult> check)
    {
        _steps.Add(new CheckStep(time, id, check));
        return this;
    }

    /// <summary>Declares a diagnostic (category and message part) that this act expects.</summary>
    public ActBuilder Expect(string? category, string messagePart, DiagnosticSeverity? severity = null)
    {
        _expected.Add(new ExpectedDiagnostic(category, messagePart, severity));
        return this;
    }

    public Act Build() => new(Number, Title, _steps.ToArray(), _expected.ToArray());
}
