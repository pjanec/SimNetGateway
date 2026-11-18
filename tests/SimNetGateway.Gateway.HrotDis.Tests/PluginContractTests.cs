using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>DD-016: the plugin contract and the registry a host builds from its plugins.</summary>
public class PluginContractTests
{
    private sealed class MinimalPlugin : IGatewayPlugin
    {
        public string Name => "dis";

        public int InterfaceVersion => PluginInterface.Version;

        public IProtocolSide Side { get; } = new DisSide();

        public PluginBound Bind(SideBinding binding) => PluginBound.None;
    }

    [Fact]
    public void A_plugin_with_the_defaults_has_no_host_service_and_wants_no_replay_events()
    {
        IGatewayPlugin plugin = new MinimalPlugin();
        Assert.Null(plugin.CreateHostService(new object(), new PluginHostContext(_ => { })));
        Assert.False(plugin.WantsEventsInReplay(new object()));
        Assert.Null(plugin.Check(new object(), new PluginHostContext(_ => { })));
        Assert.Null(PluginBound.None.Lease);
        Assert.Null(PluginBound.None.Pump);
        Assert.Equal(1, PluginInterface.Version);
    }

    [Fact]
    public void An_empty_registry_takes_the_sides_of_plugins_in_order_and_refuses_a_second_side_with_the_same_key()
    {
        var registry = SideRegistry.Empty.With(new DisSide()).With(new HlaSide());
        Assert.Equal(new[] { "dis", "hla" }, registry.Sides.Select(s => s.Key).ToArray());
        Assert.Empty(SideRegistry.Empty.Sides);
        Assert.Throws<ArgumentException>(() => registry.With(new DisSide()));
        Assert.Equal(new[] { "dis", "hla" }, SideRegistry.Builtin.Sides.Select(s => s.Key).ToArray()); // the built-in registry is unchanged
    }

    [Fact]
    public void A_configuration_file_is_read_with_the_sides_of_the_registry_only()
    {
        var onlyDis = SideRegistry.Empty.With(new DisSide());
        var load = GatewayConfigurationFile.Parse("""{ "dis": { "exerciseId": 1 }, "hla": { "federation": "RPR" } }""", "test", onlyDis);
        Assert.NotEmpty(load.Errors); // no plugin, no section
        Assert.Contains(load.Errors, e => e.Contains("hla"));
    }
}
