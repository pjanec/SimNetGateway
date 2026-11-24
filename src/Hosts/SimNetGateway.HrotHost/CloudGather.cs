using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Physics.Components;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// DD-033 (T-078): the gas and smoke clouds. A cloud is a replicated entity with <c>AreaEffectCloud</c> and a <c>SimTransform</c> (its cylinder's base) and no body, so the adapter's entity loop hands it
/// here INSTEAD of publishing it as an <c>Entity</c>. HROT drifts it by the ground wind once a second (<c>AreaEffectCloudSystem</c>: position += wind x elapsed, no velocity component, radius and height
/// never change), so the gateway sends the position at the time of sending and the wind it drifts on (<c>AreaEffectClouds.DriftWind</c>, the one function HROT's drift and its gas-footprint prediction both
/// read; zero while the cloud sits on its venting canister) and the client moves it. A new sample goes out when the cloud appears, when its kind, drift, size, transmittance or life changes, or when
/// its position leaves the straight line the last sample predicted (a cloud moved some other way). A steady cloud allocates nothing and sends nothing; a cloud that is gone is sent ended once. Main thread only.
/// </summary>
internal sealed class CloudGather
{
    private sealed class Sent(HrotCloud message)
    {
        public HrotCloud Message = message;
        public bool Seen = true;
    }

    private readonly Dictionary<int, Sent> _sent = new();
    private readonly List<int> _gone = new();

    public void Clear() => _sent.Clear();

    /// <summary>Is <paramref name="e"/> a cloud (it is then not an entity of its own)?</summary>
    public static bool IsCloud(EntityRepository world, Entity e) => world.IsComponentTypeRegistered<AreaEffectCloud>() && world.HasComponent<AreaEffectCloud>(e);

    public void Begin()
    {
        foreach (var s in _sent.Values)
        {
            s.Seen = false;
        }
    }

    public void Gather(EntityRepository world, Entity e, int id, double simTime, InMemoryInbound inbound)
    {
        if (!world.HasComponent<SimTransform>(e))
        {
            return;
        }

        ref readonly var cloud = ref world.GetComponentRO<AreaEffectCloud>(e);
        var at = world.GetComponentRO<SimTransform>(e).Position;
        var wind = SitsOnItsSource(world, in cloud) ? Vector2.Zero : AreaEffectClouds.DriftWind(world, at, cloud.Height);
        var now = new HrotCloud(id, KindOf(cloud.Kind), simTime, new HrotVec3(at.X, at.Y, at.Z), new HrotVec3(wind.X, wind.Y, 0d), cloud.Radius, cloud.Height, cloud.SightTransmittance,
            cloud.BornAtSimSeconds.Seconds, cloud.Seconds.Seconds);
        if (_sent.TryGetValue(id, out var sent))
        {
            sent.Seen = true;
            if (!Differs(in sent.Message, in now))
            {
                return;
            }

            sent.Message = now;
        }
        else
        {
            _sent[id] = new Sent(now);
        }

        inbound.Enqueue(in now);
    }

    /// <summary>Clouds not seen this frame ended (or left the world): sent ended once.</summary>
    public void End(InMemoryInbound inbound)
    {
        _gone.Clear();
        foreach (var (id, s) in _sent)
        {
            if (!s.Seen)
            {
                _gone.Add(id);
            }
        }

        foreach (int id in _gone)
        {
            _sent.Remove(id);
            inbound.Enqueue(new HrotCloudEnded(id));
        }
    }

    private static bool Differs(in HrotCloud sent, in HrotCloud now)
    {
        if (sent.Kind != now.Kind || sent.Drift != now.Drift || sent.Radius != now.Radius || sent.Height != now.Height || sent.SightTransmittance != now.SightTransmittance
            || sent.BornAt != now.BornAt || sent.Seconds != now.Seconds)
        {
            return true;
        }

        // the position HROT steps once a second stays on the line the last sample predicted, within one step of lag; further off, something else moved the cloud
        double dt = now.SimTime - sent.SimTime;
        double px = sent.Centre.X + sent.Drift.X * dt - now.Centre.X, py = sent.Centre.Y + sent.Drift.Y * dt - now.Centre.Y, pz = sent.Centre.Z - now.Centre.Z;
        double speed = Math.Sqrt(sent.Drift.X * sent.Drift.X + sent.Drift.Y * sent.Drift.Y);
        double tolerance = 1d + 1.5d * speed;
        return px * px + py * py + pz * pz > tolerance * tolerance;
    }

    private static HrotCloudKind KindOf(CloudKind kind) => kind switch
    {
        CloudKind.TearGas => HrotCloudKind.TearGas,
        CloudKind.Smoke => HrotCloudKind.Smoke,
        _ => HrotCloudKind.None,
    };

    /// <summary>
    /// Whether the cloud still sits on its venting canister (so HROT does not drift it): the read-only half of <c>AreaEffectCloudSystem.SitsOnItsSource</c> (the system also clears the field once the source is
    /// known gone; the gateway only reads). A source that resolves holds it; one not yet seen here holds it too, unless this world has no entity map at all.
    /// </summary>
    private static bool SitsOnItsSource(EntityRepository world, in AreaEffectCloud cloud)
    {
        int source = cloud.SourceNetworkId;
        if (source <= 0)
        {
            return false;
        }

        if (!Fdp.Toolkit.Replication.Services.NetworkIdResolver.ResolveNetworkId(world, source).IsNull)
        {
            return true;
        }

        bool mapless = !world.HasSingletonManaged<Fdp.Toolkit.Replication.Services.NetworkEntityMap>();
        return !mapless && !Fdp.Toolkit.Replication.Services.NetworkIdResolver.IsKnownDeadAnchor(world, source);
    }
}
