using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.FakeRti;

/// <summary>
/// The fake in-memory RTI (DD-008 sections 3.6 and 4): holds the federation executions and hands out
/// <see cref="FakeRtiAmbassador"/>s, one per federate. Single threaded, no timers, no randomness: all behaviour is a function
/// of the call sequence, callbacks wait in per-federate queues until the federate evokes them, and attribute and parameter
/// values are opaque octets that are never decoded.
/// </summary>
public sealed class FakeRtiHub
{
    /// <summary>Longest object instance name accepted (names are printable ASCII not starting with "HLA").</summary>
    public const int MaxObjectNameLength = 255;

    private readonly Dictionary<string, FakeFederation> _federations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FomModel> _modules = new(StringComparer.Ordinal);
    private readonly List<string> _trace = new();
    private int _ambassadorCount;

    /// <summary>
    /// Every service call, thrown exception and delivered callback in order, as text lines
    /// (<c>A1 call Name(args)</c>, <c>A1 throws ExceptionName</c>, <c>A2 callback Name(args)</c>); for assertions.
    /// </summary>
    public IReadOnlyList<string> Trace => _trace;

    /// <summary>Names of the federation executions that exist, ordered.</summary>
    public IReadOnlyList<string> FederationNames => _federations.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>A new ambassador (one federate's connection to this RTI).</summary>
    public FakeRtiAmbassador CreateAmbassador() => new(this, ++_ambassadorCount);

    /// <summary>Makes a FOM module available to <c>CreateFederationExecution</c> under a name, overriding <see cref="FomModules"/>.</summary>
    public void RegisterModule(string name, FomModel model)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(model);
        _modules[name] = model;
    }

    /// <summary>Number of federates currently joined to a federation; 0 when it does not exist.</summary>
    public int JoinedFederateCount(string federationName) =>
        _federations.TryGetValue(federationName, out var f) ? f.Federates.Count : 0;

    public void ClearTrace() => _trace.Clear();

    internal void Log(string line) => _trace.Add(line);

    internal FakeFederation? Find(string name) => _federations.TryGetValue(name, out var f) ? f : null;

    internal void Add(FakeFederation federation) => _federations.Add(federation.Name, federation);

    internal void Remove(string name) => _federations.Remove(name);

    internal FomModel ResolveModule(string name) =>
        _modules.TryGetValue(name, out var model) ? model : FomModules.Get(name);
}
