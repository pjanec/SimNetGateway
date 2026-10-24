using SimNetGateway.Engine;

namespace SimNetGateway.Viewer.Tests;

/// <summary>
/// Frame and tick accounting (DD-005 section 4.1): the engine ticks at a fixed 60 Hz of simulated time; speed decides how many
/// ticks a frame owes, the per-frame budget and cap limit them (the shortfall is the backlog, reported), pause stops everything,
/// a single step is exactly one tick. Checked after every frame of seeded random sequences of actions against the invariants and
/// against an independent reference of the schedule.
/// </summary>
public class TimeControlTests
{
    private const double Eps = 1e-9;

    // ---- the simple cases -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void The_speed_decides_how_many_ticks_a_frame_owes(double speed)
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty, speed));

        Drive.Frames(session, 240);

        // 240 frames of 1/60 s at speed x: 240 * speed ticks
        Assert.Equal((long)(240 * speed), session.TicksDone);
        Assert.Equal(session.TicksDone, session.Engine.TickCount);
        Assert.Equal(session.TicksDone / 60.0, session.Clock.Now, 9);
        Assert.Equal(0, session.Backlog);
        Assert.False(session.Behind);
    }

    [Fact]
    public void A_speed_outside_the_range_is_clamped()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty, 1));
        session.Enqueue(new ViewerAction.SetSpeed(100));
        session.Frame(1.0 / 60);
        Assert.Equal(ViewerSession.MaxSpeed, session.Speed);
        session.Enqueue(new ViewerAction.SetSpeed(0));
        session.Frame(1.0 / 60);
        Assert.Equal(ViewerSession.MinSpeed, session.Speed);
    }

    [Fact]
    public void Pause_advances_no_tick_however_long_the_frame()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty, 8, playing: false));

        Drive.Frames(session, 20, 5.0);

        Assert.Equal(0, session.TicksDone);
        Assert.Equal(0, session.Engine.TickCount);
        Assert.Equal(0.0, session.Clock.Now);
        Assert.Equal(0.0, session.TicksOwedTotal);
    }

    [Fact]
    public void A_single_step_advances_exactly_one_tick_and_only_while_paused()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty, 8, playing: false));

        session.Enqueue(new ViewerAction.Step());
        session.Frame(1.0);
        Assert.Equal(1, session.TicksDone);
        Assert.Equal(1, session.TicksLastFrame);
        Assert.Equal(1 / 60.0, session.Clock.Now, 12);

        session.Frame(1.0);
        Assert.Equal(1, session.TicksDone);

        session.Enqueue(new ViewerAction.Step());
        session.Enqueue(new ViewerAction.Step());
        session.Enqueue(new ViewerAction.Step());
        session.Frame(0.0);
        Assert.Equal(4, session.TicksDone);
        Assert.Equal(4, session.SteppedTicks);
        Assert.Equal(0, session.PlayedTicks);

        // playing: a step is refused and changes nothing
        session.Enqueue(new ViewerAction.Play());
        session.Enqueue(new ViewerAction.Step());
        session.Frame(0.0);
        Assert.False(session.ActionLog[^1].Accepted);
        Assert.Equal(4, session.TicksDone);
    }

    [Fact]
    public void Pausing_gives_up_what_was_owed_so_that_play_does_not_burst()
    {
        var options = Drive.Unlimited(ViewerScenario.Empty, 8) with { MaxTicksPerFrame = 2 };
        var session = new ViewerSession(options);
        session.Frame(1.0 / 60);
        Assert.Equal(2, session.TicksDone);
        Assert.Equal(6, session.Backlog);
        Assert.True(session.Behind);

        session.Enqueue(new ViewerAction.Pause());
        session.Frame(1.0);
        Assert.Equal(2, session.TicksDone);
        Assert.Equal(0, session.Backlog);
        Assert.Equal(6.0, session.TicksDropped, 9);

        session.Enqueue(new ViewerAction.Play());
        session.Frame(0.0);
        Assert.Equal(2, session.TicksDone);
    }

    // ---- budget, cap, backlog ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_tick_budget_limits_a_frame_and_the_backlog_is_reported_then_caught_up()
    {
        var timer = new FakeTimer();
        var options = new ViewerSessionOptions
        {
            Scenario = ViewerScenario.Empty,
            Speed = 8,
            TickBudgetMs = 10,
            Timestamp = timer.Now,
            TimestampFrequency = 1000,
            MaxBacklogTicks = 10_000,
        };
        var session = new ViewerSession(options);
        session.TickCompleted += _ => timer.Milliseconds += 4;   // every tick costs 4 ms: 3 fit in the 10 ms budget (4, 8, 12 >= 10)

        // x8 at 1/60 s owes 8 ticks; the budget lets 3 run (the check comes before a tick, once one has run)
        session.Frame(1.0 / 60);
        Assert.Equal(3, session.TicksDone);
        Assert.Equal(5, session.Backlog);
        Assert.True(session.Behind);

        session.Frame(1.0 / 60);
        Assert.Equal(6, session.TicksDone);
        Assert.Equal(10, session.Backlog);
        Assert.True(session.Behind);
        Assert.Equal(16.0, session.TicksOwedTotal, 9);

        // the gateway gets faster: the backlog is worked off, at most the budget per frame
        session.TickCompleted += _ => timer.Milliseconds -= 3;   // a tick now costs 1 ms
        while (session.Backlog > 0)
        {
            session.Frame(0.0);
        }

        Assert.False(session.Behind);
        Assert.Equal(16, session.TicksDone);
        Assert.Equal(0.0, session.TicksDropped);
    }

    [Fact]
    public void At_least_one_owed_tick_runs_even_when_the_budget_is_zero()
    {
        var timer = new FakeTimer();
        var session = new ViewerSession(new ViewerSessionOptions
        {
            Scenario = ViewerScenario.Empty,
            Speed = 4,
            TickBudgetMs = 0,
            Timestamp = timer.Now,
            TimestampFrequency = 1000,
        });

        Drive.Frames(session, 10);

        Assert.Equal(10, session.TicksDone);
        Assert.True(session.Behind);
    }

    [Fact]
    public void A_backlog_beyond_the_cap_is_dropped_and_counted()
    {
        var options = Drive.Unlimited(ViewerScenario.Empty, 8) with { MaxTicksPerFrame = 1, MaxBacklogTicks = 20 };
        var session = new ViewerSession(options);

        Drive.Frames(session, 100);

        Assert.Equal(100, session.TicksDone);
        Assert.Equal(20, session.Backlog);
        Assert.Equal(800.0, session.TicksOwedTotal, 6);
        Assert.Equal(session.TicksOwedTotal, session.PlayedTicks + session.BacklogExact + session.TicksDropped, 6);
        Assert.Equal(800.0 - 100 - 20, session.TicksDropped, 6);
        Assert.True(session.Behind);
    }

    // ---- seeded random sequences ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Random_action_sequences_keep_the_tick_accounting_exact(int seed)
    {
        var random = new Random(seed);
        var timer = new FakeTimer();
        int maxPerFrame = random.Next(3) == 0 ? 0 : random.Next(1, 12);
        int maxBacklog = random.Next(2) == 0 ? 600 : random.Next(0, 40);
        int budgetMs = random.Next(2) == 0 ? 1_000_000 : random.Next(0, 30);
        int costMs = random.Next(0, 6);
        var scenario = random.Next(3) == 0 ? ViewerScenario.Demo : ViewerScenario.Empty;
        var options = new ViewerSessionOptions
        {
            Scenario = scenario,
            Speed = 1,
            Playing = random.Next(2) == 0,
            TickBudgetMs = budgetMs,
            MaxTicksPerFrame = maxPerFrame,
            MaxBacklogTicks = maxBacklog,
            Timestamp = timer.Now,
            TimestampFrequency = 1000,
        };
        var session = new ViewerSession(options);
        session.TickCompleted += _ => timer.Milliseconds += costMs;

        // the reference: the schedule written down independently of the session
        bool playing = options.Playing;
        double speed = 1, owed = 0, owedTotal = 0, dropped = 0;
        long ticks = 0, played = 0, stepped = 0;
        double[] dts = [1.0 / 60, 1.0 / 30, 1.0 / 120, 0.0, 0.05, 0.25];
        double[] speeds = [0.25, 0.5, 1, 2, 4, 8, 3, 100, 0.01];
        long demoTicks = 1200;

        for (int frame = 0; frame < 300; frame++)
        {
            double dt = dts[random.Next(dts.Length)];
            int steps = 0;
            int actions = random.Next(0, 4);
            for (int a = 0; a < actions; a++)
            {
                switch (random.Next(5))
                {
                    case 0:
                        session.Enqueue(new ViewerAction.Play());
                        playing = true;
                        break;
                    case 1:
                        session.Enqueue(new ViewerAction.Pause());
                        playing = false;
                        dropped += owed;
                        owed = 0;
                        break;
                    case 2:
                        session.Enqueue(new ViewerAction.Step());
                        if (!playing && !(scenario == ViewerScenario.Demo && ticks + steps >= demoTicks))
                        {
                            steps++;
                        }

                        break;
                    default:
                        double s = speeds[random.Next(speeds.Length)];
                        session.Enqueue(new ViewerAction.SetSpeed(s));
                        speed = Math.Clamp(s, 0.25, 8);
                        break;
                }
            }

            session.Frame(dt);

            // reference for this frame
            long end = scenario == ViewerScenario.Demo ? demoTicks : long.MaxValue;
            int ranSteps = 0;
            for (int i = 0; i < steps && ticks < end; i++)
            {
                ticks++;
                stepped++;
                ranSteps++;
            }

            int ranPlayed = 0;
            if (playing && ticks < end)
            {
                owed += speed * dt * 60;
                owedTotal += speed * dt * 60;
                long elapsed = ranSteps * (long)costMs;
                while (ticks < end && owed >= 1 - Eps)
                {
                    if (maxPerFrame > 0 && ranPlayed >= maxPerFrame)
                    {
                        break;
                    }

                    if (ranPlayed > 0 && elapsed >= budgetMs)
                    {
                        break;
                    }

                    owed -= 1;
                    ticks++;
                    played++;
                    ranPlayed++;
                    elapsed += costMs;
                }
            }

            if (ticks >= end && owed != 0)
            {
                dropped += owed;
                owed = 0;
            }

            if (Math.Floor(owed + Eps) > maxBacklog)
            {
                dropped += owed - maxBacklog;
                owed = maxBacklog;
            }

            string ctx = $"seed {seed} frame {frame}";

            // the reference schedule
            Assert.True(ticks == session.TicksDone, $"{ctx}: ticks {session.TicksDone}, reference {ticks}");
            Assert.Equal(played, session.PlayedTicks);
            Assert.Equal(stepped, session.SteppedTicks);
            Assert.Equal(owed, session.BacklogExact, 6);
            Assert.Equal(dropped, session.TicksDropped, 6);
            Assert.Equal(owedTotal, session.TicksOwedTotal, 6);
            Assert.Equal(ranSteps + ranPlayed, session.TicksLastFrame);
            Assert.Equal(ranPlayed, session.PlayedLastFrame);

            // the invariants, stated without the reference
            Assert.Equal(session.PlayedTicks + session.SteppedTicks, session.TicksDone);
            Assert.Equal(session.TicksDone, session.Engine.TickCount);
            Assert.Equal(session.TicksDone / 60.0, session.Clock.Now, 9);
            Assert.Equal(session.TicksOwedTotal, session.PlayedTicks + session.BacklogExact + session.TicksDropped, 6);
            Assert.True(session.BacklogExact >= -Eps);
            Assert.True(session.Backlog <= maxBacklog);
            if (maxPerFrame > 0)
            {
                Assert.True(session.PlayedLastFrame <= maxPerFrame, ctx);
            }

            if (!session.Playing)
            {
                Assert.Equal(0, session.PlayedLastFrame);
                Assert.Equal(0, session.Backlog);
            }

            if (session.Behind)
            {
                Assert.True(session.Backlog >= 1, ctx);
            }

            if (!session.Playing && steps == 0)
            {
                Assert.Equal(0, session.TicksLastFrame);
            }
        }
    }

    [Fact]
    public void Without_budget_or_cap_nothing_is_ever_behind_and_the_ticks_follow_the_speed_schedule_exactly()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty));
        var random = new Random(77);
        double[] speeds = [0.25, 0.5, 1, 2, 4, 8];
        double owed = 0;
        for (int i = 0; i < 500; i++)
        {
            double speed = speeds[random.Next(speeds.Length)];
            session.Enqueue(new ViewerAction.SetSpeed(speed));
            session.Frame(1.0 / 60);
            owed += speed;
            Assert.Equal((long)Math.Floor(owed + Eps), session.TicksDone);
            Assert.False(session.Behind);
            Assert.True(session.BacklogExact < 1);
        }
    }

    [Fact]
    public void The_manual_clock_stays_at_ticks_over_60_across_a_scenario_switch()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty, 3));
        Drive.Frames(session, 50);
        session.Enqueue(new ViewerAction.SwitchScenario(ViewerScenario.Demo));
        Drive.Frames(session, 33);

        Assert.Equal(99, session.TicksDone);
        Assert.Equal(99 / 60.0, session.Clock.Now, 9);
        Assert.Equal(99, session.Engine.TickCount);
        Assert.Equal(32, session.Frames);     // the frame that applied the switch counts for the old gateway
        Assert.Equal(83, session.TotalFrames);
    }
}
