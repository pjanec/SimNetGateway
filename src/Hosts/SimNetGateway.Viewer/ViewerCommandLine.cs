using System.Globalization;
using System.Text;

namespace SimNetGateway.Viewer;

/// <summary>
/// The parsed command line of the viewer: <c>--scenario convoy|demo|traffic|empty</c> (default convoy), <c>--entities N</c>
/// (traffic generator), <c>--speed X</c> (0.25 .. 8), <c>--headless --frames N</c> (no window; R-076) and <c>--help</c>. A bad
/// command line has <see cref="Error"/> set and <see cref="ExitCode"/> 2.
/// </summary>
public sealed class ViewerCommandLine
{
    public const int DefaultEntities = 1000;
    public const int DefaultFrames = 600;

    private ViewerCommandLine()
    {
    }

    public ViewerScenario Scenario { get; private set; } = ViewerScenario.Convoy;

    public int Entities { get; private set; } = DefaultEntities;

    public double Speed { get; private set; } = 1.0;

    public bool Headless { get; private set; }

    public int Frames { get; private set; } = DefaultFrames;

    public bool Help { get; private set; }

    /// <summary>Windowed mode: screenshots at wall-clock seconds after the start; the viewer exits after the last (for documentation and checks, never in the tests).</summary>
    public IReadOnlyList<(double Seconds, string Path)> Screenshots => _shots;

    /// <summary>Windowed mode: panels to open at the start (the first word of the title, case-insensitive).</summary>
    public IReadOnlyList<string> OpenPanels => _open;

    /// <summary>Windowed mode: panels to close at the start.</summary>
    public IReadOnlyList<string> ClosePanels => _close;

    /// <summary>Windowed mode: select the entity with this name as soon as it exists.</summary>
    public string? SelectName { get; private set; }

    /// <summary>Windowed mode: simulated seconds to run before the window opens (start in the middle of a scenario).</summary>
    public double Advance { get; private set; }

    /// <summary>Windowed mode: centre the map on the entity with this name once it exists, showing about <see cref="FocusMetres"/> across.</summary>
    public string? FocusName { get; private set; }

    public double FocusMetres { get; private set; } = 200;

    /// <summary>Windowed mode: the inspector tab shown first (attributes, modules, structure, writes).</summary>
    public string? InspectorTab { get; private set; }

    /// <summary>Windowed mode: start with the tracer recording everything.</summary>
    public bool TraceAll { get; private set; }

    private readonly List<(double, string)> _shots = new();
    private readonly List<string> _open = new();
    private readonly List<string> _close = new();

    /// <summary>The problem with the command line; null when it is valid.</summary>
    public string? Error { get; private set; }

    /// <summary>0 for a valid command line, 2 for an invalid one.</summary>
    public int ExitCode => Error is null ? 0 : 2;

