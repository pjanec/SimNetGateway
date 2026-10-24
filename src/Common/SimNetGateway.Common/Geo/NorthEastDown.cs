using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// The local tangent frame at a geodetic point: x north, y east, z down (the north-east-down frame; not the NED protocol).
/// Direction vectors only: rotation, no translation.
/// </summary>
public static class NorthEastDown
{
    /// <summary>The matrix whose columns are north, east and down expressed in ECEF (maps NorthEastDown vectors to ECEF).</summary>
    public static Matrix3 RotationToEcef(Geodetic at)
    {
        double sinLat = Math.Sin(at.Latitude), cosLat = Math.Cos(at.Latitude);
        double sinLon = Math.Sin(at.Longitude), cosLon = Math.Cos(at.Longitude);
        return new Matrix3(
            -sinLat * cosLon, -sinLon, -cosLat * cosLon,
            -sinLat * sinLon, cosLon, -cosLat * sinLon,
            cosLat, 0.0, -sinLat);
    }

    /// <summary>A direction vector in north/east/down components to ECEF components.</summary>
    public static Vec3 ToEcef(Vec3 neVector, Geodetic at) => RotationToEcef(at) * neVector;

    /// <summary>A direction vector in ECEF components to north/east/down components.</summary>
    public static Vec3 FromEcef(Vec3 ecefVector, Geodetic at) => RotationToEcef(at).Transpose() * ecefVector;
}
