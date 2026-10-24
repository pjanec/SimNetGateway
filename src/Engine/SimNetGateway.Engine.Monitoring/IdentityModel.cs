namespace SimNetGateway.Engine.Monitoring;

/// <summary>
/// What a host can tell about the allocator behind an identity map (allocators are module and Common types the engine does
/// not know): how many keys are in use, free, in quarantine, and the total. Exhaustion explains "not published".
/// </summary>
public readonly record struct AllocatorInfo(string Name, long InUse, long Available, long Quarantined, long Capacity);

/// <summary>One identity map: how many entities it knows, and its allocator if the host described one.</summary>
public readonly record struct IdentityMapRow(string Name, string Side, string KeyType, int Count, AllocatorInfo? Allocator);

/// <summary>
/// The Identities panel's model (DD-005 section 3): every identity map with its bound count and, through an optional hook the
/// host supplies, the availability of the allocator behind it.
/// </summary>
public sealed class IdentityModel
{
    private readonly GatewayEngine _engine;
    private readonly Func<IIdentityMap, AllocatorInfo?>? _describeAllocator;
    private readonly List<IdentityMapRow> _maps = new();

    public IdentityModel(GatewayEngine engine, Func<IIdentityMap, AllocatorInfo?>? describeAllocator = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _describeAllocator = describeAllocator;
    }

    public IReadOnlyList<IdentityMapRow> Maps => _maps;

    public void Refresh()
    {
        _maps.Clear();
        foreach (var map in _engine.Identities.All)
        {
            _maps.Add(new IdentityMapRow(map.Name, Names.Source(_engine, map.Side), map.KeyType.Name, map.Count,
                _describeAllocator?.Invoke(map)));
        }
    }
}
