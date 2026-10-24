using System.Reflection;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// Self-tests of the showcase harness (DD-002 section 8, Appendix B.4): the runner's timing, the step kinds, the report, the test-only
/// modules, and - one test per invariant - that the <see cref="InvariantChecker"/> detects each G-* violation when one is injected on
/// purpose (by a small faulty step, a manipulated recorder or a tampered index).
/// </summary>
public class ShowcaseHarnessTests
{
    private static (ScenarioReport Report, ScenarioContext Context) Execute(Action<ActBuilder> build, double end = 2.0,
        string[]? conditions = null, GatewayConfiguration? configuration = null)
    {
        var act = new ActBuilder(0, "test", 0);
        build(act);
        var script = new ScenarioScript("test", [act.Build()], [0.0],
            (conditions ?? []).Select(c => new SuccessCondition(c, "condition " + c)).ToArray(), end);
        var runner = new ScenarioRunner(configuration);
        var report = runner.Run(script);
        return (report, runner.Context!);
    }

    private static IReadOnlyList<GViolation> ViolationsOf(ScenarioReport report, string id) =>
        report.Violations.Where(v => v.Id == id).ToList();

    // ---- the runner's timing ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_step_runs_in_the_tick_of_its_runner_time_before_the_engine_ticks_and_a_check_after_it()
    {
        var seen = new List<(string What, long Tick, double Runner)>();
        var (report, _) = Execute(a =>
        {
            a.Do(1.0, "step", ctx => seen.Add(("step", ctx.Tick, ctx.RunnerTime)));
            a.Check(1.0, "C-1", ctx =>
            {
                seen.Add(("check", ctx.Tick, ctx.RunnerTime));
                return CheckResult.Pass();
            });
        }, conditions: ["C-1"]);

        Assert.Equal([("step", 59L, 1.0), ("check", 60L, 1.0)], seen); // the step before tick 60, the check after it
        Assert.True(report.Passed);
    }

    [Fact]
    public void The_clock_is_a_manual_clock_at_60_Hz_and_the_run_ends_at_the_end_time()
    {
        var (report, ctx) = Execute(_ => { }, end: 2.0);
        Assert.Equal(120, report.Ticks);
        Assert.Equal(120, ctx.Engine.TickCount);
        Assert.Equal(2.0, ctx.Clock.Now);
        Assert.Equal(2.0, ctx.RunnerTime);
    }

    [Fact]
    public void A_step_at_time_zero_runs_in_the_first_tick()
    {
        var seen = new List<(long Tick, double Runner)>();
        Execute(a => a.Do(0.0, "step", ctx => seen.Add((ctx.Tick, ctx.RunnerTime))), end: 0.5);
        Assert.Equal([(0L, 1 / 60.0)], seen);
    }

    [Fact]
    public void Steps_are_scheduled_on_the_runner_clock_not_on_sim_time()
    {
        var seen = new Dictionary<string, (double Runner, double Sim)>();
        void Note(string key, ScenarioContext ctx) => seen[key] = (ctx.RunnerTime, ctx.SimTime);
        var (_, ctx) = Execute(a =>
        {
            a.Inject(1.0, "Load", h => h.SendNed(new NedSimControl(NedControlKind.Load, "x")));
            a.Inject(1.5, "Start", h => h.SendNed(new NedSimControl(NedControlKind.Start, null)));
            a.Do(2.5, "running", c => Note("running", c));
            a.Inject(3.0, "Pause", h => h.SendNed(new NedSimControl(NedControlKind.Pause, null)));
            a.Do(3.5, "paused", c => Note("paused", c));
            a.Do(4.5, "paused later", c => Note("paused later", c));
        }, end: 5.0);

        Assert.Equal(2.5, seen["running"].Runner);
        Assert.InRange(seen["running"].Sim, 0.98, 1.0); // reset to 0 by the Load, running since 1.5 s
        Assert.Equal(4.5, seen["paused later"].Runner); // the step still ran at runner time 4.5 although the sim time stood still
        Assert.Equal(seen["paused"].Sim, seen["paused later"].Sim);
        Assert.InRange(seen["paused later"].Sim, 1.48, 1.52);
        Assert.Equal(SessionState.Paused, ctx.Engine.Session.State);
    }

    [Fact]
    public void Steps_of_one_tick_run_in_script_order_whatever_their_kind()
    {
        var order = new List<string>();
        Execute(a =>
        {
            a.Do(1.0, "first", _ => order.Add("action 1"));
            a.Inject(1.0, "second", _ => order.Add("inject"));
            a.Operator(1.0, "RprEntityEgress/Damage", enable: false);
            a.Do(1.0, "fourth", ctx => order.Add("action 2 sees the rule " + (ctx.Rule("RprEntityEgress/Damage").Enabled ? "on" : "off")));
        });

        Assert.Equal(["action 1", "inject", "action 2 sees the rule off"], order);
    }

