using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Animals;
using Fdp.Toolkit.Replication.Components;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// A two-wheeler's lean into a turn (DD-018 3.5b). HROT's 3-D map draws it in <c>MotionTracker</c> (Hrot.Presentation, <c>case VisualFamily.Motorcycle</c>), an assembly the host
/// must not reference, so the rule is carried here as it stands: target = clamp(-atan(v * yawRate / g), +-MaxBank), eased with a BankSeconds time constant. HROT's sign is
/// the roll about +x (forward) with a left turn negative, which is the contract's: positive = the top toward the right. HROT differences its frames for v and yaw rate; the
/// host uses the simulation's own forward speed and angular velocity. <c>BankRuleTests</c> pins the numbers.
/// </summary>
internal static class BankRule
{
    /// <summary>HROT's <c>MotionTracker.MaxBank</c> (about 35 degrees) and <c>BankSeconds</c>.</summary>
    public const float MaxBank = 0.61f, BankSeconds = 0.15f;

    private const float Gravity = 9.81f;

    /// <summary>The lean the rider needs: forward speed in m/s and yaw rate in rad/s (counter-clockwise positive, seen from above).</summary>
    public static float Target(float speed, float yawRate) => Math.Clamp(-MathF.Atan(speed * yawRate / Gravity), -MaxBank, MaxBank);

    /// <summary>The lean after <paramref name="dt"/> simulation seconds of easing toward <paramref name="target"/>; no time passed (a paused simulation) keeps it.</summary>
    public static float Ease(float bank, float target, double dt) => dt > 1e-4 ? bank + (target - bank) * (1f - MathF.Exp(-(float)dt / BankSeconds)) : bank;
}

/// <summary>
/// The lean of every two-wheeler the adapter publishes, eased on simulation time. A two-wheeler is an entity whose type's visual family is HROT's Motorcycle (the family
/// HROT's own look library derives from the DIS type's class chain); everything else is 0. Main thread only.
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
        float target = BankRule.Target(speed, velocity.Angular.Z);
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

        float bank = BankRule.Ease(previous.Bank, target, dt);
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
        if (world.IsComponentTypeRegistered<FlockState>() && world.HasComponent<FlockState>(e))
        {
            var flock = world.GetComponentRO<FlockState>(e);
            count = flock.Count;
            initial = flock.InitialCount;
            seed = flock.Seed;
            flockMode = flock.Mode.ToString();
        }

        message = new HrotAnimal(id, state.Mode.ToString(), state.FearBand, (state.Flags & AnimalFlags.Bolting) != 0, (state.Flags & AnimalFlags.Dangerous) != 0,
            (state.Flags & AnimalFlags.Calling) != 0, count, initial, seed, flockMode);
        if (_sent.TryGetValue(id, out var previous) && previous.Equals(message))
        {
            return false;
        }

        _sent[id] = message;
        return true;
    }
}
