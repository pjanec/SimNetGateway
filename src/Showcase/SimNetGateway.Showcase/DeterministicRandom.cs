namespace SimNetGateway.Showcase;

/// <summary>
/// A small seeded generator (splitmix64) whose sequence is fixed by this code, so demo and benchmark runs are the same on
/// every platform and runtime version.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(int seed)
    {
        _state = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x1234567UL;
    }

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A value in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>A value in [min, max).</summary>
    public double NextDouble(double min, double max) => min + (max - min) * NextDouble();

    /// <summary>An integer in [0, bound).</summary>
    public int NextInt(int bound) => (int)(NextUInt64() % (ulong)bound);

    /// <summary>A generator whose sequence is independent of this one's future values.</summary>
    public DeterministicRandom Fork() => new((int)(NextUInt64() & 0x7FFFFFFF));
}
