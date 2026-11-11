using SimNetGateway.Common;
using SimNetGateway.Common.Geo;
using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// The independent oracle of the tests for the frame of an origin: built straight from Common (<see cref="UtmFrame"/>, <see cref="LocalTangentFrame"/>,
/// <see cref="LocalToGeocentricOrientation"/>), not through the gateway's <c>GeoFrame</c>, so that a defect in the gateway's use of the frame is not
/// mirrored in the expectation. Directions take the local position they belong to (the grid convergence differs from point to point).
/// </summary>
public sealed class TestFrame
{
    public TestFrame(double lat, double lon, double alt, int zoneOverride = 0, bool southOverride = false) =>
        Utm = UtmFrame.AtOrigin(lat, lon, alt, zoneOverride, southOverride);

    public UtmFrame Utm { get; }

    public Vec3 PointToEcef(Vec3 enu) => Utm.PointToEcef(enu);

    public Vec3 PointFromEcef(Vec3 ecef) => Utm.PointFromEcef(ecef);

    private LocalTangentFrame Tangent(Vec3 at) => new(Utm.ToGeodetic(at));

    private Matrix3 GridToTrue(Vec3 at) => Matrix3.RotationZ(-Utm.ConvergenceRad(at));

    public Vec3 VectorToEcef(Vec3 at, Vec3 enu) => Tangent(at).VectorToEcef(GridToTrue(at) * enu);

    public Vec3 VectorFromEcef(Vec3 at, Vec3 ecef) => GridToTrue(at).Transpose() * Tangent(at).VectorFromEcef(ecef);

    /// <summary>The geocentric Euler angles (psi, theta, phi) of a body-to-grid-ENU quaternion (FLU body) at <paramref name="at"/>.</summary>
    public Vec3 ToDisEuler(Vec3 at, Quaternion enuFlu) =>
        LocalToGeocentricOrientation.ToDisEuler(Quaternion.FromMatrix(GridToTrue(at) * enuFlu.ToMatrix()), Tangent(at));

    /// <summary>The body-to-grid-ENU quaternion (w &gt;= 0) of geocentric Euler angles at <paramref name="at"/>.</summary>
    public Quaternion FromDisEuler(Vec3 at, Vec3 psiThetaPhi) =>
        Quaternion.FromMatrix(GridToTrue(at).Transpose() * LocalToGeocentricOrientation.FromDisEuler(psiThetaPhi, Tangent(at)).ToMatrix());
}
