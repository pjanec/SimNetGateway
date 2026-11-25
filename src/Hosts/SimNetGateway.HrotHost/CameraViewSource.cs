using Fdp.Core;
using Fdp.Toolkit.Time;
using Hrot.Look;
using Hrot.Map.Common.Components;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The suggested view as HROT states it (DD-019 3.1): HROT's own <c>CameraSuggestion</c> world singleton, read through its look library's <c>CameraAdvice.TryGet</c>. The map
/// writes it every frame while its 3-D view is on, so a node with no map (or a map showing 2-D) suggests nothing. HROT's names pass through as names; the pose is HROT's world
/// frame as every other gathered pose is.
/// <para>The director's picture (DD-022 P-8): from a <c>Director</c> source the view carries <c>Fade</c>, <c>CaptionPosition</c> and <c>CaptionAlpha</c> as HROT states them and the subtitle TEXT, which is read from HROT's
/// <c>FilmCaption</c> managed singleton only when <c>CaptionSerial</c> changes (the text is cached with its serial: a steady frame does no string work). Every other source sends 0, "" and no place.
/// The take name is cached by its value too, so a steady frame allocates nothing. Main thread only.</para>
/// </summary>
public sealed class HrotCameraViewSource : ISuggestedViewSource
{
    private EntityRepository? _world;
    private int _serial = -1;
    private string _text = string.Empty;
    private FixedString64 _take;
    private string _takeName = string.Empty;

    /// <summary>How many times the caption text was read from HROT (a rail pins it to the serial changes).</summary>
    public int TextReads { get; private set; }

    public bool TryRead(EntityRepository world, out HrotView view)
    {
        if (!CameraAdvice.TryGet(world, out var s))
        {
            view = default;
            return false;
        }

        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            _serial = -1;
            _text = string.Empty;
            _take = default;
            _takeName = string.Empty;
        }

        if (!_take.Equals(s.Take))
        {
            _take = s.Take;
            _takeName = s.Take.ToString();
        }

        float fade = 0f, alpha = 0f;
        string subtitle = string.Empty;
        byte place = HrotView.NoSubtitlePosition;
        if (s.Source == CameraSource.Director)
        {
            fade = Unit(s.Fade);
            alpha = Unit(s.CaptionAlpha);
            place = s.CaptionPosition;
            if (s.CaptionSerial != _serial)
            {
                _serial = s.CaptionSerial;
                _text = world.HasSingletonManaged<FilmCaption>() ? world.GetSingletonManaged<FilmCaption>()?.Text ?? string.Empty : string.Empty;
                TextReads++;
            }

            subtitle = _text;
        }

        // the director names its follow unit by network id; the other drivers bind an entity
        int target = s.BoundNetId != 0 ? s.BoundNetId : LookIds.NetworkIdOf(world, s.Bound);
        view = new HrotView(
            PivotEnum<HrotViewSource>.Of(s.Source),
            PivotEnum<HrotViewMode>.Of(s.Mode),   // by member name; the contract's name for HROT's FixedKey is a rename row in PivotEnums
            target,
            s.Distance,
            s.ShoulderSide,
            new HrotVec3(s.Position.X, s.Position.Y, s.Position.Z),
            new HrotQuat(s.Orientation.X, s.Orientation.Y, s.Orientation.Z, s.Orientation.W),
            s.FovyDegrees,
            _takeName,
            SimClock.TotalTimeOf(world),
            s.Cut,
            fade, subtitle, place, alpha);
        return true;
    }

    private static float Unit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