    // ---- the step kinds ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Operator_steps_toggle_rules_and_are_listed_in_the_timeline()
    {
        const string rule = "RprEntityEgress/Damage";
        var seen = new List<bool>();
        var (report, _) = Execute(a =>
        {
            a.Operator(1.0, rule, enable: false);
            a.Check(1.2, "C-1", ctx =>
            {
                seen.Add(ctx.Rule(rule).Enabled);
                return CheckResult.Pass();
            });
            a.Operator(2.0, rule, enable: true);
            a.Check(2.2, "C-1", ctx =>
            {
                seen.Add(ctx.Rule(rule).Enabled);
                return CheckResult.Pass();
            });
        }, end: 3.0, conditions: ["C-1"]);

        Assert.Equal([false, true], seen);
        Assert.Contains(report.Timeline, e => e.Kind == "op" && e.Text == "disable " + rule && e.RunnerTime == 1.0);
        Assert.Contains(report.Timeline, e => e.Kind == "op" && e.Text == "enable " + rule && e.RunnerTime == 2.0);
    }

    [Fact]
    public void An_operator_step_for_a_rule_that_does_not_exist_is_a_failed_step_and_fails_the_report()
    {
        var (report, _) = Execute(a => a.Operator(1.0, "Nobody/Nothing", enable: false));
        Assert.False(report.Passed);
        Assert.Single(report.StepFailures);
        Assert.Contains("Nobody/Nothing", report.StepFailures[0]);
        Assert.Contains("Steps that threw", report.RenderSummary());
        Assert.EndsWith("Result: FAIL\n", report.RenderSummary());
    }

    [Fact]
    public void A_step_that_throws_is_reported_and_the_run_goes_on()
    {
        bool later = false;
        var (report, ctx) = Execute(a =>
        {
            a.Do(1.0, "boom", _ => throw new InvalidOperationException("boom"));
            a.Do(1.5, "later", _ => later = true);
        });
        Assert.True(later);
        Assert.Equal(120, ctx.Engine.TickCount);
        Assert.Single(report.StepFailures);
        Assert.False(report.Passed);
    }

    [Fact]
    public void An_inject_step_enqueues_a_typed_message_that_is_processed_in_that_tick_and_logged_as_an_input()
    {
        var (report, ctx) = Execute(a => a.Inject(1.0, "Hawk's geo-spatial", h => h.SendNed(T.Geo(T.Hawk))));
        Assert.NotNull(ctx.NedEntity(T.Hawk));
        var input = Assert.Single(ctx.Inputs);
        Assert.Equal("NED", input.Side);
        Assert.Equal(60, input.Tick);
        Assert.Equal(1.0, input.RunnerTime);
        Assert.InRange(input.SimTime, 0.99, 1.01); // Idle runs free: sim time is the stamp of the tick that processed it
        var entry = Assert.Single(report.Timeline, e => e.Kind == "in");
        Assert.Contains("NedGeoSpatial id=42", entry.Text);
        Assert.Contains("[Hawk's geo-spatial]", entry.Text);
        Assert.Equal(1.0, entry.RunnerTime);
        Assert.InRange(entry.SimTime, 0.99, 1.01);
    }

    [Fact]
    public void An_action_step_may_switch_the_loopback_on_and_off()
    {
        var (report, ctx) = Execute(a =>
        {
            a.Do(0.5, "loopback on", c => c.LoopbackNed = true);
            a.Inject(1.0, "an RPR platform appears", h => h.SendRpr(S.Platform(1, "T-1")));
            a.Do(1.5, "loopback off", c => c.LoopbackNed = false);
            a.Inject(2.0, "another one", h => h.SendRpr(S.Platform(2, "T-2")));
        }, end: 3.0);

        Assert.Equal(2, ctx.LoopedNed); // master + geo-spatial of the first platform; the second came after the loopback ended
        Assert.Equal(4, ctx.Ned.Count);
        Assert.Equal(2, report.Timeline.Count(e => e.Kind == "loop"));
        Assert.Empty(report.Violations); // the looped messages were ignored (echo)
    }

    // ---- the report ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_failing_check_fails_its_condition_and_the_report_and_shows_the_details()
    {
        var (report, _) = Execute(a => a.Check(1.0, "C-1", _ => CheckResult.Fail("the answer was 41")), conditions: ["C-1"]);
        Assert.False(report.Passed);
        Assert.False(report.Find("C-1")!.Passed);
        string summary = report.RenderSummary();
        Assert.Contains("C-1    FAIL", summary);
        Assert.Contains("the answer was 41", summary);
        Assert.EndsWith("Result: FAIL\n", summary);
    }

