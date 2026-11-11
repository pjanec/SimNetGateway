using SimNetGateway.Common.Geo;

namespace SimNetGateway.Common.Tests;

/// <summary>The UTM projection (DD-014 D-4): round trip, and reference rails generated once from HROT's own implementation (generic places).</summary>
public class UtmTests
{
    [Fact]
    public void Utm_RoundTrip_Under1mm()
    {
        // The series are not exact inverses: about 3e-9 degrees (0.3 mm) inside the zone, a few 1e-9 more towards its edge (3 degrees). Within the
        // neighbouring zones (up to 9 degrees from the central meridian, only reached by a zone override) the inverse series degrade, towards the
        // poles to about a metre; UtmFrame refines the inverse (UtmFrameTests).
        var random = new Random(20261111);
        int inZone = 0, neighbour = 0;
        for (int i = 0; i < 40000; i++)
        {
            double lat = random.NextDouble() * 162.0 - 79.0;
            double lon = random.NextDouble() * 360.0 - 180.0;
            int zone = ((Utm.ZoneOf(lon) - 1 + random.Next(-1, 2) + 60) % 60) + 1;
            bool south = lat < 0 || random.Next(10) == 0;
            double dLon = Math.Abs(((lon - Utm.CentralMeridianDeg(zone) + 540.0) % 360.0) - 180.0);
            bool own = dLon <= 3.0;
            if (!own && dLon > 9.0)
            {
                continue;
            }

            var (e, n) = Utm.FromGeodetic(lat, lon, zone, south);
            var (lat2, lon2) = Utm.ToGeodetic(e, n, zone, south);
            double error = Math.Max(Math.Abs(lat2 - lat), Math.Abs(((lon2 - lon + 540.0) % 360.0) - 180.0));
            Assert.True(error < (own ? 1e-8 : 2e-5), $"({lat}, {lon}) zone {zone} south {south}: round trip off by {error} degrees");
            if (own) inZone++; else neighbour++;
        }

        Assert.True(inZone > 5000 && neighbour > 5000);
    }

    [Fact]
    public void Utm_a_zone_next_to_the_date_line_sees_longitudes_across_it()
    {
        // zone 60 (central meridian 177 E) and a point at 177.74 W, i.e. 5.26 degrees east of it: the same as the continuous longitude 182.26
        var (e, n) = Utm.FromGeodetic(57.5, -177.74, 60, south: false);
        var (e2, n2) = Utm.FromGeodetic(57.5, 182.26, 60, south: false);
        Assert.Equal(e2, e, 6);
        Assert.Equal(n2, n, 6);
        Assert.True(e > 500000.0);
        var (lat, lon) = Utm.ToGeodetic(e, n, 60, south: false);
        Assert.Equal(57.5, lat, 6);
        Assert.Equal(-177.74, lon, 6);
        Assert.True(Utm.ConvergenceDeg(57.5, -177.74, 60) > 0.0);
    }

    [Theory]
    // lat, lon, zone, south, easting, northing, convergence (deg), then HROT's own inverse of (easting, northing): lat, lon
    [InlineData(52.0, 14.5, 33, false, 465674.83254503895, 5761156.236174925, -0.3940091678619993, 52.00000000315809, 14.499999999964793)]
    [InlineData(48.2, 12.3, 33, false, 299380.0434503978, 5342054.883253149, -2.013447210131192, 48.20000000202465, 12.299999997488207)]
    [InlineData(-33.0, -74.8, 18, true, 518683.1822507114, 6348695.2961929375, -0.10892811818625639, -32.99999999972291, -74.80000000000062)]
    [InlineData(-33.9, 151.2, 56, true, 333568.9410100591, 6247473.336787824, 1.0041687830622126, -33.89999999977369, 151.19999999991046)]
    [InlineData(-25.0, 153.5, 56, true, 550455.2696741897, 7234959.210405306, -0.21131353695524757, -24.999999999543547, 153.49999999999815)]
    // 2 km east of the edge between zones 31 and 32 (6 E), in its natural zone 32 and projected in the west neighbour (zone 31)
    [InlineData(50.0, 6.0262, 32, false, 286892.9986502569, 5542868.983727214, -2.2789082704054207, 50.00000000245701, 6.02619999492698)]
    [InlineData(50.0, 6.0262, 31, false, 716861.4643346154, 5543019.713466803, 2.3190944795333195, 50.00000000244193, 6.026200005715088)]
    [InlineData(0.5, -60.0, 21, false, 166034.0983655032, 55341.3882154088, -0.02620355522583741, 0.49999999998722394, -59.999999999873644)]
    [InlineData(70.0, 25.0, 35, false, 423669.3425899013, 7767125.171905255, -1.879474519468143, 70.00000000705903, 24.999999998880995)]
    public void Utm_ReferenceRails(double lat, double lon, int zone, bool south, double easting, double northing, double convergenceDeg, double inverseLat, double inverseLon)
    {
        var (e, n) = Utm.FromGeodetic(lat, lon, zone, south);
        Assert.InRange(Math.Abs(e - easting), 0.0, 1e-3);
        Assert.InRange(Math.Abs(n - northing), 0.0, 1e-3);
        Assert.InRange(Math.Abs(Utm.ConvergenceDeg(lat, lon, zone) - convergenceDeg), 0.0, 1e-9);
        var (lat2, lon2) = Utm.ToGeodetic(easting, northing, zone, south);
        Assert.InRange(Math.Abs(lat2 - inverseLat), 0.0, 1e-9);
        Assert.InRange(Math.Abs(lon2 - inverseLon), 0.0, 1e-9);
    }

    [Theory]
    [InlineData(14.5, 33)]
    [InlineData(6.0262, 32)]
    [InlineData(5.9999, 31)]
    [InlineData(-74.8, 18)]
    [InlineData(151.2, 56)]
    [InlineData(-180.0, 1)]
    [InlineData(180.0, 60)]
    [InlineData(0.0, 31)]
    public void Utm_ZoneOf_is_the_six_degree_band(double lon, int zone) => Assert.Equal(zone, Utm.ZoneOf(lon));

    [Fact]
    public void Utm_CentralMeridian_of_the_zones()
    {
        Assert.Equal(15.0, Utm.CentralMeridianDeg(33));
        Assert.Equal(-177.0, Utm.CentralMeridianDeg(1));
        Assert.Equal(177.0, Utm.CentralMeridianDeg(60));
    }

    [Fact]
    public void Utm_the_central_meridian_has_no_convergence_and_the_false_origins_are_as_defined()
    {
        Assert.Equal(0.0, Utm.ConvergenceDeg(52.0, 15.0, 33), 12);
        var (e, n) = Utm.FromGeodetic(0.0, 15.0, 33, south: false);
        Assert.Equal(500000.0, e, 6);
        Assert.Equal(0.0, n, 6);
        var (es, ns) = Utm.FromGeodetic(0.0, 15.0, 33, south: true);
        Assert.Equal(500000.0, es, 6);
        Assert.Equal(10000000.0, ns, 6);
    }
}
