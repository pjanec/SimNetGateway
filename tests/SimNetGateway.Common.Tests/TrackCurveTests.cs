using SimNetGateway.Common.Tracks;

namespace SimNetGateway.Common.Tests;

/// <summary>The track rule of DD-018 3.6b / 3.6c: the Hermite curve, and the knot policy that keeps a client within the tolerance.</summary>
public class TrackCurveTests
{
    private const double Deg = Math.PI / 180.0;

    private static TrackKnot Knot(double t, double x, double y, double z = 0, double heading = 0, double pitch = 0, double speed = 0) => new(t, x, y, z, heading, pitch, speed);

    // ---- the curve ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_curve_passes_through_its_knots_and_a_straight_constant_speed_stretch_is_exactly_straight()
    {
        var a = Knot(10, 100, 50, 2, 0.3, 0.05, 12);
        var b = Knot(11, 100 + a.VelocityX, 50 + a.VelocityY, 2 + a.VelocityZ, 0.3, 0.05, 12);

        var atA = TrackCurve.Between(a, b, 10);
        var atB = TrackCurve.Between(a, b, 11);
        var mid = TrackCurve.Between(a, b, 10.37);

        Assert.Equal((a.X, a.Y, a.Z), (atA.X, atA.Y, atA.Z));
        Assert.Equal((b.X, b.Y, b.Z), (atB.X, atB.Y, atB.Z));
        Assert.Equal(a.X + a.VelocityX * 0.37, mid.X, 9);   // the tangents are the straight line: the Hermite is the line
        Assert.Equal(a.Y + a.VelocityY * 0.37, mid.Y, 9);
        Assert.Equal(a.Z + a.VelocityZ * 0.37, mid.Z, 9);
        Assert.Equal((0.3, 0.05, 12.0), (mid.HeadingRad, mid.PitchRad, mid.Speed));
    }

    [Fact]
    public void Heading_goes_the_short_way_across_the_wrap_and_times_outside_the_span_are_clamped()
    {
        var a = Knot(0, 0, 0, heading: 179 * Deg);
        var b = Knot(1, 0, 0, heading: -179 * Deg);

        Assert.Equal(180.0, Math.Abs(TrackCurve.Between(a, b, 0.5).HeadingRad) / Deg, 6);   // through 180, not through 0
        Assert.Equal(179 * Deg, TrackCurve.Between(a, b, -5).HeadingRad, 9);
        Assert.Equal(-179 * Deg, TrackCurve.WrapPi(TrackCurve.Between(a, b, 5).HeadingRad), 9);
        Assert.Equal(0.0, TrackCurve.WrapPi(2 * Math.PI), 12);
        Assert.Equal(Math.PI, TrackCurve.WrapPi(-Math.PI), 12);
    }

    [Fact]
    public void Past_the_newest_knot_the_pose_runs_on_along_its_velocity_and_a_whole_track_is_read_in_three_parts()
    {
        var knots = new[] { Knot(0, 0, 0, heading: 0, speed: 10), Knot(1, 10, 0, heading: 0, speed: 10) };

        Assert.True(TrackCurve.TryAt(knots, -1, out var before));
        Assert.Equal(0.0, before.X);
        Assert.True(TrackCurve.TryAt(knots, 0.5, out var inside));
        Assert.Equal(5.0, inside.X, 9);
        Assert.True(TrackCurve.TryAt(knots, 1.5, out var after));
        Assert.Equal(15.0, after.X, 9);
        Assert.False(TrackCurve.TryAt([], 0, out _));
    }

    // ---- the knot policy ------------------------------------------------------------------------------------------------------

    /// <summary>Feeds a path (a function of time) at <paramref name="hz"/> for <paramref name="seconds"/> and returns the knots and every pose.</summary>
    private static (List<TrackKnot> Knots, List<TrackKnot> Poses) Run(Func<double, (double X, double Y, double Z, double H)> path, double seconds, double hz = 20, bool flat = false,
        KnotPolicy? policy = null)
    {
        var builder = new TrackBuilder(flat, policy);
        var knots = new List<TrackKnot>();
        var poses = new List<TrackKnot>();
        for (double t = 0; t <= seconds + 1e-9; t += 1.0 / hz)
        {
            var (x, y, z, h) = path(t);
            builder.Add(t, x, y, z, h, knots);
            poses.Add(new TrackKnot(t, x, y, z, h, 0, 0));
        }

        return (knots, poses);
    }

