using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Physics.Components;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Terrain;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>damage-record entities (building breaches, craters) are not entities to publish.</summary>
public class RecordEntityTests
{
    private static Entity Record<T>(PackHarness h, int id)
        where T : unmanaged
    {
        var e = h.World.CreateEntity();
        h.World.AddComponent(e, new NetworkIdentity(id));
        h.World.AddComponent(e, new TkbIdentity { TkbType = new TkbTypeId(7203) });
        h.World.AddComponent(e, new SimTransform { Position = new Vector3(5, 5, 0), Rotation = Quaternion.Identity });
        h.World.AddComponent(e, default(T));
        return e;
    }

    [Fact]
    public void RecordEntities_AreNotGathered()
    {
        using var h = new PackHarness(dis: true);
        h.Steps(3);
        h.AddHrotEntity(2001, new Vector3(1, 1, 0));
        Record<BuildingDamage>(h, 2002);
        Record<GroundDamage>(h, 2003);
        h.Steps(10);
        Assert.Equal(new[] { 2001 }, h.Adapter.PublishedIds.ToArray());
    }

    [Fact]
    public void ProcessTeardown_DoesNotTouchTheWorld_ReplicasStayUntilABoundarySweepsThem()
    {
        using var h = new PackHarness(dis: true);
        h.Steps(3);
        h.DisEntity(7, 10, 10, marking: "PEER");
        h.Steps(6);
        Assert.Single(h.RoutedEntities());

        h.System.Dispose();   // the capability's teardown: on a threaded node it runs off the simulation thread, so it must not read the world

        Assert.Single(h.RoutedEntities());   // nothing was swept (a read of the world by the teardown would have removed it)
        h.World.Dispose();                    // and a disposed world is no problem either
    }
}
