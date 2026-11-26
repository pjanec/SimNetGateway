using Fdp.Core;
using Fdp.Toolkit.Terrain;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// DD-035 (T-084): the size of a roadblock. A static obstacle of the SOLID kind (a sandbag wall, a concrete block, a jersey barrier, a barrel, a crate, a parked car, a guard booth) is an entity the adapter already publishes
/// (<c>Entity</c> + <c>EntityState</c>; a bulldozer's push drains its health, <c>ObstacleRemovalSystem</c> removes it at zero, so the removal reaches the client). What the entity does not say is its per-instance
/// <c>ObstacleShape</c> (Length along the heading, Width across, Height; <c>[PerInstanceValue]</c>: a scenario may stretch a wall), which is what this gather sends as <c>HrotObstacle</c>. HROT's prism
/// (<c>TerrainObstacles.PrismOf</c>) centres the box on the entity in plan and runs it from the entity's Z up by Height. A laid bridge (WalkableDeck) is no box: it has <see cref="DeckGather"/>.
/// Same bookkeeping as the deck (<see cref="PerEntitySent{TInputs}"/>), a steady obstacle allocates nothing and sends nothing. Main thread only.
/// </summary>
internal sealed class ObstacleGather
{
    private readonly record struct Inputs(float Length, float Width, float Height);

    private static readonly Func<int, HrotObstacleEnded> Ended = id => new HrotObstacleEnded(id);

    private readonly PerEntitySent<Inputs> _sent = new();

    public void Clear() => _sent.Clear();

    /// <summary>Is <paramref name="e"/> a solid static obstacle (a box with a shape; a walkable deck is not)?</summary>
    public static bool IsSolid(EntityRepository world, Entity e) =>
        world.IsComponentTypeRegistered<StaticObstacle>() && world.IsComponentTypeRegistered<ObstacleShape>() && world.HasComponent<StaticObstacle>(e) && world.HasComponent<ObstacleShape>(e)
        && world.GetComponentRO<StaticObstacle>(e).Kind == ObstacleKind.Solid;

    public void Begin() => _sent.Begin();

    public void Gather(EntityRepository world, Entity e, int id, InMemoryInbound inbound)
    {
        var shape = world.GetComponentRO<ObstacleShape>(e);
        var now = new Inputs(shape.Length, shape.Width, shape.Height);
        if (!_sent.Changed(id, in now))
        {
            return;
        }

        inbound.Enqueue(new HrotObstacle(id, now.Length, now.Width, now.Height));
    }

    /// <summary>Obstacles not seen this frame are gone (pushed down to zero health and removed, or left the world): sent ended once.</summary>
    public void End(InMemoryInbound inbound) => _sent.End(inbound, Ended);
}
