using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class TimerTests
{
    [Fact]
    public void A_timer_fires_at_its_period()
    {
        var f = new EngineFixture();
        var fired = new List<double>();
        f.Module("m", s => s.Every(TimeSpan.FromSeconds(1)).Do(t =>
        {
            fired.Add(t.WallTime);
            Assert.Equal(TimeSpan.FromSeconds(1), t.Period);
        }));

        f.Clock.Now = 0.5;
        f.Engine.Tick();
        Assert.Empty(fired);
        f.Clock.Now = 1.0;
        f.Engine.Tick();
        Assert.Equal(new[] { 1.0 }, fired);
        f.Clock.Now = 1.5;
        f.Engine.Tick();
        Assert.Single(fired);
        f.Clock.Now = 2.0;
        f.Engine.Tick();
        Assert.Equal(new[] { 1.0, 2.0 }, fired);
    }

    [Fact]
    public void A_long_gap_gives_one_firing_not_a_burst()
    {
        var f = new EngineFixture();
        int fired = 0;
        RuleInfo rule = null!;
        f.Module("m", s => rule = s.Every(TimeSpan.FromSeconds(1)).Do(_ => fired++));

        f.Clock.Now = 10.4;
        f.Engine.Tick();
        Assert.Equal(1, fired);
        f.Engine.Tick(); // same time: not due again
        Assert.Equal(1, fired);
        f.Clock.Now = 11.3;
        f.Engine.Tick(); // next due is now + period = 11.4
        Assert.Equal(1, fired);
        f.Clock.Now = 11.4;
        f.Engine.Tick();
        Assert.Equal(2, fired);
        Assert.Equal(2, rule.FireCount);
    }

    [Fact]
    public void The_first_due_time_is_the_clock_at_registration_plus_the_period()
    {
        var f = new EngineFixture();
        f.Clock.Now = 5;
        int fired = 0;
        f.Module("m", s => s.Every(TimeSpan.FromSeconds(2)).Do(_ => fired++));

        f.Clock.Now = 6.9;
        f.Engine.Tick();
        Assert.Equal(0, fired);
        f.Clock.Now = 7;
        f.Engine.Tick();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Tick_end_rules_fire_every_tick_after_the_timers()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        f.Module("m", s =>
        {
            s.OnTickEnd().Do(t => log.Add("end:" + t.Period.TotalSeconds));
            s.Every(TimeSpan.FromSeconds(1)).Do(_ => log.Add("timer"));
        });

        f.Clock.Now = 0.2;
        f.Engine.Tick();
        f.Clock.Now = 1.0;
        f.Engine.Tick();
        f.Engine.Tick();

        Assert.Equal(new[] { "end:0", "timer", "end:0", "end:0" }, log);
    }

    [Fact]
    public void Timer_triggers_carry_wall_and_sim_time()
    {
        var f = new EngineFixture();
        double wall = -1, sim = -1;
        f.Module("m", s => s.Every(TimeSpan.FromSeconds(1)).Do(t => { wall = t.WallTime; sim = t.SimTime; }));
        f.Engine.Execute(SessionCommand.Load("s"), f.Net);
        f.Engine.Execute(SessionCommand.Start, f.Net);

        f.Clock.Now = 3;
        f.Engine.Tick();

        Assert.Equal((3.0, 3.0), (wall, sim));
    }

    [Fact]
    public void Timers_fire_in_registration_order_and_disabled_ones_are_skipped()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        RuleInfo second = null!;
        f.Module("m", s =>
        {
            s.Every(TimeSpan.FromSeconds(1)).Named("a").Do(_ => log.Add("a"));
            second = s.Every(TimeSpan.FromSeconds(1)).Named("b").Do(_ => log.Add("b"));
            s.Every(TimeSpan.FromSeconds(1)).Named("c").Do(_ => log.Add("c"));
        });
        second.Enabled = false;

        f.Clock.Now = 1;
        f.Engine.Tick();

        Assert.Equal(new[] { "a", "c" }, log);
        Assert.Equal(0, second.FireCount);
    }

    [Fact]
    public void A_failing_timer_rule_is_counted_and_the_next_one_runs()
    {
        var f = new EngineFixture();
        int ran = 0;
        RuleInfo bad = null!;
        f.Module("m", s =>
        {
            bad = s.Every(TimeSpan.FromSeconds(1)).Do(_ => throw new InvalidOperationException("x"));
            s.OnTickEnd().Do(_ => ran++);
        });

        f.Clock.Now = 1;
        f.Engine.Tick();

        Assert.Equal(1, bad.Failures);
        Assert.Equal(1, ran);
    }

    [Fact]
    public void A_non_positive_period_is_a_programming_error()
    {
        var f = new EngineFixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Module("m", s => s.Every(TimeSpan.Zero)));
    }

    [Fact]
    public void Tick_end_work_dispatches_within_the_same_tick_and_has_no_cause()
    {
        var f = new EngineFixture();
        var id = f.Create();
        var seen = new List<string>();
        f.Module("m", s =>
        {
            s.OnTickEnd().Do(_ =>
            {
                var cs = f.Engine.BeginChange(f.Proc);
                Assert.True(cs.CausedBy.IsNone);
                cs.Set(id, f.Count, EngineFixture.I(1));
                cs.Commit();
            });
            s.OnAttributesChanged(f.Count).Do(t => seen.Add("count:" + t.ChangeSet.CausedBy.Value));
        });

        f.Engine.Tick();

        Assert.Equal(new[] { "count:0" }, seen);
    }
}
