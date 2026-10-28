using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Common.Tests;

/// <summary>Seeded random geometry shared by the frame and quaternion tests.</summary>
internal static class GeoRandom
{
    public static double Uniform(Random random, double min, double max) => min + (max - min) * random.NextDouble();

    /// <summary>Random ZYX Euler angles with theta at least <paramref name="marginDegrees"/> away from +-90 degrees.</summary>
    public static (double Psi, double Theta, double Phi) Euler(Random random, double marginDegrees = 1.5)
    {
        double limit = Angles.ToRadians(90.0 - marginDegrees);
        return (Uniform(random, -Math.PI, Math.PI), Uniform(random, -limit, limit), Uniform(random, -Math.PI, Math.PI));
    }

    /// <summary>A uniformly random rotation (any angle up to pi) as a unit quaternion with random sign.</summary>
    public static Quaternion Rotation(Random random)
    {
        // Marsaglia: normalised 4-vector of Gaussians is uniform on the sphere of rotations.
        double Gauss() => Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble());
        return new Quaternion(Gauss(), Gauss(), Gauss(), Gauss()).Normalized();
    }

    public static Geodetic Origin(Random random) =>
        Geodetic.FromDegrees(Uniform(random, -89.0, 89.0), Uniform(random, -180.0, 180.0), Uniform(random, -500.0, 5000.0));

    public static Vec3 Vector(Random random, double scale) =>
        new(Uniform(random, -scale, scale), Uniform(random, -scale, scale), Uniform(random, -scale, scale));
}

internal static class GeoAssert
{
    public static void Near(Vec3 expected, Vec3 actual, double tolerance, string? what = null)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance && Math.Abs(expected.Z - actual.Z) <= tolerance,
            $"{what}: expected {expected}, actual {actual}");
    }

    public static void Near(Matrix3 expected, Matrix3 actual, double tolerance, string? what = null)
    {
        for (int c = 0; c < 3; c++)
        {
            Near(expected.Column(c), actual.Column(c), tolerance, $"{what} column {c}");
        }
    }

    public static void NearQuaternion(Quaternion expected, Quaternion actual, double tolerance, string? what = null)
    {
        Assert.True(
            Math.Abs(expected.W - actual.W) <= tolerance && Math.Abs(expected.X - actual.X) <= tolerance
            && Math.Abs(expected.Y - actual.Y) <= tolerance && Math.Abs(expected.Z - actual.Z) <= tolerance,
            $"{what}: expected {expected}, actual {actual}");
    }

    /// <summary>Distance of two angles on the circle.</summary>
    public static double AngleDistance(double a, double b) => Math.Abs(Angles.NormalizePi(a - b));

    public static void OrthonormalWithPositiveDeterminant(Matrix3 m, double tolerance, string? what = null)
    {
        GeoAssert.Near(Matrix3.Identity, m * m.Transpose(), tolerance, what + " M*Mt");
        Assert.True(Math.Abs(m.Determinant() - 1.0) <= tolerance, $"{what}: determinant {m.Determinant()}");
    }
}

