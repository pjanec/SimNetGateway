using DISnet;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Dis7;
using SimNetGateway.Protocol.Hrot;
using static SimNetGateway.Gateway.HrotDis.Tests.DisPdus;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>Simulation management both ways (DD-006 6.5, DD-007 section 6 table, R-087): Start/Resume and Stop/Freeze from the HROT session, and from DIS only when DIS controls HROT.</summary>
public class DisSessionTests
{
    private static GatewayConfiguration Config(bool hrotControlsDis = true, bool disControlsHrot = false) =>
        GatewayConfiguration.CreateDefault() with
        {
            Dis = new DisConfiguration { ExerciseId = 1, HrotControlsDis = hrotControlsDis, DisControlsHrot = disControlsHrot },
        };

    /// <summary>The simulation management PDUs sent so far as letters: R = Start/Resume, F = Stop/Freeze.</summary>
    private static string Simans(HrotDisHarness h) =>
        string.Concat(h.DisChecker!.Others.Select(p => p switch { StartResumePdu => "R", StopFreezePdu => "F", _ => "?" }));

    private static HrotDisHarness New(bool hrotControlsDis = true, bool disControlsHrot = false)
    {
        var h = new HrotDisHarness(Config(hrotControlsDis, disControlsHrot));
        h.Origin(50, 14, 300);
        return h;
    }

    [Fact]
    public void The_hrot_session_is_announced_per_the_table()
    {
        var h = New();
        h.Session(ClusterPhase.Edit);
        Assert.Equal(string.Empty, Simans(h)); // Edit is silent to DIS
        h.Session(ClusterPhase.Live);
        Assert.Equal("R", Simans(h)); // Edit to Live starts the exercise
        h.Session(ClusterPhase.Live, advancing: false);
        Assert.Equal("RF", Simans(h));
        h.Session(ClusterPhase.Live, advancing: false);
        Assert.Equal("RF", Simans(h)); // no change, nothing to say
        h.Session(ClusterPhase.Live);
        Assert.Equal("RFR", Simans(h));
        h.Session(ClusterPhase.Preview);
        Assert.Equal("RFR", Simans(h)); // Preview and Live are the same row
        h.Session(ClusterPhase.Preview, advancing: false);
        h.Session(ClusterPhase.Degraded, advancing: true); // Degraded is Paused whatever the clock says
        Assert.Equal("RFRF", Simans(h));
        h.Session(ClusterPhase.Live);
        Assert.Equal("RFRFR", Simans(h));
        h.Session(ClusterPhase.Edit); // back to Loaded goes through Unload
        h.Session(ClusterPhase.Idle);
        Assert.Equal("RFRFR", Simans(h));
    }

    [Theory]
    [MemberData(nameof(SessionTests.AllTransitions), MemberType = typeof(SessionTests))]
    public void Every_transition_of_the_session_table_announces_exactly_the_live_mode_starts_and_pauses(int from, int to)
    {
        var a = SessionTests.Table[from];
        var b = SessionTests.Table[to];
        var h = New();
        h.Session(a.Phase, a.Advancing);
        string before = Simans(h);

        // the oracle: every Start / Resume / Pause command that the plan issues while the session is in Live mode is announced
        var state = h.Engine.Session.State;
        var mode = h.Engine.Session.Mode;
        var (targetState, targetMode) = HrotSessionIngress.Target(b.Phase, b.Advancing);
        string expected = string.Empty;
        foreach (var command in HrotSessionIngress.Plan(state, mode, targetState, targetMode))
        {
            switch (command.Kind)
            {
                case SessionCommandKind.Load:
                    mode = command.Mode;
                    break;
                case SessionCommandKind.Unload:
                    mode = SessionMode.Live;
                    break;
                case SessionCommandKind.Start:
                case SessionCommandKind.Resume:
                    expected += mode == SessionMode.Live ? "R" : string.Empty;
                    break;
                case SessionCommandKind.Pause:
                    expected += mode == SessionMode.Live ? "F" : string.Empty;
                    break;
            }
        }

        h.Session(b.Phase, b.Advancing);
        Assert.Equal(expected, Simans(h)[before.Length..]);
        Assert.Equal(b.State, h.Engine.Session.State);
    }

