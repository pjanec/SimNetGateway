namespace SimNetGateway.Engine;

/// <summary>Names -> simulation event type ids; registration is idempotent by name.</summary>
public sealed class EventTypeRegistry
{
    private readonly List<string> _names = new();
    private readonly List<string?> _descriptions = new();
    private readonly Dictionary<string, EventTypeId> _byName = new(StringComparer.Ordinal);

    public int Count => _names.Count;

    public EventTypeId Register(string name, string? description = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Event type name must not be empty.", nameof(name));
        }

        if (_byName.TryGetValue(name, out var existing))
        {
            _descriptions[existing.Value - 1] ??= description;
            return existing;
        }

        var id = new EventTypeId(_names.Count + 1);
        _names.Add(name);
        _descriptions.Add(description);
        _byName.Add(name, id);
        return id;
    }

    public string NameOf(EventTypeId id)
    {
        int index = id.Value - 1;
        if ((uint)index >= (uint)_names.Count)
        {
            throw new KeyNotFoundException($"Unknown event type id {id.Value}.");
        }

        return _names[index];
    }

    public string? DescriptionOf(EventTypeId id)
    {
        int index = id.Value - 1;
        return (uint)index < (uint)_descriptions.Count ? _descriptions[index] : null;
    }

    public bool TryGet(string name, out EventTypeId id)
    {
        if (name is not null && _byName.TryGetValue(name, out id))
        {
            return true;
        }

        id = default;
        return false;
    }
}
