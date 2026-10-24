namespace SimNetGateway.Engine;

/// <summary>Monotonic wall clock in seconds (R-012: no OS-specific timers).</summary>
public interface IClock
{
    double Now { get; }
}
