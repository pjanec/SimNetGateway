using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>The scripted default demo: deterministic, shows the expected messages, and keeps the DD-002 invariants.</summary>
public class DemoScenarioTests
{
    private static readonly Lazy<DemoResult> s_result = new(() => DemoScenario.Run());

    [Fact]
    public void Two_runs_produce_identical_timelines_and_summaries()
    {
        var first = DemoScenario.Run();
        var second = DemoScenario.Run();
        Assert.Equal(first.Timeline, second.Timeline);
        Assert.Equal(first.Format(), second.Format());
        Assert.Equal(first.Counts, second.Counts);
        Assert.True(first.Timeline.Count > 100);
    }

    [Fact]
    public void The_run_lasts_20_simulated_seconds_at_60_Hz()
    {
        var result = s_result.Value;
        Assert.Equal(1200, result.Ticks);
        Assert.Equal(20.0, result.Seconds, 9);
        Assert.Equal(1200, result.Host.Engine.TickCount);
    }

    [Fact]
    public void Invariants_hold_after_every_tick()
    {
        InvariantChecker? checker = null;
        var result = DemoScenario.Run(new DemoOptions
        {
            OnHostCreated = host => checker = new InvariantChecker(host),
            OnNedSent = message => checker!.OnNedSent(message),
            OnRprSent = message => checker!.OnRprSent(message),
            AfterTick = _ => checker!.Check(),
        });
        Assert.NotNull(checker);
        Assert.Equal(result.Ticks, checker!.Checks);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void The_timeline_has_no_wall_clock_values()
    {
        var result = s_result.Value;
        // Pause and resume are one tick apart in wall time, but the simulation time is frozen: both lines show the same time.
        string stop = result.Timeline.Single(l => l.Contains("RprSimulationControl StopFreeze", StringComparison.Ordinal));
        string resume = result.Timeline.Where(l => l.Contains("RprSimulationControl StartResume", StringComparison.Ordinal)).Last();
        Assert.Equal(stop.Substring(0, 8), resume.Substring(0, 8));

        // every timeline line starts with a simulated time that the engine produced, never beyond the run
        foreach (string line in result.Timeline)
        {
            Assert.True(double.TryParse(line.AsSpan(0, 8), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double time), line);
            Assert.InRange(time, 0.0, 20.0);
        }

        // the engine's simulation time stands still for the one second the demo is paused
        Assert.True(result.Host.Engine.SimTime < result.Host.Engine.WallTime - 0.9);
    }

    [Fact]
    public void The_senders_stand_still_while_the_session_is_paused()
    {
        var result = s_result.Value;
        // the script pauses at 9 s and resumes at 10 s: the senders miss that one second
        Assert.Equal(19.0, result.SenderSeconds, 1);
        Assert.True(result.SenderSeconds < result.Seconds - 0.9);
    }

    [Fact]
    public void A_registration_is_answered_and_a_full_update_follows()
    {
        var host = s_result.Value.Host;
        var registrations = host.RprOut!.OfType<RprRegisterObject>().ToList();
        Assert.Equal(3, registrations.Count);
        Assert.Equal(3, host.RegisteredHandles.Count);
        var fullUpdates = host.RprOut!.OfType<RprObjectUpdate>().Where(u => u.Marking is not null).ToList();
        foreach (var handle in host.RegisteredHandles)
        {
            var first = host.RprOut!.OfType<RprObjectUpdate>().First(u => u.Handle == handle);
            Assert.NotNull(first.Marking);
            Assert.NotNull(first.WorldLocation);
            Assert.NotNull(first.EntityType);
        }

        Assert.True(fullUpdates.Count >= 3);
        // the handle is the RTI's: the first registration of the federation
        Assert.Contains(s_result.Value.Timeline,
            l => l.Contains($"RprObjectUpdate handle=0x{host.RegisteredHandles[0].Value:X} (full) marking=Alpha-1", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rpr_tank_appears_on_the_ned_side_with_a_master_message()
    {
        var host = s_result.Value.Host;
        var master = host.NedOut!.OfType<NedEntityMaster>().Single();
        Assert.Equal("Enemy-T72", master.Name);
        Assert.Equal(400, master.TkbType);
        Assert.Equal(NedForce.Opposing, master.Force);
        Assert.Contains(host.NedOut!.OfType<NedGeoSpatial>(), g => g.Id == master.Id);
        Assert.Equal(1, s_result.Value.Count("NED", "NedEntityMaster"));
    }

    [Fact]
    public void Weapon_fire_and_detonation_cross_to_the_other_side()
    {
        var result = s_result.Value;
        var host = result.Host;
        Assert.Equal(1, result.Count("RPR", "RprWeaponFire"));
        Assert.Equal(1, result.Count("RPR", "RprMunitionDetonation"));
        Assert.Equal(1, result.Count("NED", "NedWeaponFire"));
        var rprFire = host.RprOut!.OfType<RprWeaponFire>().Single();
        Assert.NotNull(rprFire.FiringObject);
        var nedFire = host.NedOut!.OfType<NedWeaponFire>().Single();
        var master = host.NedOut!.OfType<NedEntityMaster>().Single();
        Assert.Equal(master.Id, nedFire.Shooter);
    }

    [Fact]
    public void Pause_and_resume_are_forwarded_as_stop_freeze_and_start_resume()
    {
        var controls = s_result.Value.Host.RprOut!.OfType<RprSimulationControl>().Select(c => c.Kind).ToList();
        Assert.Equal(new[] { RprControlKind.StartResume, RprControlKind.StopFreeze, RprControlKind.StartResume }, controls);
    }

    [Fact]
    public void A_disposed_ned_entity_is_removed_from_rpr_and_display()
    {
        var host = s_result.Value.Host;
        var removed = host.RprOut!.OfType<RprObjectRemoved>().Single();
        Assert.Equal(host.RegisteredHandles[2], removed.Handle);
        Assert.Contains(host.DisplayOut!.OfType<DisplayEntityRemoved>(), d => d.Label == "Bravo-2");
    }

    [Fact]
    public void The_silent_rpr_tank_is_reaped_and_disposed_on_the_ned_side()
    {
        var result = s_result.Value;
        var host = result.Host;
        var master = host.NedOut!.OfType<NedEntityMaster>().Single();
        var disposed = host.NedOut!.OfType<NedEntityDisposed>().Single();
        Assert.Equal(master.Id, disposed.Id);
        Assert.Contains(host.DisplayOut!.OfType<DisplayEntityRemoved>(), d => d.Label == "Enemy-T72");
        // it is reaped one stale timeout (12 s) after its last update, which happened between 1 s and 5 s of script time
        Assert.Equal(master.Id, ((NedEntityDisposed)host.NedOut!.Messages.Single(m => m.Message is NedEntityDisposed).Message).Id);
        double reapedAt = host.NedOut!.Messages.Single(m => m.Message is NedEntityDisposed).WallTime;
        Assert.InRange(reapedAt, 1.0 + 12.0, 5.0 + 12.0 + 1.5);
    }

    [Fact]
    public void Display_positions_are_sent_for_the_moving_entities()
    {
        var result = s_result.Value;
        var host = result.Host;
        var positions = host.DisplayOut!.OfType<DisplayEntityPosition>().ToList();
        Assert.True(positions.Count > 300);
        Assert.Contains(positions, p => p.Label == "Alpha-1");
        Assert.Contains(positions, p => p.Label == "Bravo-2");
        Assert.Contains(positions, p => p.Label == "Enemy-T72");
        Assert.Contains(positions, p => p.Label == "Checkpoint");
        Assert.Equal(positions.Count, result.Count("DISPLAY", "DisplayEntityPosition"));
    }

    [Fact]
    public void The_static_checkpoint_is_only_refreshed_by_heartbeats()
    {
        var host = s_result.Value.Host;
        var handle = host.RegisteredHandles[1];
        var updates = host.RprOut!.OfType<RprObjectUpdate>().Where(u => u.Handle == handle).ToList();
        Assert.InRange(updates.Count, 3, 5); // the full first update and one heartbeat every 5 s
        Assert.All(updates, u => Assert.Equal("Checkpoint", u.Marking));
    }

    [Fact]
    public void The_summary_counts_match_what_the_recorders_hold()
    {
        var result = s_result.Value;
        var host = result.Host;
        Assert.Equal(host.NedOut!.Count, result.CountOfSide("NED"));
        Assert.Equal(host.RprOut!.Count, result.CountOfSide("RPR"));
        Assert.Equal(host.DisplayOut!.Count, result.CountOfSide("DISPLAY"));
        Assert.Contains("sent to NED: " + host.NedOut!.Count, result.Format());
        Assert.Empty(result.Diagnostics);
    }
}
