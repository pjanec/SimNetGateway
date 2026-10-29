using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>The three outcomes of reading the configuration file (DD-007 section 3a).</summary>
public enum ConfigurationLoadKind
{
    /// <summary>There is no file: the gateway is not instantiated, and that is not an error.</summary>
    Missing,

    /// <summary>The file exists but is unreadable or invalid: not instantiated, one error per problem.</summary>
    Invalid,

    /// <summary>The file was read; see <see cref="GatewayConfiguration.AnySideEnabled"/> for whether any side runs.</summary>
    Loaded,
}

/// <summary>The result of <see cref="GatewayConfigurationFile.Load"/> / <see cref="GatewayConfigurationFile.Parse"/>.</summary>
public sealed record ConfigurationLoadResult(
    ConfigurationLoadKind Kind,
    GatewayConfiguration? Configuration,
    IReadOnlyList<string> Errors);

/// <summary>
/// Reads the gateway configuration file (R-094, DD-007 section 3a) with System.Text.Json from the shared framework. The
/// reader is strict: a JSON error, an unknown key at any level, a value of the wrong type or out of range and a missing
/// required key are all errors, every one naming the key (dotted path, e.g. <c>dis.port</c>) and the reason; all problems of
/// a file are reported together. Missing optional keys take the documented defaults.
/// </summary>
public static class GatewayConfigurationFile
{
    /// <summary>The environment variable that overrides the location of the file.</summary>
    public const string PathVariable = "SIMNETGATEWAY_CONFIG";

    /// <summary>The default file name, looked up in the process working directory.</summary>
    public const string DefaultFileName = "simnetgateway.json";

    /// <summary>The file to read: <c>SIMNETGATEWAY_CONFIG</c> when set and not empty, else <c>simnetgateway.json</c> in the current directory.</summary>
    public static string ResolvePath() => ResolvePath(Environment.GetEnvironmentVariable);

