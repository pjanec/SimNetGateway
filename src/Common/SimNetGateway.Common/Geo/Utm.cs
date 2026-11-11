namespace SimNetGateway.Common.Geo;

/// <summary>
/// UTM (transverse Mercator on WGS84, k0 0.9996, false easting 500000, false northing 10000000 in the southern hemisphere) by Snyder's
/// series (USGS PP 1395, section 8): well under a millimetre inside a zone. Angles are degrees. Longitudes outside the band of the
/// given zone are projected in that zone anyway (the frame of a local grid never jumps at a zone edge).
/// </summary>
public static class Utm
{
    private const double K0 = 0.9996;
    private const double FalseEasting = 500000.0;
    private const double FalseNorthingSouth = 10000000.0;

    /// <summary>The UTM zone (1 .. 60) of a longitude: its 6 degree band.</summary>
    public static int ZoneOf(double lonDeg) => Math.Clamp((int)Math.Floor((lonDeg + 180.0) / 6.0) + 1, 1, 60);

    /// <summary>The central meridian of a zone, degrees.</summary>
    public static double CentralMeridianDeg(int zone) => zone * 6.0 - 183.0;

    /// <summary>The longitude offset from the central meridian of a zone, degrees in [-180, 180) (a zone next to the date line sees longitudes across it).</summary>
    private static double DeltaLonDeg(double lonDeg, int zone) => WrapLonDeg(lonDeg - CentralMeridianDeg(zone));

    private static double WrapLonDeg(double lonDeg) => lonDeg - 360.0 * Math.Floor((lonDeg + 180.0) / 360.0);

    /// <summary>Latitude / longitude (degrees) to easting / northing (metres) in <paramref name="zone"/>.</summary>
    public static (double Easting, double Northing) FromGeodetic(double latDeg, double lonDeg, int zone, bool south)
    {
        double e2 = Wgs84.E2, ep2 = Wgs84.Ep2;
        double phi = Angles.ToRadians(latDeg);
        double lam = Angles.ToRadians(DeltaLonDeg(lonDeg, zone));
        double sin = Math.Sin(phi), cos = Math.Cos(phi), tan = Math.Tan(phi);
        double n = Wgs84.A / Math.Sqrt(1 - e2 * sin * sin);
        double t = tan * tan;
        double c = ep2 * cos * cos;
        double a = cos * lam;
        double m = Wgs84.A * ((1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256) * phi
            - (3 * e2 / 8 + 3 * e2 * e2 / 32 + 45 * e2 * e2 * e2 / 1024) * Math.Sin(2 * phi)
            + (15 * e2 * e2 / 256 + 45 * e2 * e2 * e2 / 1024) * Math.Sin(4 * phi)
            - (35 * e2 * e2 * e2 / 3072) * Math.Sin(6 * phi));
        double easting = K0 * n * (a + (1 - t + c) * Math.Pow(a, 3) / 6
            + (5 - 18 * t + t * t + 72 * c - 58 * ep2) * Math.Pow(a, 5) / 120) + FalseEasting;
        double northing = K0 * (m + n * tan * (a * a / 2 + (5 - t + 9 * c + 4 * c * c) * Math.Pow(a, 4) / 24
            + (61 - 58 * t + t * t + 600 * c - 330 * ep2) * Math.Pow(a, 6) / 720));
        return (easting, south ? northing + FalseNorthingSouth : northing);
    }

    /// <summary>Easting / northing (metres) in <paramref name="zone"/> to latitude / longitude (degrees).</summary>
    public static (double LatDeg, double LonDeg) ToGeodetic(double easting, double northing, int zone, bool south)
    {
        double e2 = Wgs84.E2, ep2 = Wgs84.Ep2;
        double x = easting - FalseEasting;
        double y = south ? northing - FalseNorthingSouth : northing;
        double m = y / K0;
        double mu = m / (Wgs84.A * (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256));
        double e1 = (1 - Math.Sqrt(1 - e2)) / (1 + Math.Sqrt(1 - e2));
        double phi1 = mu
            + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu)
            + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
            + (151 * Math.Pow(e1, 3) / 96) * Math.Sin(6 * mu)
            + (1097 * Math.Pow(e1, 4) / 512) * Math.Sin(8 * mu);
        double sin = Math.Sin(phi1), cos = Math.Cos(phi1), tan = Math.Tan(phi1);
        double n1 = Wgs84.A / Math.Sqrt(1 - e2 * sin * sin);
        double t1 = tan * tan;
        double c1 = ep2 * cos * cos;
        double r1 = Wgs84.A * (1 - e2) / Math.Pow(1 - e2 * sin * sin, 1.5);
        double d = x / (n1 * K0);
        double lat = phi1 - n1 * tan / r1 * (d * d / 2
            - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * ep2) * Math.Pow(d, 4) / 24
            + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);
        double lon = (d - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6
            + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / cos;
        return (Angles.ToDegrees(lat), WrapLonDeg(CentralMeridianDeg(zone) + Angles.ToDegrees(lon)));
    }

    /// <summary>
    /// The grid convergence at a point, degrees: the angle from true north to grid north, positive where grid north lies east of true
    /// north (east of the central meridian in the northern hemisphere). A grid heading plus the convergence is the true heading.
    /// </summary>
    public static double ConvergenceDeg(double latDeg, double lonDeg, int zone)
    {
        double phi = Angles.ToRadians(latDeg);
        double dLam = Angles.ToRadians(DeltaLonDeg(lonDeg, zone));
        return Angles.ToDegrees(Math.Atan(Math.Tan(dLam) * Math.Sin(phi)));
    }
}
