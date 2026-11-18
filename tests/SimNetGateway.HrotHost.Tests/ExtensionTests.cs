using Fdp.Core;
using Hrot.Common.Infrastructure;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Plugin.Dis;
using SimNetGateway.Plugin.Hla;
using SimNetGateway.Plugin.Hla.Rti.Fake;

namespace SimNetGateway.HrotHost.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}

/// <summary>When the extension composes and when it does not.</summary>
public sealed class ExtensionTests : IDisposable
{
    private const string Valid = """{ "dis": { "exerciseId": 1 } }""";
    private readonly string _dir = Directory.CreateTempSubdirectory("simnetgateway-ext-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File_(string json)
    {
        string path = Path.Combine(_dir, "simnetgateway.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static NodeExtensionContext Offline(bool offline = true) => new(NodeRole.None, offline, "Editor", "");

    private static GatewayExtension Extension(string path) => new(() => path, PluginSet.Of(new DisPlugin()));

    [Fact]
    public void An_offline_node_with_a_valid_file_gets_the_capability()
    {
        var x = Extension(File_(Valid));
        var capability = Assert.IsType<GatewayCapability>(x.CreateCapability(Offline()));
        Assert.Equal("cap:ext:simnetgateway", capability.Key);
        Assert.Empty(capability.Needs);
        Assert.Empty(x.Errors);
        Assert.Equal("simnetgateway", x.Name);
    }

    [Fact]
    public void Without_the_file_nothing_is_composed_and_it_is_no_error()
    {
        var x = Extension(Path.Combine(_dir, "absent.json"));
        Assert.Null(x.CreateCapability(Offline()));
        Assert.Empty(x.Errors);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "dis": { "exerciseId": 1, "port": 0 } }""")]
    [InlineData("""{ "dis": { "exerciseId": 1 }, "unknown": {} }""")]
    [InlineData("""{ "other": { "domain": 9 } }""")]
    public void An_invalid_file_composes_nothing_with_exactly_one_error_naming_the_file(string json)
    {
        string path = File_(json);
        var x = Extension(path);
        Assert.Null(x.CreateCapability(Offline()));
        string error = Assert.Single(x.Errors);
        Assert.Contains(path, error);
    }

    [Fact]
    public void Dis_controls_hrot_is_refused_with_one_error_naming_the_key()
    {
        var x = Extension(File_("""{ "dis": { "exerciseId": 1, "disControlsHrot": true } }"""));
        Assert.Null(x.CreateCapability(Offline()));
        Assert.Contains("disControlsHrot", Assert.Single(x.Errors));
    }

    [Theory]
    [InlineData("""{ "dis": { "exerciseId": 1 }, "hla": { "federation": "RPR" } }""")]
    [InlineData("""{ "hla": { "federation": "RPR", "enabled": false }, "dis": { "exerciseId": 1 } }""")]
    public void An_hla_section_without_a_binding_for_its_rti_is_refused_naming_hla_rti(string json)
    {
        var x = new GatewayExtension(() => File_(json), PluginSet.Of(new DisPlugin(), new HlaPlugin()));
        Assert.Null(x.CreateCapability(Offline()));
        string error = Assert.Single(x.Errors);
        Assert.Contains("'hla.rti'", error);
        Assert.Contains("'fake'", error);
    }

    [Fact]
    public void An_hla_section_with_the_binding_of_its_rti_composes()
    {
        var x = new GatewayExtension(() => File_("""{ "dis": { "exerciseId": 1 }, "hla": { "federation": "RPR", "rti": "fake" } }"""),
            PluginSet.Of(new DisPlugin(), new HlaPlugin([new FakeRtiBinding()])));
        Assert.IsType<GatewayCapability>(x.CreateCapability(Offline()));
        Assert.Empty(x.Errors);
    }

    [Fact]
    public void A_section_whose_plugin_is_not_installed_is_an_invalid_file_naming_the_section()
    {
        var x = new GatewayExtension(() => File_("""{ "dis": { "exerciseId": 1 }, "hla": { "federation": "RPR" } }"""), PluginSet.Of(new DisPlugin()));
        Assert.Null(x.CreateCapability(Offline()));
        string error = Assert.Single(x.Errors);
        Assert.Contains("'hla'", error);
        Assert.Contains("no plugin", error);
    }

    [Fact]
    public void A_networked_node_gets_nothing_even_with_a_valid_file_and_no_error()
    {
        var x = Extension(File_(Valid));
        Assert.Null(x.CreateCapability(Offline(offline: false)));
        Assert.Empty(x.Errors);
    }

    [Fact]
    public void A_file_with_no_enabled_side_composes_nothing_with_one_error()
    {
        var x = Extension(File_("""{ "dis": { "exerciseId": 1, "enabled": false } }"""));
        Assert.Null(x.CreateCapability(Offline()));
        Assert.Contains("no protocol side is enabled", Assert.Single(x.Errors));
    }

    [Collection(ProcessEnvironmentCollection.Name)]
    public sealed class ResolutionTests : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("simnetgateway-env-").FullName;
        private readonly string? _saved = Environment.GetEnvironmentVariable(GatewayConfigurationFile.PathVariable);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(GatewayConfigurationFile.PathVariable, _saved);
            Directory.Delete(_dir, recursive: true);
        }

        [Fact]
        public void The_default_extension_reads_the_file_named_by_SIMNETGATEWAY_CONFIG()
        {
            string path = Path.Combine(_dir, "elsewhere.json");
            File.WriteAllText(path, Valid);
            Environment.SetEnvironmentVariable(GatewayConfigurationFile.PathVariable, path);
            var x = new GatewayExtension(PluginSet.Of(new DisPlugin()));
            Assert.IsType<GatewayCapability>(x.CreateCapability(Offline()));

            Environment.SetEnvironmentVariable(GatewayConfigurationFile.PathVariable, Path.Combine(_dir, "nothing-here.json"));
            Assert.Null(new GatewayExtension(PluginSet.Of(new DisPlugin())).CreateCapability(Offline()));
        }

        [Fact]
        public void Without_the_variable_the_file_is_simnetgateway_json_in_the_working_directory()
        {
            Environment.SetEnvironmentVariable(GatewayConfigurationFile.PathVariable, null);
            string saved = Directory.GetCurrentDirectory();
            try
            {
                File.WriteAllText(Path.Combine(_dir, "simnetgateway.json"), Valid);
                Directory.SetCurrentDirectory(_dir);
                Assert.IsType<GatewayCapability>(new GatewayExtension(PluginSet.Of(new DisPlugin())).CreateCapability(Offline()));
            }
            finally
            {
                Directory.SetCurrentDirectory(saved);
            }
        }
    }
}