    /// <summary>The invariant: at every pose time up to the newest knot a client is within the tolerance.</summary>
    private static void AssertWithinTolerance(List<TrackKnot> knots, List<TrackKnot> poses, KnotPolicy? policy = null)
    {
        var p = policy ?? KnotPolicy.Default;
        double newest = knots[^1].Time;
        foreach (var pose in poses.Where(s => s.Time <= newest))
        {
            Assert.True(TrackCurve.TryAt(knots, pose.Time, out var shown));
            double error = Math.Sqrt(Math.Pow(shown.X - pose.X, 2) + Math.Pow(shown.Y - pose.Y, 2) + Math.Pow(shown.Z - pose.Z, 2));
            Assert.True(error <= p.PositionToleranceM + 1e-9, $"t={pose.Time:F2}: off by {error:F3} m");
            Assert.True(Math.Abs(TrackCurve.WrapPi(shown.HeadingRad - pose.HeadingRad)) <= p.HeadingToleranceRad + 1e-9, $"t={pose.Time:F2}: heading off");
        }
    }

    [Fact]
    public void The_first_pose_is_a_knot_and_a_straight_road_makes_about_one_knot_a_second()
    {
        var (knots, poses) = Run(t => (100 + 14 * t, 20, 0, 0), seconds: 30);

        Assert.Equal(0.0, knots[0].Time);   // at the spawn
        Assert.InRange(knots.Count, 28, 36);   // ~1 per second (plus the start-up while the speed is not known yet)
        Assert.All(knots.Zip(knots.Skip(1)), p => Assert.True(p.Second.Time - p.First.Time <= 1.0 + 0.05 + 1e-9, "never more than a second (and a pose period) apart"));
        AssertWithinTolerance(knots, poses);
        Assert.Equal(14.0, knots[^1].Speed, 6);   // the speed comes from the poses
    }

    [Fact]
    public void A_turn_adds_knots_and_the_tolerance_holds_all_the_way()
    {
        // a sharp 90 degree left turn of radius 6 m at 10 m/s (a junction), then straight again
        const double R = 6, V = 10;
        double turnTime = Math.PI / 2 * R / V;
        (double, double, double, double) Path(double t)
        {
            if (t < 5)
            {
                return (V * t, 0, 0, 0);
            }

            if (t < 5 + turnTime)
            {
                double a = (t - 5) * V / R;   // arc angle
                return (V * 5 + R * Math.Sin(a), R * (1 - Math.Cos(a)), 0, a);
            }

            double s = (t - 5 - turnTime) * V;
            return (V * 5 + R, R + s, 0, Math.PI / 2);
        }

        var (knots, poses) = Run(Path, seconds: 12);
        var (straightKnots, _) = Run(t => (V * t, 0, 0, 0), seconds: 12);

        Assert.True(knots.Count > straightKnots.Count + 1, $"the turn needs more knots ({knots.Count} vs {straightKnots.Count})");
        AssertWithinTolerance(knots, poses);
    }

    [Fact]
    public void A_looser_tolerance_makes_fewer_knots_and_the_looser_bound_holds()
    {
        var loose = new KnotPolicy(1.0, 10 * Deg, 1.0);
        (double, double, double, double) Circle(double t) => (30 * Math.Sin(t / 3), 30 * (1 - Math.Cos(t / 3)), 0, t / 3);   // radius 30 m, 10 m/s

        var (tight, tightPoses) = Run(Circle, 40);
        var (relaxed, relaxedPoses) = Run(Circle, 40, policy: loose);

        Assert.True(relaxed.Count < tight.Count, $"{relaxed.Count} vs {tight.Count}");
        AssertWithinTolerance(tight, tightPoses);
        AssertWithinTolerance(relaxed, relaxedPoses, loose);
    }

