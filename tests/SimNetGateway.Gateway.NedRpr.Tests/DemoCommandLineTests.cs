using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>The demo host's command line: every option and combination, the defaults and the errors.</summary>
public class DemoCommandLineTests
{
    private static DemoCommandLine Parse(params string[] args) => DemoCommandLine.Parse(args);

    [Fact]
    public void No_arguments_run_the_default_demo_with_the_defaults()
    {
        var line = Parse();
        Assert.Null(line.Error);
        Assert.Equal(0, line.ExitCode);
        Assert.Equal(DemoMode.Demo, line.Mode);
        Assert.Equal("ned-rpr/default", line.ConfigName);
        Assert.Equal(10_000, line.Entities);
        Assert.Equal(30.0, line.Seconds);
        Assert.Equal(1, line.Seed);
        Assert.Equal(1.0, line.ThresholdMetres);
        Assert.Same(NedRprConfigurations.Default, line.Configuration);
    }

    [Theory]
    [InlineData("--list", DemoMode.List)]
    [InlineData("--schema", DemoMode.Schema)]
    [InlineData("--bench", DemoMode.Bench)]
    [InlineData("--showcase", DemoMode.Showcase)]
    [InlineData("--help", DemoMode.Help)]
    [InlineData("-h", DemoMode.Help)]
    public void Each_mode_option_selects_its_mode(string option, DemoMode mode)
    {
        var line = Parse(option);
        Assert.Null(line.Error);
        Assert.Equal(mode, line.Mode);
        Assert.Equal(0, line.ExitCode);
    }

    [Fact]
    public void Config_selects_the_named_configuration()
    {
        var line = Parse("--config", "ned-rpr/default", "--schema");
        Assert.Null(line.Error);
        Assert.Equal(DemoMode.Schema, line.Mode);
        Assert.Same(NedRprConfigurations.Default, line.Configuration);
    }

    [Fact]
    public void Bench_options_are_read_in_any_order()
    {
        var line = Parse("--threshold", "2.5", "--bench", "--seed", "7", "--seconds", "12.5", "--entities", "300");
        Assert.Null(line.Error);
        Assert.Equal(DemoMode.Bench, line.Mode);
        Assert.Equal(300, line.Entities);
        Assert.Equal(12.5, line.Seconds);
        Assert.Equal(7, line.Seed);
        Assert.Equal(2.5, line.ThresholdMetres);
    }

    [Fact]
    public void Each_bench_option_changes_only_its_own_value()
    {
        Assert.Equal(123, Parse("--bench", "--entities", "123").Entities);
        Assert.Equal(30.0, Parse("--bench", "--entities", "123").Seconds);
        Assert.Equal(4.0, Parse("--bench", "--seconds", "4").Seconds);
        Assert.Equal(10_000, Parse("--bench", "--seconds", "4").Entities);
        Assert.Equal(9, Parse("--bench", "--seed", "9").Seed);
        Assert.Equal(1.0, Parse("--bench", "--seed", "9").ThresholdMetres);
        Assert.Equal(0.0, Parse("--bench", "--threshold", "0").ThresholdMetres);
        Assert.Equal(1, Parse("--bench", "--threshold", "0").Seed);
        Assert.Equal(-3, Parse("--bench", "--seed", "-3").Seed);
    }

    [Fact]
    public void Numbers_use_the_invariant_culture()
    {
        Assert.Equal(0.75, Parse("--bench", "--threshold", "0.75").ThresholdMetres);
        Assert.NotNull(Parse("--bench", "--threshold", "0,75").Error);
    }

    [Fact]
    public void Bench_options_without_bench_are_accepted()
    {
        var line = Parse("--entities", "5");
        Assert.Null(line.Error);
        Assert.Equal(DemoMode.Demo, line.Mode);
        Assert.Equal(5, line.Entities);
    }

    [Fact]
    public void The_same_mode_twice_is_fine()
    {
        Assert.Null(Parse("--bench", "--bench").Error);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--bench extra")]
    [InlineData("--entities")]
    [InlineData("--bench --entities")]
    [InlineData("--entities --bench")]
    [InlineData("--entities abc")]
    [InlineData("--entities 1.5")]
    [InlineData("--entities 0")]
    [InlineData("--entities -4")]
    [InlineData("--entities 60001")]
    [InlineData("--seconds x")]
    [InlineData("--seconds 0")]
    [InlineData("--seconds -1")]
    [InlineData("--seconds NaN")]
    [InlineData("--seed")]
    [InlineData("--seed 1.5")]
    [InlineData("--seed 99999999999")]
    [InlineData("--threshold -0.5")]
    [InlineData("--threshold Infinity")]
    [InlineData("--threshold")]
    [InlineData("--config")]
    [InlineData("--config --bench")]
    [InlineData("--config nope/none")]
    [InlineData("--list --bench")]
    [InlineData("--schema --list")]
    [InlineData("--help --bench")]
    [InlineData("--bench --showcase")]
    public void A_bad_command_line_has_exit_code_2_and_an_error(string commandLine)
    {
        var line = DemoCommandLine.Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.NotNull(line.Error);
        Assert.Equal(2, line.ExitCode);
    }

    [Fact]
    public void An_unknown_option_names_it_and_lists_the_valid_options()
    {
        var line = Parse("--frobnicate");
        Assert.Contains("--frobnicate", line.Error);
        foreach (string option in new[] { "--config", "--list", "--schema", "--bench", "--entities", "--seconds", "--seed", "--threshold", "--showcase", "--help" })
        {
            Assert.Contains(option, line.Error);
        }
    }

    [Fact]
    public void An_unknown_configuration_names_it_and_lists_the_valid_ones()
    {
        var line = Parse("--config", "nope/none");
        Assert.Contains("nope/none", line.Error);
        foreach (var configuration in NedRprConfigurations.All)
        {
            Assert.Contains(configuration.Name, line.Error);
        }

        Assert.Null(line.Configuration);
    }

    [Fact]
    public void A_missing_value_names_the_option()
    {
        Assert.Contains("--entities", Parse("--bench", "--entities").Error);
        Assert.Contains("needs a value", Parse("--bench", "--entities").Error);
        Assert.Contains("--seed", Parse("--seed", "--bench").Error);
        Assert.Contains("needs a value", Parse("--seed", "--bench").Error);
        Assert.Contains("needs a value", Parse("--config", "--bench").Error);
    }

    [Fact]
    public void A_non_numeric_value_names_the_option_and_the_value()
    {
        var line = Parse("--seconds", "soon");
        Assert.Contains("--seconds", line.Error);
        Assert.Contains("soon", line.Error);
    }

    [Fact]
    public void The_registry_of_the_demo_lists_the_default_configuration()
    {
        Assert.Contains(DemoConfigurations.All, c => c.Name == "ned-rpr/default");
        Assert.Equal("ned-rpr/default", DemoConfigurations.DefaultName);
    }

    [Fact]
    public void Usage_mentions_every_option_and_configuration()
    {
        string usage = DemoCommandLine.Usage();
        foreach (string option in new[] { "--config", "--list", "--schema", "--bench", "--entities", "--seconds", "--seed", "--threshold", "--showcase", "--help" })
        {
            Assert.Contains(option, usage);
        }

        Assert.Contains("ned-rpr/default", usage);
    }
}