    public static ViewerCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var line = new ViewerCommandLine();
        bool framesGiven = false;
        for (int i = 0; i < args.Count && line.Error is null; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                case "-?":
                    line.Help = true;
                    break;
                case "--headless":
                    line.Headless = true;
                    break;
                case "--scenario":
                    if (!Next(args, ref i, arg, line, out string? name))
                    {
                        break;
                    }

                    switch (name!.ToLowerInvariant())
                    {
                        case "convoy": line.Scenario = ViewerScenario.Convoy; break;
                        case "demo": line.Scenario = ViewerScenario.Demo; break;
                        case "traffic": line.Scenario = ViewerScenario.Traffic; break;
                        case "empty": line.Scenario = ViewerScenario.Empty; break;
                        default: line.Error = $"Unknown scenario '{name}'; use convoy, demo, traffic or empty."; break;
                    }

                    break;
                case "--entities":
                    if (Next(args, ref i, arg, line, out string? entities))
                    {
                        if (int.TryParse(entities, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= 60_000)
                        {
                            line.Entities = n;
                        }
                        else
                        {
                            line.Error = $"--entities needs a number from 1 to 60000, not '{entities}'.";
                        }
                    }

                    break;
                case "--speed":
                    if (Next(args, ref i, arg, line, out string? speed))
                    {
                        if (double.TryParse(speed, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                            && x >= ViewerSession.MinSpeed && x <= ViewerSession.MaxSpeed)
                        {
                            line.Speed = x;
                        }
                        else
                        {
                            line.Error = $"--speed needs a number from {ViewerSession.MinSpeed} to {ViewerSession.MaxSpeed}, not '{speed}'.";
                        }
                    }

                    break;
                case "--screenshot-after":
                    if (Next(args, ref i, arg, line, out string? after) && Next(args, ref i, arg, line, out string? path))
                    {
                        if (double.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) && sec >= 0)
                        {
                            line._shots.Add((sec, path!));
                        }
                        else
                        {
                            line.Error = $"--screenshot-after needs seconds and a path, not '{after}'.";
                        }
                    }

                    break;
                case "--open":
                    if (Next(args, ref i, arg, line, out string? open))
                    {
                        line._open.AddRange(open!.Split(',', StringSplitOptions.RemoveEmptyEntries));
                    }

                    break;
                case "--close":
                    if (Next(args, ref i, arg, line, out string? close))
                    {
                        line._close.AddRange(close!.Split(',', StringSplitOptions.RemoveEmptyEntries));
                    }

                    break;
                case "--select":
                    if (Next(args, ref i, arg, line, out string? select))
                    {
                        line.SelectName = select;
                    }

                    break;
                case "--inspector-tab":
                    if (Next(args, ref i, arg, line, out string? tab))
                    {
                        line.InspectorTab = tab;
                    }

                    break;
                case "--advance":
                    if (Next(args, ref i, arg, line, out string? advance))
                    {
                        if (double.TryParse(advance, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && a >= 0)
                        {
                            line.Advance = a;
                        }
                        else
                        {
                            line.Error = $"--advance needs simulated seconds, not '{advance}'.";
                        }
                    }

                    break;
                case "--map-focus":
                    if (Next(args, ref i, arg, line, out string? focus))
                    {
                        string[] parts = focus!.Split(',');
                        line.FocusName = parts[0];
                        if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double metres) && metres > 0)
                        {
                            line.FocusMetres = metres;
                        }
                    }

                    break;
                case "--trace-all":
                    line.TraceAll = true;
                    break;
                case "--frames":
                    if (Next(args, ref i, arg, line, out string? frames))
                    {
                        if (int.TryParse(frames, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) && f >= 0)
                        {
                            line.Frames = f;
                            framesGiven = true;
                        }
                        else
                        {
                            line.Error = $"--frames needs a number of at least 0, not '{frames}'.";
                        }
                    }

                    break;
                default:
                    line.Error = $"Unknown argument '{arg}'.";
                    break;
            }
        }

        if (line.Error is null && framesGiven && !line.Headless)
        {
            line.Error = "--frames only makes sense with --headless.";
        }

        return line;
    }

    public static string Usage()
    {
        var sb = new StringBuilder();
        sb.Append("SimNetGateway viewer - monitor and diagnostics viewer of the NED-RPR gateway (DD-005)\n");
        sb.Append("Usage: SimNetGateway.Viewer [options]\n");
        sb.Append("  --scenario convoy|demo|traffic|empty   what the gateway runs (default convoy: the Convoy Ambush showcase)\n");
        sb.Append($"  --entities N                           size of the traffic generator, 1..60000 (default {DefaultEntities})\n");
        sb.Append("  --speed X                              playback speed 0.25 .. 8 (default 1)\n");
        sb.Append($"  --headless --frames N                  no window: run N frames of the viewer's frame loop and print a summary (default {DefaultFrames})\n");
        sb.Append("  --screenshot-after S PATH              window only: save a PNG S seconds after the start (repeatable), exit after the last\n");
        sb.Append("  --open P1,P2 / --close P1,P2           window only: open or close panels at the start (Rules, Trace, Journal, Identities, ...)\n");
        sb.Append("  --select NAME / --trace-all            window only: select the entity of that name; trace everything from the start\n");
        sb.Append("  --advance S                            window only: run S simulated seconds before the window opens\n");
        sb.Append("  --map-focus NAME[,METRES]              window only: centre the map on that entity, METRES across (default 200)\n");
        sb.Append("  --help                                 this text\n");
        sb.Append("Exit codes: 0 success, 1 the headless Convoy Ambush did not pass, 2 bad command line or no window possible.\n");
        return sb.ToString();
    }

    private static bool Next(IReadOnlyList<string> args, ref int i, string option, ViewerCommandLine line, out string? value)
    {
        if (i + 1 >= args.Count)
        {
            line.Error = $"{option} needs a value.";
            value = null;
            return false;
        }

        value = args[++i];
        return true;
    }
}
