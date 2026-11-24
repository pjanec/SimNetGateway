using Fdp.Core;
using Fdp.Toolkit.Behavior;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Replication.Components;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// DD-032 (T-077): what carries a NON-person entity - a picked-up pistol in a man's Hands seat, a stowed rifle on his Back, the door gun in a helicopter's station. HROT says it with
/// <c>IsEmbarkedTag</c> (the carrier, the seat); whether the seat is a stowed one is HROT's own <c>CarryStations.IsStowed</c>, the call its renderer makes. Sent when any of the three
/// changes; an entity that stops being carried (dropped, or it left the world) is sent once with carrier 0, which the side turns into a dispose. A person has his seat facts in
/// <c>PersonDetail</c> and never comes here (the caller passes only non-life-forms). Main thread only; a steady frame allocates nothing.
/// </summary>
internal sealed class CarriedGather
{
    private readonly Dictionary<int, HrotCarried> _sent = new();

    public void Clear() => _sent.Clear();

    /// <summary>Gathers one non-person entity: sends its carriage when it changed, or the end of it when it was carried and is not any more.</summary>
    public void Gather(EntityRepository world, Entity e, int id, InMemoryInbound inbound)
    {
        if (world.IsComponentTypeRegistered<IsEmbarkedTag>() && world.HasComponent<IsEmbarkedTag>(e))
        {
            var tag = world.GetComponentRO<IsEmbarkedTag>(e);
            var carrier = tag.VehicleEntity;
            int carrierId = !carrier.IsNull && world.IsAlive(carrier) && world.HasComponent<NetworkIdentity>(carrier) ? world.GetComponentRO<NetworkIdentity>(carrier).Value : 0;
            if (carrierId != 0)
            {
                var now = new HrotCarried(id, carrierId, tag.Seat, CarryStations.IsStowed(world, e));
                if (!_sent.TryGetValue(id, out var previous) || previous != now)
                {
                    _sent[id] = now;
                    inbound.Enqueue(in now);
                }

                return;
            }
        }

        Forget(id, inbound);
    }

    /// <summary>The entity is no longer carried or no longer published: tell the side once, if it was told it was carried.</summary>
    public void Forget(int id, InMemoryInbound inbound)
    {
        if (_sent.Remove(id))
        {
            inbound.Enqueue(new HrotCarried(id, 0, 0, false));
        }
    }
}
