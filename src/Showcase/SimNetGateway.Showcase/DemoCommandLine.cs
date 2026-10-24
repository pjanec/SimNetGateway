using System.Globalization;
using System.Text;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.NedRpr;

namespace SimNetGateway.Showcase;

/// <summary>What the demo host does (the mode options are mutually exclusive).</summary>
public enum DemoMode
{
    /// <summary>The scripted default demo.</summary>
    Demo,
    List,
    Schema,
    Bench,
    Showcase,
    Help,
}

/// <summary>The gateway configurations the demo can run: the static registries of the gateways it references (R-052, R-054).</summary>
public static class DemoConfigurations
{
    public const string DefaultName = "ned-rpr/default";

    /// <summary>Every configuration of every referenced gateway.</summary>
    public static IReadOnlyList<GatewayConfiguration> All { get; } = NedRprConfigurations.All;
}

/// <summary>
/// The parsed command line of the demo host: <c>--config &lt;name&gt;</c>, <c>--list</c>, <c>--schema</c>, <c>--bench</c> with
/// <c>--entities N --seconds S --seed K --threshold M</c>, <c>--showcase</c> (the DD-002 showcase; it ignores <c>--config</c>) and <c>--help</c>. A bad command line
/// has <see cref="ExitCode"/> 2 and an <see cref="Error"/> text that lists the valid values.
/// </summary>
public sealed class DemoCommandLine
{
    public const int DefaultEntities = 10_000;
    public const double DefaultSeconds = 30;
    public const int DefaultSeed = 1;
    public const double DefaultThreshold = 1.0;

    private DemoCommandLine(IReadOnlyList<GatewayConfiguration> configurations)
    {
        Configurations = configurations;
    }

    public DemoMode Mode { get; private set; } = DemoMode.Demo;

    public string ConfigName { get; private set; } = DemoConfigurations.DefaultName;

    public int Entities { get; private set; } = DefaultEntities;

    public double Seconds { get; private set; } = DefaultSeconds;

    public int Seed { get; private set; } = DefaultSeed;

    public double ThresholdMetres { get; private set; } = DefaultThreshold;

    /// <summary>The problem with the command line; null when it is valid.</summary>
    public string? Error { get; private set; }

    /// <summary>0 for a valid command line, 2 for an invalid one.</summary>
    public int ExitCode => Error is null ? 0 : 2;

    private IReadOnlyList<GatewayConfiguration> Configurations { get; }

    /// <summary>The configuration named by <see cref="ConfigName"/>; null when the command line is invalid.</summary>
    public GatewayConfiguration? Configuration
    {
        get
        {
            foreach (var configuration in Configurations)
            {
                if (string.Equals(configuration.Name, ConfigName, StringComparison.Ordinal))
                {
                    return configuration;
                }
            }

            return null;
        }
    }

    public static DemoCommandLine Parse(IReadOnlyList<string> args) => Parse(args, DemoConfigurations.All);

