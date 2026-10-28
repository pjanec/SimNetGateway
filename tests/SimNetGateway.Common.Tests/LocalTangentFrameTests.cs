using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Common.Tests;

public class LocalTangentFrameTests
{
    // An independent implementation: constants spelled out, geodetic -> ECEF and the ENU basis built directly from
    // the sines and cosines of latitude and longitude (the textbook east / north / up vectors).
    private const double A = 6378137.0;
    private const double E2 = 0.00669437999014133;

    private static Vec3 IndependentEcef(double lat, double lon, double alt)
    {
        double n = A / Math.Sqrt(1 - E2 * Math.Sin(lat) * Math.Sin(lat));
        return new Vec3(
            (n + alt) * Math.Cos(lat) * Math.Cos(lon),
            (n + alt) * Math.Cos(lat) * Math.Sin(lon),
            (n * (1 - E2) + alt) * Math.Sin(lat));
    }

    private static (Vec3 East, Vec3 North, Vec3 Up) IndependentBasis(double lat, double lon) => (
        new Vec3(-Math.Sin(lon), Math.Cos(lon), 0.0),
        new Vec3(-Math.Sin(lat) * Math.Cos(lon), -Math.Sin(lat) * Math.Sin(lon), Math.Cos(lat)),
        new Vec3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat)));

    private static Vec3 Combine(Vec3 east, Vec3 north, Vec3 up, Vec3 enu) =>
        new(east.X * enu.X + north.X * enu.Y + up.X * enu.Z,
            east.Y * enu.X + north.Y * enu.Y + up.Y * enu.Z,
            east.Z * enu.X + north.Z * enu.Y + up.Z * enu.Z);

    [Fact]
    public void Enu_to_ecef_and_back_is_the_identity_and_matches_the_independent_implementation()
    {
        var random = new Random(201);
        for (int i = 0; i < 2000; i++)
        {
            var origin = GeoRandom.Origin(random);
            var frame = new LocalTangentFrame(origin);
            var enu = GeoRandom.Vector(random, 100_000.0);

            var ecef = frame.PointToEcef(enu);
            GeoAssert.Near(enu, frame.PointFromEcef(ecef), 1e-6, "round trip");

            var (east, north, up) = IndependentBasis(origin.Latitude, origin.Longitude);
            var expected = IndependentEcef(origin.Latitude, origin.Longitude, origin.Altitude) + Combine(east, north, up, enu);
            GeoAssert.Near(expected, ecef, 1e-6, "independent ECEF");
            GeoAssert.Near(east, frame.East, 1e-12, "east");
            GeoAssert.Near(north, frame.North, 1e-12, "north");
            GeoAssert.Near(up, frame.Up, 1e-12, "up");
        }
    }

    [Fact]
    public void The_basis_is_orthonormal_right_handed_and_up_is_the_ellipsoid_normal()
    {
        var random = new Random(202);
        for (int i = 0; i < 1000; i++)
        {
            var origin = GeoRandom.Origin(random);
            var frame = new LocalTangentFrame(origin);

            GeoAssert.OrthonormalWithPositiveDeterminant(frame.RotationToEcef, 1e-12, "R");
            GeoAssert.Near(Matrix3.Identity, frame.RotationFromEcef * frame.RotationToEcef, 1e-12, "Rt R");
            // east x north = up (right-handed)
            var e = frame.East; var n = frame.North;
            GeoAssert.Near(new Vec3(e.Y * n.Z - e.Z * n.Y, e.Z * n.X - e.X * n.Z, e.X * n.Y - e.Y * n.X), frame.Up, 1e-12, "e x n");

            // The ellipsoid normal at a point on the surface is the gradient of x^2/a^2 + y^2/a^2 + z^2/b^2 there.
            var surface = Wgs84.ToEcef(new Geodetic(origin.Latitude, origin.Longitude, 0.0));
            double b2 = Wgs84.B * Wgs84.B;
            var gradient = new Vec3(surface.X / (A * A), surface.Y / (A * A), surface.Z / b2);
            gradient = gradient * (1.0 / gradient.Length);
            GeoAssert.Near(gradient, frame.Up, 1e-9, "up equals the ellipsoid normal");
        }
    }

    [Fact]
    public void Vectors_rotate_without_the_translation()
    {
        var random = new Random(203);
        for (int i = 0; i < 1000; i++)
        {
            var frame = new LocalTangentFrame(GeoRandom.Origin(random));
            var p = GeoRandom.Vector(random, 50_000.0);
            var v = GeoRandom.Vector(random, 300.0);

            var asVector = frame.VectorToEcef(v);
            GeoAssert.Near(frame.PointToEcef(p + v) - frame.PointToEcef(p), asVector, 1e-6, "difference of points");
            GeoAssert.Near(frame.RotationToEcef * v, asVector, 1e-12, "R v");
            GeoAssert.Near(v, frame.VectorFromEcef(asVector), 1e-9, "round trip");
            Assert.True(Math.Abs(asVector.Length - v.Length) <= 1e-9, "length preserved");
            GeoAssert.Near(Vec3.Zero, frame.VectorToEcef(Vec3.Zero), 0.0, "zero stays zero");
            GeoAssert.Near(frame.OriginEcef, frame.PointToEcef(Vec3.Zero), 0.0, "origin");
        }
    }

    [Fact]
    public void Rotations_transform_with_R_and_its_transpose()
    {
        var random = new Random(204);
        for (int i = 0; i < 1000; i++)
        {
            var frame = new LocalTangentFrame(GeoRandom.Origin(random));
            var q = GeoRandom.Rotation(random);

            var inEcef = frame.RotationToEcefFrame(q);
            GeoAssert.Near(frame.RotationToEcef * q.ToMatrix(), inEcef.ToMatrix(), 1e-9, "R M");
            GeoAssert.NearQuaternion(q.WithNonNegativeW(), frame.RotationFromEcefFrame(inEcef), 1e-9, "inverse");
            GeoAssert.Near(q.ToMatrix(), frame.RotationFromEcefFrame(frame.RotationToEcefFrame(q.ToMatrix())), 1e-9, "matrix round trip");
        }
    }

    [Fact]
    public void Known_origins()
    {
        // lat 0, lon 0: east = +y, north = +z, up = +x (the point is on the x axis).
        var frame = new LocalTangentFrame(new Geodetic(0, 0, 0));
        GeoAssert.Near(new Vec3(0, 1, 0), frame.East, 1e-15);
        GeoAssert.Near(new Vec3(0, 0, 1), frame.North, 1e-15);
        GeoAssert.Near(new Vec3(1, 0, 0), frame.Up, 1e-15);
        GeoAssert.Near(new Vec3(A, 0, 0), frame.OriginEcef, 1e-9);
        GeoAssert.Near(new Vec3(A + 10, 5, 7), frame.PointToEcef(new Vec3(5, 7, 10)), 1e-9, "5 east, 7 north, 10 up");

        // lat 0, lon 90 degrees: east = -x, north = +z, up = +y.
        var east90 = new LocalTangentFrame(Geodetic.FromDegrees(0, 90, 0));
        GeoAssert.Near(new Vec3(-1, 0, 0), east90.East, 1e-15);
        GeoAssert.Near(new Vec3(0, 1, 0), east90.Up, 1e-15);
    }
}
