using SimNetGateway.Common.Geo;
using SimNetGateway.Engine;

namespace SimNetGateway.Common.Tests;

/// <summary>The UTM local frame (DD-014 D-2, D-5): the frame rule of HROT's <c>UtmTransform.AtOrigin</c>, exact positions, the convergence sign.</summary>
public class UtmFrameTests
{
    private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    [Fact]
    public void UtmFrame_AtOrigin_Rule()
    {
        // no override: the zone of the longitude, the hemisphere of the latitude
        var a = UtmFrame.AtOrigin(52.0, 14.5, 120.0, 0, false);
        Assert.Equal((33, false, 120.0), (a.Zone, a.South, a.H0));
        var (e, n) = Utm.FromGeodetic(52.0, 14.5, 33, false);
        Assert.Equal(e, a.E0);
        Assert.Equal(n, a.N0);

        var b = UtmFrame.AtOrigin(-33.9, 151.2, 0.0, 0, false);
        Assert.Equal((56, true), (b.Zone, b.South));

        // the south override is ignored without an override zone
        var c = UtmFrame.AtOrigin(52.0, 14.5, 0.0, 0, southOverride: true);
        Assert.Equal((33, false), (c.Zone, c.South));

        // an override zone: that zone, and the override's hemisphere (not the latitude's)
        var d = UtmFrame.AtOrigin(50.0, 6.0262, 0.0, 31, false);
        Assert.Equal((31, false), (d.Zone, d.South));
        Assert.Equal(32, Utm.ZoneOf(6.0262)); // its natural zone is the east neighbour
        var (e31, n31) = Utm.FromGeodetic(50.0, 6.0262, 31, false);
        Assert.Equal((e31, n31), (d.E0, d.N0));

        var f = UtmFrame.AtOrigin(50.0, 6.0262, 0.0, 33, southOverride: true);
        Assert.Equal((33, true), (f.Zone, f.South));
        var (e33, n33) = Utm.FromGeodetic(50.0, 6.0262, 33, true);
        Assert.Equal((e33, n33), (f.E0, f.N0));
    }

    [Fact]
    public void UtmFrame_RejectsAZoneOutsideOneToSixty()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UtmFrame(0, false, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UtmFrame(61, false, 0, 0, 0));
    }

    [Fact]
    public void UtmFrame_TheOriginIsExactlyItsLatitudeAndLongitude()
    {
        var random = new Random(31);
        for (int i = 0; i < 500; i++)
        {
            double lat = random.NextDouble() * 160 - 80, lon = random.NextDouble() * 360 - 180, alt = random.NextDouble() * 3000 - 100;
            var frame = UtmFrame.AtOrigin(lat, lon, alt, 0, false);
            var g = frame.ToGeodetic(Vec3.Zero);
            Assert.InRange(Math.Abs(g.LatitudeDegrees - lat), 0.0, 1e-11);
            Assert.InRange(Math.Abs(g.LongitudeDegrees - lon), 0.0, 1e-11);
            Assert.Equal(alt, g.Altitude, 9);
        }
    }

    [Fact]
    public void UtmFrame_PointRoundTripThroughEcef()
    {
        var random = new Random(32);
        for (int i = 0; i < 2000; i++)
        {
            double lat = random.NextDouble() * 160 - 80, lon = random.NextDouble() * 360 - 180;
            int zoneOverride = random.Next(4) == 0 ? ((Utm.ZoneOf(lon) - 1 + random.Next(-1, 2) + 60) % 60) + 1 : 0;
            var frame = UtmFrame.AtOrigin(lat, lon, random.NextDouble() * 1000, zoneOverride, southOverride: lat < 0);
            var p = new Vec3(random.NextDouble() * 2e4 - 1e4, random.NextDouble() * 2e4 - 1e4, random.NextDouble() * 1e3 - 500);
            var back = frame.PointFromEcef(frame.PointToEcef(p));
            Assert.True((back - p).Length < 1e-6, $"({lat}, {lon}) zone {frame.Zone}: {p} came back as {back}");
        }
    }

    [Fact]
    public void UtmFrame_TheEcefPointIsOnTheEllipsoidAtTheHeight()
    {
        var frame = UtmFrame.AtOrigin(48.2, 12.3, 250.0, 0, false);
        var g = Wgs84.ToGeodetic(frame.PointToEcef(new Vec3(1234, -4321, 17)));
        Assert.Equal(267.0, g.Altitude, 6); // height = H0 + z
    }

    [Fact]
    public void UtmFrame_Convergence_MatchesTheFiniteDifferenceOfGridNorth()
    {
        // Independent of the convergence formula: grid north at a point is the direction of the projected y axis (central difference of the ECEF
        // position); its signed angle from true north, positive towards east, is the convergence. Pins the sign convention (positive = grid
        // north east of true north, i.e. true heading = grid heading + convergence).
        var random = new Random(33);
        int checkedPoints = 0;
        for (int i = 0; i < 300; i++)
        {
            double lat = random.NextDouble() * 150 - 75, lon = random.NextDouble() * 360 - 180;
            var frame = UtmFrame.AtOrigin(lat, lon, 0.0, 0, southOverride: lat < 0);
            var p = new Vec3(random.NextDouble() * 4e5 - 2e5, random.NextDouble() * 4e5 - 2e5, 0.0);
            const double d = 50.0;
            var chord = frame.PointToEcef(p + new Vec3(0, d, 0)) - frame.PointToEcef(p - new Vec3(0, d, 0));
            var gridNorth = chord * (1.0 / chord.Length);
            var tangent = new LocalTangentFrame(frame.ToGeodetic(p));
            double east = Dot(gridNorth, tangent.East);
            double north = Dot(gridNorth, tangent.North);
            double measured = Math.Atan2(east, north);
            double gamma = frame.ConvergenceRad(p);
            Assert.True(Math.Abs(measured - gamma) < 2e-6, $"({lat}, {lon}) at {p}: measured {measured}, convergence {gamma}");
            if (Math.Abs(gamma) > 1e-3)
            {
                checkedPoints++;
            }
        }

        Assert.True(checkedPoints > 100, "the sample must contain many points with a sizeable convergence");
    }

    [Fact]
    public void UtmFrame_ConvergenceIsZeroOnTheCentralMeridianAndOppositeInTheTwoHemispheres()
    {
        var north = UtmFrame.AtOrigin(52.0, 15.0, 0.0, 0, false);
        Assert.Equal(0.0, north.ConvergenceRad(Vec3.Zero), 10);
        // 100 km east of the central meridian: grid north is east of true north in the north, west of it in the south
        double inNorth = UtmFrame.AtOrigin(45.0, 15.0, 0.0, 0, false).ConvergenceRad(new Vec3(1e5, 0, 0));
        double inSouth = UtmFrame.AtOrigin(-45.0, 15.0, 0.0, 0, false).ConvergenceRad(new Vec3(1e5, 0, 0));
        Assert.True(inNorth > 0 && inSouth < 0);
        Assert.Equal(inNorth, -inSouth, 5);
    }
}
