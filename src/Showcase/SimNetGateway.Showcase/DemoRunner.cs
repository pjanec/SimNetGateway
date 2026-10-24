using System.Globalization;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Showcase;

/// <summary>
/// The demo host's behaviour behind <c>Program.Main</c>: parses the command line, runs the chosen mode, prints, and returns
/// the exit code (0 success, 1 a showcase that failed, 2 bad command line). Kept here so that it is testable.
/// </summary>
public static class DemoRunner
{
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var line = DemoCommandLine.Parse(args);
        if (line.Error is not null)
        {
            error.Write(line.Error + "\n");
            error.Write(DemoCommandLine.Usage());
            return line.ExitCode;
        }

        switch (line.Mode)
        {
            case DemoMode.Help:
                output.Write(DemoCommandLine.Usage());
                return 0;
            case DemoMode.List:
                foreach (var configuration in DemoConfigurations.All)
                {
                    output.Write(configuration.Name + "\n");
                }

                return 0;
            case DemoMode.Schema:
                output.Write(Schema(line.Configuration!));
                return 0;
            case DemoMode.Showcase:
                var report = new ScenarioRunner().Run(ConvoyAmbushScript.Create());
                output.Write(report.Render());
                return report.Passed ? 0 : 1;
            case DemoMode.Bench:
                var result = BenchmarkRunner.Run(new BenchmarkOptions
                {
                    Configuration = line.Configuration,
                    Entities = line.Entities,
                    Seconds = line.Seconds,
                    Seed = line.Seed,
                    ThresholdMetres = line.ThresholdMetres,
                    Progress = text => output.Write(text + "\n"),
                });
                output.Write(result.Format());
                return 0;
            default:
                output.Write(DemoScenario.Run(new DemoOptions { Configuration = line.Configuration }).Format());
                return 0;
        }
    }

    /// <summary>The validation result and the vocabulary dump of a configuration.</summary>
    public static string Schema(GatewayConfiguration configuration)
    {
        var host = new NedRprHost(configuration, new ManualClock(), new NedRprHostOptions { Record = false });
        var report = host.Binding.Validation;
        var text = new System.Text.StringBuilder();
        text.Append(string.Create(CultureInfo.InvariantCulture,
            $"Validation of {configuration.Name}: {report.Errors.Count} errors, {report.Warnings.Count} warnings\n"));
        foreach (var issue in report.Errors)
        {
            text.Append("  ").Append(issue).Append('\n');
        }

        foreach (var issue in report.Warnings)
        {
            text.Append("  ").Append(issue).Append('\n');
        }

        text.Append('\n').Append(report.Dump());
        return text.ToString();
    }
}