    [Fact]
    public void A_hrot_that_starts_in_live_announces_the_start()
    {
        var h = New();
        h.Session(ClusterPhase.Live);
        Assert.Equal("R", Simans(h));
    }

    [Fact]
    public void Nothing_is_announced_in_replay_but_a_return_to_live_running_is()
    {
        var h = New();
        h.Session(ClusterPhase.Replay);
        h.Session(ClusterPhase.Replay, advancing: false);
        h.Session(ClusterPhase.Review);
        Assert.Equal(string.Empty, Simans(h));
        h.Session(ClusterPhase.Live);
        Assert.Equal("R", Simans(h));
    }

    [Fact]
    public void Nothing_is_announced_when_hrot_does_not_control_dis()
    {
        var h = New(hrotControlsDis: false);
        h.Session(ClusterPhase.Edit);
        h.Session(ClusterPhase.Live);
        h.Session(ClusterPhase.Live, advancing: false);
        h.Session(ClusterPhase.Live);
        h.Session(ClusterPhase.Idle);
        Assert.Equal(string.Empty, Simans(h));
        Assert.Empty(h.DisChecker!.Others);
    }

    [Fact]
    public void The_pdus_go_to_everybody_from_the_gateway_with_increasing_request_ids()
    {
        var h = New();
        h.Session(ClusterPhase.Live);
        h.Run(2);
        h.Session(ClusterPhase.Live, advancing: false);
        h.Session(ClusterPhase.Live);
        var start = (StartResumePdu)h.DisChecker!.Others[0];
        var stop = (StopFreezePdu)h.DisChecker.Others[1];
        var resume = (StartResumePdu)h.DisChecker.Others[2];
        Assert.Equal(PduTypes.StartResume, start.PduType);
        Assert.Equal(PduTypes.StopFreeze, stop.PduType);
        foreach (Pdu pdu in h.DisChecker.Others)
        {
            Assert.Equal(1, pdu.ExerciseID);
            Assert.False(DisTimestamp.IsAbsolute(pdu.Timestamp));
        }

        Assert.Equal(new DisEntityKey(1, 3001, 0), DisConversions.KeyOf(start.OriginatingEntityID));
        Assert.Equal(new DisEntityKey(0xFFFF, 0xFFFF, 0xFFFF), DisConversions.KeyOf(start.ReceivingEntityID));
        Assert.Equal(new DisEntityKey(0xFFFF, 0xFFFF, 0xFFFF), DisConversions.KeyOf(stop.ReceivingEntityID));
        Assert.Equal(DisSessionEgress.ReasonRecess, stop.Reason);
        Assert.Equal(0, stop.FrozenBehavior);
        Assert.Equal<uint>(1, start.RequestID);
        Assert.Equal<uint>(2, stop.RequestID);
        Assert.Equal<uint>(3, resume.RequestID);
        double wall = h.Clock.Now;
        Assert.Equal(0u, resume.RealWorldTime.Hour);
        Assert.Equal(wall, DisTimestamp.ToSecondsPastHour(resume.RealWorldTime.TimePastHour), 3);
        Assert.True(DisTimestamp.IsAbsolute(resume.RealWorldTime.TimePastHour));
    }

    // ---- DIS controls HROT ---------------------------------------------------------------------------------------------------

    private static HrotDisHarness Running(bool disControlsHrot)
    {
        var h = New(disControlsHrot: disControlsHrot);
        h.Session(ClusterPhase.Live);
        return h;
    }

