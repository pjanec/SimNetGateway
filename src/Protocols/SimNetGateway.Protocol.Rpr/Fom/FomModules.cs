using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Protocol.Rpr.Fom;

/// <summary>
/// The code registry that resolves FOM module names to models (R-054, DD-008 section 3.2): the port's
/// <c>CreateFederationExecution</c> takes module names, not URLs or files. <c>RPR-FOM-2.0</c> is registered by default;
/// tests and other FOMs can register more. The real RTI binding passes the official XML modules instead.
/// </summary>
public static class FomModules
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Func<FomModel>> Registry = new(StringComparer.Ordinal)
    {
        [RprFom2.ModuleName] = RprFom2.Model,
    };

    /// <summary>The model registered under a name; <see cref="NameNotFound"/> when there is none.</summary>
    public static FomModel Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Func<FomModel>? factory;
        lock (Gate)
        {
            Registry.TryGetValue(name, out factory);
        }
        return factory?.Invoke() ?? throw new NameNotFound($"no FOM module registered as '{name}'");
    }

    public static bool TryGet(string name, out FomModel? model)
    {
        try
        {
            model = Get(name);
            return true;
        }
        catch (NameNotFound)
        {
            model = null;
            return false;
        }
    }

    /// <summary>Registers (or replaces) a module.</summary>
    public static void Register(string name, FomModel model)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(model);
        lock (Gate)
        {
            Registry[name] = () => model;
        }
    }

    /// <summary>The registered module names.</summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            lock (Gate)
            {
                return Registry.Keys.Order(StringComparer.Ordinal).ToList();
            }
        }
    }
}
