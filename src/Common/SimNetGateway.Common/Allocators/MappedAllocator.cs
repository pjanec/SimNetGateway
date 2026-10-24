using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// Adapts an <see cref="IIdAllocator{TKey}"/> of numbers to a structured key, e.g. a DIS EntityIdentifier made of a
/// fixed site and application plus the pool number. The mapping functions are supplied by the gateway (Common knows no
/// protocol types).
/// </summary>
public sealed class MappedAllocator<TKey> : IIdAllocator<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly IIdAllocator<int> _numbers;
    private readonly Func<int, TKey> _toKey;
    private readonly Func<TKey, int> _toNumber;

    public MappedAllocator(IIdAllocator<int> numbers, Func<int, TKey> toKey, Func<TKey, int> toNumber)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        ArgumentNullException.ThrowIfNull(toKey);
        ArgumentNullException.ThrowIfNull(toNumber);
        _numbers = numbers;
        _toKey = toKey;
        _toNumber = toNumber;
    }

    /// <summary>The allocator of the numbers this one maps (read-only access for diagnostics: pool occupancy, quarantine).</summary>
    public IIdAllocator<int> Numbers => _numbers;

    public bool TryAllocate(out TKey key)
    {
        if (_numbers.TryAllocate(out int number))
        {
            key = _toKey(number);
            return true;
        }

        key = default!;
        return false;
    }

    public void Release(in TKey key) => _numbers.Release(_toNumber(key));
}
