using Fdp.Core;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// One entity of another protocol as a replica in the HROT world: the entity, its network id (the gateway's <c>hrot.entity</c> id) and the dead-reckoning
/// snapshot the poser extrapolates every frame. It is kept in the adapter's book instead of a component, because the
/// ClusterRunner enforces explicit component ids (<c>FdpConfig.EnforceExplicitComponentIds</c>) and a foreign extension owns no id block.
/// </summary>
public sealed class DisReplica
{
    public DisReplica(int id, Entity entity, in HrotDrSnapshot state)
    {
        Id = id;
        Entity = entity;
        State = state;
    }

    public int Id { get; }

    public Entity Entity { get; }

    public HrotDrSnapshot State { get; set; }
}
