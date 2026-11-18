using System.Reflection;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>A plugin of the current contract, for the discovery tests (public, with a default constructor).</summary>
public sealed class SampleDiscoveredPlugin : IGatewayPlugin
{
    public string Name => "sample";

    public int InterfaceVersion => PluginInterface.Version;

    public IProtocolSide Side => new DisSide();

    public PluginBound Bind(SideBinding binding) => PluginBound.None;
}

/// <summary>A plugin of a contract version the host does not speak.</summary>
public sealed class OtherVersionPlugin : IGatewayPlugin
{
    public string Name => "future";

    public int InterfaceVersion => PluginInterface.Version + 41;

    public IProtocolSide Side => new DisSide();

    public PluginBound Bind(SideBinding binding) => PluginBound.None;
}

/// <summary>DD-016: plugins are found in folders with a manifest, each in its own load context; a plugin of another interface version is skipped with one warning.</summary>
public sealed class PluginDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("simnetgateway-plugins-").FullName;

    // a loaded assembly stays locked on Windows: the temporary folder is removed when it can be
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string PluginFolder(string name, string? manifest = null)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        string assembly = Assembly.GetExecutingAssembly().Location;
        File.Copy(assembly, Path.Combine(dir, Path.GetFileName(assembly)), overwrite: true);
        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(dir, PluginDiscovery.ManifestName), manifest);
        }

        return dir;
    }

    private static string Manifest => "{ \"assembly\": \"" + Path.GetFileName(Assembly.GetExecutingAssembly().Location) + "\" }";

    [Fact]
    public void A_folder_with_a_manifest_yields_its_plugins_loaded_in_a_context_of_their_own()
    {
        string dir = PluginFolder("sample", Manifest);
        var result = PluginDiscovery.Scan(_root);
        var plugin = Assert.Single(result.Plugins, p => p.Plugin.Name == "sample");
        Assert.Equal(dir, plugin.Folder);
        // the contract is the host's (one type identity), the plugin's own assembly is a second copy in its own context
        Assert.IsAssignableFrom<IGatewayPlugin>(plugin.Plugin);
        Assert.NotSame(typeof(SampleDiscoveredPlugin).Assembly, plugin.Plugin.GetType().Assembly);
    }

    [Fact]
    public void A_plugin_of_another_interface_version_is_skipped_with_one_warning()
    {
        PluginFolder("mixed", Manifest);
        var result = PluginDiscovery.Scan(_root);
        Assert.DoesNotContain(result.Plugins, p => p.Plugin.Name == "future");
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("future", warning);
        Assert.Contains("interface version", warning);
    }

    [Fact]
    public void A_folder_without_a_manifest_is_not_a_plugin_and_a_missing_folder_is_empty()
    {
        PluginFolder("no-manifest");
        var result = PluginDiscovery.Scan(_root);
        Assert.Empty(result.Plugins);
        Assert.Empty(result.Warnings);
        Assert.Empty(PluginDiscovery.Scan(Path.Combine(_root, "absent")).Plugins);
    }

    [Fact]
    public void A_broken_manifest_or_assembly_is_one_warning_each_and_does_not_stop_the_others()
    {
        PluginFolder("a-broken", "{ not json");
        string missing = Path.Combine(_root, "b-missing");
        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, PluginDiscovery.ManifestName), "{ \"assembly\": \"Nothing.dll\" }");
        PluginFolder("c-good", Manifest);
        var result = PluginDiscovery.Scan(_root);
        Assert.Contains(result.Plugins, p => p.Plugin.Name == "sample");
        Assert.Equal(2, result.Warnings.Count(w => w.Contains("could not be loaded")));
        Assert.Single(result.Warnings, w => w.Contains("interface version")); // the future plugin of the good folder
    }

    [Fact]
    public void LoadAll_finds_the_add_ons_of_a_type_in_the_folders_under_a_folder()
    {
        PluginFolder("one", Manifest);
        var warnings = new List<string>();
        var found = PluginDiscovery.LoadAll<IGatewayPlugin>(_root, null, warnings);
        Assert.Contains(found, f => f.Instance.Name == "sample");
    }
}
