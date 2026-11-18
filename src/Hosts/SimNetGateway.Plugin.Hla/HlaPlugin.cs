using System.Runtime.Loader;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Transport.HlaRpr;

namespace SimNetGateway.Plugin.Hla;

/// <summary>
/// The HLA side as a plugin: binds the port "rpr" to an RPR connection on the RTI that <c>hla.rti</c> names. The RTIs are bindings (<see cref="IRtiBinding"/>), found as
/// plugins in the <c>rti</c> folder beside this plugin, or given in code (tests); a configuration naming an RTI without a binding is refused.
/// </summary>
public sealed class HlaPlugin : IGatewayPlugin
{
    private readonly List<IRtiBinding> _bindings = new();
    private readonly object _lock = new();
    private bool _scanned;

    public HlaPlugin()
    {
    }

    /// <summary>With bindings given in code; the <c>rti</c> folder is not searched.</summary>
    public HlaPlugin(IEnumerable<IRtiBinding> bindings)
    {
        _bindings.AddRange(bindings);
        _scanned = true;
    }

    public string Name => HlaSide.SideKey;

    public int InterfaceVersion => PluginInterface.Version;

    public IProtocolSide Side { get; } = new HlaSide();

    /// <summary>The bindings known so far (the folder is scanned on first use).</summary>
    public IReadOnlyList<IRtiBinding> Bindings(string? pluginFolder, Action<string>? log = null)
    {
        lock (_lock)
        {
            if (!_scanned)
            {
                _scanned = true;
                if (pluginFolder is not null)
                {
                    var warnings = new List<string>();
                    var parent = AssemblyLoadContext.GetLoadContext(typeof(HlaPlugin).Assembly);
                    foreach (var (binding, _) in PluginDiscovery.LoadAll<IRtiBinding>(Path.Combine(pluginFolder, "rti"), parent, warnings))
                    {
                        _bindings.Add(binding);
                    }

                    foreach (string warning in warnings)
                    {
                        log?.Invoke(warning);
                    }
                }
            }

            return _bindings.ToList();
        }
    }

    public string? Check(object settings, PluginHostContext context)
    {
        var hla = (HlaConfiguration)settings;
        var found = Bindings(context.Folder, context.Log);
        if (found.Any(b => b.Name == hla.Rti))
        {
            return null;
        }

        string known = found.Count == 0 ? "none is installed" : "installed: " + string.Join(", ", found.Select(b => $"'{b.Name}'"));
        return $"'hla.rti' names the RTI '{hla.Rti}' but no binding for it is available ({known})";
    }

    public PluginBound Bind(SideBinding binding)
    {
        var hla = (HlaConfiguration)binding.Settings;
        var rti = Bindings(binding.PluginFolder, binding.Log).FirstOrDefault(b => b.Name == hla.Rti)
            ?? throw new InvalidOperationException($"No RTI binding named '{hla.Rti}'.");
        var settings = new RprHlaSettings
        {
            FederationName = hla.Federation,
            FederateName = hla.FederateName,
            FederateType = hla.FederateType,
            FomModules = hla.FomModules,
            CreateIfMissing = hla.CreateFederation,
        };
        var builder = binding.Builder;
        var connection = new RprHlaConnection(rti.CreateAmbassador(), settings, binding.Clock, builder.RequireInbound("rpr").Source, "rpr");
        try
        {
            connection.Start();
            builder.BindInbound("rpr", connection);
            builder.BindOutbound<IRprMessage>("rpr", connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new PluginBound(connection, connection.Pump);
    }
}