    public static DemoCommandLine Parse(IReadOnlyList<string> args, IReadOnlyList<GatewayConfiguration> configurations)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configurations);
        var line = new DemoCommandLine(configurations);
        bool modeSet = false;
        for (int i = 0; i < args.Count && line.Error is null; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--list":
                    line.SetMode(DemoMode.List, arg, ref modeSet);
                    break;
                case "--schema":
                    line.SetMode(DemoMode.Schema, arg, ref modeSet);
                    break;
                case "--bench":
                    line.SetMode(DemoMode.Bench, arg, ref modeSet);
                    break;
                case "--showcase":
                    line.SetMode(DemoMode.Showcase, arg, ref modeSet);
                    break;
                case "--help":
                case "-h":
                    line.SetMode(DemoMode.Help, arg, ref modeSet);
                    break;
                case "--config":
                    if (line.TryValue(args, ref i, out string? name))
                    {
                        line.ConfigName = name!;
                    }

                    break;
                case "--entities":
                    if (line.TryValue(args, ref i, out string? entities))
                    {
                        line.Entities = line.ParseInt(arg, entities!, 1, BenchmarkRunner.MaxEntities, line.Entities);
                    }

                    break;
                case "--seconds":
                    if (line.TryValue(args, ref i, out string? seconds))
                    {
                        line.Seconds = line.ParseDouble(arg, seconds!, positive: true, line.Seconds);
                    }

                    break;
                case "--seed":
                    if (line.TryValue(args, ref i, out string? seed))
                    {
                        line.Seed = line.ParseInt(arg, seed!, int.MinValue, int.MaxValue, line.Seed);
                    }

                    break;
                case "--threshold":
                    if (line.TryValue(args, ref i, out string? threshold))
                    {
                        line.ThresholdMetres = line.ParseDouble(arg, threshold!, positive: false, line.ThresholdMetres);
                    }

                    break;
                default:
                    line.Error = $"Unknown option '{arg}'. Valid options: {ValidOptions}.";
                    break;
            }
        }

        if (line.Error is null && line.Mode != DemoMode.Showcase && line.Configuration is null)
        {
            line.Error = $"Unknown configuration '{line.ConfigName}'. Valid configurations: " +
                string.Join(", ", configurations.Select(c => c.Name)) + ".";
        }

        return line;
    }

    /// <summary>The usage text.</summary>
    public static string Usage(IReadOnlyList<GatewayConfiguration>? configurations = null)
    {
        configurations ??= DemoConfigurations.All;
        var sb = new StringBuilder();
        sb.Append("SimNetGateway demo host\n");
        sb.Append("Usage: SimNetGateway.Demo [--config <name>] [--list | --schema | --bench | --showcase | --help]\n");
        sb.Append("  (no mode)           run the scripted demo and print its timeline\n");
        sb.Append("  --config <name>     the gateway configuration (default ").Append(DemoConfigurations.DefaultName).Append(")\n");
        sb.Append("  --list              print the configuration names\n");
        sb.Append("  --schema            print the validation result and the vocabulary with producers and consumers\n");
        sb.Append("  --bench             run the benchmark; options:\n");
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"      --entities N      number of entities (default {DefaultEntities})\n"));
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"      --seconds S       simulated seconds measured (default {DefaultSeconds})\n"));
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"      --seed K          random seed (default {DefaultSeed})\n"));
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"      --threshold M     sender dead-reckoning threshold in metres (default {DefaultThreshold})\n"));
        sb.Append("  --showcase          run the DD-002 showcase \"Convoy Ambush\" and print its timeline and PASS/FAIL table;\n");
        sb.Append("                      exit code 0 on PASS, 1 on FAIL; --config is ignored (always ").Append(NedRprConfigurations.Showcase.Name).Append(")\n");
        sb.Append("  --help              this text\n");
        sb.Append("Configurations: ").Append(string.Join(", ", configurations.Select(c => c.Name))).Append('\n');
        return sb.ToString();
    }

    private const string ValidOptions =
        "--config, --list, --schema, --bench, --entities, --seconds, --seed, --threshold, --showcase, --help";

    private void SetMode(DemoMode mode, string option, ref bool modeSet)
    {
        if (modeSet && Mode != mode)
        {
            Error = $"Option '{option}' conflicts with the mode already chosen ({ModeOption(Mode)}); give only one of " +
                "--list, --schema, --bench, --showcase, --help.";
            return;
        }

        Mode = mode;
        modeSet = true;
    }

    private static string ModeOption(DemoMode mode) => mode switch
    {
        DemoMode.List => "--list",
        DemoMode.Schema => "--schema",
        DemoMode.Bench => "--bench",
        DemoMode.Showcase => "--showcase",
        DemoMode.Help => "--help",
        _ => "demo",
    };

    private bool TryValue(IReadOnlyList<string> args, ref int index, out string? value)
    {
        string option = args[index];
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Error = $"Option '{option}' needs a value. Valid options: {ValidOptions}.";
            value = null;
            return false;
        }

        value = args[++index];
        return true;
    }

    private int ParseInt(string option, string text, int min, int max, int fallback)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            Error = $"Option '{option}' needs an integer, got '{text}'. Valid options: {ValidOptions}.";
            return fallback;
        }

        if (value < min || value > max)
        {
            Error = $"Option '{option}' must be {min}..{max}, got {value}. Valid options: {ValidOptions}.";
            return fallback;
        }

        return value;
    }

    private double ParseDouble(string option, string text, bool positive, double fallback)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
        {
            Error = $"Option '{option}' needs a number, got '{text}'. Valid options: {ValidOptions}.";
            return fallback;
        }

        if (positive ? value <= 0 : value < 0)
        {
            Error = $"Option '{option}' must be {(positive ? "greater than 0" : "0 or more")}, got {text}. Valid options: {ValidOptions}.";
            return fallback;
        }

        return value;
    }
}