    [Fact]
    public void A_standing_mover_sends_one_knot_a_second_with_speed_zero_and_a_pedestrian_has_no_pitch()
    {
        var (knots, _) = Run(t => (5, 6, 0, 1.0), seconds: 10);

        Assert.InRange(knots.Count, 10, 12);
        Assert.All(knots, k => Assert.Equal((5.0, 6.0, 0.0), (k.X, k.Y, k.Speed)));

        // a walker up a slope: flat means pitch 0 whatever the climb, a car's pitch is the climb
        (double, double, double, double) Climb(double t) => (1.4 * t, 0, 0.3 * t, 0);
        var (walker, _) = Run(Climb, 10, flat: true);
        var (car, _) = Run(t => (14 * t, 0, 1.4 * t, 0), 10);   // 10 % grade
        Assert.All(walker, k => Assert.Equal(0.0, k.PitchRad));
        Assert.Equal(Math.Atan2(1.4, 14), car[^1].PitchRad, 4);
        Assert.True(car[^1].PitchRad > 0);
    }

    [Fact]
    public void A_reversing_car_has_a_negative_speed_and_the_pitch_that_keeps_its_velocity_on_the_climb()
    {
        // heading +x, moving backwards (-5 m/s) up a 10 % slope: the velocity is (-5, 0, +0.5), so speed -5 and pitch = -atan(0.1) (the nose points down the slope)
        var (knots, poses) = Run(t => (-5 * t, 0, 0.5 * t, 0), seconds: 10);

        Assert.Equal(-5.0 / Math.Cos(-Math.Atan2(0.5, 5)), knots[^1].Speed, 6);   // along the slope
        Assert.Equal(-Math.Atan2(0.5, 5), knots[^1].PitchRad, 4);
        Assert.Equal(0.5, knots[^1].VelocityZ, 4);
        AssertWithinTolerance(knots, poses);
    }

    [Fact]
    public void Random_smooth_paths_always_stay_within_the_tolerance_and_never_more_than_a_second_apart()
    {
        var random = new Random(20261120);
        for (int run = 0; run < 25; run++)
        {
            // a car that accelerates, brakes and steers at random rates, integrated at 50 Hz
            double x = random.NextDouble() * 1000, y = random.NextDouble() * 1000, h = random.NextDouble() * 6.28, v = 5 + random.NextDouble() * 10, yawRate = 0, accel = 0;
            var builder = new TrackBuilder();
            var knots = new List<TrackKnot>();
            var poses = new List<TrackKnot>();
            const double Dt = 0.02;
            for (int i = 0; i <= 50 * 60; i++)
            {
                double t = i * Dt;
                if (i % 100 == 0)
                {
                    yawRate = (random.NextDouble() - 0.5) * 0.8;
                    accel = (random.NextDouble() - 0.5) * 3;
                }

                v = Math.Clamp(v + accel * Dt, 0.5, 20);
                h += yawRate * Dt;
                x += v * Math.Cos(h) * Dt;
                y += v * Math.Sin(h) * Dt;
                builder.Add(t, x, y, 0, TrackCurve.WrapPi(h), knots);
                poses.Add(new TrackKnot(t, x, y, 0, TrackCurve.WrapPi(h), 0, 0));
            }

            AssertWithinTolerance(knots, poses);
            Assert.All(knots.Zip(knots.Skip(1)), p => Assert.True(p.Second.Time - p.First.Time <= 1.0 + Dt + 1e-9));
            Assert.InRange(knots.Count, 55, 400);   // never one a frame, never starved
        }
    }

    [Fact]
    public void Poses_that_are_not_later_than_the_newest_knot_are_ignored()
    {
        var builder = new TrackBuilder();
        var knots = new List<TrackKnot>();
        builder.Add(5.0, 0, 0, 0, 0, knots);
        builder.Add(5.0, 1, 0, 0, 0, knots);
        builder.Add(4.0, 2, 0, 0, 0, knots);

        Assert.Single(knots);
        Assert.Equal(1, builder.Sent);
    }
}
