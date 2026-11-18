using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>A plugin found in a folder, with the folder it was loaded from.</summary>
public sealed record DiscoveredPlugin(IGatewayPlugin Plugin, string Folder);

/// <summary>What <see cref="PluginDiscovery.Scan"/> found and what it left out (one line per skipped plugin or folder).</summary>
public sealed class PluginDiscoveryResult
{
    public PluginDiscoveryResult(IReadOnlyList<DiscoveredPlugin> plugins, IReadOnlyList<string> warnings)
    {
        Plugins = plugins;
        Warnings = warnings;
    }

    public IReadOnlyList<DiscoveredPlugin> Plugins { get; }

    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>
/// Finds plugins in folders (DD-016): a plugin folder holds a <c>plugin.json</c> <c>{ "assembly": "Name.dll" }</c> and the plugin's own assemblies; it is loaded into
/// a context of its own. An assembly is looked up in the host first (one type identity for the contract, the engine, the protocols and the host's own libraries),
/// then in the parent plugin (an add-on of a plugin sees its plugin), then in the plugin's folder.
/// </summary>
public static class PluginDiscovery
{
    /// <summary>The manifest every plugin folder has.</summary>
    public const string ManifestName = "plugin.json";

    /// <summary>The plugins of the folders directly under <paramref name="pluginsFolder"/> (a folder without the manifest is not a plugin); a plugin of another interface version is skipped with one warning.</summary>
    public static PluginDiscoveryResult Scan(string pluginsFolder)
    {
        var warnings = new List<string>();
        var found = new List<DiscoveredPlugin>();
        foreach (var (instance, folder) in LoadAll<IGatewayPlugin>(pluginsFolder, null, warnings))
        {
            if (instance.InterfaceVersion != PluginInterface.Version)
            {
                warnings.Add($"plugin '{instance.Name}' in '{folder}' was built for plugin interface version {instance.InterfaceVersion}, this host speaks {PluginInterface.Version}: skipped.");
                continue;
            }

            found.Add(new DiscoveredPlugin(instance, folder));
        }

        return new PluginDiscoveryResult(found, warnings);
    }

    /// <summary>
    /// The objects of type <typeparamref name="T"/> in the plugin folders directly under <paramref name="folder"/> (add-ons of a plugin, such as its bindings), each folder
    /// loaded into its own context with <paramref name="parent"/> as the plugin it belongs to; problems are added to <paramref name="warnings"/>.
    /// </summary>
    public static IReadOnlyList<(T Instance, string Folder)> LoadAll<T>(string folder, AssemblyLoadContext? parent, List<string> warnings)
        where T : class
    {
        var result = new List<(T, string)>();
        if (!Directory.Exists(folder))
        {
            return result;
        }

        foreach (string dir in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.Ordinal))
        {
            string manifest = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                string assemblyName;
                using (var doc = JsonDocument.Parse(File.ReadAllText(manifest)))
                {
                    assemblyName = doc.RootElement.GetProperty("assembly").GetString() ?? throw new InvalidDataException("no assembly named");
                }

                string path = Path.Combine(dir, assemblyName);
                var context = new PluginLoadContext(dir, parent);
                var assembly = context.LoadFromAssemblyPath(path);
                foreach (var type in TypesOf(assembly, warnings, dir))
                {
                    if (type.IsPublic && !type.IsAbstract && typeof(T).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
                    {
                        result.Add(((T)Activator.CreateInstance(type)!, dir));
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"plugin folder '{dir}' could not be loaded: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return result;
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly, List<string> warnings, string dir)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            warnings.Add($"plugin folder '{dir}': some types could not be loaded ({ex.LoaderExceptions.FirstOrDefault()?.Message}).");
            return ex.Types.Where(t => t is not null)!;
        }
    }

    /// <summary>The context of one plugin folder: host first, then the parent plugin, then the folder.</summary>
    public sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _folder;
        private readonly AssemblyLoadContext? _parent;

        public PluginLoadContext(string folder, AssemblyLoadContext? parent)
            : base("plugin:" + folder)
        {
            _folder = folder;
            _parent = parent;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            foreach (var shared in new[] { AssemblyLoadContext.Default, _parent })
            {
                if (shared is null)
                {
                    continue;
                }

                try
                {
                    return shared.LoadFromAssemblyName(assemblyName);
                }
                catch (FileNotFoundException)
                {
                }
            }

            string path = Path.Combine(_folder, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string path = Path.Combine(_folder, unmanagedDllName);
            if (!File.Exists(path) && !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                path += ".dll";
            }

            return File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}
