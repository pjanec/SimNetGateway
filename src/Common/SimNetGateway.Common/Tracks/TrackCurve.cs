namespace SimNetGateway.Common.Tracks;

/// <summary>
/// One point of a track (DD-018 3.6b, 3.6c): the time, the position (metres, x east, y north, z up), the heading (radians about up, 0 = +x, counter-clockwise), the pitch
/// (radians, positive = nose up) and the speed along the heading (m/s). The velocity is speed * (cos h cos p, sin h cos p, sin p).
/// </summary>
public readonly record struct TrackKnot(double Time, double X, double Y, double Z, double HeadingRad, double PitchRad, double Speed)
{
    public double VelocityX => Speed * Math.Cos(HeadingRad) * Math.Cos(PitchRad);

    public double VelocityY => Speed * Math.Sin(HeadingRad) * Math.Cos(PitchRad);

    public double VelocityZ => Speed * Math.Sin(PitchRad);
}

/// <summary>A pose read off a track.</summary>
public readonly record struct TrackPose(double X, double Y, double Z, double HeadingRad, double PitchRad, double Speed);

/// <summary>
/// The curve rule of a delayed track, the ONE implementation for the sender (which checks its tolerance against it) and a client (which plays it): between two knots a cubic
/// Hermite in position with the knots' velocities as tangents, the heading and pitch along the shortest way and the speed linearly; past the newest knot a straight
/// extrapolation along its velocity.
/// </summary>
public static class TrackCurve
{
    /// <summary>The pose at <paramref name="time"/> on the curve from <paramref name="a"/> to <paramref name="b"/> (times outside the span are clamped to its ends).</summary>
    public static TrackPose Between(in TrackKnot a, in TrackKnot b, double time)
    {
        double span = b.Time - a.Time;
        if (span <= 1e-9)
        {
            return PoseOf(in b);
        }

        double u = Math.Clamp((time - a.Time) / span, 0.0, 1.0);
        double u2 = u * u, u3 = u2 * u;
        double h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
        return new TrackPose(
            h00 * a.X + h10 * span * a.VelocityX + h01 * b.X + h11 * span * b.VelocityX,
            h00 * a.Y + h10 * span * a.VelocityY + h01 * b.Y + h11 * span * b.VelocityY,
            h00 * a.Z + h10 * span * a.VelocityZ + h01 * b.Z + h11 * span * b.VelocityZ,
            a.HeadingRad + WrapPi(b.HeadingRad - a.HeadingRad) * u,
            a.PitchRad + (b.PitchRad - a.PitchRad) * u,
            a.Speed + (b.Speed - a.Speed) * u);
    }

    /// <summary>The pose <paramref name="time"/> after (or before) the newest knot, straight along its velocity.</summary>
    public static TrackPose Extrapolate(in TrackKnot last, double time)
    {
        double dt = time - last.Time;
        return new TrackPose(last.X + last.VelocityX * dt, last.Y + last.VelocityY * dt, last.Z + last.VelocityZ * dt, last.HeadingRad, last.PitchRad, last.Speed);
    }

    /// <summary>The pose at <paramref name="time"/> on a whole track (knots in time order): before the first knot the first, inside the curve, after the last the extrapolation.</summary>
    public static bool TryAt(IReadOnlyList<TrackKnot> knots, double time, out TrackPose pose)
    {
        pose = default;
        if (knots.Count == 0)
        {
            return false;
        }

        if (time <= knots[0].Time)
        {
            pose = PoseOf(knots[0]);
            return true;
        }

        for (int i = 1; i < knots.Count; i++)
        {
            if (time <= knots[i].Time)
            {
                pose = Between(knots[i - 1], knots[i], time);
                return true;
            }
        }

        pose = Extrapolate(knots[^1], time);
        return true;
    }

    public static TrackPose PoseOf(in TrackKnot k) => new(k.X, k.Y, k.Z, k.HeadingRad, k.PitchRad, k.Speed);

    /// <summary>An angle wrapped into (-pi, pi].</summary>
    public static double WrapPi(double a)
    {
        a %= 2.0 * Math.PI;
        if (a > Math.PI)
        {
            a -= 2.0 * Math.PI;
        }
        else if (a <= -Math.PI)
        {
            a += 2.0 * Math.PI;
        }

        return a;
    }
}

/// <summary>The tolerance of a delayed track and its longest gap between knots (DD-018 3.6c).</summary>
public readonly record struct KnotPolicy(double PositionToleranceM, double HeadingToleranceRad, double MaxIntervalS)
{
    /// <summary>0.10 m, 2 degrees, at most 1 s between knots.</summary>
    public static KnotPolicy Default { get; } = new(0.10, 2.0 * Math.PI / 180.0, 1.0);
}

