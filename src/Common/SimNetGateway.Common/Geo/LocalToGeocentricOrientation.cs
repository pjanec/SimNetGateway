using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// The composite orientation conversions of DD-006 section 5. A local orientation is a body-to-ENU rotation of a
/// FLU body (x forward, y left, z up); the geocentric one is the DIS/RPR Euler triple (psi, theta, phi) of the
/// FRD body (x forward, y right, z down) relative to ECEF (<see cref="Orientation"/>):
/// <c>C_frd_to_ecef = R * C(q_local) * F</c> with <c>R</c> from <see cref="LocalTangentFrame"/> and
/// <c>F = diag(1,-1,-1)</c> (<see cref="BodyAxes"/>). The inverse is <c>C(q_local) = R^T * C_frd_to_ecef * F</c>.
/// </summary>
public static class LocalToGeocentricOrientation
{
    /// <summary>
    /// The unit quaternion of a transmitted vector part: <c>w = +sqrt(max(0, 1 - |v|^2))</c>, so the all-zero vector is the
    /// identity. A vector part longer than 1 (rounding) is normalised.
    /// </summary>
    public static Quaternion FromVectorPart(double x, double y, double z)
    {
        double w = Math.Sqrt(Math.Max(0.0, 1.0 - (x * x + y * y + z * z)));
        return new Quaternion(w, x, y, z).Normalized();
    }

    /// <summary>The vector part (x, y, z) of the representative of a rotation with <c>w &gt;= 0</c>.</summary>
    public static Vec3 ToVectorPart(Quaternion q) => q.Normalized().WithNonNegativeW().Vector;

    /// <summary>Local ENU/FLU quaternion to DIS Euler angles <c>Vec3(psi, theta, phi)</c> (ECEF, FRD body).</summary>
    public static Vec3 ToDisEuler(Quaternion enuFlu, LocalTangentFrame frame)
    {
        var frdToEcef = frame.RotationToEcef * BodyAxes.BodyFluToFrd(enuFlu.ToMatrix());
        return Orientation.FromMatrix(frdToEcef);
    }

    /// <summary>DIS Euler angles (ECEF, FRD body) to the local ENU/FLU quaternion, normalised to <c>w &gt;= 0</c>.</summary>
    public static Quaternion FromDisEuler(Vec3 psiThetaPhi, LocalTangentFrame frame)
    {
        var fluToEnu = BodyAxes.BodyFrdToFlu(frame.RotationFromEcef * Orientation.ToMatrix(psiThetaPhi));
        return Quaternion.FromMatrix(fluToEnu);
    }

    /// <summary>Angular velocity in ENU axes to body FRD axes: <c>omega_body = F^T C(q)^T omega_enu</c>.</summary>
    public static Vec3 AngularVelocityEnuToBodyFrd(Quaternion enuFlu, Vec3 omegaEnu) =>
        BodyAxes.Flip.Transpose() * (enuFlu.ToMatrix().Transpose() * omegaEnu);

    /// <summary>Angular velocity in body FRD axes to ENU axes: <c>omega_enu = C(q) F omega_body</c>.</summary>
    public static Vec3 AngularVelocityBodyFrdToEnu(Quaternion enuFlu, Vec3 omegaBodyFrd) =>
        enuFlu.ToMatrix() * (BodyAxes.Flip * omegaBodyFrd);
}
