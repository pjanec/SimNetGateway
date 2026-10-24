using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Common.Tests;

public class GeoTests
{
    private static void AssertVec(Vec3 expected, Vec3 actual, double tolerance)
    {
        Assert.True(Math.Abs(expected.X - actual.X) <= tolerance, $"X: expected {expected.X}, actual {actual.X}");
        Assert.True(Math.Abs(expected.Y - actual.Y) <= tolerance, $"Y: expected {expected.Y}, actual {actual.Y}");
        Assert.True(Math.Abs(expected.Z - actual.Z) <= tolerance, $"Z: expected {expected.Z}, actual {actual.Z}");
    }

    [Fact]
    public void Constants_are_the_wgs84_values()
    {
        Assert.Equal(6378137.0, Wgs84.A);
        Assert.Equal(6356752.314245179, Wgs84.B, 6);
        Assert.Equal(0.0066943799901413165, Wgs84.E2, 15);
        Assert.Equal(0.0067394967422764341, Wgs84.Ep2, 15);
    }

    [Fact]
    public void Equator_at_the_prime_meridian_is_on_the_x_axis()
    {
        AssertVec(new Vec3(6378137.0, 0, 0), Wgs84.ToEcef(new Geodetic(0, 0, 0)), 1e-9);
    }

    [Fact]
    public void The_north_pole_is_on_the_z_axis_at_the_semi_minor_axis()
    {
        var p = Wgs84.ToEcef(Geodetic.FromDegrees(90, 0, 0));
        AssertVec(new Vec3(0, 0, 6356752.314245179), p, 1e-6);
    }

    [Theory]
    [InlineData(50.0755, 14.4378, 200.0, 3972024.045798417, 1022636.1442032334, 4868336.209202541)]
    [InlineData(-33.8688, 151.2093, 58.0, -4646093.477288303, 2553229.5358170713, -3534404.710910369)]
    public void Known_points_match_independently_computed_values(double lat, double lon, double alt, double x, double y, double z)
    {
        // Reference values computed with an independent implementation of the standard formulas (Python).
        var ecef = Wgs84.ToEcef(Geodetic.FromDegrees(lat, lon, alt));
        AssertVec(new Vec3(x, y, z), ecef, 1e-6);
        var back = Wgs84.ToGeodetic(new Vec3(x, y, z));
        Assert.Equal(lat, back.LatitudeDegrees, 9);
        Assert.Equal(lon, back.LongitudeDegrees, 9);
        Assert.Equal(alt, back.Altitude, 5);
    }

    [Fact]
    public void Round_trip_over_the_globe_is_accurate()
    {
        double[] altitudes = { -1000, 0, 1, 10000, 100000 };
        double worstMetres = 0, worstRadians = 0;
        for (int latDeg = -90; latDeg <= 90; latDeg += 5)
        {
            for (int lonDeg = -180; lonDeg <= 180; lonDeg += 15)
            {
                foreach (double alt in altitudes)
                {
                    var g = Geodetic.FromDegrees(latDeg, lonDeg, alt);
                    var back = Wgs84.ToGeodetic(Wgs84.ToEcef(g));
                    worstMetres = Math.Max(worstMetres, Math.Abs(back.Altitude - alt));
                    worstRadians = Math.Max(worstRadians, Math.Abs(back.Latitude - g.Latitude));
                    if (Math.Abs(latDeg) < 90)
                    {
                        double dLon = Math.Abs(Angles.NormalizePi(back.Longitude - g.Longitude));
                        worstRadians = Math.Max(worstRadians, dLon);
                        Assert.True(back.Longitude > -Math.PI && back.Longitude <= Math.PI);
                    }

                    // Also compare in metres through the ECEF position.
                    var again = Wgs84.ToEcef(back);
                    var original = Wgs84.ToEcef(g);
                    Assert.True((again - original).Length < 1e-6, $"{latDeg},{lonDeg},{alt}");
                }
            }
        }

        Assert.True(worstMetres < 1e-6, $"altitude error {worstMetres}");
        Assert.True(worstRadians < 1e-11, $"angle error {worstRadians}");
    }

