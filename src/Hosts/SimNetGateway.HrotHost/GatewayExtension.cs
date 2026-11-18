using System.Text.Json;
using Hrot.Common.Infrastructure;
using SimNetGateway.Gateway.HrotDis;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The HROT node extension (DD-016): contributes the gateway capability to the offline node (the editor) when <c>simnetgateway.json</c> (or
/// <c>SIMNETGATEWAY_CONFIG</c>) exists and is valid; null otherwise. Every protocol side is a plugin found in the <c>plugins</c> folder beside the host.
/// A refusal is one line in <see cref="Errors"/> and on stderr: an invalid file (a section whose plugin is not installed is one), a configuration a plugin
/// cannot run (<see cref="IGatewayPlugin.Check"/>), no enabled side.
/// </summary>
public sealed class GatewayExtension : INodeExtension
{
    /// <summary>"simnetgateway".</summary>
    public const string ExtensionName = "simnetgateway";

    private readonly Func<string> _configPath;
    private readonly Lazy<PluginSet> _plugins;
    private readonly List<string> _errors = new();

    /// <summary>The way HROT builds it: the file is <c>SIMNETGATEWAY_CONFIG</c> or <c>simnetgateway.json</c> in the working directory; the plugins are those of the folder beside the host.</summary>
    public GatewayExtension()
        : this(GatewayConfigurationFile.ResolvePath, () => PluginSet.FromFolder(PluginSet.DefaultFolder))
    {
    }

    public GatewayExtension(Func<string> configPath, PluginSet plugins)
        : this(configPath, () => plugins ?? throw new ArgumentNullException(nameof(plugins)))
    {
    }

    private GatewayExtension(Func<string> configPath, Func<PluginSet> plugins)
    {
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _plugins = new Lazy<PluginSet>(plugins);
    }

    public string Name => ExtensionName;

    /// <summary>One line per refusal.</summary>
    public IReadOnlyList<string> Errors => _errors;

    public INodeCapability? CreateCapability(NodeExtensionContext context)
    {
        // the offline node only; in a cluster every process that sees the file would publish the same HROT entities again
        if (!context.Offline)
        {
            return null;
        }

        string path = _configPath();
        var plugins = _plugins.Value;
        var loaded = GatewayConfigurationFile.Load(path, plugins.Registry);
        switch (loaded.Kind)
        {
            case ConfigurationLoadKind.Missing:
                return null;
            case ConfigurationLoadKind.Invalid:
                Refuse($"configuration '{path}' is invalid, the gateway is not instantiated: {string.Join("; ", loaded.Errors)}{MissingPlugins(path, plugins)}");
                return null;
        }

        var config = loaded.Configuration!;
        foreach (var found in plugins.Plugins)
        {
            if (config.SettingsOf(found.Plugin.Side.Key) is { } settings && found.Plugin.Check(settings) is { } reason)
            {
                Refuse($"configuration '{path}': {reason}, the gateway is not instantiated.");
                return null;
            }
        }

        if (!config.AnySideEnabledIn(plugins.Registry))
        {
            Refuse($"configuration '{path}': no protocol side is enabled, the gateway is not instantiated.");
            return null;
        }

        foreach (string warning in plugins.Warnings)
        {
            Console.Error.WriteLine("[SimNetGateway] " + warning);
        }

        return new GatewayCapability(config, plugins);
    }

    // a section whose plugin is not in the plugins folder reads as an unknown key: say which plugin to install
    private static string MissingPlugins(string path, PluginSet plugins)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var missing = document.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(name => name is not ("shared" or "hrot") && plugins.Find(name) is null)
                .ToList();
            return missing.Count == 0
                ? string.Empty
                : $" (no plugin for the section{(missing.Count > 1 ? "s" : string.Empty)} {string.Join(", ", missing.Select(m => $"'{m}'"))} is installed in '{PluginSet.DefaultFolder}')";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private void Refuse(string line)
    {
        _errors.Add(line);
        Console.Error.WriteLine("[SimNetGateway] " + line);
    }
}
