using Fdp.Core;
using Hrot.Common.Infrastructure;
using SimNetGateway.Gateway.HrotDis;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>The folder scripts/package-extension.sh makes, as an installation holds it: the layout and the plugins found in it.</summary>
public sealed class PackagedFolderTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "staged", "extensions", "simnetgateway");

    [Fact]
    public void The_folder_holds_the_host_and_the_shared_assemblies_and_nothing_of_the_product_it_extends()
    {
        Assert.True(Directory.Exists(Folder), "the package script did not stage " + Folder);
        var files = Directory.GetFiles(Folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.Contains("SimNetGateway.HrotHost.dll", files);
        Assert.Contains("SimNetGateway.Gateway.HrotDis.dll", files);
        Assert.DoesNotContain(files, f => f!.StartsWith("Hrot.", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Fdp.", StringComparison.OrdinalIgnoreCase)
            || f.StartsWith("CycloneDDS", StringComparison.OrdinalIgnoreCase) || f.Contains("ddsc", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetDirectories(Folder, "runtimes", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_plugin_folder_holds_no_assembly_the_host_already_holds()
    {
        var top = Directory.GetFiles(Folder, "*.dll", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToHashSet();
        foreach (string dll in Directory.GetFiles(Path.Combine(Folder, "plugins"), "*.dll", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(Path.GetFileName(dll), top);
        }
    }

    [Fact]
    public void The_plugins_of_the_folder_are_found_and_the_hla_plugin_finds_the_fake_rti_in_its_rti_folder()
    {
        var found = PluginDiscovery.Scan(Path.Combine(Folder, "plugins"));
        Assert.Empty(found.Warnings);
        Assert.Equal(["dis", "dps", "hla"], found.Plugins.Select(p => p.Plugin.Name).Order().ToArray());
        var hla = found.Plugins.Single(p => p.Plugin.Name == "hla");
        var bindings = (System.Collections.IEnumerable)hla.Plugin.GetType().GetMethod("Bindings")!.Invoke(hla.Plugin, [hla.Folder, null])!;
        Assert.Contains(bindings.Cast<object>(), b => (string)b.GetType().GetProperty("Name")!.GetValue(b)! == "fake");
    }

    [Fact]
    public void The_host_composes_from_the_discovered_plugins_and_runs_an_hla_configuration_on_the_fake_rti()
    {
        string path = Path.Combine(Path.GetTempPath(), "simnetgateway-staged-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "dis": { "exerciseId": 1 }, "hla": { "federation": "RPR" } }""");
        try
        {
            var extension = new GatewayExtension(() => path, PluginSet.FromFolder(Path.Combine(Folder, "plugins")));
            var capability = extension.CreateCapability(new NodeExtensionContext(NodeRole.None, true, "Editor", ""));
            Assert.IsType<GatewayCapability>(capability);
            Assert.Empty(extension.Errors);
            (capability as IDisposable)?.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
