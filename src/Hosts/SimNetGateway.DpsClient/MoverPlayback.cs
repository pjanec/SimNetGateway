using SimNetGateway.Common.Tracks;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.DpsClient;

/// <summary>
/// The reference implementation of DELAYED playback (DD-018 3.6c): ambient movers are shown <see cref="PlaybackDelay"/> behind the newest knot time, so a mover is always on the
/// curve between two real knots (<see cref="TrackCurve"/>, the same rule the sender checked its tolerance against) and never needs extrapolation unless a knot is late.
/// The simulation clock is the newest batch's time plus the client time since it arrived (at most <see cref="MaxExtrapolationS"/>: a paused simulation sends no batches
/// and the movers stand). A mover is shown from its second knot. Free of the DDS readers: <see cref="DpsWorld"/> feeds it. Main thread only.
/// </summary>
public sealed class MoverPlayback
{
    /// <summary>How far behind the newest knot time movers are drawn, in seconds: the one place of the constant (DD-018 3.6b).</summary>
    public const double PlaybackDelay = 1.2;

    /// <summary>The longest the simulation clock runs on past the newest batch on the client's own clock.</summary>
    public const double MaxExtrapolationS = 0.25;

    private const double KeepBehindS = 5.0;

    private sealed class Track
    {
        public D.Mover? Info;
        public readonly List<TrackKnot> Knots = new();
    }

    private readonly Dictionary<int, Track> _tracks = new();
    private double _newestSim = double.NegativeInfinity;
    private double _newestAt;

    /// <summary>The mover ids known now.</summary>
    public int Count => _tracks.Count;

    /// <summary>The newest knot time received (negative infinity before the first).</summary>
    public double NewestKnotTime => _newestSim;

    public void Clear()
    {
        _tracks.Clear();
        _newestSim = double.NegativeInfinity;
        _newestBatchSim = double.NegativeInfinity;
    }

    public void SetMover(in D.Mover mover) => TrackOf(mover.MoverId).Info = mover;

    public void Remove(int moverId) => _tracks.Remove(moverId);

    /// <summary>Takes a batch received at client time <paramref name="clockNow"/>.</summary>
    public void AddBatch(in D.MoverTracks batch, double clockNow)
    {
        if (batch.Knots is null)
        {
            return;
        }

        foreach (var k in batch.Knots)
        {
            var knots = TrackOf(k.MoverId).Knots;
            double time = batch.SimTime + k.Dt;
            if (knots.Count > 0 && time <= knots[^1].Time)
            {
                continue;   // a late or repeated knot
            }

            knots.Add(new TrackKnot(time, k.X, k.Y, k.Z, k.HeadingCentiDeg * Math.PI / 18000.0, k.PitchCentiDeg * Math.PI / 18000.0, k.Speed));
            if (time > _newestSim)
            {
                _newestSim = time;
            }
        }

        _newestBatchSim = Math.Max(_newestBatchSim, batch.SimTime);
        _newestAt = clockNow;
    }

    private double _newestBatchSim = double.NegativeInfinity;

    /// <summary>The simulation time the movers are drawn at when the client clock reads <paramref name="clockNow"/>: the simulation clock less <see cref="PlaybackDelay"/>.</summary>
    public double PlaybackTime(double clockNow) => _newestBatchSim + Math.Clamp(clockNow - _newestAt, 0.0, MaxExtrapolationS) - PlaybackDelay;

    /// <summary>The pose of mover <paramref name="moverId"/> at the playback time; false before its second knot.</summary>
    public bool TryPose(int moverId, double clockNow, out TrackPose pose)
    {
        pose = default;
        if (!_tracks.TryGetValue(moverId, out var track) || track.Knots.Count < 2 || double.IsNegativeInfinity(_newestBatchSim))
        {
            return false;
        }

        double t = PlaybackTime(clockNow);
        if (t < track.Knots[0].Time)
        {
            return false;   // not yet: the mover appears when playback reaches its first knot
        }

        TrimBefore(track, t);
        return TrackCurve.TryAt(track.Knots, t, out pose);
    }

    /// <summary>Every mover that has something to show now, with its pose.</summary>
    public IEnumerable<(D.Mover Info, TrackPose Pose)> Visible(double clockNow)
    {
        foreach (var (id, track) in _tracks)
        {
            if (track.Info is { } info && TryPose(id, clockNow, out var pose))
            {
                yield return (info, pose);
            }
        }
    }

    public D.Mover? InfoOf(int moverId) => _tracks.TryGetValue(moverId, out var t) ? t.Info : null;

    private Track TrackOf(int id)
    {
        if (!_tracks.TryGetValue(id, out var track))
        {
            _tracks[id] = track = new Track();
        }

        return track;
    }

    private static void TrimBefore(Track track, double playbackTime)
    {
        int drop = 0;
        while (drop + 2 < track.Knots.Count && track.Knots[drop + 1].Time < playbackTime - KeepBehindS)
        {
            drop++;
        }

        if (drop > 0)
        {
            track.Knots.RemoveRange(0, drop);
        }
    }
}
