using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>Message builders and the analytic reference values of the end-to-end tests.</summary>
internal static class T
{
    public const ulong Hawk = 42;
    public const ulong Viper = 43;
    public const ulong Gate = 44;

    public static NedEntityMaster Master(ulong id, string name, int tkb = 200, NedForce force = NedForce.Friendly) =>
        new(id, name, tkb, force);

    public static NedGeoSpatial Geo(ulong id, double lat = 50.0, double lon = 14.0, double alt = 300.0, double heading = 90.0,
        double pitch = 0.0, double roll = 0.0, double speed = 100.0) =>
        new(id, lat, lon, alt, heading, pitch, roll, speed);

    /// <summary>The ECEF position of a NED geo-spatial message.</summary>
    public static Vec3 Position(in NedGeoSpatial g)
    {
        NedRprConversions.GeoToPivot(in g, out var p, out _, out _);
        return p;
    }

    public static Vec3 VelocityOf(in NedGeoSpatial g)
    {
        NedRprConversions.GeoToPivot(in g, out _, out _, out var v);
        return v;
    }

    public static Vec3 OrientationOf(in NedGeoSpatial g)
    {
        NedRprConversions.GeoToPivot(in g, out _, out var o, out _);
        return o;
    }

    /// <summary>The analytic constant-velocity (FPW) position after <paramref name="dt"/> seconds.</summary>
    public static Vec3 Fpw(in NedGeoSpatial g, double dt)
    {
        var p = Position(in g);
        var v = VelocityOf(in g);
        return new Vec3(p.X + v.X * dt, p.Y + v.Y * dt, p.Z + v.Z * dt);
    }

    public static double Distance(Vec3 a, Vec3 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public static Vec3 ToVec3(WorldLocation w) => new(w.X, w.Y, w.Z);

    public static DisEntityType Dis(int tkb)
    {
        var catalog = new TypeCatalog();
        Assert.True(catalog.TryToDis(tkb, out var dis));
        return dis;
    }

    /// <summary>A complete RPR update of a new RPR-origin entity at the given geodetic state.</summary>
    public static RprObjectUpdate RprFull(uint handle, string marking, int tkb, double lat, double lon, double alt, double heading,
        double speed, ushort entityNumber, RprForceId force = RprForceId.Opposing)
    {
        NedRprConversions.GeoToPivot(lat, lon, alt, heading, 0, 0, speed, out var p, out var o, out var v);
        return new RprObjectUpdate(new RprObjectHandle(handle))
        {
            Marking = marking,
            EntityType = Dis(tkb),
            ForceId = force,
            EntityIdentifier = new DisEntityId(9, 9, entityNumber),
            WorldLocation = NedRprConversions.ToWorldLocation(p),
            Orientation = NedRprConversions.ToRprOrientation(o),
            VelocityVector = NedRprConversions.ToRprVector(v),
            DeadReckoningAlgorithm = (byte)(speed > 0 ? 2 : 1),
        };
    }

    /// <summary>Publishes a NED entity to RPR (master, geo-spatial, handle) and returns its repository record.</summary>
    public static EntityRecord PublishNed(GatewayHarness h, ulong id, string name, int tkb = 200, NedGeoSpatial? geo = null)
    {
        h.NedStep(Master(id, name, tkb));
        h.NedStep(geo ?? Geo(id));
        h.Steps(2); // the RTI answers one tick after the request
        var entity = h.EntityOfNed(id);
        Assert.NotNull(entity);
        Assert.True(h.RprHandles.Contains(entity!.Id), "the entity has no RPR handle after publication");
        return entity;
    }
}
