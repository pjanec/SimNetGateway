using SimNetGateway.Common;
using SimNetGateway.Common.Geo;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>The services of the gateway (DD-006 section 2): geo frame, catalogs, damage mapping, event ids, fire correlator, id allocator.</summary>
public class ServiceTests
{
    private static readonly DisTypeCode Tank = new(1, 1, 225, 1, 1, 3, 0);

    // ---- GeoFrame ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_frame_is_invalid_until_an_origin_arrives_and_refuses_the_zero_origin_unless_allowed()
    {
        var geo = new GeoFrame();
        Assert.False(geo.IsValid);
        Assert.Equal(0, geo.Generation);
        Assert.Throws<InvalidOperationException>(() => geo.PointToEcef(new HrotVec3(1, 2, 3)));

        Assert.False(geo.TryApply(HrotOrigin.Utm(0, 0, 0, 1), out string? problem));
        Assert.NotNull(problem);
        Assert.False(geo.IsValid);

        Assert.True(new GeoFrame(allowZeroOrigin: true).TryApply(HrotOrigin.Utm(0, 0, 0, 1), out _));

        Assert.True(geo.TryApply(HrotOrigin.Utm(50, 14, 300, 3), out problem));
        Assert.Null(problem);
        Assert.True(geo.IsValid);
        Assert.Equal(3, geo.Generation);
        Assert.False(geo.TryApply(HrotOrigin.Utm(50, 14, 300, 3), out problem)); // unchanged: nothing to re-base
        Assert.Null(problem);
        Assert.True(geo.TryApply(HrotOrigin.Utm(50, 14, 300, 4), out _)); // a new generation re-bases
        Assert.Equal(4, geo.Generation);
        Assert.True(geo.TryApply(HrotOrigin.Utm(51, 14, 300, 4), out _)); // so does a different place
    }

    [Theory]
    [InlineData(91.0, 0.0, 0.0)]
    [InlineData(-90.5, 10.0, 0.0)]
    [InlineData(10.0, 181.0, 0.0)]
    [InlineData(10.0, -180.5, 0.0)]
    [InlineData(double.NaN, 10.0, 0.0)]
    [InlineData(10.0, double.PositiveInfinity, 0.0)]
    [InlineData(10.0, 10.0, double.NaN)]
    public void An_unacceptable_origin_is_refused_and_the_previous_frame_stays(double lat, double lon, double alt)
    {
        var geo = new GeoFrame();
        Assert.True(geo.TryApply(HrotOrigin.Utm(50, 14, 300, 1), out _));
        Assert.False(geo.TryApply(HrotOrigin.Utm(lat, lon, alt, 2), out string? problem));
        Assert.NotNull(problem);
        Assert.Equal(1, geo.Generation);
        Assert.Equal(50.0, geo.Origin!.Value.LatitudeDeg);
    }

    [Fact]
    public void East_north_and_up_are_the_axes_of_the_local_frame()
    {
        // At latitude 0, longitude 0 the ECEF axes are: x up, y east, z north.
        var geo = new GeoFrame();
        Assert.True(geo.TryApply(HrotOrigin.Utm(0, 0, 100, 1), out _));
        var origin = geo.PointToEcef(new HrotVec3(0, 0, 0));
        Assert.Equal(Wgs84.A + 100.0, origin.X, 6);
        Assert.Equal(0.0, origin.Y, 6);
        Assert.Equal(0.0, origin.Z, 6);
        var east = geo.PointToEcef(new HrotVec3(10, 0, 0)) - origin;
        var north = geo.PointToEcef(new HrotVec3(0, 10, 0)) - origin;
        var up = geo.PointToEcef(new HrotVec3(0, 0, 10)) - origin;
        Assert.Equal(10.0, east.Length, 1);
        Assert.Equal(10.0, east.Y, 1);
        Assert.Equal(10.0, north.Z, 1);
        Assert.Equal(10.0, up.X, 6);
        // grid metres are not true metres: the UTM scale factor (0.9996 at the central meridian, up to about 0.9999 at 3 degrees off it) is ignored for vectors
        var at = new HrotVec3(0, 0, 0);
        Assert.Equal(10.0, geo.VectorToEcef(at, new HrotVec3(10, 0, 0)).Y, 1);
        Assert.Equal(10.0, geo.VectorToEcef(at, new HrotVec3(0, 10, 0)).Z, 1);
    }

