using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

public class ConversionTests
{
    private static readonly double[] Latitudes = { -90, -89.999, -60, -33.8688, -1e-7, 0, 1e-7, 30, 50.0755, 89.999, 90 };
    private static readonly double[] Longitudes = { -180, -179.9999, -90, -0.0001, 0, 14.4378, 90, 179.9999, 180 };
    private static readonly double[] Altitudes = { -400, 0, 1, 250, 12000, 100000 };
    private static readonly double[] Headings = { 0, 0.5, 45, 90, 135, 180, 225, 270, 315, 359.999, 360 };
    private static readonly double[] Pitches = { -80, -45, -10, 0, 10, 45, 80 };
    private static readonly double[] Rolls = { -180, -135, -90, -30, 0, 30, 90, 135, 180 };

    private static double AngleDiffDeg(double a, double b)
    {
        double d = (a - b) % 360.0;
        if (d > 180) d -= 360;
        if (d < -180) d += 360;
        return Math.Abs(d);
    }

    private static void AssertVec(Vec3 expected, Vec3 actual, double tolerance)
    {
        Assert.True(Math.Abs(expected.X - actual.X) <= tolerance, $"X: expected {expected.X}, actual {actual.X}");
        Assert.True(Math.Abs(expected.Y - actual.Y) <= tolerance, $"Y: expected {expected.Y}, actual {actual.Y}");
        Assert.True(Math.Abs(expected.Z - actual.Z) <= tolerance, $"Z: expected {expected.Z}, actual {actual.Z}");
    }

    // ---- independent reference values (computed by hand from the frame definitions) ------------------------------------

    [Fact]
    public void Known_position_matches_the_independent_reference()
    {
        var ecef = NedRprConversions.GeodeticToEcef(50.0755, 14.4378, 200.0);
        AssertVec(new Vec3(3972024.045798417, 1022636.1442032334, 4868336.209202541), ecef, 1e-6);
        NedRprConversions.EcefToGeodetic(new Vec3(-4646093.477288303, 2553229.5358170713, -3534404.710910369), out double lat, out double lon, out double alt);
        Assert.Equal(-33.8688, lat, 9);
        Assert.Equal(151.2093, lon, 9);
        Assert.Equal(58.0, alt, 5);
    }

    [Theory]
    [InlineData(0, 0, 10, 0, 0, 10)]      // north at (0,0): +Z
    [InlineData(90, 0, 10, 0, 10, 0)]     // east: +Y
    [InlineData(180, 0, 10, 0, 0, -10)]   // south: -Z
    [InlineData(270, 0, 10, 0, -10, 0)]   // west: -Y
    [InlineData(0, 90, 10, 10, 0, 0)]     // straight up: +X (outward)
    [InlineData(0, -90, 10, -10, 0, 0)]   // straight down: -X
    public void Velocity_directions_at_the_equator_and_prime_meridian(double heading, double pitch, double speed, double x, double y, double z)
    {
        NedRprConversions.GeoToPivot(0, 0, 0, heading, pitch, 0, speed, out _, out _, out var v);
        AssertVec(new Vec3(x, y, z), v, 1e-9);
    }

    [Fact]
    public void Velocity_with_heading_and_pitch_combined()
    {
        // north-east-down = (10 cos30, 0, -10 sin30) -> ECEF (x = -down, y = east, z = north)
        NedRprConversions.GeoToPivot(0, 0, 0, 0, 30, 0, 10, out _, out _, out var v);
        AssertVec(new Vec3(5, 0, 10 * Math.Cos(Math.PI / 6)), v, 1e-9);
    }

    [Fact]
    public void Velocity_east_at_longitude_90_points_to_minus_x()
    {
        NedRprConversions.GeoToPivot(0, 90, 0, 90, 0, 0, 10, out _, out _, out var v);
        AssertVec(new Vec3(-10, 0, 0), v, 1e-9);
    }

