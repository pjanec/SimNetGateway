using System.Numerics;
using Fdp.Modules.Geographic.Transforms;
using SimNetGateway.Common.Geo;
using SimNetGateway.Protocol.Dis7;
using SimNetGateway.Protocol.Hrot;
using DISnet;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>HROT's geo reference (IGeographicTransform) into the gateway's geo reference v2.</summary>
public class GeoRefTests
{
    [Fact]
    public void GeoRef_FromUtmTransform_NoOverride()
    {
        var geo = UtmTransform.AtOrigin(50.0, 14.0, 300.0);
        Assert.Equal(new HrotGeoRef(true, 50.0, 14.0, 300.0, 0, false), HrotGeoRef.From(geo));

        // the zone and hemisphere stated explicitly but equal to the natural ones are no override
        var explicitNatural = UtmTransform.AtOrigin(-33.9, 151.2, 20.0, zoneOverride: 56, southOverride: true);
        Assert.Equal(new HrotGeoRef(true, -33.9, 151.2, 20.0, 0, false), HrotGeoRef.From(explicitNatural));

        using var h = new PackHarness(dis: true);
        h.GeoRef = HrotGeoRef.From(geo);
        h.Steps(3);
        Assert.Equal(HrotOrigin.Utm(50.0, 14.0, 300.0, 1), h.System.LastOrigin);
        Assert.Equal(1, h.System.OriginGeneration);
    }

    [Fact]
    public void GeoRef_FromUtmTransform_OverrideZone()
    {
        // 2 km east of the edge between zones 31 and 32, projected in the west neighbour
        var west = UtmTransform.AtOrigin(50.0, 6.0262, 10.0, zoneOverride: 31, southOverride: false);
        var reference = HrotGeoRef.From(west)!.Value;
        Assert.Equal(new HrotGeoRef(true, 50.0, 6.0262, 10.0, 31, false), reference);
        var south = UtmTransform.AtOrigin(-33.9, 151.2, 0.0, zoneOverride: 55, southOverride: true);
        Assert.Equal(new HrotGeoRef(true, -33.9, 151.2, 0.0, 55, true), HrotGeoRef.From(south));
        // the hemisphere alone differs from the latitude's sign
        var wrongHemisphere = UtmTransform.AtOrigin(5.0, 14.0, 0.0, zoneOverride: 33, southOverride: true);
        Assert.Equal(new HrotGeoRef(true, 5.0, 14.0, 0.0, 33, true), HrotGeoRef.From(wrongHemisphere));

        // through the system: the gateway's frame is the override zone's (a position 100 m east of the origin is where that zone's grid says)
        using var h = new PackHarness(dis: true);
        h.GeoRef = reference;
        h.Steps(3);
        Assert.Equal(HrotOrigin.Utm(50.0, 6.0262, 10.0, 1, 31, false), h.System.LastOrigin);
        h.AddHrotEntity(2001, new Vector3(100, 200, 5), name: "WEST");
        h.Steps(5);
        var state = h.Sent<EntityStatePdu>().First();
        var expected = UtmFrame.AtOrigin(50.0, 6.0262, 10.0, 31, false).PointToEcef(new SimNetGateway.Engine.Vec3(100, 200, 5));
        Assert.InRange(state.EntityLocation.X, expected.X - 1, expected.X + 1);
        Assert.InRange(state.EntityLocation.Y, expected.Y - 1, expected.Y + 1);
        Assert.InRange(state.EntityLocation.Z, expected.Z - 1, expected.Z + 1);
        // the origin's own zone (32) turns the grid by the convergence difference: it would put the point elsewhere
        var natural = UtmFrame.AtOrigin(50.0, 6.0262, 10.0, 0, false).PointToEcef(new SimNetGateway.Engine.Vec3(100, 200, 5));
        Assert.True((natural - expected).Length > 5.0); // the zones differ by the convergence (4.6 degrees), 18 m at 220 m
    }

    [Fact]
    public void GeoRef_LocalOnlyFrame_SendsFrameNone()
    {
        Assert.Equal(new HrotGeoRef(false, 0.0, 0.0, 0.0, 0, false), HrotGeoRef.From(new LocalOnlyFrame()));
        Assert.Null(HrotGeoRef.From(null));

        using var h = new PackHarness(dis: true);
        h.GeoRef = HrotGeoRef.From(new LocalOnlyFrame());
        h.Steps(3);
        var entity = h.AddHrotEntity(2001, new Vector3(100, 200, 5), name: "LOCAL");
        h.Steps(5);
        Assert.Equal(HrotOrigin.None(1), h.System.LastOrigin);
        Assert.Empty(h.Sent<EntityStatePdu>()); // nothing positional crosses

        h.GeoRef = HrotGeoRef.From(UtmTransform.AtOrigin(50.0, 14.0, 300.0));
        h.Move(entity, new Vector3(101, 200, 5)); // the next HROT state gives it a position
        h.Steps(5);
        Assert.Equal(HrotOrigin.Utm(50.0, 14.0, 300.0, 2), h.System.LastOrigin);
        Assert.NotEmpty(h.Sent<EntityStatePdu>());
    }

    [Fact]
    public void GeoRef_NoTransform_SendsNothing()
    {
        using var h = new PackHarness(dis: true);
        h.GeoRef = null;
        h.Steps(5);
        Assert.Null(h.System.LastOrigin);
        Assert.Equal(0, h.System.OriginGeneration);
    }

    [Fact]
    public void GeoRef_Change_BumpsGeneration()
    {
        using var h = new PackHarness(dis: true);
        var steps = new (HrotGeoRef? Reference, int Generation)[]
        {
            (new HrotGeoRef(true, 50.0, 14.0, 300.0, 0, false), 1),
            (new HrotGeoRef(true, 50.0, 14.0, 300.0, 0, false), 1),   // the same: no bump
            (new HrotGeoRef(true, 50.1, 14.0, 300.0, 0, false), 2),   // latitude
            (new HrotGeoRef(true, 50.1, 14.2, 300.0, 0, false), 3),   // longitude
            (new HrotGeoRef(true, 50.1, 14.2, 310.0, 0, false), 4),   // altitude
            (new HrotGeoRef(true, 50.1, 14.2, 310.0, 32, false), 5),  // zone override
            (new HrotGeoRef(true, 50.1, 14.2, 310.0, 32, true), 6),   // hemisphere of the override
            (new HrotGeoRef(false, 0.0, 0.0, 0.0, 0, false), 7),      // local-only
            (new HrotGeoRef(false, 0.0, 0.0, 0.0, 0, false), 7),
            (new HrotGeoRef(true, 50.0, 14.0, 300.0, 0, false), 8),   // and back
        };
        foreach (var (reference, generation) in steps)
        {
            h.GeoRef = reference;
            h.Steps(2);
            Assert.Equal(generation, h.System.OriginGeneration);
            Assert.Equal(generation, h.System.LastOrigin!.Value.Generation);
        }
    }
}
