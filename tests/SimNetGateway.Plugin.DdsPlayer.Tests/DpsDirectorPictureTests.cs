using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>T-067 (DD-022 P-8): the director's fade and subtitle on the live View - mapped, bounded, sent on the rule (a step of 0.02, 0 or 1 reached, a new text or place) and the place enum pinned.</summary>
public class DpsDirectorPictureTests
{
    private static HrotView View(double t, float fade = 0f, string subtitle = "", byte place = HrotView.NoSubtitlePosition, float alpha = 0f) =>
        new(HrotViewSource.Director, HrotViewMode.Free, 0, 0f, 0f, new HrotVec3(10, 20, 3), HrotQuat.Identity, 60f, "shot", t, 0, fade, subtitle, place, alpha);

    /// <summary>The mapping of HROT's place number to the contract's enum, pinned: 0 bottom, 1 top, 2 middle, anything else (incl. none stated) unknown.</summary>
    [Fact]
    public void The_subtitle_place_maps_hrots_numbers_to_the_contract_enum()
    {
        Assert.Equal(D.SubtitlePosition.SubtitlePositionBottom, DpsViewOutbound.SubtitlePositionOf(0));
        Assert.Equal(D.SubtitlePosition.SubtitlePositionTop, DpsViewOutbound.SubtitlePositionOf(1));
        Assert.Equal(D.SubtitlePosition.SubtitlePositionCentre, DpsViewOutbound.SubtitlePositionOf(2));
        Assert.Equal(D.SubtitlePosition.SubtitlePositionUnknown, DpsViewOutbound.SubtitlePositionOf(3));
        Assert.Equal(D.SubtitlePosition.SubtitlePositionUnknown, DpsViewOutbound.SubtitlePositionOf(HrotView.NoSubtitlePosition));
        // the enum's own numbers (the client's): 0 unknown, 1 bottom, 2 top, 3 centre
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<D.SubtitlePosition>().Select(v => (int)v).Order().ToArray());
        Assert.Equal((1, 2, 3), ((int)D.SubtitlePosition.SubtitlePositionBottom, (int)D.SubtitlePosition.SubtitlePositionTop, (int)D.SubtitlePosition.SubtitlePositionCentre));
    }

    [Fact]
    public void Fade_subtitle_place_and_alpha_reach_the_topic_clamped_and_the_text_is_bounded()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var sent = new List<D.View>();
        rig.Transport.View.Tap = sent.Add;

        rig.Send(View(1.0, 0.5f, "Hello", 1, 0.75f));
        rig.Send(View(1.1, 7f, new string('s', 600), 2, float.NaN));

        Assert.Equal(2, sent.Count);
        Assert.Equal((0.5f, "Hello", D.SubtitlePosition.SubtitlePositionTop, 0.75f), (sent[0].Fade, sent[0].Subtitle, sent[0].SubtitlePosition, sent[0].SubtitleAlpha));
        Assert.Equal((1f, new string('s', 512), D.SubtitlePosition.SubtitlePositionCentre, 0f), (sent[1].Fade, sent[1].Subtitle, sent[1].SubtitlePosition, sent[1].SubtitleAlpha));
    }

    [Fact]
    public void A_view_with_no_director_picture_carries_zero_empty_and_unknown()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var sent = new List<D.View>();
        rig.Transport.View.Tap = sent.Add;

        rig.Send(View(1.0) with { Source = HrotViewSource.Follow });

        var v = Assert.Single(sent);
        Assert.Equal((0f, string.Empty, D.SubtitlePosition.SubtitlePositionUnknown, 0f), (v.Fade, v.Subtitle, v.SubtitlePosition, v.SubtitleAlpha));
    }

    [Fact]
    public void A_fade_or_an_alpha_is_sent_again_past_a_step_and_when_it_reaches_zero_or_one_a_new_text_or_place_is_sent_a_held_picture_is_not()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var sent = new List<D.View>();
        rig.Transport.View.Tap = sent.Add;
        double t = 1.0;
        void Send(float fade = 0f, string subtitle = "", byte place = 0, float alpha = 0f) => rig.Send(View(t += 0.1, fade, subtitle, place, alpha));

        Send();
        Assert.Single(sent);
        Send();
        Send();
        Assert.Single(sent);   // nothing moved
        Send(fade: 0.015f);
        Assert.Single(sent);   // inside the step
        Send(fade: 0.03f);
        Assert.Equal(2, sent.Count);   // past the step since the last SENT value
        Send(fade: 0.04f);
        Assert.Equal(2, sent.Count);
        Send(fade: 0.99f);
        Assert.Equal(3, sent.Count);
        Send(fade: 1f);
        Assert.Equal(4, sent.Count);   // 1 is reached even though the step is small
        Send(fade: 1f);
        Assert.Equal(4, sent.Count);
        Send(fade: 0.015f);
        Assert.Equal(5, sent.Count);   // a big drop
        Send(fade: 0f);
        Assert.Equal(6, sent.Count);   // 0 is reached (0.015 -> 0 is inside the step)
        Send(subtitle: "Line one", alpha: 0.01f);
        Assert.Equal(7, sent.Count);   // the text changed
        Send(subtitle: "Line one", alpha: 0.02f);
        Assert.Equal(7, sent.Count);
        Send(subtitle: "Line one", alpha: 0.5f);
        Assert.Equal(8, sent.Count);   // the alpha
        Send(subtitle: "Line one", place: 1, alpha: 0.5f);
        Assert.Equal(9, sent.Count);   // the place
        Send(subtitle: "Line two", place: 1, alpha: 0.5f);
        Assert.Equal(10, sent.Count);
        Assert.Equal("Line two", sent[^1].Subtitle);
    }
}
