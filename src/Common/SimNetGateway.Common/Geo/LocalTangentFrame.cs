using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// The local east-north-up (ENU) tangent frame at a geodetic origin and its relation to ECEF. <see cref="RotationToEcef"/>
/// <c>R</c> has the columns east, north, up (expressed in ECEF); a point <c>p</c> in ENU metres is
/// <c>ecef = ecef(origin) + R p</c>, a direction vector is <c>R v</c> (no translation), a rotation matrix <c>M</c>
/// (body to ENU) becomes <c>R M</c> (body to ECEF). The inverses use <c>R</c> transposed. All in double precision.
/// The up axis is the ellipsoid normal at the origin (geodetic latitude), not the direction to the Earth centre.
/// </summary>
public sealed class LocalTangentFrame
{
    public LocalTangentFrame(Geodetic origin)
    {
        Origin = origin;
        OriginEcef = Wgs84.ToEcef(origin);

        // ENU columns from the NorthEastDown columns (north, east, down): east = c1, north = c0, up = -c2.
        var ned = NorthEastDown.RotationToEcef(origin);
        var down = ned.Column(2);
        RotationToEcef = Matrix3.FromColumns(ned.Column(1), ned.Column(0), new Vec3(-down.X, -down.Y, -down.Z));
        RotationFromEcef = RotationToEcef.Transpose();
    }

    public Geodetic Origin { get; }

    public Vec3 OriginEcef { get; }

    /// <summary>Maps ENU direction vectors to ECEF; the columns are east, north and up.</summary>
    public Matrix3 RotationToEcef { get; }

    /// <summary>The transpose of <see cref="RotationToEcef"/> (= its inverse).</summary>
    public Matrix3 RotationFromEcef { get; }

    public Vec3 East => RotationToEcef.Column(0);

    public Vec3 North => RotationToEcef.Column(1);

    public Vec3 Up => RotationToEcef.Column(2);

    /// <summary>A point in local ENU metres to ECEF metres.</summary>
    public Vec3 PointToEcef(Vec3 enu) => OriginEcef + RotationToEcef * enu;

    /// <summary>A point in ECEF metres to local ENU metres.</summary>
    public Vec3 PointFromEcef(Vec3 ecef) => RotationFromEcef * (ecef - OriginEcef);

    /// <summary>A direction vector (velocity, acceleration, angular velocity) in ENU components to ECEF components.</summary>
    public Vec3 VectorToEcef(Vec3 enu) => RotationToEcef * enu;

    /// <summary>A direction vector in ECEF components to ENU components.</summary>
    public Vec3 VectorFromEcef(Vec3 ecef) => RotationFromEcef * ecef;

    /// <summary>A body-to-ENU rotation to the body-to-ECEF rotation.</summary>
    public Matrix3 RotationToEcefFrame(in Matrix3 bodyToEnu) => RotationToEcef * bodyToEnu;

    /// <summary>A body-to-ECEF rotation to the body-to-ENU rotation.</summary>
    public Matrix3 RotationFromEcefFrame(in Matrix3 bodyToEcef) => RotationFromEcef * bodyToEcef;

    /// <summary>A body-to-ENU quaternion to the body-to-ECEF quaternion (w &gt;= 0).</summary>
    public Quaternion RotationToEcefFrame(Quaternion bodyToEnu) =>
        Quaternion.FromMatrix(RotationToEcef * bodyToEnu.ToMatrix());

    /// <summary>A body-to-ECEF quaternion to the body-to-ENU quaternion (w &gt;= 0).</summary>
    public Quaternion RotationFromEcefFrame(Quaternion bodyToEcef) =>
        Quaternion.FromMatrix(RotationFromEcef * bodyToEcef.ToMatrix());
}
