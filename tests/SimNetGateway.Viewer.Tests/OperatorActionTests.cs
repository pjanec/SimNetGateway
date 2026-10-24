using SimNetGateway.Engine;

namespace SimNetGateway.Viewer.Tests;

/// <summary>
/// Operator actions (DD-005 P-51, R-074): applied in order, only between ticks - an action enqueued from inside a dispatch takes
/// effect after that tick and before the next, never during it - and they reach the engine; a scenario switch or restart builds a
/// fresh engine.
/// </summary>
public class OperatorActionTests
{
    private sealed class Probe : NullObserver
    {
        public ViewerSession Session = null!;
        public readonly List<string> Log = new();
        public readonly List<(int Tick, bool Enabled)> EnabledInTick = new();
        public readonly List<string> SessionSources = new();
        public RuleInfo Rule = null!;
        public bool Armed;
        public int Tick;
        public int EnqueuedAtTick = -1;

        public override void InputStarted(CorrelationId correlation, SourceId source, Type messageType)
        {
            bool ticking = Session.Phase == ViewerPhase.Ticking;
            Log.Add(ticking ? "D@T" : "D@I");
            if (ticking)
            {
                EnabledInTick.Add((Tick, Rule.Enabled));
                if (Armed)
                {
                    Armed = false;
                    EnqueuedAtTick = Tick;
                    Log.Add("E");
                    Session.Enqueue(new ViewerAction.SetRuleEnabled(Rule.FullName, false));
                    Session.Enqueue(new ViewerAction.ExecuteSession(SessionCommand.Load("operator-test")));
                }
            }
        }

        public override void SessionCommandApplied(SessionCommand command, SourceId source, CorrelationId correlation) =>
            SessionSources.Add(Session.Engine.Sources.NameOf(source) + ":" + command.Kind);
    }