    [Fact]
    public void Orientation_heading_east_at_the_equator_is_psi_90_theta_0_phi_minus_90()
    {
        NedRprConversions.GeoToPivot(0, 0, 0, 90, 0, 0, 0, out _, out var o, out _);
        AssertVec(new Vec3(Math.PI / 2, 0, -Math.PI / 2), o, 1e-12);
    }

    [Fact]
    public void Orientation_heading_45_at_the_equator()
    {
        NedRprConversions.GeoToPivot(0, 0, 0, 45, 0, 0, 0, out _, out var o, out _);
        AssertVec(new Vec3(Math.PI / 2, -Math.PI / 4, -Math.PI / 2), o, 1e-12);
    }

    [Fact]
    public void Orientation_roll_and_pitch_have_their_signs_at_the_equator()
    {
        // heading east, then pitch up 20 deg: nose (east = +Y) tilts toward up (+X): rotation about the body y axis (south)
        NedRprConversions.GeoToPivot(0, 0, 0, 90, 20, 0, 0, out _, out var pitched, out _);
        var back = NedRprConversions.PivotToGeo(1, new Vec3(Wgs84Equator, 0, 0), pitched, Vec3.Zero);
        Assert.Equal(20, back.PitchDeg, 9);
        Assert.Equal(0, back.RollDeg, 9);

        NedRprConversions.GeoToPivot(0, 0, 0, 90, 0, 25, 0, out _, out var rolled, out _);
        var back2 = NedRprConversions.PivotToGeo(1, new Vec3(Wgs84Equator, 0, 0), rolled, Vec3.Zero);
        Assert.Equal(25, back2.RollDeg, 9);
        Assert.Equal(0, back2.PitchDeg, 9);
        Assert.NotEqual(pitched, rolled);
    }

    private const double Wgs84Equator = 6378137.0;

    [Fact]
    public void Pivot_to_geo_reads_a_hand_built_state_with_correct_axes()
    {
        // at (0,0): position on +X, ECEF velocity +Y (east) 7 m/s, orientation heading east (psi 90, phi -90)
        var g = NedRprConversions.PivotToGeo(42, new Vec3(Wgs84Equator + 100, 0, 0), new Vec3(Math.PI / 2, 0, -Math.PI / 2), new Vec3(0, 7, 0));
        Assert.Equal(42UL, g.Id);
        Assert.Equal(0, g.LatitudeDeg, 9);
        Assert.Equal(0, g.LongitudeDeg, 9);
        Assert.Equal(100, g.Altitude, 6);
        Assert.Equal(90, g.HeadingDeg, 9);
        Assert.Equal(0, g.PitchDeg, 9);
        Assert.Equal(0, g.RollDeg, 9);
        Assert.Equal(7, g.Speed, 12);

        // at lat 0 / lon 90 the same local state has a different ECEF orientation: east is -X there
        var g2 = NedRprConversions.PivotToGeo(1, new Vec3(0, Wgs84Equator, 0), Vec3.Zero, new Vec3(-3, 0, 0));
        Assert.Equal(90, g2.LongitudeDeg, 9);
        Assert.Equal(3, g2.Speed, 12);
    }

    // ---- round trips --------------------------------------------------------------------------------------------------

