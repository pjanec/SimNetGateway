using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// The acceptance test of the showcase "Convoy Ambush" (DD-002, R-043): the whole script runs on the manual clock; every success
/// condition S-* is a row of a theory (so a failure names it), no global invariant G-* is violated at any tick, the final state
/// is empty, and the report is the same text in two runs.
/// </summary>
public class ShowcaseAcceptanceTests
{
    /// <summary>The success conditions of DD-002 section 5, listed here again so that a condition dropped from the script is noticed.</summary>
    private static readonly string[] DesignConditions =
    [
        "S-0.1", "S-0.2",
        "S-1.1", "S-1.2", "S-1.3", "S-1.4",
        "S-2.1", "S-2.2", "S-2.3",
        "S-3.1", "S-3.2", "S-3.3", "S-3.4", "S-3.5",
        "S-4.1", "S-4.2", "S-4.3", "S-4.4", "S-4.5",
        "S-5.1", "S-5.2", "S-5.3", "S-5.4", "S-5.5", "S-5.6",
        "S-6.1", "S-6.2", "S-6.3",
        "S-7.1", "S-7.2", "S-7.3", "S-7.4", "S-7.5", "S-7.6",
        "S-8.1", "S-8.2", "S-8.3",
        "S-9.1", "S-9.2", "S-9.3", "S-9.4",
        "S-10.1", "S-10.2", "S-10.3",
        "S-11.1", "S-11.2", "S-11.3",
    ];

    private sealed record Outcome(ScenarioReport Report, ScenarioContext Context);

    private static readonly Lazy<Outcome> Shared = new(() =>
    {
        var runner = new ScenarioRunner();
        var report = runner.Run(ConvoyAmbushScript.Create());
        return new Outcome(report, runner.Context!);
    });

    private static ScenarioReport Report => Shared.Value.Report;

    public static IEnumerable<object[]> ConditionIds() => ConvoyAmbushScript.Conditions.Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(ConditionIds))]
    public void The_success_condition_passes(string id)
    {
        var condition = Report.Find(id);
        Assert.NotNull(condition);
        Assert.True(condition!.Evaluated, id + " was never evaluated");
        Assert.True(condition.Passed, id + " failed:\n" + string.Join("\n", condition.Evaluations.Where(e => !e.Passed).Select(e => $"  r={e.Time:F3}: {e.Details}")));
    }

    [Fact]
    public void The_script_declares_every_condition_of_the_design()
    {
        var declared = ConvoyAmbushScript.Conditions.Select(c => c.Id).ToHashSet();
        foreach (string id in DesignConditions)
        {
            Assert.Contains(id, declared);
        }

        Assert.Equal(declared.Count, ConvoyAmbushScript.Conditions.Count); // no id twice
    }

    [Fact]
    public void No_global_invariant_is_violated_at_any_tick()
    {
        var report = Report;
        Assert.True(report.Violations.Count == 0, string.Join("\n", report.Violations.Select(v => $"{v.Id} at tick {v.Tick}: {v.Detail}")));
        Assert.Equal(8400, report.Ticks); // 140 s at 60 Hz: the invariants were checked after every one of them
    }

    [Fact]
    public void No_step_of_the_script_throws()
    {
        Assert.Empty(Report.StepFailures);
    }

    [Fact]
    public void The_final_state_is_empty_as_in_S_10_2_and_S_11_3()
    {
        // the context of the runner holds the state after the script ended
        var ctx = Shared.Value.Context;
        Assert.True(Report.Passed, Report.RenderSummary());
        Assert.Equal(0, ctx.Engine.State.Count);
        foreach (var map in ctx.Engine.Identities.All)
        {
            Assert.Equal(0, map.Count);
        }

        Assert.Equal(ctx.NedRange.Capacity, ctx.NedRange.Available);
        Assert.Equal(0, ctx.DisPool.InUse);
        Assert.Equal(SimNetGateway.Engine.SessionState.Idle, ctx.Engine.Session.State);
        Assert.Equal(SimNetGateway.Engine.SessionMode.Live, ctx.Engine.Session.Mode);
    }

    [Fact]
    public void The_overall_verdict_is_PASS()
    {
        var report = Report;
        Assert.True(report.Passed, report.RenderSummary());
        Assert.EndsWith("Result: PASS\n", report.RenderSummary());
    }

    [Fact]
    public void The_report_is_identical_across_two_runs()
    {
        string first = Report.Render(); // the run shared by the other tests
        string second = new ScenarioRunner().Run(ConvoyAmbushScript.Create()).Render();
        Assert.Equal(first, second);
        Assert.True(first.Length > 20_000); // a real timeline, not two empty texts
    }

    [Fact]
    public void The_timeline_prints_runner_time_and_sim_time_for_every_entry_and_lists_inputs_and_outputs_of_every_side()
    {
        var report = Report;
        Assert.All(report.Timeline, e => Assert.True(e.RunnerTime >= 0 && e.SimTime >= 0));
        string text = report.Render();
        foreach (string side in new[] { "<- NED", "<- RPR", "-> NED", "-> RPR", "-> DISPLAY" })
        {
            Assert.Contains(side, text);
        }

        Assert.Contains("Act 0 - Setup and validation", text);
        Assert.Contains("Act 11 - Debrief replay", text);
        Assert.Contains("r=  50.000 sim=  43.500", text); // the query of S-4.5: runner time 50 s, sim time 43.5 s (Start at 6.5 s)
    }

    [Fact]
    public void No_condition_of_the_run_raises_a_design_question_after_R_113()
    {
        var withQuestions = Report.Conditions.Where(c => c.DesignQuestions.Count > 0).Select(c => c.Id).ToList();
        Assert.Empty(withQuestions);
        Assert.DoesNotContain("Design questions", Report.RenderSummary());
    }
}
