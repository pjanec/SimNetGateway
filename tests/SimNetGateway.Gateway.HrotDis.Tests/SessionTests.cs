using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>The session table of DD-007 section 6 (R-099): the cluster state and the clock drive the engine session.</summary>
public class SessionTests
{
    /// <summary>The table of the design, written out independently of the implementation (phase, advancing, state, mode).</summary>
    public static readonly (ClusterPhase Phase, bool Advancing, SessionState State, SessionMode Mode)[] Table =
    [
        (ClusterPhase.Idle, true, SessionState.Idle, SessionMode.Live),
        (ClusterPhase.Idle, false, SessionState.Idle, SessionMode.Live),
        (ClusterPhase.Edit, true, SessionState.Loaded, SessionMode.Live),
        (ClusterPhase.Edit, false, SessionState.Loaded, SessionMode.Live),
        (ClusterPhase.Preview, true, SessionState.Running, SessionMode.Live),
        (ClusterPhase.Preview, false, SessionState.Paused, SessionMode.Live),
        (ClusterPhase.Live, true, SessionState.Running, SessionMode.Live),
        (ClusterPhase.Live, false, SessionState.Paused, SessionMode.Live),
        (ClusterPhase.Replay, true, SessionState.Running, SessionMode.Replay),
        (ClusterPhase.Replay, false, SessionState.Paused, SessionMode.Replay),
        (ClusterPhase.Review, true, SessionState.Running, SessionMode.Replay),
        (ClusterPhase.Review, false, SessionState.Paused, SessionMode.Replay),
        (ClusterPhase.Degraded, true, SessionState.Paused, SessionMode.Live),
        (ClusterPhase.Degraded, false, SessionState.Paused, SessionMode.Live),
    ];

    public static TheoryData<int, int> AllTransitions()
    {
        var data = new TheoryData<int, int>();
        for (int from = 0; from < Table.Length; from++)
        {
            for (int to = 0; to < Table.Length; to++)
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Fact]
    public void The_table_covers_every_phase_with_both_clock_states()
    {
        Assert.Equal(Enum.GetValues<ClusterPhase>().Length * 2, Table.Length);
        foreach (var phase in Enum.GetValues<ClusterPhase>())
        {
            Assert.Equal(2, Table.Count(r => r.Phase == phase));
        }
    }

    [Theory]
    [MemberData(nameof(AllTransitions))]
    public void Every_phase_and_clock_state_maps_to_the_session_of_the_table_from_every_starting_point(int from, int to)
    {
        var h = new HrotDisHarness();
        var a = Table[from];
        var b = Table[to];
        h.HrotStep(new HrotSession(a.Phase, a.Advancing, 1.0, 0.0));
        Assert.Equal((a.State, a.Mode), (h.Engine.Session.State, h.Engine.Session.Mode));
        h.HrotStep(new HrotSession(b.Phase, b.Advancing, 1.0, 5.0));
        Assert.Equal((b.State, b.Mode), (h.Engine.Session.State, h.Engine.Session.Mode));
        Assert.Equal(0, h.Engine.Diagnostics.SessionRejections);
        // and the same message again changes nothing
        h.HrotStep(new HrotSession(b.Phase, b.Advancing, 1.0, 6.0));
        Assert.Equal((b.State, b.Mode), (h.Engine.Session.State, h.Engine.Session.Mode));
        Assert.Equal(0, h.Engine.Diagnostics.SessionRejections);
    }

    [Fact]
    public void The_pure_mapping_function_is_the_table()
    {
        foreach (var row in Table)
        {
            Assert.Equal((row.State, row.Mode), HrotSessionIngress.Target(row.Phase, row.Advancing));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => HrotSessionIngress.Target((ClusterPhase)99, true));
    }

    [Fact]
    public void Random_sequences_of_phases_always_end_in_the_session_of_the_table()
    {
        var random = new Random(31337);
        var h = new HrotDisHarness();
        for (int i = 0; i < 400; i++)
        {
            var row = Table[random.Next(Table.Length)];
            h.HrotStep(new HrotSession(row.Phase, row.Advancing, 1.0, i));
            Assert.Equal((row.State, row.Mode), (h.Engine.Session.State, h.Engine.Session.Mode));
        }

        Assert.Equal(0, h.Engine.Diagnostics.SessionRejections);
    }

    [Fact]
    public void An_unknown_phase_aborts_the_message()
    {
        var h = new HrotDisHarness();
        h.HrotStep(new HrotSession(ClusterPhase.Live, true, 1.0, 0.0));
        h.HrotStep(new HrotSession((ClusterPhase)42, true, 1.0, 0.0));
        Assert.Equal(1, h.Engine.Diagnostics.TransactionFailures);
        Assert.Equal((SessionState.Running, SessionMode.Live), (h.Engine.Session.State, h.Engine.Session.Mode));
    }

    [Fact]
    public void A_time_scale_other_than_one_while_running_warns_once()
    {
        var h = new HrotDisHarness();
        h.HrotStep(new HrotSession(ClusterPhase.Live, true, 2.0, 0.0));
        h.HrotStep(new HrotSession(ClusterPhase.Live, true, 4.0, 0.0));
        h.HrotStep(new HrotSession(ClusterPhase.Live, false, 3.0, 0.0));
        Assert.Equal(1, h.WarningCount(nameof(HrotSessionIngress)));
    }

    [Fact]
    public void Edit_keeps_the_pivot_and_a_return_to_it_from_running_unloads()
    {
        var h = new HrotDisHarness();
        h.GoLive();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 2, 3)));
        h.Session(ClusterPhase.Live, advancing: false);
        Assert.NotNull(h.EntityOfHrot(1)); // pause keeps the entities
        h.Session(ClusterPhase.Edit);
        Assert.Null(h.EntityOfHrot(1)); // back to Loaded goes through Unload
        Assert.Equal(SessionState.Loaded, h.Engine.Session.State);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 2, 3)));
        h.Session(ClusterPhase.Preview); // Loaded -> Running keeps them
        Assert.NotNull(h.EntityOfHrot(1));
    }
}