    /// <summary><see cref="ResolvePath()"/> with an injectable environment lookup (tests do not touch the process environment).</summary>
    public static string ResolvePath(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var overridden = getEnvironmentVariable(PathVariable);
        return !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Directory.GetCurrentDirectory(), DefaultFileName);
    }

    /// <summary>
    /// True and the configuration when the file was read (<see cref="ConfigurationLoadKind.Loaded"/>; check
    /// <see cref="GatewayConfiguration.AnySideEnabled"/>). False for a missing file (no errors) and for an invalid one (errors);
    /// use <see cref="Load"/> to tell the two apart.
    /// </summary>
    public static bool TryLoad(string path, out GatewayConfiguration? config, out IReadOnlyList<string> errors)
    {
        var result = Load(path);
        config = result.Configuration;
        errors = result.Errors;
        return result.Kind == ConfigurationLoadKind.Loaded;
    }

    /// <summary>Reads and validates the file.</summary>
    public static ConfigurationLoadResult Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return new ConfigurationLoadResult(ConfigurationLoadKind.Missing, null, []);
            }

            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigurationLoadResult(ConfigurationLoadKind.Invalid, null, [$"{path}: cannot read the file: {ex.Message}"]);
        }

        return Parse(text, path);
    }

    /// <summary>Validates configuration text; <paramref name="source"/> (usually the file path) prefixes every error.</summary>
    public static ConfigurationLoadResult Parse(string json, string source = "configuration")
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Invalid([$"{source}: invalid JSON: {ex.Message}"]);
        }

        using (document)
        {
            var errors = new List<string>();
            var config = Read(document.RootElement, source, errors);
            return errors.Count > 0 || config is null
                ? Invalid(errors)
                : new ConfigurationLoadResult(ConfigurationLoadKind.Loaded, config, []);
        }
    }

    /// <summary>The configuration as JSON with every key written (it parses back to an equal configuration).</summary>
    public static string ToJson(GatewayConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartObject("shared");
            w.WriteNumber("site", config.Shared.Site);
            w.WriteNumber("application", config.Shared.Application);
            w.WriteNumber("entityMaxHealth", config.Shared.EntityMaxHealth);
            w.WriteStartObject("damageThresholds");
            w.WriteNumber("slight", config.Shared.DamageThresholds.Slight);
            w.WriteNumber("moderate", config.Shared.DamageThresholds.Moderate);
            w.WriteEndObject();
            w.WriteNumber("defaultTkbForUnknown", config.Shared.DefaultTkbForUnknown);
            w.WriteBoolean("forwardUnitEntities", config.Shared.ForwardUnitEntities);
            w.WriteBoolean("sendEventsInReplay", config.Shared.SendEventsInReplay);
            w.WriteEndObject();
            w.WriteStartObject("hrot");
            w.WriteNumber("sampleRateHz", config.Hrot.SampleRateHz);
            w.WriteNumber("replicaOwnerNodeId", config.Hrot.ReplicaOwnerNodeId);
            w.WriteBoolean("allowZeroOrigin", config.Hrot.AllowZeroOrigin);
            w.WriteEndObject();
            if (config.Dis is { } dis)
            {
                w.WriteStartObject("dis");
                w.WriteBoolean("enabled", dis.Enabled);
                w.WriteNumber("exerciseId", dis.ExerciseId);
                w.WriteNumber("port", dis.Port);
                w.WriteString("address", dis.Address);
                w.WriteNumber("positionThresholdM", dis.PositionThresholdM);
                w.WriteNumber("orientationThresholdDeg", dis.OrientationThresholdDeg);
                w.WriteNumber("heartbeatS", dis.HeartbeatS);
                w.WriteNumber("timeoutS", dis.TimeoutS);
                w.WriteBoolean("hrotControlsDis", dis.HrotControlsDis);
                w.WriteBoolean("disControlsHrot", dis.DisControlsHrot);
                w.WriteEndObject();
            }

            if (config.Hla is { } hla)
            {
                w.WriteStartObject("hla");
                w.WriteBoolean("enabled", hla.Enabled);
                w.WriteString("rti", hla.Rti);
                w.WriteString("federation", hla.Federation);
                w.WriteString("federateName", hla.FederateName);
                w.WriteString("federateType", hla.FederateType);
                w.WriteStartArray("fomModules");
                foreach (var module in hla.FomModules)
                {
                    w.WriteStringValue(module);
                }

                w.WriteEndArray();
                w.WriteBoolean("createFederation", hla.CreateFederation);
                w.WriteNumber("heartbeatS", hla.HeartbeatS);
                w.WriteNumber("timeoutS", hla.TimeoutS);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ConfigurationLoadResult Invalid(IReadOnlyList<string> errors) =>
        new(ConfigurationLoadKind.Invalid, null, errors);

    // ---- reading ----------------------------------------------------------------------------------------------------

    private static GatewayConfiguration? Read(JsonElement root, string source, List<string> errors)
    {
        var top = Section.Open(root, source, "", errors);
        if (top is null)
        {
            return null;
        }

        var shared = ReadShared(top.Child("shared"));
        var hrot = ReadHrot(top.Child("hrot"));
        var dis = ReadDis(top.Child("dis"));
        var hla = ReadHla(top.Child("hla"));
        top.Finish();
        return errors.Count > 0 ? null : new GatewayConfiguration(shared, hrot, dis, hla);
    }

    private static SharedConfiguration ReadShared(Section? s)
    {
        var d = new SharedConfiguration();
        if (s is null)
        {
            return d;
        }

        var thresholds = ReadThresholds(s.Child("damageThresholds"), d.DamageThresholds);
        var result = new SharedConfiguration
        {
            Site = (int)s.Integer("site", d.Site, 0, 65535),
            Application = (int)s.Integer("application", d.Application, 0, 65535),
            EntityMaxHealth = s.Number("entityMaxHealth", d.EntityMaxHealth, min: 0, minExclusive: true),
            DamageThresholds = thresholds,
            DefaultTkbForUnknown = s.Integer("defaultTkbForUnknown", d.DefaultTkbForUnknown, 0, long.MaxValue),
            ForwardUnitEntities = s.Boolean("forwardUnitEntities", d.ForwardUnitEntities),
            SendEventsInReplay = s.Boolean("sendEventsInReplay", d.SendEventsInReplay),
        };
        s.Finish();
        return result;
    }

    private static DamageThresholds ReadThresholds(Section? s, DamageThresholds d)
    {
        if (s is null)
        {
            return d;
        }

        double slight = s.Number("slight", d.Slight, min: 0, max: 1);
        double moderate = s.Number("moderate", d.Moderate, min: 0, max: 1);
        if (moderate > slight)
        {
            s.Error("moderate", $"must not exceed slight ({Format(slight)}), got {Format(moderate)}");
        }

        s.Finish();
        return new DamageThresholds { Slight = slight, Moderate = moderate };
    }

    private static HrotConfiguration ReadHrot(Section? s)
    {
        var d = new HrotConfiguration();
        if (s is null)
        {
            return d;
        }

        var result = new HrotConfiguration
        {
            SampleRateHz = s.Number("sampleRateHz", d.SampleRateHz, min: 0, minExclusive: true),
            ReplicaOwnerNodeId = s.Integer("replicaOwnerNodeId", d.ReplicaOwnerNodeId, 0, int.MaxValue),
            AllowZeroOrigin = s.Boolean("allowZeroOrigin", d.AllowZeroOrigin),
        };
        s.Finish();
        return result;
    }

    private static DisConfiguration? ReadDis(Section? s)
    {
        if (s is null)
        {
            return null;
        }

        var d = new DisConfiguration { ExerciseId = 0 };
        bool enabled = s.Boolean("enabled", d.Enabled);
        int exercise = (int)s.RequiredInteger("exerciseId", 0, 255);
        int port = (int)s.Integer("port", d.Port, 1, 65535);
        string address = s.String("address", d.Address);
        if (!string.IsNullOrEmpty(address) && !IPAddress.TryParse(address, out _))
        {
            s.Error("address", $"must be an IP address, got \"{address}\"");
        }

        var result = new DisConfiguration
        {
            Enabled = enabled,
            ExerciseId = exercise,
            Port = port,
            Address = address,
            PositionThresholdM = s.Number("positionThresholdM", d.PositionThresholdM, min: 0),
            OrientationThresholdDeg = s.Number("orientationThresholdDeg", d.OrientationThresholdDeg, min: 0),
            HeartbeatS = s.Number("heartbeatS", d.HeartbeatS, min: 0, minExclusive: true),
            TimeoutS = s.Number("timeoutS", d.TimeoutS, min: 0, minExclusive: true),
            HrotControlsDis = s.Boolean("hrotControlsDis", d.HrotControlsDis),
            DisControlsHrot = s.Boolean("disControlsHrot", d.DisControlsHrot),
        };
        s.Finish();
        return result;
    }

    private static HlaConfiguration? ReadHla(Section? s)
    {
        if (s is null)
        {
            return null;
        }

        var d = new HlaConfiguration { Federation = "" };
        var result = new HlaConfiguration
        {
            Enabled = s.Boolean("enabled", d.Enabled),
            Rti = s.NonEmptyString("rti", d.Rti),
            Federation = s.RequiredNonEmptyString("federation"),
            FederateName = s.NonEmptyString("federateName", d.FederateName),
            FederateType = s.NonEmptyString("federateType", d.FederateType),
            FomModules = s.StringList("fomModules", d.FomModules),
            CreateFederation = s.Boolean("createFederation", d.CreateFederation),
            HeartbeatS = s.Number("heartbeatS", d.HeartbeatS, min: 0, minExclusive: true),
            TimeoutS = s.Number("timeoutS", d.TimeoutS, min: 0, minExclusive: true),
        };
        s.Finish();
        return result;
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>One JSON object being read: typed accessors that record errors, and a check for unknown keys.</summary>
    private sealed class Section
    {
        private readonly Dictionary<string, JsonElement> _properties = new(StringComparer.Ordinal);
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private readonly string _source;
        private readonly string _prefix;
        private readonly List<string> _errors;

        private Section(string source, string prefix, List<string> errors)
        {
            _source = source;
            _prefix = prefix;
            _errors = errors;
        }

        public static Section? Open(JsonElement element, string source, string prefix, List<string> errors)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                string name = prefix.Length == 0 ? "(top level)" : prefix.TrimEnd('.');
                errors.Add($"{source}: {name}: expected an object, got {element.ValueKind}");
                return null;
            }

            var section = new Section(source, prefix, errors);
            foreach (var property in element.EnumerateObject())
            {
                if (!section._properties.TryAdd(property.Name, property.Value))
                {
                    errors.Add($"{source}: {prefix}{property.Name}: duplicate key");
                }
            }

            return section;
        }

        public Section? Child(string key)
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
                Error(key, $"must be {(minExclusive ? "greater than" : "at least")} {Format(lo)}, got {Format(value)}");
                return fallback;
            }

            if (max is { } hi && value > hi)
            {
                Error(key, $"must be at most {Format(hi)}, got {Format(value)}");
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
    }
}