    [Fact]
    public void A_check_that_throws_fails_its_condition_with_the_exception()
    {
        var (report, _) = Execute(a => a.Check(1.0, "C-1", _ => throw new InvalidCastException("bad cast")), conditions: ["C-1"]);
        Assert.False(report.Find("C-1")!.Passed);
        Assert.Contains("InvalidCastException", report.Find("C-1")!.Evaluations[0].Details);
        Assert.Contains("bad cast", report.Find("C-1")!.Evaluations[0].Details);
    }

    [Fact]
    public void A_condition_checked_at_several_times_passes_only_when_all_evaluations_pass()
    {
        var (report, _) = Execute(a =>
        {
            a.Check(1.0, "C-1", _ => CheckResult.Pass("first"));
            a.Check(1.5, "C-1", _ => CheckResult.Fail("second"));
            a.Check(1.0, "C-2", _ => CheckResult.Pass());
            a.Check(1.5, "C-2", _ => CheckResult.Pass());
        }, conditions: ["C-1", "C-2"]);
        Assert.False(report.Find("C-1")!.Passed);
        Assert.Equal(2, report.Find("C-1")!.Evaluations.Count);
        Assert.True(report.Find("C-2")!.Passed);
    }

    [Fact]
    public void A_declared_condition_that_is_never_checked_fails()
    {
        var (report, _) = Execute(_ => { }, conditions: ["C-1"]);
        Assert.False(report.Find("C-1")!.Evaluated);
        Assert.False(report.Passed);
        Assert.Contains("not evaluated", report.RenderSummary());
    }

    [Fact]
    public void A_condition_the_script_did_not_declare_is_added_to_the_report()
    {
        var (report, _) = Execute(a => a.Check(1.0, "X-9", _ => CheckResult.Pass()));
        Assert.True(report.Find("X-9")!.Passed);
        Assert.True(report.Passed);
    }

    [Fact]
    public void A_design_question_of_a_check_is_listed_and_marks_the_condition()
    {
        var (report, _) = Execute(a => a.Check(1.0, "C-1", _ => new CheckResult(true, "ok", "it cannot be checked as written")), conditions: ["C-1"]);
        Assert.True(report.Passed);
        string summary = report.RenderSummary();
        Assert.Contains("C-1    PASS* ", summary);
        Assert.Contains("Design questions", summary);
        Assert.Contains("C-1: it cannot be checked as written", summary);
    }

    [Fact]
    public void The_timeline_has_runner_time_and_sim_time_for_inputs_outputs_and_diagnostics()
    {
        var (report, _) = Execute(a =>
        {
            a.Expect("Module", "Unknown TKB type");
            a.Inject(1.0, "a NED entity of an unknown type", h =>
            {
                h.SendNed(T.Master(T.Hawk, "Hawk", tkb: 777));
                h.SendNed(T.Geo(T.Hawk));
            });
        });

        var rpr = report.Timeline.Where(e => e.Kind == "out" && e.Side == "RPR" && e.Text.StartsWith("RprObjectUpdate", StringComparison.Ordinal)).ToList();
        Assert.Single(rpr);
        Assert.Equal(1.0 + 1 / 60.0, rpr[0].RunnerTime, 9); // the handle arrives one tick later
        Assert.InRange(rpr[0].SimTime, 1.0, 1.1);
        Assert.Contains(report.Timeline, e => e.Kind == "diag" && e.Text.Contains("Unknown TKB type 777", StringComparison.Ordinal));
        string text = report.Render();
        Assert.Contains("r=   1.000 sim=   1.000 A0  <- NED     NedEntityMaster id=42", text);
        Assert.Contains("-> RPR     RprRegisterObject token=1", text);
        Assert.Contains("!! GW      Warning Module", text);
        Assert.Empty(report.Violations);
    }

    [Fact]
    public void Quiet_inputs_and_display_positions_are_grouped_per_second_in_the_timeline()
    {
        var (report, _) = Execute(a =>
        {
            a.Inject(1.0, "Hawk", h =>
            {
                h.SendNed(T.Master(T.Hawk, "Hawk"));
                h.SendNed(T.Geo(T.Hawk));
            });
            a.Inject(1.5, "keep-alive 1", h => h.SendNed(T.Geo(T.Hawk)), quiet: true);
            a.Inject(1.6, "keep-alive 2", h => h.SendNed(T.Geo(T.Hawk)), quiet: true);
            a.Inject(1.7, "keep-alive 3", h => h.SendNed(T.Geo(T.Hawk)), quiet: true);
        }, end: 3.0);

        string text = report.Render();
        Assert.Contains("input (quiet) x3 in r=1..2 (keep-alive 1 x1, keep-alive 2 x1, keep-alive 3 x1)", text);
        Assert.DoesNotContain("keep-alive 1\n", text);
        Assert.Contains("DisplayEntityPosition x", text);
        Assert.Contains("(Hawk x", text);
        Assert.DoesNotContain("DisplayEntityPosition Hawk lat=", text); // positions are summarised, not listed one by one
    }

