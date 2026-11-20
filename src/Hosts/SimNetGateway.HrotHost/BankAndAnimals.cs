using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Animals;
using Fdp.Toolkit.Replication.Components;
using Hrot.Look;
using Hrot.Look.Vehicles;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The lean of every two-wheeler the adapter publishes, eased on simulation time. A two-wheeler is an entity whose type's visual family is HROT's Motorcycle (the family
/// HROT's own look library derives from the DIS type's class chain); everything else is 0. The lean is HROT's own rule, <c>Hrot.Look.Vehicles.MotionRules.BankTarget</c> and
/// <c>EaseBank</c> (the per-entity easing state is the caller's); HROT's sign (roll about +x, a left turn negative) is the contract's: positive = the top toward the right.
/// HROT differences its frames for v and yaw rate; the host uses the simulation's own forward speed and angular velocity. Main thread only.
/// </summary>
internal sealed class BankTracker
{
    private readonly Dictionary<long, bool> _isTwoWheeler = new();
    private readonly Dictionary<int, (double SimTime, float Bank)> _state = new();

    public void Forget(int id) => _state.Remove(id);

    public void Clear()
    {
        _state.Clear();
        _isTwoWheeler.Clear();
    }

    /// <summary>The entity's lean at <paramref name="simTime"/> (radians); 0 for anything but a two-wheeler.</summary>
    public double Update(EntityRepository world, Entity e, int id, long tkbType, long disType, double simTime)
    {
        if (!_isTwoWheeler.TryGetValue(tkbType, out bool twoWheeler))
        {
            twoWheeler = disType != 0 && Hrot.UI.Common.Map3D.VisualFamilies.Of(new DISEntityType { Value = (ulong)disType }) == Hrot.UI.Common.Map3D.VisualFamily.Motorcycle;
            _isTwoWheeler[tkbType] = twoWheeler;
        }

        if (!twoWheeler)
        {
            return 0.0;
        }

        var rotation = (Quaternion)world.GetComponentRO<SimTransform>(e).Rotation;
        if (rotation.LengthSquared() < 0.5f)
        {
            rotation = Quaternion.Identity;
        }

        var velocity = world.IsComponentTypeRegistered<SimVelocity>() && world.HasComponent<SimVelocity>(e) ? world.GetComponentRO<SimVelocity>(e) : default;
        float speed = Vector3.Dot(velocity.Linear, Vector3.Transform(Vector3.UnitX, rotation));   // along the body's forward axis (x), signed
        float target = MotionRules.BankTarget(speed, velocity.Angular.Z);
        // the first sight starts upright, as HROT's tracker does; time that went backwards (a restore) starts over
        if (!_state.TryGetValue(id, out var previous) || simTime < previous.SimTime)
        {
            _state[id] = (simTime, 0f);
            return 0.0;
        }

        double dt = simTime - previous.SimTime;
        if (dt <= 1e-4)
        {
            return previous.Bank;   // no time passed: the same answer
        }

        float bank = MotionRules.EaseBank(previous.Bank, target, (float)dt);
        _state[id] = (simTime, bank);
        return bank;
    }
}

/// <summary>An animal's state and a flock's (HROT's <c>AnimalState</c> / <c>FlockState</c>), sent when it changes. Main thread only.</summary>
internal sealed class AnimalGather
{
    private readonly Dictionary<int, HrotAnimal> _sent = new();

    public void Forget(int id) => _sent.Remove(id);

    public void Clear() => _sent.Clear();

    /// <summary>An animal is what HROT's <c>Animals.IsAnimal</c> says: it carries an animal state.</summary>
    public static bool IsAnimal(EntityRepository world, Entity e) => world.IsComponentTypeRegistered<AnimalState>() && Animals.IsAnimal(world, e);

    public bool TryGather(EntityRepository world, Entity e, int id, out HrotAnimal message)
    {
        var state = world.GetComponentRO<AnimalState>(e);
        int count = 0, initial = 0;
        uint seed = 0;
        string flockMode = string.Empty;
        bool airborne = Airborne.Of(world, e);   // HROT's own reader: a flock flying, a lone bird in flight, a flyer's flight state
        if (world.IsComponentTypeRegistered<FlockState>() && world.HasComponent<FlockState>(e))
        {
            var flock = world.GetComponentRO<FlockState>(e);
            count = flock.Count;
            initial = flock.InitialCount;
            seed = flock.Seed;
            flockMode = flock.Mode.ToString();
        }

        message = new HrotAnimal(id, state.Mode.ToString(), state.FearBand, (state.Flags & AnimalFlags.Bolting) != 0, (state.Flags & AnimalFlags.Dangerous) != 0,
            (state.Flags & AnimalFlags.Calling) != 0, count, initial, seed, flockMode, airborne);
        if (_sent.TryGetValue(id, out var previous) && previous.Equals(message))
        {
            return false;
        }

        _sent[id] = message;
        return true;
    }
}
