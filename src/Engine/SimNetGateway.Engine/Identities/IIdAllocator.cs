namespace SimNetGateway.Engine;

/// <summary>
/// Produces wire identities for entities the gateway introduces to a side (DD-001 3.6). Strategies (a reserved number
/// range, a pool with quarantine, ...) are modules' business; exhaustion is a normal outcome, not an exception.
/// </summary>
public interface IIdAllocator<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    /// <summary>Allocates a key; false when none is available (the caller does not publish and records a diagnostic).</summary>
    bool TryAllocate(out TKey key);

    /// <summary>Takes back a key allocated by this allocator, after the removal triggers of its entity ran.</summary>
    void Release(in TKey key);
}
