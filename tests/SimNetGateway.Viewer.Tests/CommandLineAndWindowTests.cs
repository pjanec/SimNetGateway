namespace SimNetGateway.Viewer.Tests;

/// <summary>The command line, and the behaviour without a display or without the native library (DD-005 section 7, R-076).</summary>
public class CommandLineAndWindowTests
{
    [Fact]
    public void The_defaults_are_the_convoy_scenario_in_a_window()
    {
        var line = ViewerCommandLine.Parse([]);

        Assert.Null(line.Error);
        Assert.Equal(ViewerScenario.Convoy, line.Scenario);
        Assert.False(line.Headless);
        Assert.Equal(1.0, line.Speed);
        Assert.Equal(ViewerCommandLine.DefaultEntities, line.Entities);
        Assert.False(line.Help);
    }

    [Theory]
    [InlineData("convoy", ViewerScenario.Convoy)]
    [InlineData("demo", ViewerScenario.Demo)]
    [InlineData("traffic", ViewerScenario.Traffic)]
    [InlineData("empty", ViewerScenario.Empty)]
    [InlineData("TRAFFIC", ViewerScenario.Traffic)]
    public void Every_scenario_can_be_chosen(string name, ViewerScenario expected)
    {
        var line = ViewerCommandLine.Parse(["--scenario", name]);

        Assert.Null(line.Error);
        Assert.Equal(expected, line.Scenario);
    }

    [Fact]
    public void Entities_speed_and_frames_are_parsed_with_the_invariant_culture()
    {
        var line = ViewerCommandLine.Parse(["--headless", "--frames", "1200", "--speed", "0.25", "--entities", "5000", "--scenario", "traffic"]);

        Assert.Null(line.Error);
        Assert.True(line.Headless);
        Assert.Equal(1200, line.Frames);
        Assert.Equal(0.25, line.Speed);
        Assert.Equal(5000, line.Entities);
    }

    [Theory]
    [InlineData("--scenario", "bogus")]
    [InlineData("--speed", "9")]
    [InlineData("--speed", "0.1")]
    [InlineData("--speed", "fast")]
    [InlineData("--entities", "0")]
    [InlineData("--entities", "60001")]
    [InlineData("--entities", "many")]
    public void Bad_values_are_refused_with_exit_code_2_and_the_usage(string option, string value)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var launcher = new RecordingLauncher();

        int exit = ViewerProgram.Run([option, value], output, error, launcher);