    [Fact]
    public void Round_trip_of_random_points_is_accurate()
    {
        var rng = new Random(12345);
        for (int i = 0; i < 20000; i++)
        {
            var g = new Geodetic(
                Math.Asin(rng.NextDouble() * 2 - 1),
                (rng.NextDouble() * 2 - 1) * Math.PI,
                -1000 + rng.NextDouble() * 101000);
            var back = Wgs84.ToGeodetic(Wgs84.ToEcef(g));
            Assert.True(Math.Abs(back.Altitude - g.Altitude) < 1e-6);
            Assert.True(Math.Abs(back.Latitude - g.Latitude) < 1e-11);
            Assert.True(Math.Abs(Angles.NormalizePi(back.Longitude - g.Longitude)) < 1e-11);
        }
    }

    [Fact]
    public void The_polar_axis_gives_no_NaN()
    {
        var north = Wgs84.ToGeodetic(new Vec3(0, 0, Wgs84.B + 123));
        Assert.Equal(Math.PI / 2, north.Latitude, 12);
        Assert.Equal(0.0, north.Longitude);
        Assert.Equal(123.0, north.Altitude, 6);

        var south = Wgs84.ToGeodetic(new Vec3(0, 0, -(Wgs84.B - 500)));
        Assert.Equal(-Math.PI / 2, south.Latitude, 12);
        Assert.Equal(-500.0, south.Altitude, 6);
        Assert.False(double.IsNaN(south.Longitude));

        var centre = Wgs84.ToGeodetic(Vec3.Zero);
        Assert.False(double.IsNaN(centre.Latitude) || double.IsNaN(centre.Longitude) || double.IsNaN(centre.Altitude));
    }

    [Fact]
    public void Longitude_is_normalised_to_minus_pi_exclusive_pi_inclusive()
    {
        var east = Wgs84.ToGeodetic(Wgs84.ToEcef(Geodetic.FromDegrees(10, 180, 0)));
        var west = Wgs84.ToGeodetic(Wgs84.ToEcef(Geodetic.FromDegrees(10, -180, 0)));
        Assert.Equal(Math.PI, east.Longitude);
        Assert.Equal(Math.PI, west.Longitude);

        var justWest = Wgs84.ToGeodetic(new Vec3(-1000, -1e-9, 0));
        Assert.True(justWest.Longitude < -3.14 && justWest.Longitude > -Math.PI - 1e-12);

        var beyond = Wgs84.ToGeodetic(Wgs84.ToEcef(Geodetic.FromDegrees(0, 190, 0)));
        Assert.Equal(-170.0, beyond.LongitudeDegrees, 9);
    }

    [Fact]
    public void Degree_helpers_and_normalisation()
    {
        Assert.Equal(Math.PI, Angles.ToRadians(180), 15);
        Assert.Equal(90.0, Angles.ToDegrees(Math.PI / 2), 12);
        Assert.Equal(Math.PI / 2, Angles.NormalizeTwoPi(-3 * Math.PI / 2), 12);
        Assert.Equal(0.0, Angles.NormalizeTwoPi(2 * Math.PI));
        Assert.Equal(0.0, Angles.NormalizeTwoPi(-1e-20));
        Assert.Equal(Math.PI, Angles.NormalizePi(-Math.PI));
        Assert.Equal(Math.PI, Angles.NormalizePi(3 * Math.PI));
        Assert.Equal(-Math.PI / 2, Angles.NormalizePi(3 * Math.PI / 2), 12);
        var g = Geodetic.FromDegrees(12.5, -45.25, 7);
        Assert.Equal(12.5, g.LatitudeDegrees, 12);
        Assert.Equal(-45.25, g.LongitudeDegrees, 12);
    }

    public static IEnumerable<object[]> Points() => new[]
    {
        new object[] { 0.0, 0.0 },
        new object[] { 50.0, 14.0 },
        new object[] { -33.0, 151.0 },
        new object[] { 89.9, 30.0 },
        new object[] { -89.9, -120.0 },
        new object[] { 10.0, 179.9 },
        new object[] { 90.0, 0.0 },
    };

