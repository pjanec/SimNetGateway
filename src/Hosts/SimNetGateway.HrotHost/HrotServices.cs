using Fdp.Interfaces;
using Fdp.Toolkit.Orchestration;
using Fdp.Toolkit.Replication.Services;

namespace SimNetGateway.HrotHost;

/// <summary>
/// What the pack reads from the HROT node, as delegates (the capability fills it from <c>HrotNodeContext</c>; tests fill it by hand):
/// the cluster state, the geo reference, the id allocator, the network entity map and the TKB database.
/// </summary>
public sealed class HrotServices
{
    /// <summary>The node's cluster state (<c>ClusterSlave.LocalClusterState</c>).</summary>
    public Func<ClusterState> State { get; init; } = () => ClusterState.OperatingLive;

    /// <summary>HROT's geo reference: null = no geographic transform at all (a bare node); see <see cref="HrotGeoRef"/>.</summary>
    public Func<HrotGeoRef?> GeoReference { get; init; } = () => null;

    /// <summary>The node id allocator (<c>IdAllocator.AllocateId</c>); null = the gateway's built-in range.</summary>
    public Func<int>? AllocateId { get; init; }

    /// <summary>The node's network entity map: replicas answer to their network id.</summary>
    public NetworkEntityMap? EntityMap { get; init; }

    /// <summary>The TKB database, when loaded (it may arrive after composition).</summary>
    public Func<ITkbDatabase?> Tkb { get; init; } = () => null;

    /// <summary>The local node id (the replica's <c>NetworkAuthority.LocalNodeId</c>).</summary>
    public int LocalNodeId { get; init; }
}

/// <summary>Counters of the pack (diagnostics and tests).</summary>
public sealed class PackCounters
{
    public int StatesSent { get; internal set; }
    public int RemovalsSent { get; internal set; }
    public int FiresSent { get; internal set; }
    public int DetonationsSent { get; internal set; }
    public int TurretPosesSent { get; internal set; }
    public int ReplicasCreated { get; internal set; }
    public int ReplicasRemoved { get; internal set; }
    public int FiresInjected { get; internal set; }
    public int DetonationsInjected { get; internal set; }
    public int OwnershipMessagesIgnored { get; internal set; }
    public int TimeRequestsIgnored { get; internal set; }
    public int DroppedInReplay { get; internal set; }
}

/// <summary>
/// The geo reference of the node's local frame as HROT's <c>IGeographicTransform</c> states it: <see cref="HasFrame"/> false = a local-only terrain (no
/// place on the Earth); else the origin (degrees, metres above the ellipsoid) and the UTM zone override (0 = the origin's own zone;
/// <see cref="SouthOverride"/> is meaningful only with a zone override).
/// </summary>
public readonly record struct HrotGeoRef(bool HasFrame, double LatDeg, double LonDeg, double AltM, int ZoneOverride, bool SouthOverride)
{
    /// <summary>
    /// Reads the geo reference of a transform: null for no transform, <c>HasFrame = false</c> for a local-only frame, else the origin and the zone
    /// override by HROT's own rule (<c>RecorderTickSystem.StampWorld</c>): the override is written only when the frame's zone or hemisphere is not the
    /// origin's natural one.
    /// </summary>
    public static HrotGeoRef? From(Fdp.Modules.Geographic.IGeographicTransform? geo)
    {
        if (geo is null)
        {
            return null;
        }

        if (!geo.HasFrame)
        {
            return new HrotGeoRef(false, 0.0, 0.0, 0.0, 0, false);
        }

        var (lat, lon, alt) = geo.Origin;
        int zone = 0;
        bool south = false;
        if (geo.UtmZone is { } z && (z.Zone != Fdp.Modules.Geographic.Transforms.UtmTransform.ZoneOf(lon) || z.South != (lat < 0)))
        {
            zone = z.Zone;
            south = z.South;
        }

        return new HrotGeoRef(true, lat, lon, alt, zone, south);
    }
}