    [Fact]
    public void Points_vectors_and_orientations_round_trip_through_the_frame()
    {
        var random = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            var geo = new GeoFrame();
            Assert.True(geo.TryApply(HrotOrigin.Utm(random.NextDouble() * 160 - 80, random.NextDouble() * 360 - 180, random.NextDouble() * 3000, i + 1), out _));
            var enu = new HrotVec3(random.NextDouble() * 2e4 - 1e4, random.NextDouble() * 2e4 - 1e4, random.NextDouble() * 1e3 - 500);
            var back = geo.PointFromEcef(geo.PointToEcef(enu));
            Assert.True(Math.Abs(back.X - enu.X) < 2e-3 && Math.Abs(back.Y - enu.Y) < 2e-3 && Math.Abs(back.Z - enu.Z) < 2e-3); // the series are not exact inverses: under 1 mm
            var rotation = RandomQuaternion(random);
            var again = geo.FromEuler(enu, geo.ToEuler(enu, rotation));
            Assert.True(Quaternion.AngleBetween(GeoFrame.ToQuaternion(rotation), GeoFrame.ToQuaternion(again)) < 1e-9);
            var omega = new HrotVec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            var omegaBack = geo.AngularVelocityFromBody(enu, rotation, geo.AngularVelocityToBody(enu, rotation, omega));
            Assert.True(Math.Abs(omegaBack.X - omega.X) < 1e-12 && Math.Abs(omegaBack.Y - omega.Y) < 1e-12 && Math.Abs(omegaBack.Z - omega.Z) < 1e-12);
        }
    }

    private static GeoFrame RandomFrame(Random random, int generation = 1) => RandomFrame(random, generation, out _);

    private static GeoFrame RandomFrame(Random random, int generation, out bool overridden)
    {
        var geo = new GeoFrame();
        double lat = random.NextDouble() * 150 - 75, lon = random.NextDouble() * 360 - 180;
        // an override only near the edge of the override zone (up to 4 degrees from its central meridian), where its series are accurate
        int zone = ((Utm.ZoneOf(lon) - 1 + random.Next(-1, 2) + 60) % 60) + 1;
        double offCentre = Math.Abs(((lon - Utm.CentralMeridianDeg(zone) + 540.0) % 360.0) - 180.0);
        int zoneOverride = random.Next(5) == 0 && offCentre <= 4.0 ? zone : 0;
        overridden = zoneOverride != 0;
        Assert.True(geo.TryApply(HrotOrigin.Utm(lat, lon, random.NextDouble() * 1000, generation, zoneOverride, southOverride: lat < 0), out _));
        return geo;
    }

    private static HrotVec3 RandomLocal(Random random) =>
        new(random.NextDouble() * 2e4 - 1e4, random.NextDouble() * 2e4 - 1e4, random.NextDouble() * 1e3 - 500);

    private static double AngleDeg(Vec3 a, Vec3 b) =>
        Math.Atan2(new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X).Length, a.X * b.X + a.Y * b.Y + a.Z * b.Z) * 180.0 / Math.PI;

    [Fact]
    public void UtmFrame_VectorMatchesFiniteDifference()
    {
        // A vector in the local frame is the difference of two projected points (a central difference): pins the sign and size of the convergence
        // rotation. The projection is conformal, so a horizontal vector keeps its direction exactly (only its length changes by the scale factor,
        // ignored for vectors, design D-5: within 0.1 percent inside a zone). The height is not scaled, so the elevation of a sloped vector differs
        // from the finite difference by that factor (under 0.02 degrees).
        var random = new Random(41);
        const double dt = 1e-3;
        for (int i = 0; i < 400; i++)
        {
            var geo = RandomFrame(random, i + 1, out bool overridden);
            var p = RandomLocal(random);
            foreach (double vertical in new[] { 0.0, random.NextDouble() * 20 - 10 })
            {
                var v = new HrotVec3(random.NextDouble() * 200 - 100, random.NextDouble() * 200 - 100, vertical);
                var ahead = new HrotVec3(p.X + v.X * dt, p.Y + v.Y * dt, p.Z + v.Z * dt);
                var behind = new HrotVec3(p.X - v.X * dt, p.Y - v.Y * dt, p.Z - v.Z * dt);
                var difference = (geo.PointToEcef(ahead) - geo.PointToEcef(behind)) * (1.0 / (2 * dt));
                var vector = geo.VectorToEcef(p, v);
                double angle = AngleDeg(vector, difference);
                double allowed = vertical == 0.0 ? 0.001 : 0.02;
                Assert.True(angle < allowed, $"frame {i} {geo.Origin} zone {geo.Utm.Zone} at {p}: the vector {vector} is {angle} degrees from the finite difference {difference}");
                if (vertical == 0.0)
                {
                    Assert.InRange(difference.Length / vector.Length, overridden ? 0.99 : 0.999, overridden ? 1.01 : 1.001);
                }

                // and back
                var again = geo.VectorFromEcef(p, vector);
                Assert.True(Math.Abs(again.X - v.X) < 1e-9 && Math.Abs(again.Y - v.Y) < 1e-9 && Math.Abs(again.Z - v.Z) < 1e-9);
            }
        }
    }

    [Fact]
    public void GeoFrame_EulerRoundTrip()
    {
        // G-2: any body quaternion at any point within 10 km of the origin round-trips through the geocentric Euler angles, and the three body
        // axes (FLU, rotated into grid ENU, then to ECEF as vectors) are the axes of the DIS Euler matrix (FRD: x forward, y right, z down).
        var random = new Random(42);
        for (int i = 0; i < 400; i++)
        {
            var geo = RandomFrame(random, i + 1);
            var p = RandomLocal(random);
            var q = RandomQuaternion(random);
            var euler = geo.ToEuler(p, q);
            var back = geo.FromEuler(p, euler);
            Assert.True(Quaternion.AngleBetween(GeoFrame.ToQuaternion(q), GeoFrame.ToQuaternion(back)) * 180.0 / Math.PI < 0.01, $"frame {i}");

            var body = GeoFrame.ToQuaternion(q);
            var dis = Orientation.ToMatrix(euler);
            double[] sign = [1.0, -1.0, -1.0];
            for (int axis = 0; axis < 3; axis++)
            {
                var flu = body.Rotate(new Vec3(axis == 0 ? 1 : 0, axis == 1 ? 1 : 0, axis == 2 ? 1 : 0));
                var inEcef = geo.VectorToEcef(p, new HrotVec3(flu.X, flu.Y, flu.Z));
                var expected = dis.Column(axis) * sign[axis];
                Assert.True(AngleDeg(inEcef, expected) < 1e-6, $"frame {i} axis {axis}: {inEcef} against {expected}");
            }
        }
    }

    [Fact]
    public void GeoFrame_AngularVelocityRoundTripAndBodyAxes()
    {
        var random = new Random(43);
        for (int i = 0; i < 200; i++)
        {
            var geo = RandomFrame(random, i + 1);
            var p = RandomLocal(random);
            var q = RandomQuaternion(random);
            var w = new HrotVec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            var body = geo.AngularVelocityToBody(p, q, w);
            // the body-axis rates do not depend on where the grid points: the quaternion's own FLU components, flipped to FRD
            var flu = GeoFrame.ToQuaternion(q).ToMatrix().Transpose() * new Vec3(w.X, w.Y, w.Z);
            Assert.True((body - new Vec3(flu.X, -flu.Y, -flu.Z)).Length < 1e-12);
            var back = geo.AngularVelocityFromBody(p, q, body);
            Assert.True(Math.Abs(back.X - w.X) < 1e-12 && Math.Abs(back.Y - w.Y) < 1e-12 && Math.Abs(back.Z - w.Z) < 1e-12);
        }
    }

    [Fact]
    public void GeoFrame_UsesTheZoneOverrideOfTheOrigin()
    {
        var geo = new GeoFrame();
        Assert.True(geo.TryApply(HrotOrigin.Utm(50.0, 6.0262, 10.0, 1, zoneOverride: 31), out _));
        Assert.Equal(31, geo.Utm.Zone);
        Assert.False(geo.Utm.South);
        Assert.True(geo.TryApply(HrotOrigin.Utm(50.0, 6.0262, 10.0, 2), out _)); // another generation, no override: the natural zone
        Assert.Equal(32, geo.Utm.Zone);
        Assert.False(geo.TryApply(HrotOrigin.Utm(50.0, 6.0262, 10.0, 3, zoneOverride: 61), out string? problem));
        Assert.NotNull(problem);
        Assert.Equal(32, geo.Utm.Zone);
    }

    [Theory]
    [InlineData(1, (byte)1)]
    [InlineData(0, (byte)1)]
    [InlineData(3, (byte)1)]
    [InlineData(2, (byte)0)]
    [InlineData(2, (byte)3)]
    [InlineData(2, (byte)255)]
    public void GeoFrame_RefusesV1AndUnknownFrame(int version, byte frame)
    {
        var geo = new GeoFrame();
        var refused = new HrotOrigin(version, frame, 50.0, 14.0, 300.0, 0, false, 5);
        Assert.False(geo.TryApply(refused, out string? problem));
        Assert.False(string.IsNullOrEmpty(problem));
        Assert.False(geo.IsValid);
        Assert.False(geo.IsLocalOnly);
        Assert.Equal(0, geo.Generation);
        Assert.Null(geo.Origin);

        // a refusal leaves the frame that was there
        Assert.True(geo.TryApply(HrotOrigin.Utm(51.0, 15.0, 100.0, 6), out _));
        Assert.False(geo.TryApply(refused with { Generation = 7 }, out problem));
        Assert.False(string.IsNullOrEmpty(problem));
        Assert.True(geo.IsValid);
        Assert.Equal(6, geo.Generation);
        Assert.Equal(51.0, geo.Origin!.Value.LatitudeDeg);
    }

    [Fact]
    public void GeoFrame_FrameNone_NotValid()
    {
        var geo = new GeoFrame();
        Assert.True(geo.TryApply(HrotOrigin.None(1), out string? problem));
        Assert.Null(problem);
        Assert.False(geo.IsValid);
        Assert.True(geo.IsLocalOnly);
        Assert.Equal(1, geo.Generation);
        Assert.Throws<InvalidOperationException>(() => geo.PointToEcef(new HrotVec3(1, 2, 3)));
        Assert.Throws<InvalidOperationException>(() => _ = geo.Utm);
        Assert.False(geo.TryApply(HrotOrigin.None(1), out problem)); // unchanged
        Assert.Null(problem);

        Assert.True(geo.TryApply(HrotOrigin.Utm(50.0, 14.0, 300.0, 2), out _));
        Assert.True(geo.IsValid);
        Assert.False(geo.IsLocalOnly);
        Assert.Equal(2, geo.Generation);

        // and back: the terrain changed to a local-only one
        Assert.True(geo.TryApply(HrotOrigin.None(3), out _));
        Assert.False(geo.IsValid);
        Assert.True(geo.IsLocalOnly);
        Assert.Equal(3, geo.Generation);
    }

    /// <summary>A random unit quaternion (x, y, z, w) with w &gt;= 0.</summary>
    public static HrotQuat RandomQuaternion(Random random)
    {
        double x, y, z, w, n;
        do
        {
            x = Gaussian(random);
            y = Gaussian(random);
            z = Gaussian(random);
            w = Gaussian(random);
            n = Math.Sqrt(x * x + y * y + z * z + w * w);
        }
        while (n < 1e-6);
        double s = w < 0 ? -1.0 / n : 1.0 / n;
        return new HrotQuat(x * s, y * s, z * s, w * s);
    }

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble());

    // ---- catalogs ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Dis_types_pack_into_64_bits_and_unpack()
    {
        var type = new DisTypeCode(255, 7, 54321, 9, 8, 7, 6);
        Assert.Equal(type, DisTypeCode.Unpack(type.Pack()));
        Assert.Equal(0L, default(DisTypeCode).Pack());
        Assert.Equal(1L << 56 | 2L << 48 | 3L << 32 | 4L << 24 | 5L << 16 | 6L << 8 | 7L, new DisTypeCode(1, 2, 3, 4, 5, 6, 7).Pack());
        Assert.True(Tank.IsComplete);
        Assert.False(new DisTypeCode(1, 1, 0, 1, 0, 0, 0).IsComplete);
        Assert.False(DisTypeCode.IsCompletePacked(0));
    }


    [Fact]
    public void A_template_type_without_a_country_is_usable_and_gets_the_default_country()
    {
        long soldier = new DisTypeCode(3, 1, 0, 1, 0, 0, 0).Pack(); // kind, domain, category; no country
        Assert.False(DisTypeCode.IsCompletePacked(soldier));
        Assert.True(DisTypeCode.IsUsablePacked(soldier));

        Assert.True(new EntityTypeCatalog().TryResolveDisType(2002, soldier, out long packed));
        Assert.Equal(soldier, packed); // default country 0: "other"

        Assert.True(new EntityTypeCatalog(defaultDisCountry: 225).TryResolveDisType(2002, soldier, out packed));
        Assert.Equal(new DisTypeCode(3, 1, 225, 1, 0, 0, 0).Pack(), packed);

        // no kind, no domain or no category: still not usable (the table decides)
        Assert.False(new EntityTypeCatalog().TryResolveDisType(99999, new DisTypeCode(5, 0, 0, 0, 0, 0, 0).Pack(), out _));
    }
    [Fact]
    public void The_type_the_hrot_pack_provides_comes_before_the_catalogue()
    {
        var catalog = new EntityTypeCatalog();
        long provided = new DisTypeCode(1, 2, 3, 4, 5, 6, 7).Pack();
        Assert.True(catalog.TryResolveDisType(100, provided, out long packed));
        Assert.Equal(provided, packed);

        Assert.True(catalog.TryResolveDisType(100, 0, out packed)); // incomplete -> the table
        Assert.Equal(Tank.Pack(), packed);
        long incomplete = new DisTypeCode(1, 1, 0, 0, 0, 0, 0).Pack();
        Assert.True(catalog.TryResolveDisType(100, incomplete, out packed));
        Assert.Equal(Tank.Pack(), packed);
        Assert.False(catalog.TryResolveDisType(99999, 0, out _));
    }

    [Fact]
    public void A_dis_type_finds_its_tkb_type_exactly_or_by_the_longest_prefix()
    {
        var catalog = new EntityTypeCatalog(defaultTkbForUnknown: 77);
        Assert.Equal(77, catalog.DefaultTkb);
        Assert.True(catalog.TryDisToTkb(Tank.Pack(), out long tkb));
        Assert.Equal(100, tkb);
        // same category, other specific/extra: still the tank
        Assert.True(catalog.TryDisToTkb((Tank with { Specific = 9, Extra = 1 }).Pack(), out tkb));
        Assert.Equal(100, tkb);
        // another subcategory of the same category: still matched at category level (the only 1/1/225/1 entry)
        Assert.True(catalog.TryDisToTkb((Tank with { Subcategory = 4 }).Pack(), out tkb));
        Assert.Equal(100, tkb);
        // a different country: no match
        Assert.False(catalog.TryDisToTkb((Tank with { Country = 1 }).Pack(), out _));
        Assert.False(catalog.TryDisToTkb(0, out _));
        // each catalogued TKB type maps to a DIS type that maps back to it
        foreach (long entry in catalog.TkbTypes)
        {
            Assert.True(catalog.TryTkbToDis(entry, out long dis));
            Assert.True(catalog.TryDisToTkb(dis, out long back));
            Assert.Equal(entry, back);
        }
    }

    [Fact]
    public void An_unknown_type_is_noted_once()
    {
        var catalog = new EntityTypeCatalog();
        Assert.True(catalog.NoteUnknownTkb(5));
        Assert.False(catalog.NoteUnknownTkb(5));
        Assert.True(catalog.NoteUnknownTkb(6));
        Assert.True(catalog.NoteUnknownDis(5));
        Assert.False(catalog.NoteUnknownDis(5));
        var munitions = new MunitionCatalog();
        Assert.True(munitions.NoteUnknownTkb(9));
        Assert.False(munitions.NoteUnknownTkb(9));
        Assert.True(munitions.NoteUnknownDis(9));
        Assert.False(munitions.NoteUnknownDis(9));
    }

    [Fact]
    public void A_munition_is_described_by_the_provided_type_or_the_catalogue()
    {
        var munitions = new MunitionCatalog();
        var known = munitions.Describe(501, 0, out bool isKnown);
        Assert.True(isKnown);
        Assert.NotEqual(0, known.DisType);
        Assert.NotEqual(0, known.Warhead);
        Assert.True(munitions.TryGet(501, out var entry));
        Assert.Equal(entry.DisType, known.DisType);

        long provided = new DisTypeCode(2, 9, 225, 3, 3, 3, 0).Pack();
        Assert.Equal(provided, munitions.Describe(501, provided, out _).DisType);
        Assert.Equal(known.Warhead, munitions.Describe(501, provided, out _).Warhead);

        var unknown = munitions.Describe(9999, 0, out isKnown);
        Assert.False(isKnown);
        Assert.Equal(0, unknown.DisType);
        Assert.Equal(0, unknown.Warhead);
        Assert.Equal(0, unknown.Fuse);
        Assert.Equal(provided, munitions.Describe(9999, provided, out _).DisType);

        Assert.True(munitions.TryDisToTkb(entry.DisType, out long tkb));
        Assert.Equal(501, tkb);
        Assert.False(munitions.TryDisToTkb(Tank.Pack(), out _)); // a tank is not a munition
        foreach (long t in munitions.TkbTypes)
        {
            Assert.True(munitions.TryDisToTkb(munitions.Describe(t, 0, out _).DisType, out long back));
            Assert.Equal(t, back);
        }
    }

    // ---- damage mapping -----------------------------------------------------------------------------------------------

    [Fact]
    public void Health_maps_to_damage_by_the_documented_thresholds()
    {
        var m = new DamageMapping(new SharedConfiguration());
        Assert.Equal(DamageMapping.None, m.DamageOf(100, 100));
        Assert.Equal(DamageMapping.None, m.DamageOf(120, 100));
        Assert.Equal(DamageMapping.Slight, m.DamageOf(99.9, 100));
        Assert.Equal(DamageMapping.Slight, m.DamageOf(50, 100));
        Assert.Equal(DamageMapping.Moderate, m.DamageOf(49.9, 100));
        Assert.Equal(DamageMapping.Moderate, m.DamageOf(0.1, 100));
        Assert.Equal(DamageMapping.Destroyed, m.DamageOf(0, 100));
        Assert.Equal(DamageMapping.Destroyed, m.DamageOf(-5, 100));
        Assert.Equal(DamageMapping.None, m.DamageOf(0, 0)); // no maximum: undamaged
        Assert.Equal(100.0, m.MaxHealth);
    }

    [Fact]
    public void Damage_maps_to_health_as_100_75_40_0_percent_of_the_maximum()
    {
        var m = new DamageMapping(new SharedConfiguration { EntityMaxHealth = 200 });
        Assert.Equal(200.0, m.HealthOf(DamageMapping.None));
        Assert.Equal(150.0, m.HealthOf(DamageMapping.Slight));
        Assert.Equal(80.0, m.HealthOf(DamageMapping.Moderate));
        Assert.Equal(0.0, m.HealthOf(DamageMapping.Destroyed));
        Assert.Equal(0.0, m.HealthOf(99)); // anything else counts as destroyed
    }

    [Fact]
    public void The_health_of_a_damage_state_maps_back_to_it_and_falls_with_the_damage_for_any_thresholds()
    {
        var random = new Random(99);
        for (int i = 0; i < 500; i++)
        {
            double moderate = random.NextDouble() * 0.9;
            double slight = moderate + 0.01 + random.NextDouble() * (0.98 - moderate);
            var shared = new SharedConfiguration
            {
                EntityMaxHealth = 1 + random.NextDouble() * 1000,
                DamageThresholds = new DamageThresholds { Slight = slight, Moderate = moderate },
            };
            var m = new DamageMapping(shared);
            double previous = double.MaxValue;
            for (int damage = DamageMapping.None; damage <= DamageMapping.Destroyed; damage++)
            {
                double health = m.HealthOf(damage);
                Assert.Equal(damage, m.DamageOf(health, m.MaxHealth));
                Assert.True(health < previous || (damage == DamageMapping.None), $"health must fall with the damage: {damage} thresholds {slight}/{moderate}");
                previous = health;
            }

            // monotonic: more health never means more damage
            int last = DamageMapping.Destroyed;
            for (double fraction = 0.0; fraction <= 1.2; fraction += 0.013)
            {
                int d = m.DamageOf(fraction * m.MaxHealth, m.MaxHealth);
                Assert.True(d <= last);
                last = d;
            }
        }
    }

    [Fact]
    public void Thresholds_follow_the_shared_section()
    {
        var m = new DamageMapping(new SharedConfiguration { DamageThresholds = new DamageThresholds { Slight = 0.8, Moderate = 0.3 } });
        Assert.Equal(DamageMapping.Slight, m.DamageOf(80, 100));
        Assert.Equal(DamageMapping.Moderate, m.DamageOf(79, 100));
        Assert.Equal(DamageMapping.Moderate, m.DamageOf(31, 100));
        Assert.Equal(DamageMapping.Destroyed, m.DamageOf(30, 100));
    }

    // ---- event ids and the correlator ---------------------------------------------------------------------------------

    [Fact]
    public void Event_ids_count_from_one_and_wrap_after_65535()
    {
        var ids = new DisEventIds(1, 3001);
        Assert.Equal(new DisEventId(1, 3001, 1), ids.Next());
        Assert.Equal(new DisEventId(1, 3001, 2), ids.Next());
        DisEventId last = default;
        for (int i = 3; i <= 65535; i++)
        {
            last = ids.Next();
        }

        Assert.Equal(65535, last.Event);
        Assert.Equal(new DisEventId(1, 3001, 1), ids.Next());
    }

    [Fact]
    public void A_detonation_gets_the_id_of_the_oldest_fire_of_the_same_shooter_and_munition_within_30_seconds()
    {
        var c = new FireCorrelator();
        var a = new DisEventId(1, 1, 1);
        var b = new DisEventId(1, 1, 2);
        c.OnFire(10, 501, a, 100.0);
        c.OnFire(10, 501, b, 101.0);
        c.OnFire(11, 501, new DisEventId(1, 1, 3), 100.0);
        Assert.Equal(3, c.Pending);
        Assert.Null(c.MatchDetonation(10, 502, 102.0)); // other munition
        Assert.Null(c.MatchDetonation(12, 501, 102.0)); // other shooter
        Assert.Equal(a, c.MatchDetonation(10, 501, 102.0)); // first in, first out
        Assert.Equal(b, c.MatchDetonation(10, 501, 103.0));
        Assert.Null(c.MatchDetonation(10, 501, 104.0)); // each fire links once
    }

    [Fact]
    public void The_correlation_window_is_30_seconds()
    {
        var c = new FireCorrelator();
        var id = new DisEventId(1, 1, 1);
        c.OnFire(1, 1, id, 0.0);
        Assert.Equal(id, c.MatchDetonation(1, 1, 30.0));
        c.OnFire(1, 1, id, 0.0);
        Assert.Null(c.MatchDetonation(1, 1, 30.001));
        Assert.Equal(0, c.Pending); // expired fires are forgotten
        c.OnFire(1, 1, id, 0.0);
        Assert.Equal(id, c.MatchDetonation(1, 1, 29.9));
        Assert.Equal(30.0, FireCorrelator.Window);
    }

    // ---- id allocator and articulation ----------------------------------------------------------------------------------

    [Fact]
    public void The_range_allocator_hands_out_distinct_ids_and_takes_them_back()
    {
        var allocator = new HrotIdRangeAllocator(1000, 1002);
        Assert.True(allocator.TryAllocate(out int a));
        Assert.True(allocator.TryAllocate(out int b));
        Assert.True(allocator.TryAllocate(out int c));
        Assert.Equal(new int[] { 1000, 1001, 1002 }, new[] { a, b, c }.Order().ToArray());
        Assert.False(allocator.TryAllocate(out _));
        Assert.Equal(3UL, allocator.InUse);
        allocator.Release(in b);
        Assert.Equal(1UL, allocator.Available);
        Assert.True(allocator.TryAllocate(out int d));
        Assert.Equal(b, d);
        int outside = 5;
        allocator.Release(in outside); // ignored
        Assert.Equal(3UL, allocator.InUse);
        Assert.Throws<ArgumentException>(() => new HrotIdRangeAllocator(5, 4));
    }

    [Fact]
    public void Articulated_parts_are_replaced_by_id_ordered_and_compared_by_value()
    {
        var one = ArticulationParts.Empty.With(new ArticulationPart(2, 0.1, 0.2)).With(new ArticulationPart(1, 0.3, 0.4));
        Assert.Equal([1, 2], one.Parts.Select(p => p.PartId).ToArray());
        var replaced = one.With(new ArticulationPart(2, 0.5, 0.6));
        Assert.Equal(2, replaced.Parts.Count);
        Assert.Equal(0.5, replaced.Parts[1].Azimuth);
        Assert.Equal(one, ArticulationParts.Empty.With(new ArticulationPart(1, 0.3, 0.4)).With(new ArticulationPart(2, 0.1, 0.2)));
        Assert.NotEqual(one, replaced);
        Assert.Empty(ArticulationParts.Empty.Parts);
    }
}
