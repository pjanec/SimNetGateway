using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Replication.Components;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>The turret gather of the pack: HROT's turret part children as <c>HrotTurretPose</c>, own entities only, on a change of more than 0.5 degrees or every second.</summary>
public class TurretGatherTests
{
    private static PackHarness Live()
    {
        var h = new PackHarness(dis: true);
        if (!h.World.IsComponentTypeRegistered<PartMetadata>())
        {
            h.World.RegisterComponent<PartMetadata>();
        }

        if (!h.World.IsComponentTypeRegistered<TurretPose>())
        {
            h.World.RegisterComponent<TurretPose>();
        }

        h.Steps(3);
        return h;
    }

    private static Entity Turret(PackHarness h, Entity parent, int index, double azimuthDeg, double elevationDeg)
    {
        var part = TurretParts.Ensure(h.World, parent, index);
        Assert.False(part.IsNull);
        Pose(h, part, azimuthDeg, elevationDeg);
        return part;
    }

    private static void Pose(PackHarness h, Entity part, double azimuthDeg, double elevationDeg) =>
        h.World.SetComponent(part, new TurretPose { Azimuth = Angle.FromDegrees((float)azimuthDeg), Elevation = Angle.FromDegrees((float)elevationDeg) });

    private static ArticulationParts? PartsOf(PackHarness h, int hrotId)
    {
        var ids = (IdentityMap<int>)h.Engine.Identities.Find("hrot.entity")!;
        var schema = new PivotSchema(h.Engine.Attributes, h.Engine.EventTypes);
        return ids.TryResolve(hrotId, out var id) && h.Engine.State.Find(id) is { } record && record.TryGet(schema.ArticulationParts, out var value)
            ? value.AsObject<ArticulationParts>()
            : null;
    }

    [Fact]
    public void TurretGather_EmitsOnChange()
    {
        using var h = Live();
        var tank = h.AddHrotEntity(2001, new Vector3(10, 20, 0));
        var part = Turret(h, tank, 0, 30, 5);
        h.Steps(3);
        Assert.Equal(1, h.Adapter.Counters.TurretPosesSent);
        var parts = PartsOf(h, 2001)!;
        var p = Assert.Single(parts.Parts);
        Assert.Equal(2000, p.PartId);
        Assert.Equal(30.0 * Math.PI / 180.0, p.Azimuth, 4); // radians, counter-clockwise: as HROT has them
        Assert.Equal(5.0 * Math.PI / 180.0, p.Elevation, 4);

        // under half a degree: nothing
        Pose(h, part, 30.3, 5.2);
        h.Steps(5);
        Assert.Equal(1, h.Adapter.Counters.TurretPosesSent);

        // over half a degree, in either angle
        Pose(h, part, 31, 5.2);
        h.Steps(3);
        Assert.Equal(2, h.Adapter.Counters.TurretPosesSent);
        Pose(h, part, 31, 6);
        h.Steps(3);
        Assert.Equal(3, h.Adapter.Counters.TurretPosesSent);
        Assert.Equal(6.0 * Math.PI / 180.0, PartsOf(h, 2001)!.Parts[0].Elevation, 4);

        // a still turret is repeated once a second
        h.Steps(70);
        Assert.True(h.Adapter.Counters.TurretPosesSent >= 4);
    }

    [Fact]
    public void TurretGather_TwoTurretsAreTwoParts()
    {
        using var h = Live();
        var tank = h.AddHrotEntity(2001, new Vector3(10, 20, 0));
        Turret(h, tank, 0, 10, 1);
        Turret(h, tank, 1, -45, 2);
        h.Steps(3);
        Assert.Equal(new[] { 2000, 2001 }, PartsOf(h, 2001)!.Parts.Select(x => x.PartId).ToArray());
        Assert.Equal(-45.0 * Math.PI / 180.0, PartsOf(h, 2001)!.Parts[1].Azimuth, 4);
    }

    [Fact]
    public void TurretGather_OwnEntitiesOnly_ReplicasAreNotGathered()
    {
        using var h = Live();
        var replica = h.World.CreateEntity();
        h.World.AddComponent(replica, new NetworkIdentity(9001));
        h.World.AddComponent(replica, new ReplicationRoute(ReplicationRoute.ExternalGateway));
        Turret(h, replica, 0, 40, 4);
        var own = h.AddHrotEntity(2001, new Vector3(1, 1, 0));
        h.Steps(5);
        Assert.Equal(0, h.Adapter.Counters.TurretPosesSent);

        Turret(h, own, 0, 20, 2);
        h.Steps(3);
        Assert.Equal(1, h.Adapter.Counters.TurretPosesSent);
        Assert.Null(PartsOf(h, 9001));
    }

    [Fact]
    public void TurretGather_WorldWithoutTurretComponents_IsQuiet()
    {
        using var h = new PackHarness(dis: true);
        h.AddHrotEntity(2001, new Vector3(1, 1, 0));
        h.Steps(5);
        Assert.Equal(0, h.Adapter.Counters.TurretPosesSent);
    }
}