public class QuaternionTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void Euler_to_quaternion_to_euler_round_trips_away_from_gimbal_lock()
    {
        var random = new Random(101);
        for (int i = 0; i < 2000; i++)
        {
            var (psi, theta, phi) = GeoRandom.Euler(random);
            var q = Quaternion.FromEuler(psi, theta, phi);

            Assert.True(Math.Abs(q.Length - 1.0) <= 1e-12);
            Assert.True(q.W >= 0.0);
            var back = q.ToEuler();
            Assert.True(GeoAssert.AngleDistance(psi, back.X) <= Tolerance, $"psi {psi} vs {back.X}");
            Assert.True(Math.Abs(theta - back.Y) <= Tolerance, $"theta {theta} vs {back.Y}");
            Assert.True(GeoAssert.AngleDistance(phi, back.Z) <= Tolerance, $"phi {phi} vs {back.Z}");
        }
    }

    [Fact]
    public void Quaternion_and_matrix_conversions_agree_with_the_existing_orientation_helpers()
    {
        var random = new Random(102);
        for (int i = 0; i < 2000; i++)
        {
            var (psi, theta, phi) = GeoRandom.Euler(random);
            var dcm = Orientation.ToMatrix(psi, theta, phi);
            var q = Quaternion.FromEuler(psi, theta, phi);

            GeoAssert.Near(dcm, q.ToMatrix(), Tolerance, "FromEuler.ToMatrix vs Orientation.ToMatrix");
            GeoAssert.NearQuaternion(q, Quaternion.FromMatrix(dcm), Tolerance, "FromMatrix(Orientation.ToMatrix)");
            GeoAssert.Near(Orientation.FromMatrix(dcm), q.ToEuler(), Tolerance, "Euler angles of the same DCM");
        }
    }

    [Fact]
    public void Matrix_to_quaternion_to_matrix_round_trips_for_every_rotation_including_half_turns()
    {
        var random = new Random(103);
        for (int i = 0; i < 5000; i++)
        {
            var q = GeoRandom.Rotation(random);
            var dcm = q.ToMatrix();

            GeoAssert.OrthonormalWithPositiveDeterminant(dcm, Tolerance, "DCM");
            var back = Quaternion.FromMatrix(dcm);
            Assert.True(back.W >= 0.0);
            Assert.True(Math.Abs(back.Length - 1.0) <= 1e-12);
            GeoAssert.Near(dcm, back.ToMatrix(), Tolerance, "DCM round trip");
            GeoAssert.NearQuaternion(q.WithNonNegativeW(), back, Tolerance, "same rotation, w >= 0");
        }

        // The four branches of the conversion, at exact half turns about each axis and the identity.
        foreach (var m in new[]
        {
            Matrix3.Identity, Matrix3.RotationX(Math.PI), Matrix3.RotationY(Math.PI), Matrix3.RotationZ(Math.PI),
        })
        {
            GeoAssert.Near(m, Quaternion.FromMatrix(m).ToMatrix(), Tolerance, "half turn");
        }
    }

    [Fact]
    public void Gimbal_lock_gives_phi_zero_and_reproduces_the_same_dcm()
    {
        var random = new Random(104);
        foreach (double sign in new[] { 1.0, -1.0 })
        {
            for (int i = 0; i < 500; i++)
            {
                double psi = GeoRandom.Uniform(random, -Math.PI, Math.PI);
                double phi = GeoRandom.Uniform(random, -Math.PI, Math.PI);
                double theta = sign * Math.PI / 2.0;
                var q = Quaternion.FromEuler(psi, theta, phi);
                var dcm = q.ToMatrix();

                var euler = q.ToEuler();
                Assert.Equal(0.0, euler.Z); // phi = 0 at gimbal lock (Orientation convention)
                Assert.True(Math.Abs(Math.Abs(euler.Y) - Math.PI / 2.0) <= 1e-7, $"theta {euler.Y}");
                GeoAssert.Near(dcm, Orientation.ToMatrix(euler), 1e-7, "same DCM from the gimbal-lock Euler triple");
            }
        }
    }

    [Fact]
    public void Multiplication_conjugate_and_rotation_match_the_matrix_algebra()
    {
        var random = new Random(105);
        for (int i = 0; i < 2000; i++)
        {
            var a = GeoRandom.Rotation(random);
            var b = GeoRandom.Rotation(random);
            var v = GeoRandom.Vector(random, 100.0);

            GeoAssert.Near(a.ToMatrix() * b.ToMatrix(), (a * b).ToMatrix(), Tolerance, "(a*b) applies b first");
            GeoAssert.Near(a.ToMatrix() * v, a.Rotate(v), 1e-8, "Rotate");
            GeoAssert.Near(v, a.Conjugate().Rotate(a.Rotate(v)), 1e-8, "conjugate inverts");
            var identity = a * a.Conjugate();
            GeoAssert.NearQuaternion(Quaternion.Identity, identity, Tolerance, "q * q*");
            Assert.True(Quaternion.AngleBetween(a, a.Conjugate().Conjugate()) <= 1e-7);
        }
    }

    [Fact]
    public void A_quaternion_and_its_negative_are_one_rotation()
    {
        var random = new Random(106);
        for (int i = 0; i < 500; i++)
        {
            var q = GeoRandom.Rotation(random);
            var negative = new Quaternion(-q.W, -q.X, -q.Y, -q.Z);
            GeoAssert.Near(q.ToMatrix(), negative.ToMatrix(), 1e-12, "same DCM");
            GeoAssert.NearQuaternion(q.WithNonNegativeW(), negative.WithNonNegativeW(), 1e-12, "same representative");
            Assert.True(Quaternion.AngleBetween(q, negative) <= 1e-7);
        }
    }

    [Fact]
    public void Known_rotation_and_degenerate_inputs()
    {
        // 90 degrees about z (counter-clockwise seen from +z) turns x into y.
        var q = new Quaternion(Math.Cos(Math.PI / 4), 0, 0, Math.Sin(Math.PI / 4));
        GeoAssert.Near(new Vec3(0, 1, 0), q.Rotate(new Vec3(1, 0, 0)), 1e-12);
        GeoAssert.Near(Matrix3.RotationZ(Math.PI / 2), q.ToMatrix(), 1e-12);
        var euler = q.ToEuler();
        Assert.Equal(Math.PI / 2, euler.X, 12); // psi
        Assert.Equal(0.0, euler.Y, 12);
        Assert.Equal(0.0, euler.Z, 12);

        Assert.Equal(Quaternion.Identity, new Quaternion(0, 0, 0, 0).Normalized());
        Assert.Equal(Quaternion.Identity, new Quaternion(double.NaN, 0, 0, 0).Normalized());
        Assert.Equal(1.0, new Quaternion(3, 0, 4, 0).Normalized().Y * 5.0 / 4.0, 12);
    }
}
