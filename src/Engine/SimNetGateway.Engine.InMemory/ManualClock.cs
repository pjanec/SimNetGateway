using SimNetGateway.Engine;

namespace SimNetGateway.Engine.InMemory;

/// <summary>A clock the test moves by hand (deterministic tests, no sleeping).</summary>
public sealed class ManualClock : IClock
{
    public ManualClock(double start = 0) => Now = start;

    public double Now { get; set; }

    public void Advance(double seconds) => Now += seconds;
}