    [Theory]
    [MemberData(nameof(Points))]
    public void NorthEastDown_axes_are_orthonormal_and_right_handed(double lat, double lon)
    {
        var at = Geodetic.FromDegrees(lat, lon, 100);
        var m = NorthEastDown.RotationToEcef(at);
        var n = m.Column(0);
        var e = m.Column(1);
        var d = m.Column(2);
        Assert.Equal(1.0, n.Length, 12);
        Assert.Equal(1.0, e.Length, 12);
        Assert.Equal(1.0, d.Length, 12);
        Assert.Equal(0.0, n.X * e.X + n.Y * e.Y + n.Z * e.Z, 12);
        Assert.Equal(0.0, n.X * d.X + n.Y * d.Y + n.Z * d.Z, 12);
        var cross = new Vec3(n.Y * e.Z - n.Z * e.Y, n.Z * e.X - n.X * e.Z, n.X * e.Y - n.Y * e.X);
        AssertVec(d, cross, 1e-12);
        var product = m * m.Transpose();
        Assert.Equal(1.0, product.M00, 12);
        Assert.Equal(1.0, product.M11, 12);
        Assert.Equal(1.0, product.M22, 12);
        Assert.Equal(0.0, product.M01, 12);
    }

    [Fact]
    public void NorthEastDown_directions_at_the_origin_point_the_expected_way()
    {
        var at = new Geodetic(0, 0, 0);
        AssertVec(new Vec3(0, 0, 1), NorthEastDown.ToEcef(new Vec3(1, 0, 0), at), 1e-12);  // north
        AssertVec(new Vec3(0, 1, 0), NorthEastDown.ToEcef(new Vec3(0, 1, 0), at), 1e-12);  // east
        AssertVec(new Vec3(-1, 0, 0), NorthEastDown.ToEcef(new Vec3(0, 0, 1), at), 1e-12); // down
    }

    [Theory]
    [MemberData(nameof(Points))]
    public void NorthEastDown_vector_round_trip(double lat, double lon)
    {
        var at = Geodetic.FromDegrees(lat, lon, 1000);
        var v = new Vec3(12.5, -3.25, 77.0);
        var ecef = NorthEastDown.ToEcef(v, at);
        Assert.Equal(v.Length, ecef.Length, 10);
        AssertVec(v, NorthEastDown.FromEcef(ecef, at), 1e-10);
    }

    [Fact]
    public void A_level_aircraft_heading_north_at_the_origin_has_theta_minus_90_degrees()
    {
        // Nose along ECEF +z (up the polar axis): a gimbal-lock pose, reported with phi = 0.
        var euler = Orientation.ToGeocentric(0, 0, 0, new Geodetic(0, 0, 0));
        AssertVec(new Vec3(0, -Math.PI / 2, 0), euler, 1e-9);
    }

    [Fact]
    public void A_level_aircraft_heading_east_at_the_origin_has_psi_90_and_phi_minus_90_degrees()
    {
        var euler = Orientation.ToGeocentric(Math.PI / 2, 0, 0, new Geodetic(0, 0, 0));
        AssertVec(new Vec3(Math.PI / 2, 0, -Math.PI / 2), euler, 1e-12);
    }