    [Fact]
    public void Ned_to_pivot_to_ned_round_trips_over_the_grid()
    {
        double worstMetres = 0, worstDeg = 0;
        int cases = 0;
        foreach (double lat in Latitudes)
        foreach (double lon in Longitudes)
        {
            bool pole = Math.Abs(lat) == 90;
            if (pole && lon != 0)
            {
                continue; // the longitude (and so the frame) on the polar axis is undefined: see PivotToGeo remarks
            }

            foreach (double alt in Altitudes)
            foreach (double heading in Headings)
            foreach (double pitch in Pitches)
            foreach (double roll in Rolls)
            {
                double speed = 1 + (cases % 7) * 12.5;
                cases++;
                var input = new NedGeoSpatial(9, lat, lon, alt, heading, pitch, roll, speed);
                NedRprConversions.GeoToPivot(input, out var p, out var o, out var v);
                var back = NedRprConversions.PivotToGeo(9, p, o, v);

                Assert.Equal(9UL, back.Id);
                Assert.InRange(back.HeadingDeg, 0.0, 360.0);
                Assert.True(back.HeadingDeg < 360.0, $"heading not normalised: {back.HeadingDeg}");

                var pBack = NedRprConversions.GeodeticToEcef(back.LatitudeDeg, back.LongitudeDeg, back.Altitude);
                worstMetres = Math.Max(worstMetres, (pBack - p).Length);
                worstMetres = Math.Max(worstMetres, Math.Abs(back.Altitude - alt));
                worstDeg = Math.Max(worstDeg, Math.Abs(back.LatitudeDeg - lat));
                if (!pole)
                {
                    worstDeg = Math.Max(worstDeg, AngleDiffDeg(back.LongitudeDeg, lon));
                }

                worstDeg = Math.Max(worstDeg, AngleDiffDeg(back.HeadingDeg, heading));
                worstDeg = Math.Max(worstDeg, Math.Abs(back.PitchDeg - pitch));
                worstDeg = Math.Max(worstDeg, AngleDiffDeg(back.RollDeg, roll));
                Assert.True(Math.Abs(back.Speed - speed) < 1e-9, $"speed {speed} -> {back.Speed}");
            }
        }

        Assert.True(cases > 50000);
        Assert.True(worstMetres < 1e-6, $"worst position error {worstMetres} m");
        Assert.True(worstDeg < 1e-9, $"worst angle error {worstDeg} deg");
    }

    [Fact]
    public void Round_trip_pitch_near_plus_minus_90_still_gives_back_pitch_heading_roll_when_not_gimbal_locked()
    {
        foreach (double pitch in new[] { -89.0, 89.0 })
        {
            NedRprConversions.GeoToPivot(40, 20, 100, 123, pitch, 17, 5, out var p, out var o, out var v);
            var back = NedRprConversions.PivotToGeo(1, p, o, v);
            Assert.True(Math.Abs(back.PitchDeg - pitch) < 1e-7);
            Assert.True(AngleDiffDeg(back.HeadingDeg, 123) < 1e-7);
            Assert.True(AngleDiffDeg(back.RollDeg, 17) < 1e-7);
        }
    }

    [Fact]
    public void Heading_is_normalised_into_0_360()
    {
        foreach (double input in new[] { -720, -361, -360, -90, -1e-12, 0, 360, 361, 450, 720, 1080.5 })
        {
            NedRprConversions.GeoToPivot(10, 10, 0, input, 0, 0, 5, out var p, out var o, out var v);
            var back = NedRprConversions.PivotToGeo(1, p, o, v);
            Assert.True(back.HeadingDeg >= 0 && back.HeadingDeg < 360, $"{input} -> {back.HeadingDeg}");
            Assert.True(AngleDiffDeg(back.HeadingDeg, input) < 1e-9, $"{input} -> {back.HeadingDeg}");
            double h = NedRprConversions.LocalHeadingDeg(p, o);
            Assert.True(h >= 0 && h < 360);
            Assert.Equal(back.HeadingDeg, h, 12);
        }

        // west is 270, never -90
        NedRprConversions.GeoToPivot(0, 0, 0, -90, 0, 0, 0, out var pw, out var ow, out _);
        Assert.Equal(270, NedRprConversions.PivotToGeo(1, pw, ow, Vec3.Zero).HeadingDeg, 9);
    }

