using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>DIS controls HROT's time only when the configuration says so (R-087, DD-007 section 3 item 9).</summary>
public class TimeRequestTests
{
    private static HrotDisHarness Harness(bool disControlsHrot)
    {
        var dis = new DisConfiguration { ExerciseId = 1, DisControlsHrot = disControlsHrot };
        var h = new HrotDisHarness(GatewayConfiguration.CreateDefault() with { Dis = dis });
        h.GoLive();
        return h;
    }

    [Fact]
    public void A_dis_pause_and_resume_become_time_requests_when_dis_controls_hrot()
    {
        var h = Harness(disControlsHrot: true);
        Assert.True(h.Engine.Execute(SessionCommand.Pause, h.DisSource));
        Assert.Equal(SessionState.Paused, h.Engine.Session.State);
        Assert.Equal([new TimeRequest(TimeRequestKind.Pause)], h.Sent<TimeRequest>());

        Assert.True(h.Engine.Execute(SessionCommand.Resume, h.DisSource));
        Assert.Equal(SessionState.Running, h.Engine.Session.State);
        Assert.Equal([new TimeRequest(TimeRequestKind.Pause), new TimeRequest(TimeRequestKind.Resume)], h.Sent<TimeRequest>());
    }

    [Fact]
    public void Without_the_setting_dis_commands_are_vetoed_and_nothing_is_requested()
    {
        var h = Harness(disControlsHrot: false);
        Assert.False(h.Engine.Execute(SessionCommand.Pause, h.DisSource));
        Assert.Equal(SessionState.Running, h.Engine.Session.State);
        Assert.Empty(h.Sent<TimeRequest>());
        Assert.Equal(1, h.Engine.Diagnostics.SessionRejections);
        h.Session(ClusterPhase.Live, advancing: false); // HROT itself still pauses the session
        Assert.Equal(SessionState.Paused, h.Engine.Session.State);
        Assert.False(h.Engine.Execute(SessionCommand.Resume, h.DisSource));
        Assert.Equal(SessionState.Paused, h.Engine.Session.State);
        Assert.Empty(h.Sent<TimeRequest>());
    }

    [Fact]
    public void Commands_of_hrot_itself_never_become_time_requests()
    {
        var h = Harness(disControlsHrot: true);
        h.Session(ClusterPhase.Live, advancing: false);
        h.Session(ClusterPhase.Live, advancing: true);
        Assert.Equal(SessionState.Running, h.Engine.Session.State);
        Assert.Empty(h.Sent<TimeRequest>());
    }

    [Fact]
    public void Only_pause_and_resume_are_requests()
    {
        var h = Harness(disControlsHrot: true);
        Assert.True(h.Engine.Execute(SessionCommand.Pause, h.DisSource));
        h.Engine.Execute(SessionCommand.Unload, h.DisSource); // allowed by the setting, but not a time request
        Assert.Single(h.Sent<TimeRequest>());
    }
}
