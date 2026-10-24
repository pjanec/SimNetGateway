using SimNetGateway.Engine;
using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>The modes of the demo host behind <c>Program.Main</c>, run through <see cref="DemoRunner"/>.</summary>
public class DemoRunnerTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = DemoRunner.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void List_prints_the_configuration_names()
    {
        var (exit, output, _) = Run("--list");
        Assert.Equal(0, exit);
        Assert.Contains("ned-rpr/default", output.Split('\n'));
    }

    [Fact]
    public void Schema_prints_the_validation_result_and_every_attribute_and_event()
    {
        var (exit, output, error) = Run("--schema");
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        Assert.Contains("0 errors, 0 warnings", output);

        var attributes = new AttributeRegistry();
        var events = new EventTypeRegistry();
        _ = new PivotSchema(attributes, events);
        Assert.True(attributes.Count > 15);
        foreach (var attribute in attributes)
        {
            Assert.Contains(attribute.Name, output);
        }

        Assert.True(events.Count >= 2);
        for (int id = 1; id <= events.Count; id++)
        {
            Assert.Contains(events.NameOf(new EventTypeId(id)), output);
        }
    }

    [Fact]
    public void Schema_of_an_explicit_configuration_is_the_same()
    {
        Assert.Equal(Run("--schema").Out, Run("--config", "ned-rpr/default", "--schema").Out);
    }

    [Fact]
    public void Showcase_is_not_available_before_stage_7()
    {
        var (exit, output, error) = Run("--showcase");
        Assert.Equal(2, exit);
        Assert.Contains("not available before stage 7", error);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void Help_prints_the_usage_and_exits_0()
    {
        var (exit, output, _) = Run("--help");
        Assert.Equal(0, exit);
        Assert.Equal(DemoCommandLine.Usage(), output);
    }

    [Fact]
    public void An_unknown_option_prints_the_valid_ones_and_exits_2()
    {
        var (exit, output, error) = Run("--nonsense");
        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, output);
        Assert.Contains("--nonsense", error);
        Assert.Contains("--bench", error);
    }

    [Fact]
    public void An_unknown_configuration_prints_the_valid_ones_and_exits_2()
    {
        var (exit, _, error) = Run("--config", "other/one", "--bench");
        Assert.Equal(2, exit);
        Assert.Contains("other/one", error);
        Assert.Contains("ned-rpr/default", error);
    }

    [Fact]
    public void Bench_runs_with_the_given_entity_count_and_prints_the_report()
    {
        var (exit, output, error) = Run("--bench", "--entities", "50", "--seconds", "1", "--seed", "3", "--threshold", "2");
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        Assert.Contains("Benchmark ned-rpr/default", output);
        Assert.Contains("50 (35 NED-origin, 15 RPR-origin", output);
        Assert.Contains("3 / 2.00 m", output);
        Assert.Contains("measurement 1.0 s (60 ticks)", output);
    }

    [Fact]
    public void The_default_mode_runs_the_scripted_demo()
    {
        var (exit, output, _) = Run();
        Assert.Equal(0, exit);
        Assert.StartsWith("Timeline", output);
        Assert.Contains("Summary", output);
        Assert.Contains("NedEntityDisposed", output);
    }
}