    [Fact]
    public void Heading_just_below_north_never_comes_back_as_360()
    {
        // A heading a hair below 0 (= just below 360) must stay in [0, 360) after the radian to degree conversion.
        for (int k = 1; k <= 3000; k++)
        {
            double headingDeg = -k * 1e-15;
            NedRprConversions.GeoToPivot(0, 0, 0, headingDeg, 0, 0, 1, out var p, out var o, out var v);
            double local = NedRprConversions.LocalHeadingDeg(p, o);
            Assert.True(local >= 0 && local < 360.0, $"{headingDeg} -> {local}");
            var geo = NedRprConversions.PivotToGeo(1, p, o, v);
            Assert.True(geo.HeadingDeg >= 0 && geo.HeadingDeg < 360.0, $"{headingDeg} -> {geo.HeadingDeg}");
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(360, 0)]
    [InlineData(-90, 270)]
    [InlineData(-360, 0)]
    [InlineData(725, 5)]
    [InlineData(359.5, 359.5)]
    public void NormalizeHeadingDeg_maps_into_the_half_open_circle(double input, double expected)
    {
        Assert.Equal(expected, NedRprConversions.NormalizeHeadingDeg(input), 9);
    }

    [Fact]
    public void NormalizeHeadingDeg_never_returns_360_for_a_tiny_negative_input()
    {
        double r = NedRprConversions.NormalizeHeadingDeg(-1e-300);
        Assert.True(r >= 0 && r < 360.0);
    }

    [Fact]
    public void Speed_zero_gives_zero_speed_back_and_heading_comes_from_the_orientation()
    {
        // The velocity of a stopped entity has no direction; heading, pitch and roll come from the orientation alone.
        NedRprConversions.GeoToPivot(33, -70, 500, 211, 7, -12, 0, out var p, out var o, out var v);
        Assert.Equal(Vec3.Zero, v);
        var back = NedRprConversions.PivotToGeo(1, p, o, v);
        Assert.Equal(0.0, back.Speed);
        Assert.Equal(211, back.HeadingDeg, 9);
        Assert.Equal(7, back.PitchDeg, 9);
        Assert.Equal(-12, back.RollDeg, 9);
    }

    [Fact]
    public void Ned_to_pivot_to_rpr_value_structs_to_pivot_is_exact()
    {
        foreach (double lat in new[] { -80.0, 0, 51.5 })
        foreach (double heading in new[] { 0.0, 77.7, 300 })
        {
            NedRprConversions.GeoToPivot(lat, 12.3, 456, heading, 11, -22, 33, out var p, out var o, out var v);

            var wl = NedRprConversions.ToWorldLocation(p);
            var ro = NedRprConversions.ToRprOrientation(o);
            var rv = NedRprConversions.ToRprVector(v);

            Assert.Equal(p.X, wl.X);
            Assert.Equal(p.Y, wl.Y);
            Assert.Equal(p.Z, wl.Z);
            Assert.Equal(o.X, ro.Psi);
            Assert.Equal(o.Y, ro.Theta);
            Assert.Equal(o.Z, ro.Phi);
            Assert.Equal(v.X, rv.X);
            Assert.Equal(v.Y, rv.Y);
            Assert.Equal(v.Z, rv.Z);

            Assert.Equal(p, NedRprConversions.ToVec3(wl));
            Assert.Equal(o, NedRprConversions.ToVec3(ro));
            Assert.Equal(v, NedRprConversions.ToVec3(rv));
        }
    }

    [Fact]
    public void Value_struct_fields_are_not_permuted()
    {
        var v = new Vec3(1, 2, 3);
        Assert.Equal(new WorldLocation(1, 2, 3), NedRprConversions.ToWorldLocation(v));
        Assert.Equal(new RprOrientation(1, 2, 3), NedRprConversions.ToRprOrientation(v));
        Assert.Equal(new RprVector(1, 2, 3), NedRprConversions.ToRprVector(v));
        Assert.Equal(v, NedRprConversions.ToVec3(new WorldLocation(1, 2, 3)));
        Assert.Equal(v, NedRprConversions.ToVec3(new RprOrientation(1, 2, 3)));
        Assert.Equal(v, NedRprConversions.ToVec3(new RprVector(1, 2, 3)));
    }

    [Fact]
    public void Display_heading_matches_the_ned_heading()
    {
        NedRprConversions.GeoToPivot(48, 11, 0, 137, 3, 4, 1, out var p, out var o, out _);
        Assert.Equal(137, NedRprConversions.LocalHeadingDeg(p, o), 9);
    }

    // ---- bijective, range-checked mappings ----------------------------------------------------------------------------

    [Fact]
    public void Damage_mapping_is_a_bijection_on_0_to_3_and_rejects_everything_else()
    {
        var seen = new HashSet<RprDamageState>();
        for (int level = -1000; level <= 1000; level++)
        {
            bool ok = NedRprConversions.TryDamageToRpr(level, out var state);
            Assert.Equal(level is >= 0 and <= 3, ok);
            Assert.Equal(ok, NedRprConversions.IsValidDamage(level));
            if (ok)
            {
                Assert.Equal(level, (int)state);
                Assert.True(seen.Add(state));
                Assert.True(NedRprConversions.TryDamageFromRpr(state, out int back));
                Assert.Equal(level, back);
            }
            else
            {
                Assert.Equal(default, state);
            }
        }

        Assert.Equal(4, seen.Count);
        Assert.Equal(RprDamageState.NoDamage, Map(0));
        Assert.Equal(RprDamageState.SlightDamage, Map(1));
        Assert.Equal(RprDamageState.ModerateDamage, Map(2));
        Assert.Equal(RprDamageState.Destroyed, Map(3));
        Assert.False(NedRprConversions.TryDamageToRpr(int.MinValue, out _));
        Assert.False(NedRprConversions.TryDamageToRpr(int.MaxValue, out _));

        static RprDamageState Map(int l)
        {
            Assert.True(NedRprConversions.TryDamageToRpr(l, out var s));
            return s;
        }
    }

    [Fact]
    public void Rpr_damage_states_outside_the_range_are_rejected_for_every_byte()
    {
        for (int b = 0; b <= 255; b++)
        {
            bool ok = NedRprConversions.TryDamageFromRpr((RprDamageState)b, out int level);
            Assert.Equal(b <= 3, ok);
            Assert.Equal(ok ? b : 0, level);
        }
    }

    [Fact]
    public void Force_mapping_is_a_bijection_on_0_to_3_and_rejects_everything_else()
    {
        for (int pivot = -300; pivot <= 300; pivot++)
        {
            bool valid = pivot is >= 0 and <= 3;
            Assert.Equal(valid, NedRprConversions.TryForceFromPivot(pivot, out var ned));
            Assert.Equal(valid, NedRprConversions.TryForceToRpr(pivot, out var rpr));
            if (!valid)
            {
                continue;
            }

            Assert.Equal(pivot, (int)ned);
            Assert.Equal(pivot, (int)rpr);
            Assert.True(NedRprConversions.TryForceToPivot(ned, out int p1));
            Assert.True(NedRprConversions.TryForceFromRpr(rpr, out int p2));
            Assert.Equal(pivot, p1);
            Assert.Equal(pivot, p2);
        }

        for (int b = 0; b <= 255; b++)
        {
            Assert.Equal(b <= 3, NedRprConversions.TryForceToPivot((NedForce)b, out int p));
            Assert.Equal(b <= 3 ? b : 0, p);
            Assert.Equal(b <= 3, NedRprConversions.TryForceFromRpr((RprForceId)b, out int q));
            Assert.Equal(b <= 3 ? b : 0, q);
        }

        // names, not only numbers
        Assert.True(NedRprConversions.TryForceFromPivot(2, out var opposing));
        Assert.Equal(NedForce.Opposing, opposing);
        Assert.True(NedRprConversions.TryForceToRpr(2, out var rprOpposing));
        Assert.Equal(RprForceId.Opposing, rprOpposing);
        Assert.True(NedRprConversions.TryForceToRpr(1, out var friendly));
        Assert.Equal(RprForceId.Friendly, friendly);
        Assert.True(NedRprConversions.TryForceToRpr(3, out var neutral));
        Assert.Equal(RprForceId.Neutral, neutral);
    }

    [Fact]
    public void Detonation_result_mapping_is_a_bijection_between_the_pivot_0_to_3_and_the_four_FOM_codes_and_rejects_everything_else()
    {
        // pivot -> FOM numbering (R-105): Other 0, EntityImpact 1, GroundImpact 3, Detonation 5
        int[] fom = [0, 1, 3, 5];
        for (int pivot = -300; pivot <= 300; pivot++)
        {
            bool valid = pivot is >= 0 and <= 3;
            Assert.Equal(valid, NedRprConversions.IsValidDetonationResult(pivot));
            Assert.Equal(valid, NedRprConversions.TryResultFromPivot(pivot, out var ned));
            Assert.Equal(valid, NedRprConversions.TryResultToRpr(pivot, out var rpr));
            if (!valid)
            {
                continue;
            }

            Assert.Equal(pivot, (int)ned);
            Assert.Equal(fom[pivot], (int)rpr);
            Assert.True(NedRprConversions.TryResultToPivot(ned, out int p1));
            Assert.True(NedRprConversions.TryResultFromRpr(rpr, out int p2));
            Assert.Equal(pivot, p1);
            Assert.Equal(pivot, p2);
        }

        for (int b = 0; b <= 255; b++)
        {
            Assert.Equal(b <= 3, NedRprConversions.TryResultToPivot((NedDetonationResult)b, out int p));
            Assert.Equal(b <= 3 ? b : 0, p);
            bool fromRpr = NedRprConversions.TryResultFromRpr((RprDetonationResult)b, out int q);
            Assert.Equal(b is 0 or 1 or 3 or 5, fromRpr);
            Assert.Equal(b switch { 0 => 0, 1 => 1, 3 => 2, 5 => 3, _ => 0 }, q);
        }

        Assert.True(NedRprConversions.TryResultToRpr(2, out var ground));
        Assert.Equal(RprDetonationResult.GroundImpact, ground);
        Assert.Equal(3, (int)ground);
        Assert.True(NedRprConversions.TryResultToRpr(3, out var detonation));
        Assert.Equal(RprDetonationResult.Detonation, detonation);
        Assert.Equal(5, (int)detonation);
        Assert.True(NedRprConversions.TryResultFromPivot(3, out var det));
        Assert.Equal(NedDetonationResult.Detonation, det);
    }

    // ---- DIS ids ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Entity_ids_are_valid_exactly_for_1_to_65534()
    {
        for (int n = -5; n <= 70000; n++)
        {
            bool valid = n >= 1 && n <= 65534;
            Assert.Equal(valid, NedRprConversions.IsValidEntityNumber(n));
            Assert.Equal(valid, NedRprConversions.TryMakeEntityId(1, 3001, n, out var id));
            if (valid)
            {
                Assert.Equal(new DisEntityId(1, 3001, (ushort)n), id);
                Assert.Equal(n, NedRprConversions.EntityNumber(id));
            }
            else
            {
                Assert.Equal(default, id);
            }
        }

        Assert.False(NedRprConversions.TryMakeEntityId(1, 1, int.MinValue, out _));
        Assert.False(NedRprConversions.TryMakeEntityId(1, 1, int.MaxValue, out _));
        Assert.True(NedRprConversions.TryMakeEntityId(7, 9, 5, out var x));
        Assert.Equal((ushort)7, x.Site);
        Assert.Equal((ushort)9, x.Application);
        Assert.Equal((ushort)5, x.Entity);
    }
}