    [Fact]
    public void Report_rendering_is_deterministic()
    {
        string Make() => Execute(a =>
        {
            a.Inject(1.0, "platform", h => h.SendRpr(S.Platform(1, "T-1")));
            a.Inject(1.2, "viper", h => h.SendNed(S.Viper(0, 1, 2)));
            a.Check(1.5, "C-1", ctx => CheckResult.Pass($"{ctx.Ned.Count} NED messages"));
        }, end: 3.0, conditions: ["C-1"]).Report.Render();

        string first = Make();
        Assert.Equal(first, Make());
        var (report, _) = Execute(a => a.Inject(1.0, "platform", h => h.SendRpr(S.Platform(1, "T-1"))));
        Assert.Equal(report.Render(), report.Render());
        Assert.Equal(report.RenderSummary(), report.RenderSummary());
    }

    [Fact]
    public void The_window_helpers_are_tick_based_and_inclusive_at_both_ends()
    {
        var (_, ctx) = Execute(a => a.Inject(1.0, "platform", h => h.SendRpr(S.Platform(1, "T-1"))), end: 2.0);
        Assert.Equal(2, ctx.NedBetween(1.0, 1.0).Count); // master + geo-spatial, both in tick 60
        Assert.Equal(2, ctx.NedBetween(0.5, 1.0).Count);
        Assert.Empty(ctx.NedBetween(1.0 + 1 / 60.0, 2.0));
        Assert.Empty(ctx.NedBetween(0, 1.0 - 1 / 60.0));
        Assert.Single(ctx.NedOf<NedEntityMaster>(1.0, 1.0));
        Assert.Equal(60 * 1, ScenarioContext.TickOf(1.0));
        Assert.Equal(ctx.Ned.Count, ctx.NedBetween(0, 2.0).Count);
    }

    // ---- the test-only modules ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_faulty_module_throws_only_while_armed_and_the_transaction_is_discarded()
    {
        var (report, ctx) = Execute(a =>
        {
            a.Expect("Transaction", "failed on purpose");
            a.Inject(0.5, "Hawk", h => h.SendNed(T.Geo(T.Hawk)));
            a.Inject(1.0, "disarmed", h => h.SendNed(new NedIffStatus(T.Hawk, 1)));
            a.Do(1.2, "arm", c => c.Faulty.Armed = true);
            a.Inject(1.5, "armed", h => h.SendNed(new NedIffStatus(T.Hawk, 2)));
            a.Do(1.7, "disarm", c => c.Faulty.Armed = false);
            a.Inject(2.0, "disarmed again", h => h.SendNed(new NedIffStatus(T.Hawk, 3)));
        }, end: 3.0);

        Assert.Equal(1, ctx.Faulty.Thrown);
        Assert.Equal(1, ctx.Engine.Diagnostics.TransactionFailures);
        Assert.Empty(report.Violations);
        Assert.Equal(1, ctx.Engine.Diagnostics.TransactionFailures);
    }

    [Fact]
    public void The_rogue_rule_is_registered_disabled_and_cascades_to_the_depth_limit_when_enabled()
    {
        var (report, ctx) = Execute(a =>
        {
            a.Expect("Cascade", "refused at depth");
            a.Inject(0.5, "two entities", h =>
            {
                h.SendNed(T.Geo(T.Hawk));
                h.SendNed(T.Geo(T.Gate));
            });
            a.Do(1.0, "kick with the rule disabled", c =>
            {
                Assert.False(c.Rule(RogueProcessor.LoopRule).Enabled);
                c.Rogue.Kick(c.NedEntity(T.Hawk)!.Id);
                Assert.Equal(1L, c.NedEntity(T.Hawk)!.Get(c.Rogue.Counter).AsInt());
                Assert.Equal(0, c.Engine.Diagnostics.CascadeOverflows);
            });
            a.Operator(1.5, RogueProcessor.LoopRule, enable: true);
            a.Do(1.5, "kick with the rule enabled", c => c.Rogue.Kick(c.NedEntity(T.Gate)!.Id));
            a.Operator(1.6, RogueProcessor.LoopRule, enable: false);
        }, end: 2.0);

        Assert.Equal(1, ctx.Engine.Diagnostics.CascadeOverflows);
        long depth = ctx.Engine.Options.MaxCascadeDepth;
        Assert.Equal(depth + 1, ctx.NedEntity(T.Gate)!.Get(ctx.Rogue.Counter).AsInt());
        Assert.Empty(report.Violations);
        Assert.Empty(report.StepFailures);
    }

