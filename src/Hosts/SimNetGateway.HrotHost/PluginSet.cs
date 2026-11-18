using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.HrotHost;

/// <summary>The plugins a host runs with: their sides in order (the registry the configuration is read with) and the lines about what was skipped.</summary>
public sealed class PluginSet
{
    private readonly Dictionary<string, DiscoveredPlugin> _byKey;

    public PluginSet(IEnumerable<DiscoveredPlugin> plugins, IEnumerable<string>? warnings = null)
    {
        Warnings = (warnings ?? []).ToList();
        _byKey = new Dictionary<string, DiscoveredPlugin>(StringComparer.Ordinal);
        var accepted = new List<DiscoveredPlugin>();
        var registry = SideRegistry.Empty;
        foreach (var plugin in plugins)
        {
            string key = plugin.Plugin.Side.Key;
            if (_byKey.ContainsKey(key))
            {
                Warnings.Add($"plugin '{plugin.Plugin.Name}' in '{plugin.Folder}' has the side key '{key}' of another plugin: skipped.");
                continue;
            }

            _byKey[key] = plugin;
            accepted.Add(plugin);
            registry = registry.With(plugin.Plugin.Side);
        }

        Plugins = accepted;
        Registry = registry;
    }

    public IReadOnlyList<DiscoveredPlugin> Plugins { get; }

    public List<string> Warnings { get; }

    /// <summary>The sides of the plugins, in plugin order.</summary>
    public SideRegistry Registry { get; }

    /// <summary>The plugin whose side has this key (the key of its configuration section), or null.</summary>
    public DiscoveredPlugin? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>The plugins found in a folder (the plugins folder of the host).</summary>
    public static PluginSet FromFolder(string folder)
    {
        var result = PluginDiscovery.Scan(folder);
        return new PluginSet(result.Plugins, result.Warnings);
    }

    /// <summary>Plugins given in code (tests): no folder, so no add-ons are looked up for them.</summary>
    public static PluginSet Of(params IGatewayPlugin[] plugins) => new(plugins.Select(p => new DiscoveredPlugin(p, string.Empty)));

    /// <summary>The <c>plugins</c> folder beside the host assembly (not beside the process: the extension lives in a folder of its own under the host).</summary>
    public static string DefaultFolder { get; } = Path.Combine(Path.GetDirectoryName(typeof(PluginSet).Assembly.Location) ?? AppContext.BaseDirectory, "plugins");
}

/// <summary>The host services of the running plugins: created once per capability, pumped every frame, disposed in reverse order after every gateway runtime is closed.</summary>
public sealed class HostServices : IDisposable
{
    private readonly List<(string Key, IHostService Service)> _services = new();

    public HostServices(PluginSet plugins, GatewayConfiguration config, Action<string> log)
    {
        try
        {
            foreach (var found in plugins.Plugins)
            {
                string key = found.Plugin.Side.Key;
                if (config.SettingsOf(key) is not { } settings || !found.Plugin.Side.Runs(settings))
                {
                    continue;
                }

                var service = found.Plugin.CreateHostService(settings, new PluginHostContext(log, string.IsNullOrEmpty(found.Folder) ? null : found.Folder));
                if (service is not null)
                {
                    _services.Add((key, service));
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IHostService? Of(string key) => _services.Find(s => s.Key == key).Service;

    /// <summary>Services each of them once; one failing service must not stop the others.</summary>
    public void Pump(Action<string> log)
    {
        foreach (var (key, service) in _services)
        {
            try
            {
                service.Pump();
            }
            catch (Exception ex)
            {
                log($"the service of '{key}' failed while pumping: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        for (int i = _services.Count - 1; i >= 0; i--)
        {
            try
            {
                _services[i].Service.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SimNetGateway] closing the service of '{_services[i].Key}' failed: {ex.Message}");
            }
        }

        _services.Clear();
    }
}
