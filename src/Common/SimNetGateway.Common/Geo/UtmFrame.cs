using SimNetGateway.Engine;

namespace SimNetGateway.Common.Geo;

/// <summary>
/// A local grid frame of UTM metres: local = (E - E0, N - N0, h - H0), x grid east, y grid north, z up. Positions convert exactly
/// (through the projection); directions need the grid convergence at the point, see <see cref="ConvergenceRad"/>.
/// </summary>
public sealed class UtmFrame
{
    public UtmFrame(int zone, bool south, double e0, double n0, double h0)
    {
        if (zone < 1 || zone > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(zone), zone, "A UTM zone is 1 .. 60.");
        }

        Zone = zone;
        South = south;
        E0 = e0;
        N0 = n0;
        H0 = h0;
    }

    public int Zone { get; }

    public bool South { get; }

    public double E0 { get; }

    public double N0 { get; }

    public double H0 { get; }

    /// <summary>
    /// The frame whose origin is the given latitude / longitude / altitude: the zone is the longitude's band unless
    /// <paramref name="zoneOverride"/> is not 0 (then that zone), the hemisphere is the latitude's sign unless an override zone is given
    /// (then <paramref name="southOverride"/>), (E0, N0) is the origin projected into that zone, H0 the altitude.
    /// </summary>
    public static UtmFrame AtOrigin(double latDeg, double lonDeg, double altM, int zoneOverride, bool southOverride)
    {
        int zone = zoneOverride != 0 ? zoneOverride : Utm.ZoneOf(lonDeg);
        bool south = zoneOverride != 0 ? southOverride : latDeg < 0;
        var (e, n) = Utm.FromGeodetic(latDeg, lonDeg, zone, south);
        return new UtmFrame(zone, south, e, n, altM);
    }

    /// <summary>
    /// Latitude / longitude (degrees) of a local point: the inverse series of <see cref="Utm.ToGeodetic"/>, refined so that it inverts
    /// <see cref="Utm.FromGeodetic"/> exactly (the series alone are off by up to a few tenths of a millimetre): the origin of the frame is
    /// exactly its latitude / longitude and a point survives a round trip through ECEF to nanometres.
    /// </summary>
    private (double LatDeg, double LonDeg) LatLon(Vec3 local)
    {
        double e = E0 + local.X, n = N0 + local.Y;
        var (lat, lon) = Utm.ToGeodetic(e, n, Zone, South);
        for (int i = 0; i < 12; i++)
        {
            var (e2, n2) = Utm.FromGeodetic(lat, lon, Zone, South);
            double de = e - e2, dn = n - n2;
            if (Math.Abs(de) < 1e-10 && Math.Abs(dn) < 1e-10)
            {
                break;
            }

            double phi = Angles.ToRadians(lat);
            double sin = Math.Sin(phi), cos = Math.Cos(phi);
            double w = Math.Sqrt(1.0 - Wgs84.E2 * sin * sin);
            double nu = Wgs84.A / w;
            double rho = Wgs84.A * (1.0 - Wgs84.E2) / (w * w * w);
            lat += Angles.ToDegrees(dn / (0.9996 * rho));
            lon += Angles.ToDegrees(de / (0.9996 * nu * cos));
        }

        return (lat, lon);
    }

    /// <summary>The geodetic position (radians, metres above the ellipsoid) of a local point.</summary>
    public Geodetic ToGeodetic(Vec3 local)
    {
        var (lat, lon) = LatLon(local);
        return Geodetic.FromDegrees(lat, lon, H0 + local.Z);
    }

    public Vec3 PointToEcef(Vec3 local) => Wgs84.ToEcef(ToGeodetic(local));

    public Vec3 PointFromEcef(Vec3 ecef)
    {
        var g = Wgs84.ToGeodetic(ecef);
        var (e, n) = Utm.FromGeodetic(g.LatitudeDegrees, g.LongitudeDegrees, Zone, South);
        return new Vec3(e - E0, n - N0, g.Altitude - H0);
    }

    /// <summary>The grid convergence at a local point, radians (positive: grid north is east of true north).</summary>
    public double ConvergenceRad(Vec3 local)
    {
        var (lat, lon) = LatLon(local);
        return Angles.ToRadians(Utm.ConvergenceDeg(lat, lon, Zone));
    }
}
