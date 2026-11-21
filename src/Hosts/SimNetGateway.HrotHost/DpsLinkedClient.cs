using Hrot.Common.Infrastructure;
using SimNetGateway.Gateway.HrotDis;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The gateway as HROT's linked client (HROT CE-11026 / CE-11018, DD-022 3e, 3g): what HROT's node knows of "the viewer behind the gateway". Product-neutral: it is named
/// <see cref="ClientName"/> ("dps", the protocol) and never after what is behind it (HROT R-11009). The world members forward to the world gate of the dps plugin (the clients' readiness for
/// every load), the film members to its render gate (the render client's frames and soundtrack); both are reached through the plugin's host service, so a node whose dps side does not run
/// has neither (every load is ready at once, nothing films). The film calls return tasks completed on the gateway's pump; a failure is a faulted task with the reason as its message.
/// </summary>
public sealed class DpsLinkedClient : ILinkedClient
{
    /// <summary>"dps".</summary>
    public const string ClientName = "dps";

    private readonly Func<IClientWorldGate?> _world;
    private readonly Func<IRenderGate?> _render;

    public DpsLinkedClient(Func<IClientWorldGate?> world, Func<IRenderGate?> render)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _render = render ?? throw new ArgumentNullException(nameof(render));
    }

    /// <inheritdoc />
    public string Name => ClientName;

    /// <inheritdoc />
    public void BeginWorld(Guid loadId) => _world()?.BeginWorld(loadId);

    /// <inheritdoc />
    public bool IsWorldReady(Guid loadId, out string? failure, out string? step)
    {
        if (_world() is { } gate)
        {
            return gate.IsWorldReady(loadId, out failure, out step);
        }

        failure = null;
        step = null;
        return true;
    }

    /// <inheritdoc />
    public bool CanFilm => _render()?.CanFilm ?? false;

    /// <inheritdoc />
    public Task BeginFilm(FilmJob job) =>
        _render() is { } gate ? gate.BeginJob(Map(job)) : Task.FromException(new NotSupportedException("the gateway's dps side is not running: no render client"));

    /// <inheritdoc />
    public Task Frame(FilmFrame frame) =>
        _render() is { } gate ? gate.Frame(Map(frame)) : Task.FromException(new NotSupportedException("the gateway's dps side is not running: no render client"));

    /// <inheritdoc />
    public async Task<FilmTrack?> EndFilm(FilmJob job, FilmJobOutcome outcome)
    {
        if (_render() is not { } gate)
        {
            return null;
        }

        var end = outcome.End switch
        {
            FilmJobEnd.Ok => RenderEnd.Ok,
            FilmJobEnd.Failed => RenderEnd.Failed,
            _ => RenderEnd.Stopped,
        };
        var track = await gate.EndJob(Map(job), end, outcome.Reason).ConfigureAwait(false);
        return track is null ? null : new FilmTrack(track.Path, track.Samples, track.SampleRate, track.Channels);
    }

    internal static RenderJobSpec Map(FilmJob job) =>
        new(job.JobId, job.Kind == FilmJobKind.Reel, job.What ?? string.Empty, job.Fps, job.Width, job.Height, job.Supersample, job.Dir ?? string.Empty, job.Sound == FilmSound.Client);

    internal static RenderFrameSpec Map(FilmFrame f)
    {
        var graphics = f.Graphics.Count == 0 ? Array.Empty<RenderGraphic>() : new RenderGraphic[f.Graphics.Count];
        for (int i = 0; i < graphics.Length; i++)
        {
            var g = f.Graphics[i];
            // HROT's graphic colour is 0xRRGGBB (its default is 0xFFD23C; the opacity is the separate Alpha), whatever the record's comment says
            graphics[i] = new RenderGraphic((RenderGraphicKind)(byte)g.Kind, g.Points, g.Radius, g.Text, g.Colour & 0xFFFFFFu, g.Alpha);
        }

        var subtitle = f.Subtitle is { } s ? new RenderSubtitle(s.Text, (RenderSubtitlePosition)(byte)s.Position, s.Alpha) : null;
        return new RenderFrameSpec(f.JobId, f.Index, (RenderFrameKind)(byte)f.Kind, f.SimTime, f.FilmRate, f.Step, f.Camera.Position, f.Camera.Forward, f.Camera.Up, f.Camera.FovDeg,
            f.Width, f.Height, f.Supersample, f.Path ?? string.Empty, subtitle, graphics, f.Fade, f.LayersOn ?? Array.Empty<string>(), f.LayersOff ?? Array.Empty<string>());
    }
}
