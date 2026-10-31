using System.Globalization;
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
    public static bool TryLoad(string path, out GatewayConfiguration? config, out IReadOnlyList<string> errors) =>
        TryLoad(path, null, out config, out errors);

    /// <summary><see cref="TryLoad(string, out GatewayConfiguration?, out IReadOnlyList{string})"/> with the sides of <paramref name="sides"/> (null = <see cref="SideRegistry.Builtin"/>).</summary>
    public static bool TryLoad(string path, SideRegistry? sides, out GatewayConfiguration? config, out IReadOnlyList<string> errors)
    {
        var result = Load(path, sides);
        config = result.Configuration;
        errors = result.Errors;
        return result.Kind == ConfigurationLoadKind.Loaded;
    }

    /// <summary>Reads and validates the file.</summary>
    public static ConfigurationLoadResult Load(string path) => Load(path, null);

    /// <summary>Reads and validates the file with the sides of <paramref name="sides"/> (null = <see cref="SideRegistry.Builtin"/>).</summary>
    public static ConfigurationLoadResult Load(string path, SideRegistry? sides)
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

        return Parse(text, path, sides);
    }

    /// <summary>Validates configuration text; <paramref name="source"/> (usually the file path) prefixes every error.</summary>
    public static ConfigurationLoadResult Parse(string json, string source = "configuration") => Parse(json, source, null);

    /// <summary>
    /// Validates configuration text with the sides of <paramref name="sides"/> (null = <see cref="SideRegistry.Builtin"/>): the known
    /// top-level keys are "shared", "hrot" and the key of each side; any other key is an error naming it.
    /// </summary>
    public static ConfigurationLoadResult Parse(string json, string source, SideRegistry? sides)
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
            var config = Read(document.RootElement, source, errors, sides ?? SideRegistry.Builtin);
            return errors.Count > 0 || config is null
                ? Invalid(errors)
                : new ConfigurationLoadResult(ConfigurationLoadKind.Loaded, config, []);
        }
    }

    /// <summary>The configuration as JSON with every key written (it parses back to an equal configuration).</summary>
    public static string ToJson(GatewayConfiguration config) => ToJson(config, null);

    /// <summary>The configuration as JSON with every key written, each side through <see cref="IProtocolSide.Write"/>; <paramref name="sides"/> null = <see cref="SideRegistry.Builtin"/>.</summary>
    public static string ToJson(GatewayConfiguration config, SideRegistry? sides)
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
            foreach (var side in (sides ?? SideRegistry.Builtin).Sides)
            {
                if (config.SettingsOf(side.Key) is { } settings)
                {
                    w.WriteStartObject(side.Key);
                    side.Write(w, settings);
                    w.WriteEndObject();
                }
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ConfigurationLoadResult Invalid(IReadOnlyList<string> errors) =>
        new(ConfigurationLoadKind.Invalid, null, errors);

    // ---- reading ----------------------------------------------------------------------------------------------------

    private static GatewayConfiguration? Read(JsonElement root, string source, List<string> errors, SideRegistry sides)
    {
        var top = ConfigSection.Open(root, source, "", errors);
        if (top is null)
        {
            return null;
        }

        var shared = ReadShared(top.Child("shared"));
        var hrot = ReadHrot(top.Child("hrot"));
        var config = new GatewayConfiguration(shared, hrot, null, null);
        foreach (var side in sides.Sides)
        {
            var section = top.Child(side.Key);
            if (section is null)
            {
                continue;
            }

            var settings = side.Parse(section);
            if (settings is not null)
            {
                config = config.WithSide(side.Key, settings);
            }
        }

        top.Finish();
        return errors.Count > 0 ? null : config;
    }

    private static SharedConfiguration ReadShared(ConfigSection? s)
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

    private static DamageThresholds ReadThresholds(ConfigSection? s, DamageThresholds d)
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

    private static HrotConfiguration ReadHrot(ConfigSection? s)
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

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