    [Fact]
    public void A_message_type_nobody_handles_is_counted()
    {
        var (report, ctx) = Execute(a => a.Inject(1.0, "unknown", h => h.SendNed(new UnknownNedMessage(5))));
        Assert.Equal(1, ctx.Engine.Diagnostics.UnhandledCount(typeof(UnknownNedMessage)));
        Assert.Empty(report.Violations);
        Assert.Contains("UnknownNedMessage code=5", report.Render());
    }

    [Fact]
    public void The_probe_sees_unchanged_writes_through_the_written_rule_and_changes_through_the_changed_rule()
    {
        var (_, ctx) = Execute(a =>
        {
            a.Inject(1.0, "Hawk", h => h.SendNed(T.Geo(T.Hawk)));
            a.Inject(1.5, "Hawk again, unchanged", h => h.SendNed(T.Geo(T.Hawk)));
        });
        Assert.Equal(2, ctx.Probe.PositionWritten.Count);
        Assert.Single(ctx.Probe.SpatialChanged); // only the first message changed the position
        Assert.Single(ctx.Probe.Created);
        Assert.True(ctx.Probe.Dispatches.Count >= 2);
        Assert.Empty(ctx.Probe.Violations);
    }

    // ---- the checker detects each violation -----------------------------------------------------------------------------------

    private static readonly Action<ActBuilder> HawkAndTank = a =>
    {
        a.Inject(0.1, "Hawk (NED) and T-1 (RPR)", h =>
        {
            h.SendNed(T.Master(T.Hawk, "Hawk"));
            h.SendNed(T.Geo(T.Hawk));
            h.SendRpr(S.Platform(1, "T-1"));
        });
    };

    [Fact]
    public void G1_a_message_that_describes_an_entity_of_the_receiving_side_is_detected()
    {
        var (report, _) = Execute(a =>
        {
            HawkAndTank(a);
            a.Do(0.5, "NED is told about its own entity", c => c.Checker.OnNedSent(new NedEntityMaster(T.Hawk, "Hawk", 200, NedForce.Friendly)));
        }, end: 1.0);

        Assert.Single(ViolationsOf(report, "G-1"));
        Assert.Equal(ScenarioContext.TickOf(0.5), ViolationsOf(report, "G-1")[0].Tick);
        Assert.Contains("origin is ned", ViolationsOf(report, "G-1")[0].Detail);
        Assert.False(report.Passed);
    }

