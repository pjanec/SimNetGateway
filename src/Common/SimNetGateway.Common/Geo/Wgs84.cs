using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>A geodetic position: latitude and longitude in radians, altitude above the WGS-84 ellipsoid in metres.</summary>
public readonly record struct Geodetic(double Latitude, double Longitude, double Altitude)
{
    public static Geodetic FromDegrees(double latitude, double longitude, double altitude) =>
        new(Angles.ToRadians(latitude), Angles.ToRadians(longitude), altitude);

    public double LatitudeDegrees => Angles.ToDegrees(Latitude);

    public double LongitudeDegrees => Angles.ToDegrees(Longitude);
}

/// <summary>The WGS-84 ellipsoid and the conversion between geodetic and ECEF coordinates.</summary>
public static class Wgs84
{
    /// <summary>Semi-major axis in metres.</summary>
    public const double A = 6378137.0;

    /// <summary>Flattening.</summary>
    public const double F = 1.0 / 298.257223563;

    /// <summary>Semi-minor axis in metres.</summary>
    public const double B = A * (1.0 - F);

    /// <summary>First eccentricity squared.</summary>
    public const double E2 = F * (2.0 - F);

    /// <summary>Second eccentricity squared.</summary>
    public const double Ep2 = E2 / (1.0 - E2);

    /// <summary>Geodetic to Earth-centred, Earth-fixed coordinates (metres).</summary>
    public static Vec3 ToEcef(Geodetic g)
    {
        double sinLat = Math.Sin(g.Latitude), cosLat = Math.Cos(g.Latitude);
        double sinLon = Math.Sin(g.Longitude), cosLon = Math.Cos(g.Longitude);
        double n = A / Math.Sqrt(1.0 - E2 * sinLat * sinLat);
        double r = (n + g.Altitude) * cosLat;
        return new Vec3(r * cosLon, r * sinLon, (n * (1.0 - E2) + g.Altitude) * sinLat);
    }

    /// <summary>
    /// ECEF to geodetic. Fixed-point iteration on the latitude (converges by a factor of about e^2 per step, so a
    /// handful of steps reach machine precision); the altitude uses a formula that is stable at the poles. The
    /// longitude is in (-pi, pi]. On the polar axis (x = y = 0) the longitude is 0.
    /// </summary>
    public static Geodetic ToGeodetic(Vec3 ecef)
    {
        double x = ecef.X, y = ecef.Y, z = ecef.Z;
        double p = Math.Sqrt(x * x + y * y);
        double lat = Math.Atan2(z, p * (1.0 - E2));
        double sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
        for (int i = 0; i < 30; i++)
        {
            double n = A / Math.Sqrt(1.0 - E2 * sinLat * sinLat);
            double next = Math.Atan2(z + E2 * n * sinLat, p);
            double delta = Math.Abs(next - lat);
            lat = next;
            sinLat = Math.Sin(lat);
            cosLat = Math.Cos(lat);
            if (delta < 1e-16)
            {
                break;
            }
        }

        double alt = p * cosLat + z * sinLat - A * Math.Sqrt(1.0 - E2 * sinLat * sinLat);
        double lon = (x == 0.0 && y == 0.0) ? 0.0 : Math.Atan2(y, x);
        if (lon <= -Math.PI)
        {
            lon = Math.PI; // atan2(-0.0, negative) is -pi; the date line is +pi
        }

        return new Geodetic(lat, lon, alt);
    }
}
