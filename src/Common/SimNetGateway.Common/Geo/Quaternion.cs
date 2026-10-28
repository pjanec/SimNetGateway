using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// A rotation quaternion (Hamilton convention, w first, double precision): <c>q = w + xi + yj + zk</c> rotating a vector
/// <c>v</c> as <c>q v q*</c>, which equals <c>ToMatrix() * v</c> (active rotation, same sense as <see cref="Matrix3"/>
/// rotation helpers). Conversions to and from the direction cosine matrix (DCM) and the ZYX Euler angles are consistent
/// with <see cref="Orientation"/> (psi about z, theta about y, phi about x; at gimbal lock phi = 0).
/// </summary>
public readonly record struct Quaternion(double W, double X, double Y, double Z)
{
    public static Quaternion Identity => new(1.0, 0.0, 0.0, 0.0);

    public double LengthSquared => W * W + X * X + Y * Y + Z * Z;

    public double Length => Math.Sqrt(LengthSquared);

    /// <summary>The vector part (x, y, z).</summary>
    public Vec3 Vector => new(X, Y, Z);

    /// <summary>Scaled to unit length. A zero quaternion has no direction and becomes the identity.</summary>
    public Quaternion Normalized()
    {
        double length = Length;
        if (length == 0.0 || double.IsNaN(length) || double.IsInfinity(length))
        {
            return Identity;
        }

        return new Quaternion(W / length, X / length, Y / length, Z / length);
    }

    public Quaternion Conjugate() => new(W, -X, -Y, -Z);

    /// <summary>The same rotation with <c>w &gt;= 0</c> (q and -q are one rotation).</summary>
    public Quaternion WithNonNegativeW() => W < 0.0 ? new Quaternion(-W, -X, -Y, -Z) : this;

    /// <summary>Hamilton product: <c>(a * b)</c> applies <c>b</c> first, then <c>a</c>.</summary>
    public static Quaternion operator *(Quaternion a, Quaternion b) => new(
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

    /// <summary>Rotates a vector by this (unit) quaternion.</summary>
    public Vec3 Rotate(Vec3 v) => ToMatrix() * v;

    /// <summary>The direction cosine matrix of this quaternion (normalised first).</summary>
    public Matrix3 ToMatrix()
    {
        var q = Normalized();
        double w = q.W, x = q.X, y = q.Y, z = q.Z;
        return new Matrix3(
            1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y),
            2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x),
            2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y));
    }

    /// <summary>The unit quaternion with <c>w &gt;= 0</c> of a rotation matrix (Shepperd's method, numerically stable).</summary>
    public static Quaternion FromMatrix(in Matrix3 c)
    {
        double trace = c.M00 + c.M11 + c.M22;
        double w, x, y, z;
        if (trace > 0.0)
        {
            double s = Math.Sqrt(trace + 1.0) * 2.0; // s = 4w
            w = 0.25 * s;
            x = (c.M21 - c.M12) / s;
            y = (c.M02 - c.M20) / s;
            z = (c.M10 - c.M01) / s;
        }
        else if (c.M00 > c.M11 && c.M00 > c.M22)
        {
            double s = Math.Sqrt(1.0 + c.M00 - c.M11 - c.M22) * 2.0; // s = 4x
            w = (c.M21 - c.M12) / s;
            x = 0.25 * s;
            y = (c.M01 + c.M10) / s;
            z = (c.M02 + c.M20) / s;
        }
        else if (c.M11 > c.M22)
        {
            double s = Math.Sqrt(1.0 + c.M11 - c.M00 - c.M22) * 2.0; // s = 4y
            w = (c.M02 - c.M20) / s;
            x = (c.M01 + c.M10) / s;
            y = 0.25 * s;
            z = (c.M12 + c.M21) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + c.M22 - c.M00 - c.M11) * 2.0; // s = 4z
            w = (c.M10 - c.M01) / s;
            x = (c.M02 + c.M20) / s;
            y = (c.M12 + c.M21) / s;
            z = 0.25 * s;
        }

        return new Quaternion(w, x, y, z).Normalized().WithNonNegativeW();
    }

    /// <summary>The quaternion of ZYX Euler angles (<c>Rz(psi) Ry(theta) Rx(phi)</c>, as <see cref="Orientation.ToMatrix(double,double,double)"/>), w &gt;= 0.</summary>
    public static Quaternion FromEuler(double psi, double theta, double phi)
    {
        double cp = Math.Cos(psi / 2), sp = Math.Sin(psi / 2);
        double ct = Math.Cos(theta / 2), st = Math.Sin(theta / 2);
        double cf = Math.Cos(phi / 2), sf = Math.Sin(phi / 2);
        return new Quaternion(
            cf * ct * cp + sf * st * sp,
            sf * ct * cp - cf * st * sp,
            cf * st * cp + sf * ct * sp,
            cf * ct * sp - sf * st * cp).Normalized().WithNonNegativeW();
    }

    /// <summary>ZYX Euler angles <c>Vec3(psi, theta, phi)</c>; theta in [-pi/2, pi/2], phi = 0 at gimbal lock (exactly <see cref="Orientation.FromMatrix"/>).</summary>
    public Vec3 ToEuler() => Orientation.FromMatrix(ToMatrix());

    /// <summary>The smallest angle (radians, [0, pi]) between two rotations.</summary>
    public static double AngleBetween(Quaternion a, Quaternion b)
    {
        var d = a.Normalized().Conjugate() * b.Normalized();
        return 2.0 * Math.Atan2(d.Vector.Length, Math.Abs(d.W));
    }
}