    [Fact]
    public void G1_an_rpr_message_about_an_rpr_entity_and_an_event_sent_back_are_detected()
    {
        var id = Guid.Parse("a1000000-0000-4000-8000-000000000001");
        var (report, ctx) = Execute(a =>
        {
            HawkAndTank(a);
            a.Do(0.5, "RPR is told about its own object", c => c.Checker.OnRprSent(new RprObjectUpdate(new RprObjectHandle(0x601)) { Marking = "T-1" }));
            a.Do(0.6, "RPR receives a fire and gets it back", c =>
            {
                var fire = new RprWeaponFire(id, null, null, default, default, 1);
                c.Checker.OnRprReceived(fire);
                c.Checker.OnRprSent(fire);
            });
        }, end: 1.0);

        var violations = ViolationsOf(report, "G-1");
        Assert.Contains(violations, v => v.Detail.Contains("whose origin is rpr", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Detail.Contains("sent back to RPR", StringComparison.Ordinal));
        Assert.NotNull(ctx);
    }

    [Fact]
    public void G2_a_formation_member_that_NED_also_knows_as_an_individual_is_detected()
    {
        var (report, _) = Execute(a =>
        {
            a.Inject(0.1, "two platforms and their aggregate", h =>
            {
                h.SendRpr(S.Platform(1));
                h.SendRpr(S.Platform(2));
            });
            a.Inject(0.3, "aggregate", h => h.SendRpr(S.Aggregate(0x700, "Wolf", 1, 2)));
            a.Do(0.6, "a member gets an individual NED id as well", c =>
            {
                var member = c.RprEntity(0x601)!;
                Assert.True(member.Has(c.Schema.Parent));
                Assert.True(c.Host.NedIds.Bind(member.Id, 7_777_777UL));
            });
        }, end: 1.0);

        var violations = ViolationsOf(report, "G-2");
        Assert.NotEmpty(violations);
        Assert.Contains("formation member but NED also knows it as an individual", violations[0].Detail);
        Assert.Equal(ScenarioContext.TickOf(0.6), violations[0].Tick);
    }

    private static void Tamper(string what, ScenarioContext c)
    {
        var state = c.Engine.State;
        const BindingFlags nonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        var hawk = c.NedEntity(T.Hawk)!;
        switch (what)
        {
            case "recency":
                typeof(EntityRecord).GetProperty(nameof(EntityRecord.LastChangeWallTime))!.GetSetMethod(true)!.Invoke(hawk, [1.0e9]);
                break;
            case "presence":
            {
                var sets = (HashSet<EntityRecord>?[])typeof(StateRepository).GetField("_presence", nonPublic)!.GetValue(state)!;
                Assert.True(sets[c.Schema.Name.Value]!.Remove(hawk));
                break;
            }

            case "references":
            {
                var viper = c.NedEntity(S.ViperId)!;
                var member = c.NedMember(S.ViperId, 1)!;
                var indexes = (Array)typeof(StateRepository).GetField("_references", nonPublic)!.GetValue(state)!;
                object index = indexes.GetValue(c.Schema.Parent.Value)!;
                index.GetType().GetMethod("Remove")!.Invoke(index, [viper.Id, member]);
                break;
            }

            case "grid":
            {
                var grids = (Array)typeof(StateRepository).GetField("_grids", nonPublic)!.GetValue(state)!;
                object grid = grids.GetValue(c.Schema.Position.Value)!;
                grid.GetType().GetMethod("Remove")!.Invoke(grid, [hawk.Get(c.Schema.Position).AsVec3(), hawk]);
                break;
            }

            default:
                throw new ArgumentException(what);
        }
    }

    [Theory]
    [InlineData("recency", "not ordered by time")]
    [InlineData("presence", "presence set of Identity.Name")]
    [InlineData("references", "reverse index of Structure.Parent")]
    [InlineData("grid", "spatial grid of Spatial.Position")]
    public void G3_an_index_that_differs_from_a_full_scan_is_detected(string tamper, string expected)
    {
        var (report, _) = Execute(a =>
        {
            a.Inject(0.1, "Hawk and the convoy", h =>
            {
                h.SendNed(T.Master(T.Hawk, "Hawk"));
                h.SendNed(T.Geo(T.Hawk));
                h.SendNed(S.Viper(0, 1, 2));
            });
            a.Do(0.5, "tamper with the " + tamper + " index", c => Tamper(tamper, c));
        }, end: 1.0);

        var violations = ViolationsOf(report, "G-3");
        Assert.NotEmpty(violations);
        Assert.Contains(violations, v => v.Detail.Contains(expected, StringComparison.Ordinal));
        Assert.Equal(ScenarioContext.TickOf(0.5), violations[0].Tick);
    }

    [Fact]
    public void G3_untampered_indexes_are_consistent_through_creation_movement_and_removal()
    {
        var (report, _) = Execute(a =>
        {
            a.Inject(0.1, "Hawk, the convoy and a platform", h =>
            {
                h.SendNed(T.Master(T.Hawk, "Hawk"));
                h.SendNed(T.Geo(T.Hawk));
                h.SendNed(S.Viper(0, 1, 2));
                h.SendRpr(S.Platform(1));
            });
            a.Inject(0.5, "the convoy loses a member", h => h.SendNed(S.Viper(0, 2)));
            a.Inject(0.7, "Hawk moves to another grid cell", h => h.SendNed(T.Geo(T.Hawk, lat: 50.5)));
            a.Inject(0.9, "the platform goes", h => h.SendRpr(new RprObjectRemoved(S.PlatformHandle(1))));
            a.Inject(1.1, "Hawk goes", h => h.SendNed(new NedEntityDisposed(T.Hawk)));
        }, end: 2.0);

        Assert.Empty(report.Violations);
    }

    [Fact]
    public void G4_a_derived_write_to_a_received_spatial_attribute_is_detected()
    {
        var (report, _) = Execute(a =>
        {
            a.Inject(0.1, "Hawk", h => h.SendNed(T.Geo(T.Hawk)));
            a.Do(0.5, "a processor overwrites the received position", c =>
            {
                var changes = c.Engine.BeginChange(c.Engine.Sources.Register("test.deriver"));
                changes.Set(c.NedEntity(T.Hawk)!.Id, c.Schema.Position, AttrValue.From(new Vec3(1, 2, 3)));
                changes.Commit();
                changes.Discard();
            });
        }, end: 1.0);

        var violations = ViolationsOf(report, "G-4");
        Assert.NotEmpty(violations);
        Assert.Contains("Spatial.Position", violations[0].Detail);
        Assert.Contains("test.deriver", violations[0].Detail);
    }

    [Fact]
    public void G5_references_the_gateway_never_allocated_or_received_are_detected()
    {
        var (report, _) = Execute(a =>
        {
            HawkAndTank(a);
            a.Do(0.5, "manipulated recorder", c =>
            {
                c.Checker.OnNedSent(new NedEntityMaster(424242, "Ghost", 200, NedForce.Friendly));
                c.Checker.OnRprSent(new RprObjectUpdate(new RprObjectHandle(0xDEAD)) { Marking = "Ghost" });
                c.Checker.OnRprSent(new RprWeaponFire(Guid.NewGuid(), new RprObjectHandle(0xBEEF), null, default, default, 1));
            });
        }, end: 1.0);

        var violations = ViolationsOf(report, "G-5");
        Assert.Contains(violations, v => v.Detail.Contains("NED id 424242", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Detail.Contains("handle 57005", StringComparison.Ordinal)); // 0xDEAD
        Assert.Contains(violations, v => v.Detail.Contains("handle 48879", StringComparison.Ordinal)); // 0xBEEF
    }

    [Fact]
    public void G5_a_removal_sent_twice_is_detected()
    {
        var (report, ctx) = Execute(a =>
        {
            a.Inject(0.1, "Hawk", h =>
            {
                h.SendNed(T.Master(T.Hawk, "Hawk"));
                h.SendNed(T.Geo(T.Hawk));
            });
            a.Do(0.5, "the same removal twice", c =>
            {
                var handle = c.HandleOf(c.NedEntity(T.Hawk)!.Id)!.Value;
                c.Checker.OnRprSent(new RprObjectRemoved(handle));
                c.Checker.OnRprSent(new RprObjectRemoved(handle));
                c.Checker.OnRprSent(new RprObjectUpdate(handle) { Marking = "Hawk" });
            });
        }, end: 1.0);

        var violations = ViolationsOf(report, "G-5");
        Assert.Contains(violations, v => v.Detail.Contains("sent twice", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Detail.Contains("removed earlier", StringComparison.Ordinal));
        Assert.NotNull(ctx);
    }

    [Fact]
    public void G6_a_member_written_without_its_parent_is_detected_by_the_journal_check_and_by_the_probe()
    {
        var (report, _) = Execute(a =>
        {
            a.Inject(0.1, "Hawk", h => h.SendNed(T.Geo(T.Hawk)));
            a.Do(0.5, "a faulty module writes Structure.MemberIndex only", c =>
            {
                var changes = c.Engine.BeginChange(c.Engine.Sources.Register("test.faulty"));
                changes.Set(c.NedEntity(T.Hawk)!.Id, c.Schema.MemberIndex, AttrValue.From(3));
                changes.Commit();
                changes.Discard();
            });
        }, end: 1.0);

        var violations = ViolationsOf(report, "G-6");
        Assert.Contains(violations, v => v.Detail.Contains("writes Structure.MemberIndex", StringComparison.Ordinal) &&
                                         v.Detail.Contains("without Structure.Parent", StringComparison.Ordinal)); // the journal check
        Assert.Contains(violations, v => v.Detail.Contains("the probe sees", StringComparison.Ordinal)); // the probe
    }

    [Fact]
    public void G6_a_member_linked_to_a_destroyed_unit_is_detected()
    {
        var (report, _) = Execute(a =>
        {
            a.Inject(0.1, "a platform and a unit", h =>
            {
                h.SendRpr(S.Platform(1));
                h.SendRpr(S.Platform(2));
            });
            a.Do(0.2, "note the unit", c => c.Remember("gone", c.RprEntity(0x602)!.Id));
            a.Inject(0.3, "the unit goes", h => h.SendRpr(new RprObjectRemoved(S.PlatformHandle(2))));
            a.Do(0.6, "a faulty module links the platform to the destroyed unit", c =>
            {
                var gone = c.Recall<EntityId>("gone");
                var changes = c.Engine.BeginChange(c.Engine.Sources.Register("test.faulty"));
                changes.Set(c.RprEntity(0x601)!.Id, c.Schema.Parent, AttrValue.From(gone));
                changes.Set(c.RprEntity(0x601)!.Id, c.Schema.MemberIndex, AttrValue.From(0));
                changes.Commit();
                changes.Discard();
            });
        }, end: 1.0);

        // the repository itself refuses nothing here (an EntityRef to a dead entity is legal), the checker does not
        Assert.Contains(ViolationsOf(report, "G-6"), v => v.Detail.Contains("destroyed earlier", StringComparison.Ordinal));
    }

    [Fact]
    public void G7_an_unexpected_warning_is_detected_and_an_expected_one_is_not()
    {
        var (unexpected, _) = Execute(a => a.Do(0.5, "an unexpected diagnostic", c => c.Engine.Diagnostics.Warn("Test", "something went wrong")), end: 1.0);
        var violations = ViolationsOf(unexpected, "G-7");
        Assert.Single(violations);
        Assert.Contains("something went wrong", violations[0].Detail);

        var (expected, _) = Execute(a =>
        {
            a.Expect("Module", "something went wrong");
            a.Do(0.5, "an expected diagnostic", c => c.Engine.Diagnostics.Warn("Test", "something went wrong"));
        }, end: 1.0);
        Assert.Empty(expected.Violations);
    }

    [Fact]
    public void G7_the_whitelist_is_per_act_and_matches_category_and_message()
    {
        var act0 = new ActBuilder(0, "first", 0);
        act0.Expect("Module", "tolerated");
        act0.Do(0.5, "tolerated in act 0", c => c.Engine.Diagnostics.Warn("Test", "tolerated here"));
        act0.Do(0.6, "an error counts as well", c => c.Engine.Diagnostics.Error("Test", "tolerated here too")); // category Module, no severity given: matches
        var act1 = new ActBuilder(1, "second", 1.0);
        act1.Do(1.5, "no longer tolerated in act 1", c => c.Engine.Diagnostics.Warn("Test", "tolerated here"));
        var expectsOtherCategory = new ActBuilder(2, "third", 2.0);
        expectsOtherCategory.Expect("Cascade", "tolerated");
        expectsOtherCategory.Do(2.5, "a Module diagnostic where only a Cascade one is expected", c => c.Engine.Diagnostics.Warn("Test", "tolerated here"));
        var script = new ScenarioScript("whitelist", [act0.Build(), act1.Build(), expectsOtherCategory.Build()], [0.0, 1.0, 2.0], [], 3.0);

        var report = new ScenarioRunner().Run(script);
        var violations = ViolationsOf(report, "G-7");
        Assert.Equal(2, violations.Count);
        Assert.Equal(ScenarioContext.TickOf(1.5), violations[0].Tick);
        Assert.Equal(ScenarioContext.TickOf(2.5), violations[1].Tick);
    }

    [Fact]
    public void G8_a_dispatch_that_goes_back_in_correlation_or_an_event_before_its_record_is_detected()
    {
        var (backwards, _) = Execute(a =>
        {
            a.Inject(0.1, "Hawk", h => h.SendNed(T.Geo(T.Hawk)));
            a.Do(0.5, "the probe saw an old correlation dispatched late", c => c.Probe.Dispatches.Add(new DispatchEntry(false, 1)));
        }, end: 1.0);
        Assert.Contains(ViolationsOf(backwards, "G-8"), v => v.Detail.Contains("was dispatched after", StringComparison.Ordinal));

        var (eventFirst, _) = Execute(a =>
        {
            a.Inject(0.1, "Hawk", h => h.SendNed(T.Geo(T.Hawk)));
            a.Do(0.5, "an event, then the record of the same correlation", c =>
            {
                c.Probe.Dispatches.Add(new DispatchEntry(true, 1_000_000));
                c.Probe.Dispatches.Add(new DispatchEntry(false, 1_000_000));
            });
        }, end: 1.0);
        Assert.Contains(ViolationsOf(eventFirst, "G-8"), v => v.Detail.Contains("must precede its events", StringComparison.Ordinal));

        var (fine, _) = Execute(a =>
        {
            a.Inject(0.1, "Hawk", h => h.SendNed(T.Geo(T.Hawk)));
            a.Do(0.5, "a record and its events share a correlation", c =>
            {
                c.Probe.Dispatches.Add(new DispatchEntry(false, 2_000_000));
                c.Probe.Dispatches.Add(new DispatchEntry(true, 2_000_000));
                c.Probe.Dispatches.Add(new DispatchEntry(true, 2_000_000));
            });
        }, end: 1.0);
        Assert.Empty(fine.Violations);
    }

    [Fact]
    public void A_clean_run_has_no_violations_and_the_checker_ran_every_tick()
    {
        var (report, ctx) = Execute(a =>
        {
            HawkAndTank(a);
            a.Inject(0.5, "the convoy", h => h.SendNed(S.Viper(0, 1, 2)));
        }, end: 2.0);
        Assert.Empty(report.Violations);
        Assert.Equal(120, ctx.Checker.Checks);
        Assert.True(report.Passed);
    }
}