        Assert.Equal(2, exit);
        Assert.Contains(option, error.ToString());
        Assert.Contains("Usage:", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(0, launcher.Checks);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--frames")]
    [InlineData("--frames", "10")]            // only with --headless
    [InlineData("--headless", "--frames", "-1")]
    public void Bad_command_lines_are_refused(params string[] args)
    {
        var error = new StringWriter();

        int exit = ViewerProgram.Run(args, new StringWriter(), error, new RecordingLauncher());

        Assert.Equal(2, exit);
        Assert.NotEqual(string.Empty, error.ToString());
    }

    [Fact]
    public void The_window_only_options_for_scripted_screenshots_are_parsed()
    {
        var line = ViewerCommandLine.Parse(["--screenshot-after", "6.5", "a.png", "--screenshot-after", "2", "b.png", "--open", "Rules,Trace",
            "--close", "Entities", "--select", "Hawk", "--trace-all", "--inspector-tab", "modules"]);

        Assert.Null(line.Error);
        Assert.Equal([(6.5, "a.png"), (2.0, "b.png")], line.Screenshots);
        Assert.Equal(["Rules", "Trace"], line.OpenPanels);
        Assert.Equal(["Entities"], line.ClosePanels);
        Assert.Equal("Hawk", line.SelectName);
        Assert.True(line.TraceAll);
        Assert.Equal("modules", line.InspectorTab);
        Assert.NotNull(ViewerCommandLine.Parse(["--screenshot-after", "x", "a.png"]).Error);
        Assert.NotNull(ViewerCommandLine.Parse(["--screenshot-after", "3"]).Error);
    }

    [Fact]
    public void Help_prints_the_usage_and_exits_with_0_without_opening_anything()
    {
        var output = new StringWriter();
        var launcher = new RecordingLauncher();

        int exit = ViewerProgram.Run(["--help"], output, new StringWriter(), launcher);

        Assert.Equal(0, exit);
        Assert.Contains("--scenario", output.ToString());
        Assert.Contains("--headless", output.ToString());
        Assert.Equal(0, launcher.Checks);
        Assert.Equal(0, launcher.Runs);
    }

    // ---- no display -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Windowed_mode_without_a_display_says_so_and_exits_with_code_2_without_opening_a_window()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var launcher = new RecordingLauncher { Problem = "no display (neither DISPLAY nor WAYLAND_DISPLAY is set)" };

        int exit = ViewerProgram.Run([], output, error, launcher);

        Assert.Equal(2, exit);
        Assert.Contains("cannot open a window", error.ToString());
        Assert.Contains("no display", error.ToString());
        Assert.Contains("--headless", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(1, launcher.Checks);
        Assert.Equal(0, launcher.Runs);
    }

    [Fact]
    public void Windowed_mode_with_a_display_hands_the_session_to_the_window_and_returns_its_exit_code()
    {
        var launcher = new RecordingLauncher();

        int exit = ViewerProgram.Run(["--scenario", "demo", "--speed", "2"], new StringWriter(), new StringWriter(), launcher);

        Assert.Equal(0, exit);
        Assert.Equal(1, launcher.Checks);
        Assert.Equal(1, launcher.Runs);
    }

    [Theory]
    [InlineData(true, null, null, false)]
    [InlineData(true, ":0", null, true)]
    [InlineData(true, null, "wayland-0", true)]
    [InlineData(true, "", "", false)]
    [InlineData(false, null, null, true)]
    public void The_display_check_needs_DISPLAY_or_WAYLAND_DISPLAY_on_Linux_only(bool linux, string? display, string? wayland, bool available)
    {
        string? problem = DisplayCheck.Problem(name => name switch { "DISPLAY" => display, "WAYLAND_DISPLAY" => wayland, _ => null }, linux);

        Assert.Equal(available, problem is null);
        if (!available)
        {
            Assert.Contains("no display", problem);
        }
    }

    [Fact]
    public void The_real_environment_check_never_throws_and_agrees_with_the_display_check()
    {
        // never opens a window: only asks. On a machine without a display it must name it; with one it may find the library or say why not.
        string? problem = new Frontend.RaylibLauncher().CheckEnvironment();

        if (DisplayCheck.Problem() is { } noDisplay)
        {
            Assert.Equal(noDisplay, problem);
        }
        else
        {
            Assert.True(problem is null || problem.Contains("raylib", StringComparison.OrdinalIgnoreCase), problem);
        }
    }

    [Fact]
    public void The_headless_run_without_frames_uses_the_default_count()
    {
        var output = new StringWriter();

        int exit = ViewerProgram.Run(["--headless", "--scenario", "empty"], output, new StringWriter(), new RecordingLauncher());

        Assert.Equal(0, exit);
        Assert.Contains($"frames: {ViewerCommandLine.DefaultFrames}", output.ToString());
        Assert.Contains($"ticks: {ViewerCommandLine.DefaultFrames}", output.ToString());
    }

    [Fact]
    public void An_unfinished_headless_Convoy_Ambush_is_not_a_pass()
    {
        var output = new StringWriter();

        int exit = ViewerProgram.Run(["--headless", "--frames", "100"], output, new StringWriter(), new RecordingLauncher());

        Assert.Equal(1, exit);
        Assert.Contains("Result: INCOMPLETE", output.ToString());
        Assert.DoesNotContain("Result: PASS", output.ToString());
    }
}
