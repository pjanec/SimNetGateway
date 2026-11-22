using Fdp.Core;
using Fdp.Toolkit.Ambient;
using Fdp.Toolkit.Physics.Components;
using Fdp.Toolkit.Replication.Components;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The ambience layers (DD-028), read from HROT's own state: every entity carrying an <c>Ambience</c> component (positionless = no <c>SimTransform</c> = heard everywhere) is one layer, sent as
/// <see cref="HrotAmbience"/> when it is new or a value differs from what was last sent (kind, intensity, position, radius, end time), and <see cref="HrotAmbienceRemoved"/> once when its entity is gone
/// or torn down (HROT's expiry marks a layer <c>TearDown</c> at once). The id is the entity's network id, its slot index when it has none. Nothing is sent while a layer is held; a world boundary
/// (<see cref="Clear"/>) forgets all, so the next gather states every layer of the new world. A steady gather allocates nothing (the kind string is made when a layer is new or its kind changed). Main thread only.
/// </summary>
internal sealed class AmbienceGather
{
    private sealed class Layer
    {
        public FixedString32 Kind;
        public string KindName = string.Empty;
        public float Intensity;
        public bool HasPosition;
        public float X, Y, Z;
        public float Radius;
        public double Until;
        public int Stamp;
    }

    private readonly Dictionary<int, Layer> _sent = new();
    private readonly List<int> _gone = new();
    private EntityRepository? _world;
    private EntityQuery? _query;
    private int _stamp;

    /// <summary>How many <see cref="HrotAmbience"/> were sent, for the tests and the counters.</summary>
    public int Sent { get; private set; }

    /// <summary>How many layers were withdrawn.</summary>
    public int Withdrawn { get; private set; }

    /// <summary>A world boundary: forget everything, so the next gather states every layer of the new world.</summary>
    public void Clear()
    {
        _sent.Clear();
        _world = null;
        _query = null;
    }

    public void Gather(EntityRepository world, InMemoryInbound inbound)
    {
        if (!ReferenceEquals(_world, world))
        {
            Clear();
            _world = world;
            _query = world.IsComponentTypeRegistered<Ambience>() ? world.Query().With<Ambience>().Build() : null;
        }

        if (_query is null)
        {
            return;
        }

        _stamp++;
        foreach (var e in _query)
        {
            if (world.GetLifecycleState(e) == EntityLifecycle.TearDown)
            {
                continue;   // ending: swept below
            }

            int id = world.HasComponent<NetworkIdentity>(e) ? world.GetComponentRO<NetworkIdentity>(e).Value : e.Index;
            ref readonly var a = ref world.GetComponentRO<Ambience>(e);
            bool hasPosition = world.HasComponent<SimTransform>(e);
            float x = 0f, y = 0f, z = 0f;
            if (hasPosition)
            {
                var p = (System.Numerics.Vector3)world.GetComponentRO<SimTransform>(e).Position;
                x = p.X;
                y = p.Y;
                z = p.Z;
            }

            float radius = hasPosition ? a.Radius : 0f;   // a positionless layer has no radius
            float intensity = float.IsFinite(a.Intensity) ? Math.Clamp(a.Intensity, 0f, 1f) : 0f;
            double until = a.Until.Seconds;
            bool isNew = !_sent.TryGetValue(id, out var s);
            if (!isNew && s!.Kind.Equals(a.Kind) && s.Intensity == intensity && s.HasPosition == hasPosition && s.X == x && s.Y == y && s.Z == z && s.Radius == radius && s.Until == until)
            {
                s.Stamp = _stamp;
                continue;
            }

            if (isNew)
            {
                s = new Layer();
                _sent[id] = s;
            }

            if (isNew || !s!.Kind.Equals(a.Kind))
            {
                s!.KindName = a.Kind.ToString();
            }

            s!.Kind = a.Kind;
            s.Intensity = intensity;
            s.HasPosition = hasPosition;
            s.X = x;
            s.Y = y;
            s.Z = z;
            s.Radius = radius;
            s.Until = until;
            s.Stamp = _stamp;
            inbound.Enqueue(new HrotAmbience(id, s.KindName, intensity, hasPosition, new HrotVec3(x, y, z), radius, until));
            Sent++;
        }

        _gone.Clear();
        foreach (var (id, s) in _sent)
        {
            if (s.Stamp != _stamp)
            {
                _gone.Add(id);
            }
        }

        foreach (int id in _gone)
        {
            _sent.Remove(id);
            inbound.Enqueue(new HrotAmbienceRemoved(id));
            Withdrawn++;
        }
    }
}
