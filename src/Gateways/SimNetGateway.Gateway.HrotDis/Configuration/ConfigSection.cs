using System.Globalization;
using System.Text.Json;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// One JSON object being read: typed accessors that record errors (text format <c>source: dotted.path: reason</c>), and a check for
/// unknown keys. The built-in sides and the sides of other projects (<see cref="IProtocolSide"/>) parse their section with it.
/// </summary>
public sealed class ConfigSection
{
    private readonly Dictionary<string, JsonElement> _properties = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private readonly string _source;
    private readonly string _prefix;
    private readonly List<string> _errors;

    private ConfigSection(string source, string prefix, List<string> errors)
    {
        _source = source;
        _prefix = prefix;
        _errors = errors;
    }

    public static ConfigSection? Open(JsonElement element, string source, string prefix, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            string name = prefix.Length == 0 ? "(top level)" : prefix.TrimEnd('.');
            errors.Add($"{source}: {name}: expected an object, got {element.ValueKind}");
            return null;
        }

        var section = new ConfigSection(source, prefix, errors);
        foreach (var property in element.EnumerateObject())
        {
            if (!section._properties.TryAdd(property.Name, property.Value))
            {
                errors.Add($"{source}: {prefix}{property.Name}: duplicate key");
            }
        }

        return section;
    }

    public ConfigSection? Child(string key)
    {
        if (!_properties.TryGetValue(key, out var element))
        {
            return null;
        }

        _used.Add(key);
        return Open(element, _source, _prefix + key + ".", _errors);
    }

    public void Error(string key, string reason) => _errors.Add($"{_source}: {_prefix}{key}: {reason}");

    public void Finish()
    {
        foreach (var key in _properties.Keys)
        {
            if (!_used.Contains(key))
            {
                Error(key, "unknown key");
            }
        }
    }

    public bool Boolean(string key, bool fallback)
    {
        if (!TryGet(key, out var element))
        {
            return fallback;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        Error(key, $"expected true or false, got {element.ValueKind}");
        return fallback;
    }

    public long Integer(string key, long fallback, long min, long max)
    {
        if (!TryGet(key, out var element))
        {
            return fallback;
        }

        return ReadInteger(key, element, fallback, min, max);
    }

    public long RequiredInteger(string key, long min, long max)
    {
        if (!TryGet(key, out var element))
        {
            Error(key, "required key is missing");
            return min;
        }

        return ReadInteger(key, element, min, min, max);
    }

    private long ReadInteger(string key, JsonElement element, long fallback, long min, long max)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long value))
        {
            Error(key, $"expected an integer, got {Describe(element)}");
            return fallback;
        }

        if (value < min || value > max)
        {
            Error(key, $"must be in {min} .. {max}, got {value}");
            return fallback;
        }

        return value;
    }

    public double Number(string key, double fallback, double? min = null, double? max = null, bool minExclusive = false)
    {
        if (!TryGet(key, out var element))
        {
            return fallback;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out double value) || !double.IsFinite(value))
        {
            Error(key, $"expected a number, got {Describe(element)}");
            return fallback;
        }

        if (min is { } lo && (minExclusive ? value <= lo : value < lo))
        {
            Error(key, $"must be {(minExclusive ? "greater than" : "at least")} {FormatNumber(lo)}, got {FormatNumber(value)}");
            return fallback;
        }

        if (max is { } hi && value > hi)
        {
            Error(key, $"must be at most {FormatNumber(hi)}, got {FormatNumber(value)}");
            return fallback;
        }

        return value;
    }

    public string String(string key, string fallback)
    {
        if (!TryGet(key, out var element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? fallback;
        }

        Error(key, $"expected a string, got {element.ValueKind}");
        return fallback;
    }

    public string NonEmptyString(string key, string fallback)
    {
        string value = String(key, fallback);
        if (value.Length == 0)
        {
            Error(key, "must not be empty");
            return fallback;
        }

        return value;
    }

    public string RequiredNonEmptyString(string key)
    {
        if (!TryGet(key, out var element))
        {
            Error(key, "required key is missing");
            return "";
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            Error(key, $"expected a string, got {element.ValueKind}");
            return "";
        }

        string value = element.GetString() ?? "";
        if (value.Length == 0)
        {
            Error(key, "must not be empty");
        }

        return value;
    }

    public IReadOnlyList<string> StringList(string key, IReadOnlyList<string> fallback)
    {
        if (!TryGet(key, out var element))
        {
            return fallback;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            Error(key, $"expected an array of strings, got {element.ValueKind}");
            return fallback;
        }

        var list = new List<string>();
        int index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
            {
                Error($"{key}[{index}]", "expected a non-empty string");
                return fallback;
            }

            list.Add(item.GetString()!);
            index++;
        }

        if (list.Count == 0)
        {
            Error(key, "must not be empty");
            return fallback;
        }

        return list;
    }

    private bool TryGet(string key, out JsonElement element)
    {
        if (_properties.TryGetValue(key, out element))
        {
            _used.Add(key);
            return true;
        }

        return false;
    }

    private static string Describe(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number ? element.GetRawText() : element.ValueKind.ToString();

    private static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