    [Fact]
    public void Dis_stop_freeze_and_start_resume_are_ignored_unless_dis_controls_hrot()
    {
        var h = Running(disControlsHrot: false);
        h.DisStep(StopFreeze());
        Assert.Equal(SessionState.Running, h.Engine.Session.State);
        Assert.Empty(h.Sent<TimeRequest>());
        h.Session(ClusterPhase.Live, advancing: false);
        h.DisStep(StartResume());
        Assert.Equal(SessionState.Paused, h.Engine.Session.State);
        Assert.Empty(h.Sent<TimeRequest>());
        Assert.Equal(2, h.Services.DisIngress.SimanIgnored);
    }

    [Fact]
    public void With_dis_controlling_hrot_a_stop_freeze_pauses_and_a_start_resume_resumes_through_time_requests()
    {
        var h = Running(disControlsHrot: true);
        int before = h.DisChecker!.Others.Count;
        h.DisStep(StopFreeze());
        Assert.Equal(SessionState.Paused, h.Engine.Session.State);
        Assert.Equal(TimeRequestKind.Pause, Assert.Single(h.Sent<TimeRequest>()).Kind);
        h.DisStep(StartResume());
        Assert.Equal(SessionState.Running, h.Engine.Session.State);
        Assert.Equal([TimeRequestKind.Pause, TimeRequestKind.Resume], h.Sent<TimeRequest>().Select(r => r.Kind).ToArray());
        Assert.Equal(before, h.DisChecker.Others.Count); // a pause that came from DIS is not announced back to DIS
    }

    [Fact]
    public void A_stop_freeze_while_paused_or_a_start_resume_while_running_changes_nothing()
    {
        var h = Running(disControlsHrot: true);
        h.DisStep(StartResume());
        Assert.Empty(h.Sent<TimeRequest>());
        h.DisStep(StopFreeze());
        h.DisStep(StopFreeze());
        Assert.Single(h.Sent<TimeRequest>());
        Assert.Equal(0, h.Engine.Diagnostics.SessionRejections);
    }

    [Fact]
    public void The_hrot_session_stays_the_master_after_a_dis_pause()
    {
        var h = Running(disControlsHrot: true);
        h.DisStep(StopFreeze());
        Assert.Equal(SessionState.Paused, h.Engine.Session.State);
        h.Session(ClusterPhase.Live); // HROT did not pause: the next HROT session state wins
        Assert.Equal(SessionState.Running, h.Engine.Session.State);
    }

    [Fact]
    public void Simulation_management_pdus_must_be_addressed_to_the_gateway_and_come_from_the_right_exercise()
    {
        var h = Running(disControlsHrot: true);
        h.DisStep(StopFreeze(Receiving(9, 9, 0))); // another application
        h.DisStep(StopFreeze(Receiving(1, 9, 0))); // our site, another application
        h.DisStep(StopFreeze(exercise: 2));
        Assert.Equal(SessionState.Running, h.Engine.Session.State);
        h.DisStep(StopFreeze(Receiving(1, 3001, 0))); // exactly us
        Assert.Equal(SessionState.Paused, h.Engine.Session.State);
        h.DisStep(StartResume(Receiving(DisConversions.All, 3001)));
        Assert.Equal(SessionState.Running, h.Engine.Session.State);
        h.DisStep(StopFreeze(Receiving(1, DisConversions.All)));
        Assert.Equal(SessionState.Paused, h.Engine.Session.State);
    }

    [Fact]
    public void The_gateways_own_simulation_management_pdus_coming_back_are_ignored()
    {
        var h = Running(disControlsHrot: true);
        h.Session(ClusterPhase.Live, advancing: false);
        h.Session(ClusterPhase.Live);
        var own = h.DisChecker!.Others.ToList();
        Assert.Equal(3, own.Count);
        foreach (var pdu in own)
        {
            if (pdu is StartResumePdu start)
            {
                h.DisStep(ThroughTheWire(start));
            }
            else if (pdu is StopFreezePdu stop)
            {
                h.DisStep(ThroughTheWire(stop));
            }
        }

        Assert.Equal(SessionState.Running, h.Engine.Session.State);
        Assert.Empty(h.Sent<TimeRequest>());
        Assert.Equal(3, h.Services.DisIngress.Echoes);
    }
}
