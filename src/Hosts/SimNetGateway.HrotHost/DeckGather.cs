using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Terrain;
using Hrot.Map.Common;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// DD-035 (T-083): a laid bridge's deck. A laid or placed bridge is a replicated static-obstacle entity (<c>StaticObstacle.Kind</c> WalkableDeck, TKB 8825 / 8826) that the adapter already publishes as an
/// <c>Entity</c> (the entity query takes it once it is Active: a Constructing obstacle is held until its bake commits, as HROT's terrain draws it). This gather gives it its <c>HrotDeck</c>: the corners come from
/// HROT's one rule <c>TerrainObstacles.DeckParts</c> of the entity's position, heading (<c>TerrainObstacles.Yaw</c> of its <c>SimTransform</c> rotation) and <c>ObstacleShape</c>; which bridge it is comes from
/// its TKB type (<c>TkbEntityTypes.Obstacle_AssaultBridge / Obstacle_FloatingBridge</c>: HROT has no field for it). The rule allocates its three parts, so it runs only when the pose, shape or type changed
/// (compared on the inputs); a steady bridge allocates nothing and sends nothing; one that is gone (recovered, fallen, left the world) is sent ended once. Main thread only.
/// </summary>
internal sealed class DeckGather
{
    private readonly record struct Inputs(long Type, Vector3 Position, float Yaw, float Length, float Width, float Height);

    private static readonly Func<int, HrotDeckEnded> Ended = id => new HrotDeckEnded(id);

    private readonly PerEntitySent<Inputs> _sent = new();

    public void Clear() => _sent.Clear();

    /// <summary>Is <paramref name="e"/> a laid bridge: a static obstacle of the walkable-deck kind with a shape?</summary>
    public static bool IsDeck(EntityRepository world, Entity e) =>
        world.IsComponentTypeRegistered<StaticObstacle>() && world.IsComponentTypeRegistered<ObstacleShape>() && world.HasComponent<StaticObstacle>(e) && world.HasComponent<ObstacleShape>(e)
        && world.GetComponentRO<StaticObstacle>(e).Kind == ObstacleKind.WalkableDeck;

    public void Begin() => _sent.Begin();

    public void Gather(EntityRepository world, Entity e, int id, InMemoryInbound inbound)
    {
        var at = world.GetComponentRO<SimTransform>(e);
        var shape = world.GetComponentRO<ObstacleShape>(e);
        long type = world.GetComponentRO<TkbIdentity>(e).TkbType.Value;
        var now = new Inputs(type, at.Position, TerrainObstacles.Yaw(at.Rotation), shape.Length, shape.Width, shape.Height);
        if (!_sent.Changed(id, in now))
        {
            return;
        }

        var parts = TerrainObstacles.DeckParts(at.Position, now.Yaw, in shape);   // HROT's one rule; allocates, so only on a change
        var wire = new HrotDeckPart[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            wire[i] = new HrotDeckPart(PivotEnum<HrotDeckPartKind>.Of(p.Kind), Corner(p.C0), Corner(p.C1), Corner(p.C2), Corner(p.C3));
        }

        inbound.Enqueue(new HrotDeck(id, KindOf(type), shape.Length, shape.Width, shape.Height, wire));
    }

    /// <summary>Bridges not seen this frame are gone: sent ended once.</summary>
    public void End(InMemoryInbound inbound) => _sent.End(inbound, Ended);

    private static HrotVec3 Corner((Vector2 At, float Above) c) => new(c.At.X, c.At.Y, c.Above);

    /// <summary>The bridge of a TKB type (HROT's named constants, not numbers of ours); any other deck type is Unknown.</summary>
    private static HrotDeckKind KindOf(long type) =>
        type == TkbEntityTypes.Obstacle_AssaultBridge ? HrotDeckKind.Assault
        : type == TkbEntityTypes.Obstacle_FloatingBridge ? HrotDeckKind.Floating
        : HrotDeckKind.Unknown;
}
