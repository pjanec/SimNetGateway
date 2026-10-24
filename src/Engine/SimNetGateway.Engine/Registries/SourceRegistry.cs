namespace SimNetGateway.Engine;

/// <summary>Names -> source ids (sides, processors, the engine); registration is idempotent by name.</summary>
public sealed class SourceRegistry
{
    /// <summary>The engine's own source (e.g. unload change sets); pre-registered as "engine".</summary>
    public static readonly SourceId Engine = new(1);

    private readonly List<string> _names = new();
    private readonly Dictionary<string, SourceId> _byName = new(StringComparer.Ordinal);

    public SourceRegistry()
    {
        Register("engine");
    }

    public int Count => _names.Count;

    public SourceId Register(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Source name must not be empty.", nameof(name));
        }

        if (_byName.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var id = new SourceId(_names.Count + 1);
        _names.Add(name);
        _byName.Add(name, id);
        return id;
    }

    public string NameOf(SourceId id)
    {
        int index = id.Value - 1;
        if ((uint)index >= (uint)_names.Count)
        {
            throw new KeyNotFoundException($"Unknown source id {id.Value}.");
        }

        return _names[index];
    }

    public bool TryGet(string name, out SourceId id)
    {
        if (name is not null && _byName.TryGetValue(name, out id))
        {
            return true;
        }

        id = default;
        return false;
    }
}
