namespace SimNetGateway.Engine;

internal abstract class Rule
{
    protected Rule(RuleInfo info) => Info = info;

    public RuleInfo Info { get; }

    /// <summary>De-duplication stamp of the trigger that last collected this rule.</summary>
    public long Stamp;

    public int Index => Info.RegistrationIndex;
}

/// <summary>A registered rule: conditions (ANDed, in order) and an action over a trigger object.</summary>
internal sealed class Rule<TTrigger> : Rule where TTrigger : class
{
    private readonly Func<TTrigger, bool>[] _conditions;
    private readonly Action<TTrigger> _action;

    public Rule(RuleInfo info, Func<TTrigger, bool>[] conditions, Action<TTrigger> action)
        : base(info)
    {
        _conditions = conditions;
        _action = action;
    }

    /// <summary>
    /// Runs the rule: skipped silently when disabled; a false condition counts a reject; an exception is
    /// caught, counted and diagnosed (the remaining rules still run). The outcome is reported to the observer
    /// (null: nobody listens) after the rule ran, outside the exception handling.
    /// </summary>
    public void Invoke(TTrigger trigger, Diagnostics diagnostics, IDispatchObserver? observer, CorrelationId correlation)
    {
        var info = Info;
        if (!info.Enabled)
        {
            observer?.RuleEvaluated(info, RuleOutcome.Disabled, correlation);
            return;
        }

        RuleOutcome outcome;
        try
        {
            outcome = Run(trigger, info);
        }
        catch (Exception ex)
        {
            info.Failures++;
            diagnostics.RuleFailed(info.FullName, ex.Message);
            outcome = RuleOutcome.Failed;
        }

        observer?.RuleEvaluated(info, outcome, correlation);
    }

    private RuleOutcome Run(TTrigger trigger, RuleInfo info)
    {
        var conditions = _conditions;
        for (int i = 0; i < conditions.Length; i++)
        {
            if (!conditions[i](trigger))
            {
                info.ConditionRejects++;
                return RuleOutcome.ConditionRejected;
            }
        }

        _action(trigger);
        info.FireCount++;
        return RuleOutcome.Fired;
    }
}