    [Fact]
    public void A_level_aircraft_heading_east_at_the_north_pole_region_matches_the_matrix()
    {
        // Body x must point east: ECEF +y at lon 0, whatever the latitude.
        var at = Geodetic.FromDegrees(60, 0, 0);
        var euler = Orientation.ToGeocentric(Math.PI / 2, 0, 0, at);
        var bodyX = Orientation.ToMatrix(euler).Column(0);
        AssertVec(new Vec3(0, 1, 0), bodyX, 1e-12);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, 0.0, 0.0)]
    [InlineData(45.0, 10.0, 20.0, 30.0, 40.0)]
    [InlineData(-33.0, 151.0, 200.0, -10.0, 170.0)]
    [InlineData(89.9, 30.0, 350.0, 5.0, -20.0)]
    [InlineData(-89.9, -120.0, 90.0, 60.0, 45.0)]
    [InlineData(10.0, 179.9, 270.0, -80.0, -170.0)]
    [InlineData(90.0, 0.0, 123.0, 12.0, -34.0)]
    public void Orientation_round_trips_local_to_geocentric_and_back(double latDeg, double lonDeg, double headingDeg, double pitchDeg, double rollDeg)
    {
        var at = Geodetic.FromDegrees(latDeg, lonDeg, 500);
        double h = Angles.ToRadians(headingDeg), p = Angles.ToRadians(pitchDeg), r = Angles.ToRadians(rollDeg);
        var geo = Orientation.ToGeocentric(h, p, r, at);
        Assert.InRange(geo.X, -Math.PI, Math.PI);
        Assert.InRange(geo.Y, -Math.PI / 2, Math.PI / 2);
        Assert.InRange(geo.Z, -Math.PI, Math.PI);

        var local = Orientation.ToLocal(geo, at);
        Assert.InRange(local.Heading, 0, 2 * Math.PI);
        Assert.True(Math.Abs(Angles.NormalizePi(local.Heading - h)) < 1e-9, $"heading {local.Heading} vs {h}");
        Assert.True(Math.Abs(local.Pitch - p) < 1e-9, $"pitch {local.Pitch} vs {p}");
        Assert.True(Math.Abs(Angles.NormalizePi(local.Roll - r)) < 1e-9, $"roll {local.Roll} vs {r}");
    }

    [Fact]
    public void Euler_matrix_round_trip_survives_gimbal_lock()
    {
        foreach (double theta in new[] { Math.PI / 2, -Math.PI / 2 })
        {
            var m = Orientation.ToMatrix(0.7, theta, -0.4);
            var back = Orientation.ToMatrix(Orientation.FromMatrix(m));
            AssertVec(m.Column(0), back.Column(0), 1e-12);
            AssertVec(m.Column(1), back.Column(1), 1e-12);
            AssertVec(m.Column(2), back.Column(2), 1e-12);
        }
    }

    [Theory]
    [MemberData(nameof(Points))]
    public void Velocity_round_trips(double lat, double lon)
    {
        var at = Geodetic.FromDegrees(lat, lon, 3000);
        var ecef = Velocity.ToEcef(250.0, Angles.ToRadians(300), Angles.ToRadians(-12), at);
        Assert.Equal(250.0, ecef.Length, 10);
        var (speed, heading, pitch) = Velocity.ToLocal(ecef, at);
        Assert.Equal(250.0, speed, 9);
        Assert.Equal(Angles.ToRadians(300), heading, 10);
        Assert.Equal(Angles.ToRadians(-12), pitch, 10);
    }

    [Fact]
    public void Velocity_cases()
    {
        var at = new Geodetic(0, 0, 0);
        AssertVec(new Vec3(0, 0, 10), Velocity.ToEcef(10, 0, 0, at), 1e-12);                  // north
        AssertVec(new Vec3(0, 10, 0), Velocity.ToEcef(10, Math.PI / 2, 0, at), 1e-12);        // east
        AssertVec(new Vec3(10, 0, 0), Velocity.ToEcef(10, 0, Math.PI / 2, at), 1e-9);         // straight up
        var (s, h, p) = Velocity.ToLocal(Vec3.Zero, at);
        Assert.Equal((0.0, 0.0, 0.0), (s, h, p));
        var up = Velocity.ToLocal(new Vec3(5, 0, 0), at);
        Assert.Equal(5.0, up.Speed, 12);
        Assert.Equal(Math.PI / 2, up.Pitch, 12);
    }

    [Fact]
    public void Matrix_basics()
    {
        var rz = Matrix3.RotationZ(Math.PI / 2);
        AssertVec(new Vec3(0, 1, 0), rz * new Vec3(1, 0, 0), 1e-12);
        var composed = Matrix3.RotationX(0.3) * Matrix3.RotationY(0.4);
        AssertVec(Matrix3.RotationX(0.3) * (Matrix3.RotationY(0.4) * new Vec3(1, 2, 3)), composed * new Vec3(1, 2, 3), 1e-12);
        var skew = Matrix3.Skew(new Vec3(1, 2, 3));
        AssertVec(new Vec3(-1, 2, -1), skew * new Vec3(1, 1, 1), 1e-12); // (1,2,3) x (1,1,1)
        var rot = Matrix3.FromRotationVector(new Vec3(0, 0, Math.PI / 2));
        AssertVec(new Vec3(0, 1, 0), rot * new Vec3(1, 0, 0), 1e-12);
        var tiny = Matrix3.FromRotationVector(new Vec3(0, 0, 1e-9));
        AssertVec(new Vec3(1, 1e-9, 0), tiny * new Vec3(1, 0, 0), 1e-15);
    }
}
