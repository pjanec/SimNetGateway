using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// A 3x3 matrix, row-major (<c>Mrc</c> = row r, column c). Minimal and allocation-free: just what the frame and
/// orientation maths need. Rotation helpers are active rotations (they rotate vectors counter-clockwise about the axis).
/// </summary>
public readonly struct Matrix3
{
    public readonly double M00, M01, M02;
    public readonly double M10, M11, M12;
    public readonly double M20, M21, M22;

    public Matrix3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
    {
        M00 = m00; M01 = m01; M02 = m02;
        M10 = m10; M11 = m11; M12 = m12;
        M20 = m20; M21 = m21; M22 = m22;
    }

    public static Matrix3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>The matrix whose columns are the three vectors.</summary>
    public static Matrix3 FromColumns(Vec3 c0, Vec3 c1, Vec3 c2) =>
        new(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);

    /// <summary>The skew-symmetric matrix K with <c>K * v = w x v</c>.</summary>
    public static Matrix3 Skew(Vec3 w) => new(0, -w.Z, w.Y, w.Z, 0, -w.X, -w.Y, w.X, 0);

    public static Matrix3 RotationX(double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new(1, 0, 0, 0, c, -s, 0, s, c);
    }

    public static Matrix3 RotationY(double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new(c, 0, s, 0, 1, 0, -s, 0, c);
    }

    public static Matrix3 RotationZ(double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new(c, -s, 0, s, c, 0, 0, 0, 1);
    }

    /// <summary>Rotation by the rotation vector (axis * angle), exact (Rodrigues' formula).</summary>
    public static Matrix3 FromRotationVector(Vec3 rotation)
    {
        double angle = rotation.Length;
        var k = Skew(rotation);
        double a, b;
        if (angle < 1e-4)
        {
            // Series of sin(x)/x and (1 - cos x)/x^2; the error is below 1e-20 for x < 1e-4.
            double x2 = angle * angle;
            a = 1.0 - x2 / 6.0;
            b = 0.5 - x2 / 24.0;
        }
        else
        {
            a = Math.Sin(angle) / angle;
            b = (1.0 - Math.Cos(angle)) / (angle * angle);
        }

        return Identity + k * a + (k * k) * b;
    }

    /// <summary>The determinant (+1 for a proper rotation).</summary>
    public double Determinant() =>
        M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

    public Matrix3 Transpose() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

    public Vec3 Column(int index) => index switch
    {
        0 => new Vec3(M00, M10, M20),
        1 => new Vec3(M01, M11, M21),
        2 => new Vec3(M02, M12, M22),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static Vec3 operator *(Matrix3 m, Vec3 v) => new(
        m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
        m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
        m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);

    public static Matrix3 operator *(Matrix3 a, Matrix3 b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
        a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
        a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
        a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
        a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
        a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
        a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

    public static Matrix3 operator *(Matrix3 m, double s) => new(
        m.M00 * s, m.M01 * s, m.M02 * s, m.M10 * s, m.M11 * s, m.M12 * s, m.M20 * s, m.M21 * s, m.M22 * s);

    public static Matrix3 operator +(Matrix3 a, Matrix3 b) => new(
        a.M00 + b.M00, a.M01 + b.M01, a.M02 + b.M02,
        a.M10 + b.M10, a.M11 + b.M11, a.M12 + b.M12,
        a.M20 + b.M20, a.M21 + b.M21, a.M22 + b.M22);

    public override string ToString() =>
        FormattableString.Invariant($"[[{M00}, {M01}, {M02}], [{M10}, {M11}, {M12}], [{M20}, {M21}, {M22}]]");
}