/// <summary>
/// Turns the poses of one mover, in time order, into the knots a client needs: a knot at the first pose, then a knot when the curve from the last sent knot to the newest
/// pose would miss a pose in between by more than the tolerance (the knot is the newest pose that still fitted), and at least every <see cref="KnotPolicy.MaxIntervalS"/>.
/// Speed and pitch come from the poses (the motion over the last half second); a flat mover (a pedestrian) has pitch 0. A client playing the knots with
/// <see cref="TrackCurve"/> is never off by more than the tolerance at a pose time up to the newest sent knot. Not thread safe.
/// </summary>
public sealed class TrackBuilder
{
    private const double VelocityWindowS = 0.5, MinHorizontalSpeed = 0.5;

    private readonly KnotPolicy _policy;
    private readonly bool _flat;
    private readonly List<TrackKnot> _buffer = new();                                      // the poses after the last sent knot
    private readonly List<(double T, double X, double Y, double Z)> _motion = new();       // the poses of the last VelocityWindowS, for the speed
    private TrackKnot _last;
    private bool _hasLast;
    private double _pitch;
    private (double X, double Y, double Z, double H) _previous;
    private bool _hasPrevious;

    public TrackBuilder(bool flat = false, KnotPolicy? policy = null)
    {
        _flat = flat;
        _policy = policy ?? KnotPolicy.Default;
    }

    /// <summary>Knots sent so far.</summary>
    public int Sent { get; private set; }

    /// <summary>The pose of the mover at <paramref name="time"/>; the knots it makes are appended to <paramref name="knots"/>.</summary>
    public void Add(double time, double x, double y, double z, double headingRad, List<TrackKnot> knots)
    {
        if (_hasLast && time <= _last.Time + 1e-9)
        {
            return;   // not later than the newest knot: nothing new
        }

        // an unchanged pose is no new motion: only the longest gap can ask for a knot (a car that stands still sends one a second)
        bool same = _hasPrevious && _previous == (x, y, z, headingRad);
        if (same && _hasLast)
        {
            if (time - _last.Time >= _policy.MaxIntervalS)
            {
                Emit(new TrackKnot(time, x, y, z, headingRad, _pitch, 0.0), knots);
                _buffer.Clear();
                _motion.Clear();
                _motion.Add((time, x, y, z));
            }

            return;
        }

        _previous = (x, y, z, headingRad);
        _hasPrevious = true;
        var pose = new TrackKnot(time, x, y, z, headingRad, _pitch, 0.0);
        pose = pose with { Speed = SpeedOf(time, x, y, z, headingRad, out double pitch), PitchRad = pitch };
        _pitch = pose.PitchRad;

        if (!_hasLast)
        {
            Emit(pose, knots);   // the first knot, at the spawn
            return;
        }

        if (_buffer.Count > 0 && Violates(in pose))
        {
            // the newest pose that still fitted becomes a knot; the current one starts the next stretch
            var knot = _buffer[^1];
            _buffer.Clear();
            Emit(knot, knots);
        }

        if (time - _last.Time >= _policy.MaxIntervalS)
        {
            _buffer.Clear();
            Emit(pose, knots);
        }
        else
        {
            _buffer.Add(pose);
        }
    }

    private void Emit(in TrackKnot knot, List<TrackKnot> knots)
    {
        _last = knot;
        _hasLast = true;
        Sent++;
        knots.Add(knot);
    }

    private bool Violates(in TrackKnot candidate)
    {
        foreach (var b in _buffer)
        {
            var at = TrackCurve.Between(_last, candidate, b.Time);
            double dx = at.X - b.X, dy = at.Y - b.Y, dz = at.Z - b.Z;
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > _policy.PositionToleranceM || Math.Abs(TrackCurve.WrapPi(at.HeadingRad - b.HeadingRad)) > _policy.HeadingToleranceRad)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The speed along the heading and the pitch from the motion over the last half second (the first pose has none: 0).</summary>
    private double SpeedOf(double time, double x, double y, double z, double heading, out double pitch)
    {
        _motion.Add((time, x, y, z));
        while (_motion.Count > 2 && time - _motion[1].T >= VelocityWindowS)
        {
            _motion.RemoveAt(0);
        }

        pitch = _flat ? 0.0 : _pitch;
        var old = _motion[0];
        double dt = time - old.T;
        if (dt <= 1e-6)
        {
            return 0.0;
        }

        double vx = (x - old.X) / dt, vy = (y - old.Y) / dt, vz = (z - old.Z) / dt;
        double horizontal = Math.Sqrt(vx * vx + vy * vy);
        double along = vx * Math.Cos(heading) + vy * Math.Sin(heading);
        if (!_flat && horizontal >= MinHorizontalSpeed)
        {
            pitch = Math.Atan2(vz, horizontal) * Math.Sign(along == 0 ? 1 : along);
        }

        return along / Math.Cos(pitch);   // the speed along the heading in 3-D, so that speed * (cos h cos p, sin h cos p, sin p) is the velocity
    }
}