    private static (ViewerSession Session, Probe Probe) Traffic()
    {
        var probe = new Probe();
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Traffic, speed: 8, entities: 30) with { ExtraObserver = probe });
        probe.Session = session;
        probe.Rule = session.Engine.Rules.First(r => r.FullName.Contains("Spatial", StringComparison.Ordinal));
        session.TickCompleted += _ =>
        {
            probe.Log.Add("TE");
            probe.Tick++;
        };
        session.ActionApplied += outcome => probe.Log.Add("AP@" + session.Phase + ":" + outcome.Action.GetType().Name);
        return (session, probe);
    }

    [Fact]
    public void An_action_enqueued_inside_a_tick_is_applied_after_that_tick_and_before_the_next()
    {
        var (session, probe) = Traffic();
        Drive.Frames(session, 2);             // some history first
        probe.Armed = true;

        Drive.Frames(session, 6);              // 48 ticks, several of them with dispatches

        Assert.True(probe.EnqueuedAtTick >= 0, "the probe enqueued from inside a dispatch");
        var log = probe.Log;
        int enqueued = log.IndexOf("E");
        int tickEnd = log.IndexOf("TE", enqueued);
        Assert.True(tickEnd > enqueued, "the tick that was running ended");
        int applied = log.FindIndex(enqueued, s => s.StartsWith("AP@", StringComparison.Ordinal));
        Assert.True(applied > tickEnd, "applied after the tick ended: " + string.Join(" ", log.Skip(enqueued - 2).Take(14)));
        int nextTickingDispatch = log.FindIndex(tickEnd, s => s == "D@T");
        Assert.True(nextTickingDispatch < 0 || applied < nextTickingDispatch, "applied before the next tick's first dispatch");

        // never inside a tick: every application saw the Idle phase
        Assert.All(log.Where(s => s.StartsWith("AP@", StringComparison.Ordinal)), s => Assert.StartsWith("AP@Idle", s));

        // the rule stayed as it was during the whole tick that enqueued, and was off in every later tick
        Assert.All(probe.EnabledInTick.Where(e => e.Tick <= probe.EnqueuedAtTick), e => Assert.True(e.Enabled));
        var later = probe.EnabledInTick.Where(e => e.Tick > probe.EnqueuedAtTick).ToList();
        Assert.NotEmpty(later);
        Assert.All(later, e => Assert.False(e.Enabled));
    }

    [Fact]
    public void Rule_toggles_and_session_commands_reach_the_engine_with_the_operator_source()
    {
        var (session, probe) = Traffic();
        probe.Armed = true;

        Drive.Frames(session, 4);

        Assert.False(probe.Rule.Enabled);
        Assert.False(session.Engine.FindRule(probe.Rule.FullName)!.Enabled);
        Assert.Equal(SessionState.Loaded, session.Engine.Session.State);
        Assert.Equal("operator-test", session.Engine.Session.Scenario);
        Assert.Contains("operator:Load", probe.SessionSources);
        Assert.True(session.Engine.Sources.TryGet("operator", out _));
        Assert.Equal(2, session.ActionLog.Count);
        Assert.All(session.ActionLog, o => Assert.True(o.Accepted, o.Text));

        // and back on
        session.Enqueue(new ViewerAction.SetRuleEnabled(probe.Rule.FullName, true));
        session.Enqueue(new ViewerAction.ExecuteSession(SessionCommand.Start));
        session.Frame(1.0 / 60);
        Assert.True(probe.Rule.Enabled);
        Assert.Equal(SessionState.Running, session.Engine.Session.State);
    }

    [Fact]
    public void Refused_actions_are_logged_and_change_nothing()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty));
        session.Enqueue(new ViewerAction.SetRuleEnabled("No/SuchRule", false));
        session.Enqueue(new ViewerAction.ExecuteSession(SessionCommand.Start));   // not allowed in Idle
        session.Frame(0);

        Assert.Equal(2, session.ActionLog.Count);
        Assert.All(session.ActionLog, o => Assert.False(o.Accepted));
        Assert.Equal(SessionState.Idle, session.Engine.Session.State);
        Assert.Equal(0, session.PendingActions);
    }

    [Fact]
    public void Actions_are_applied_in_the_order_they_were_enqueued()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty, playing: false));
        var applied = new List<Type>();
        session.ActionApplied += o => applied.Add(o.Action.GetType());

        session.Enqueue(new ViewerAction.SetSpeed(2));
        session.Enqueue(new ViewerAction.Play());
        session.Enqueue(new ViewerAction.Pause());
        session.Enqueue(new ViewerAction.Step());
        Assert.Equal(4, session.PendingActions);
        Assert.Empty(applied);
        session.Frame(1.0 / 60);

        Assert.Equal([typeof(ViewerAction.SetSpeed), typeof(ViewerAction.Play), typeof(ViewerAction.Pause), typeof(ViewerAction.Step)],
            applied);
        Assert.Equal(1, session.TicksDone);   // paused again before the step, so the step ran and nothing else
    }

    [Fact]
    public void Tracer_actions_watch_unwatch_and_clear_between_ticks()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Traffic, speed: 2, entities: 20));
        Drive.Frames(session, 5);
        var first = Drive.MovingEntity(session);

        session.Enqueue(new ViewerAction.WatchEntity(first));
        Drive.Frames(session, 30);
        Assert.Contains(first, session.Tracer.WatchedEntities);
        Assert.NotEmpty(session.Tracer.Traces);
        Assert.Contains(session.Tracer.Traces, t => t.Touches(first));   // (the units kept also hold the cascades they caused)

        session.Enqueue(new ViewerAction.ClearTraces());
        session.Enqueue(new ViewerAction.UnwatchEntity(first));
        session.Frame(1.0 / 60);
        Assert.Empty(session.Tracer.WatchedEntities);
        Assert.Empty(session.Tracer.Traces);
    }

    // ---- scenario switch and restart ---------------------------------------------------------------------------------------

    [Fact]
    public void Restart_builds_a_fresh_engine_with_fresh_models_and_resets_the_accounting()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Traffic, speed: 2, entities: 40));
        Drive.Frames(session, 30);
        var oldEngine = session.Engine;
        var oldTracer = session.Tracer;
        var oldHost = session.Host;
        long oldTicks = oldEngine.TickCount;
        Assert.Equal(60, oldTicks);
        Assert.Equal(40, oldEngine.State.Count);

        session.Enqueue(new ViewerAction.Restart());
        session.Frame(0);

        Assert.NotSame(oldEngine, session.Engine);
        Assert.NotSame(oldHost, session.Host);
        Assert.NotSame(oldTracer, session.Tracer);
        Assert.Same(session.Engine, session.Host.Engine);
        Assert.Equal(2, session.Generation);
        Assert.Equal(0, session.Engine.TickCount);
        Assert.Equal(0, session.TicksDone);
        Assert.Equal(0, session.Engine.State.Count);
        Assert.Equal(0.0, session.Clock.Now);
        Assert.Equal(0, session.Sampler.Count);
        Assert.Equal(0.0, session.TicksOwedTotal);
        Assert.Equal(1, session.Refreshes.Tables);        // the new models were refreshed at once
        Assert.Equal(0, session.Dashboard.Ticks);
        Assert.Equal(0, session.Entities.TotalEntities);
        Assert.Equal(oldTicks, oldEngine.TickCount);       // the old gateway is left alone

        Drive.Frames(session, 30);
        Assert.Equal(60, session.Engine.TickCount);
        Assert.Equal(40, session.Engine.State.Count);
        Assert.Equal(oldTicks, oldEngine.TickCount);
        Assert.Equal(session.Engine.TickCount, session.Dashboard.Ticks);
    }

    [Fact]
    public void Restart_forgets_what_the_operator_did_to_the_old_engine()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty));
        string rule = session.Engine.Rules[0].FullName;
        session.Enqueue(new ViewerAction.SetRuleEnabled(rule, false));
        session.Frame(0);
        Assert.False(session.Engine.FindRule(rule)!.Enabled);

        session.Enqueue(new ViewerAction.Restart());
        session.Frame(0);

        Assert.True(session.Engine.FindRule(rule)!.Enabled);
        Assert.False(session.Engine.Sources.TryGet("operator", out _));
    }

    [Fact]
    public void Actions_queued_behind_a_restart_apply_to_the_new_engine()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty));
        var oldEngine = session.Engine;
        string rule = oldEngine.Rules[0].FullName;

        session.Enqueue(new ViewerAction.Restart());
        session.Enqueue(new ViewerAction.SetRuleEnabled(rule, false));
        session.Enqueue(new ViewerAction.ExecuteSession(SessionCommand.Load("after-restart")));
        session.Frame(0);

        Assert.True(oldEngine.FindRule(rule)!.Enabled);
        Assert.Equal(SessionState.Idle, oldEngine.Session.State);
        Assert.False(session.Engine.FindRule(rule)!.Enabled);
        Assert.Equal("after-restart", session.Engine.Session.Scenario);
    }

    [Fact]
    public void Switching_scenario_builds_that_scenario_and_keeps_the_play_state_and_the_speed()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Convoy, speed: 4));
        Drive.Frames(session, 10);
        var convoyEngine = session.Engine;
        Assert.NotNull(session.ConvoyConditions);

        session.Enqueue(new ViewerAction.SwitchScenario(ViewerScenario.Traffic, 55));
        session.Frame(0);

        Assert.Equal(ViewerScenario.Traffic, session.Scenario);
        Assert.Equal(55, session.TrafficEntities);
        Assert.NotSame(convoyEngine, session.Engine);
        Assert.Null(session.ConvoyConditions);
        Assert.Equal(4.0, session.Speed);
        Assert.True(session.Playing);
        Drive.Frames(session, 20);
        Assert.Equal(55, session.Engine.State.Count);

        session.Enqueue(new ViewerAction.SwitchScenario(ViewerScenario.Convoy));
        session.Frame(0);
        Assert.NotNull(session.ConvoyConditions);
        Assert.Equal(0, session.TicksDone);
        Assert.Equal(3, session.Generation);
        Assert.Equal(48, session.ConvoyConditions!.Count);
        Assert.All(session.ConvoyConditions, c => Assert.False(c.Evaluated));
    }

    [Fact]
    public void A_restarted_Convoy_Ambush_still_passes()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Convoy, speed: 8));
        Drive.Frames(session, 100);
        session.Enqueue(new ViewerAction.Restart());

        Drive.Frames(session, 1200);

        Assert.True(session.Finished);
        Assert.True(session.ConvoyReport!.Passed);
        Assert.Equal(8400, session.TicksDone);
    }

    [Fact]
    public void Single_step_works_after_a_restart_from_pause()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Demo, playing: false));
        session.Enqueue(new ViewerAction.Step());
        session.Frame(0);
        Assert.Equal(1, session.TicksDone);

        session.Enqueue(new ViewerAction.Restart());
        session.Enqueue(new ViewerAction.Step());
        session.Enqueue(new ViewerAction.Step());
        session.Frame(0);
        Assert.Equal(2, session.TicksDone);
        Assert.Equal(2, session.Engine.TickCount);
    }
}
