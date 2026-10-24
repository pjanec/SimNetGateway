using System.Globalization;
using System.Text;
using SimNetGateway.Viewer.Frontend;

namespace SimNetGateway.Viewer;

/// <summary>The window side of the viewer, behind a seam so that the headless mode and the tests never touch raylib (R-076).</summary>
public interface IWindowedLauncher
{
    /// <summary>Null when a window can be opened here, else the reason it cannot (no display, native library missing).</summary>
    string? CheckEnvironment();

    /// <summary>Opens the window and runs the frame loop until it is closed; returns the exit code.</summary>
    int Run(ViewerSession session, ViewerCommandLine line);
}

/// <summary>
/// The behaviour behind <c>Program.Main</c>, kept here so that it is testable: parses the command line, runs the headless
/// frame loop (<c>--headless --frames N</c>, no raylib, no ImGui) or hands the session to the window. Exit codes: 0 success,
/// 1 a headless Convoy Ambush that did not pass or finish, 2 bad command line or no window possible.
/// </summary>
public static class ViewerProgram
{
    /// <summary>The frame time of the headless loop: 60 frames per simulated second.</summary>
    public const double HeadlessFrameSeconds = 1.0 / 60.0;

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, IWindowedLauncher? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var line = ViewerCommandLine.Parse(args);
        if (line.Error is not null)
        {
            error.Write(line.Error + "\n");
            error.Write(ViewerCommandLine.Usage());
            return line.ExitCode;
        }

        if (line.Help)
        {
            output.Write(ViewerCommandLine.Usage());
            return 0;
        }

        if (line.Headless)
        {
            return RunHeadless(line, output);
        }

        launcher ??= CreateDefaultLauncher();
        string? problem = launcher.CheckEnvironment();
        if (problem is not null)
        {
            error.Write("The viewer cannot open a window: " + problem + "\n");
            error.Write("Use --headless --frames N to run the frame loop and the monitoring models without a window.\n");
            return 2;
        }

        var session = new ViewerSession(OptionsOf(line));
        Advance(session, line.Advance);
        return launcher.Run(session, line);
    }

    // kept out of Run so that the headless path never loads the window code
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IWindowedLauncher CreateDefaultLauncher() => new RaylibLauncher();

    /// <summary>The session settings of a command line.</summary>
    public static ViewerSessionOptions OptionsOf(ViewerCommandLine line) => new()
    {
        Scenario = line.Scenario,
        Entities = line.Entities,
        Speed = line.Speed,
    };

    /// <summary>Runs the session forward by simulated seconds at 60 frames per second (the window then opens in the middle of the scenario).</summary>
    public static void Advance(ViewerSession session, double seconds)
    {
        long target = (long)Math.Round(seconds * ViewerSession.TickRate);
        long guard = target * 4 + 100;
        while (session.TicksDone < target && !session.Finished && guard-- > 0)
        {
            session.Frame(HeadlessFrameSeconds * Math.Max(1.0, session.Speed) / session.Speed);
        }
    }

    /// <summary>Runs <paramref name="frames"/> frames of the session's loop at 60 frames per simulated second.</summary>
    public static void RunFrames(ViewerSession session, int frames)
    {
        ArgumentNullException.ThrowIfNull(session);
        for (int i = 0; i < frames; i++)
        {
            session.Frame(HeadlessFrameSeconds);
        }
    }

    private static int RunHeadless(ViewerCommandLine line, TextWriter output)
    {
        // no real-time cut in the headless mode: the run must not depend on how fast the machine is
        var session = new ViewerSession(OptionsOf(line) with { TickBudgetMs = double.PositiveInfinity });
        RunFrames(session, line.Frames);
        output.Write(Summary(session, line.Frames));
        if (session.Scenario == ViewerScenario.Convoy)
        {
            var report = session.ConvoyReport!;
            return session.Finished && report.Passed ? 0 : 1;
        }

        return 0;
    }

    /// <summary>The short text the headless mode prints.</summary>
    public static string Summary(ViewerSession session, int frames)
    {
        var c = CultureInfo.InvariantCulture;
        var engine = session.Engine;
        var sb = new StringBuilder();
        sb.Append(c, $"SimNetGateway viewer (headless): {session.ScenarioName}\n");
        sb.Append(c, $"frames: {frames} at {ViewerProgram.HeadlessFrameSeconds * 1000:F3} ms, speed x{session.Speed:0.##}\n");
        sb.Append(c, $"ticks: {session.TicksDone}");
        if (session.TotalTicks > 0)
        {
            sb.Append(c, $" of {session.TotalTicks} ({(session.Finished ? "finished" : "not finished")})");
        }

        sb.Append(c, $", sim time {engine.SimTime:F3} s, backlog {session.Backlog}, dropped {session.TicksDropped:F0}\n");
        sb.Append(c, $"entities: {engine.State.Count}, rules: {engine.Rules.Count}\n");
        sb.Append(c, $"models refreshed: dashboard {session.Refreshes.Dashboard}, map {session.Refreshes.Map}, tables {session.Refreshes.Tables}\n");
        sb.Append(c, $"tracer: attached (records once an entity or correlation is watched), {session.Tracer.Traces.Count} traces kept, {session.Tracer.WatchedEntities.Count} entities watched\n");
        sb.Append(c, $"tick time: p50 {session.Dashboard.TickP50:F3} ms, p95 {session.Dashboard.TickP95:F3} ms, max {session.Dashboard.TickMax:F3} ms\n");
        sb.Append(c, $"diagnostics: {session.Dashboard.Warnings} warnings, {session.Dashboard.Errors} errors, "
            + $"{session.Dashboard.Counters.TransactionFailures} transaction failures, {session.Dashboard.Counters.RuleFailures} rule failures\n");
        if (session.ConvoyConditions is { } conditions)
        {
            int passed = conditions.Count(x => x.Passed);
            sb.Append(c, $"success conditions: {passed} of {conditions.Count} passed, {session.ConvoyViolations!.Count} invariant violations\n");
            string verdict = session.Finished ? (session.ConvoyReport!.Passed ? "PASS" : "FAIL") : "INCOMPLETE";
            sb.Append("Result: ").Append(verdict).Append('\n');
        }

        return sb.ToString();
    }
}
