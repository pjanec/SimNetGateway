using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// Orientation conversions. Local angles are aerospace ZYX body-to-NorthEastDown (heading from north clockwise, pitch up
/// positive, roll right positive). Geocentric angles are the DIS/RPR Euler angles psi/theta/phi, the same ZYX sequence
/// applied to the ECEF axes: <c>C_body2ecef = Rz(psi) Ry(theta) Rx(phi)</c>. Euler angles are returned as
/// <c>Vec3(psi, theta, phi)</c> with psi and phi in (-pi, pi] and theta in [-pi/2, pi/2].
/// </summary>
public static class Orientation
{
    // |cos(theta)| below this is treated as gimbal lock (phi := 0, psi takes the combined rotation).
    private const double GimbalEpsilon = 1e-10;

    /// <summary>Body-to-reference rotation matrix of ZYX Euler angles: <c>Rz(psi) Ry(theta) Rx(phi)</c>.</summary>
    public static Matrix3 ToMatrix(double psi, double theta, double phi) =>
        Matrix3.RotationZ(psi) * Matrix3.RotationY(theta) * Matrix3.RotationX(phi);

    /// <summary>Body-to-reference rotation matrix of Euler angles <c>Vec3(psi, theta, phi)</c>.</summary>
    public static Matrix3 ToMatrix(Vec3 psiThetaPhi) => ToMatrix(psiThetaPhi.X, psiThetaPhi.Y, psiThetaPhi.Z);

    /// <summary>ZYX Euler angles <c>Vec3(psi, theta, phi)</c> of a body-to-reference rotation matrix.</summary>
    public static Vec3 FromMatrix(in Matrix3 c)
    {
        double cosTheta = Math.Sqrt(c.M00 * c.M00 + c.M10 * c.M10);
        double theta = Math.Atan2(-c.M20, cosTheta);
        if (cosTheta < GimbalEpsilon)
        {
            // Gimbal lock: only psi -/+ phi is determined; report phi = 0.
            return new Vec3(Math.Atan2(-c.M01, c.M11), theta, 0.0);
        }

        return new Vec3(Math.Atan2(c.M10, c.M00), theta, Math.Atan2(c.M21, c.M22));
    }

    /// <summary>Local heading/pitch/roll (radians) at a point to geocentric Euler angles psi/theta/phi.</summary>
    public static Vec3 ToGeocentric(double heading, double pitch, double roll, Geodetic at) =>
        FromMatrix(NorthEastDown.RotationToEcef(at) * ToMatrix(heading, pitch, roll));

    /// <summary>Geocentric Euler angles psi/theta/phi at a point to local heading [0, 2pi), pitch and roll.</summary>
    public static (double Heading, double Pitch, double Roll) ToLocal(Vec3 psiThetaPhi, Geodetic at)
    {
        var local = FromMatrix(NorthEastDown.RotationToEcef(at).Transpose() * ToMatrix(psiThetaPhi));
        return (Angles.NormalizeTwoPi(local.X), local.Y, local.Z);
    }
}
