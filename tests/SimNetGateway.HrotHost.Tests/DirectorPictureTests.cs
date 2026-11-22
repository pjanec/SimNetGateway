using System.Numerics;
using Fdp.Core;
using Hrot.Map.Common.Components;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// T-067 (DD-022 P-8): the live view carries the director's fade and subtitle, read from HROT's <c>CameraSuggestion</c> (Fade, CaptionSerial, CaptionPosition, CaptionAlpha) and the <c>FilmCaption</c> managed singleton. The text is
/// read only when the serial changes; every other source sends 0 / empty; a steady read allocates nothing.
/// </summary>
public class DirectorPictureTests
{
    private static CameraSuggestion Director(float fade = 0f, int serial = 0, byte place = 0, float alpha = 0f, CameraSource source = CameraSource.Director) => new()
    {
        Source = source,
        Mode = CameraMode.FixedKey,
        Position = new Vector3(10, 20, 30),
        Orientation = Quaternion.Identity,
        FovyDegrees = 55f,
        Take = new FixedString64("Take 3"),
        Fade = fade,
        CaptionSerial = serial,
        CaptionPosition = place,
        CaptionAlpha = alpha,
    };

    private static HrotView Read(HrotCameraViewSource source, EntityRepository world)
    {
        Assert.True(source.TryRead(world, out var view));
        return view;
    }

    [Fact]
    public void The_fade_and_the_alpha_follow_hrots_values_and_the_place_passes_as_hrots_number()
    {
        var source = new HrotCameraViewSource();
        using var world = new EntityRepository();
        foreach (var (fade, alpha, place) in new[] { (0f, 0f, (byte)0), (0.3f, 0.5f, (byte)1), (1f, 1f, (byte)2), (0.02f, 0.99f, (byte)0) })
        {
            world.SetSingleton(Director(fade, 0, place, alpha));

            var view = Read(source, world);

            Assert.Equal((fade, alpha, place), (view.Fade, view.SubtitleAlpha, view.SubtitlePosition));
        }
    }

    [Fact]
    public void The_subtitle_text_follows_the_caption_serial_and_is_read_only_when_the_serial_changes()
    {
        var source = new HrotCameraViewSource();
        using var world = new EntityRepository();
        world.SetSingletonManaged(new FilmCaption("Hello"));
        world.SetSingleton(Director(0f, 1, 0, 1f));
        Assert.Equal("Hello", Read(source, world).Subtitle);
        Assert.Equal(1, source.TextReads);

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal("Hello", Read(source, world).Subtitle);
        }

        Assert.Equal(1, source.TextReads);   // a held caption is never read again

        world.SetSingletonManaged(new FilmCaption("Changed without a serial"));   // HROT never does this; the serial is the only signal
        Assert.Equal("Hello", Read(source, world).Subtitle);

        world.SetSingleton(Director(0f, 2, 0, 1f));   // the serial moved: the new text
        Assert.Equal("Changed without a serial", Read(source, world).Subtitle);
        Assert.Equal(2, source.TextReads);

        world.SetSingletonManaged(new FilmCaption(string.Empty));
        world.SetSingleton(Director(0f, 3, 0, 0f));   // the caption ended
        Assert.Equal(string.Empty, Read(source, world).Subtitle);
        Assert.Equal(3, source.TextReads);
    }

    [Fact]
    public void A_world_with_no_caption_singleton_gives_an_empty_subtitle_and_another_world_is_read_afresh()
    {
        var source = new HrotCameraViewSource();
        using var one = new EntityRepository();
        one.SetSingleton(Director(0f, 5, 0, 1f));
        Assert.Equal(string.Empty, Read(source, one).Subtitle);

        using var two = new EntityRepository();
        two.SetSingletonManaged(new FilmCaption("Second world"));
        two.SetSingleton(Director(0f, 5, 0, 1f));   // the very same serial in another world
        Assert.Equal("Second world", Read(source, two).Subtitle);
    }

    [Theory]
    [InlineData(CameraSource.Follow)]
    [InlineData(CameraSource.Operator)]
    public void Any_other_source_sends_zero_empty_and_no_place_and_never_reads_the_text(CameraSource other)
    {
        var source = new HrotCameraViewSource();
        using var world = new EntityRepository();
        world.SetSingletonManaged(new FilmCaption("Stale"));
        world.SetSingleton(Director(0.8f, 9, 1, 0.9f, other));

        var view = Read(source, world);

        Assert.Equal((0f, string.Empty, HrotView.NoSubtitlePosition, 0f), (view.Fade, view.Subtitle, view.SubtitlePosition, view.SubtitleAlpha));
        Assert.Equal(0, source.TextReads);
    }

    [Fact]
    public void A_non_finite_or_out_of_range_fade_or_alpha_is_clamped()
    {
        var source = new HrotCameraViewSource();
        using var world = new EntityRepository();
        world.SetSingleton(Director(float.NaN, 0, 0, 7f));
        var view = Read(source, world);
        Assert.Equal((0f, 1f), (view.Fade, view.SubtitleAlpha));
        world.SetSingleton(Director(-3f, 0, 0, float.PositiveInfinity));
        view = Read(source, world);
        Assert.Equal((0f, 0f), (view.Fade, view.SubtitleAlpha));
    }

    /// <summary>0 B per steady read: a director holding a caption and a fade, the take name cached by value.</summary>
    [Fact]
    public void A_steady_read_allocates_nothing()
    {
        var source = new HrotCameraViewSource();
        using var world = new EntityRepository();
        world.SetSingletonManaged(new FilmCaption("A subtitle that is on screen for a while"));
        world.SetSingleton(Director(0.4f, 4, 2, 0.75f));
        for (int i = 0; i < 50; i++)
        {
            source.TryRead(world, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            source.TryRead(world, out _);
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
