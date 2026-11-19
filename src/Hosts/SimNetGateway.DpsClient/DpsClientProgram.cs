using System.Diagnostics;
using System.Globalization;

namespace SimNetGateway.DpsClient;

/// <summary>The command line of the reference client: <c>--domain N --partition NAME [--headless --seconds N]</c>.</summary>
public static class DpsClientProgram
{
    public const string Usage = "usage: DpsClient --domain N --partition NAME [--headless --seconds N]";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        uint domain = 0;
        string partition = "station-1";
        bool headless = false;
        double seconds = 5.0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--domain" when i + 1 < args.Length && uint.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var d):
                    domain = d;
                    i++;
                    break;
                case "--partition" when i + 1 < args.Length:
                    partition = args[++i];
                    break;
                case "--seconds" when i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0:
                    seconds = s;
                    i++;
                    break;
                case "--headless":
                    headless = true;
                    break;
                default:
                    error.WriteLine($"unknown or incomplete argument: {args[i]}");
                    error.WriteLine(Usage);
                    return 2;
            }
        }

        using var world = new DpsWorld(domain, partition);
        if (!headless)
        {
            return DpsClientWindow.Run(world, domain, partition);
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            world.Pump();
            Thread.Sleep(20);
        }

        world.Pump();
        output.WriteLine(
            $"entities={world.Entities.Count} slot={(world.Slot is { } slot ? slot.State.ToString() : "none")} gizmo_layers={world.Gizmos.Count} client_id={world.ClientId}");
        return 0;
    }
}
