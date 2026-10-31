using System.Text.Json;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// One protocol side of the HROT gateway (DD-011 section 3a, T-026a): the DIS side, the HLA side and, from other projects, any
/// other peer. A side owns its configuration section, its ports and its modules. It does not create its transport: binding the
/// ports to transports stays with the composition root, keyed by the same side key (this project may not reference transports, DD-003).
/// A side object is stateless (shared by every gateway built from a registry): all per-gateway state lives in the declaration and the context.
/// </summary>
public interface IProtocolSide
{
    /// <summary>The top-level key of the side's section in the configuration file (e.g. "dis"); unique in a registry.</summary>
    string Key { get; }

    /// <summary>Reads the side's section (present in the file): records errors in <paramref name="section"/> and returns the settings, or null when it has none. It calls <see cref="ConfigSection.Finish"/> itself.</summary>
    object? Parse(ConfigSection section);

    /// <summary>Writes the properties of the side's section (every key; the caller writes the enclosing object) so that <see cref="Parse"/> reads back equal settings.</summary>
    void Write(Utf8JsonWriter writer, object settings);

    /// <summary>True when the side runs with these settings (a section can be present and disabled).</summary>
    bool Runs(object settings);

    /// <summary>Phase 1: declares the ports, the side's origin source, the use of the shared entity numbers and the identity maps it needs.</summary>
    void Declare(SideDeclaration declaration, object settings);

    /// <summary>Phase 2: registers the side's modules and processors, after the shared context (sources, maps, services) exists.</summary>
    void Register(SideContext context, object settings);
}

/// <summary>What a running side declares in phase 1 (see <see cref="IProtocolSide.Declare"/>); the profile reads it afterwards.</summary>
public sealed class SideDeclaration
{
    private readonly List<Action<IdentityRegistry>> _maps = new();

    internal SideDeclaration(GatewayBuilder builder, string key)
    {
        Builder = builder;
        Key = key;
    }

    /// <summary>The gateway being built: the side declares its ports here (<c>RequireInbound</c>, <c>RequireOutbound</c>).</summary>
    public GatewayBuilder Builder { get; }

    /// <summary>The key of the side being declared.</summary>
    public string Key { get; }

    /// <summary>
    /// The source of the entities this side introduces (normally the source of its inbound port). Entities of a declared origin are
    /// replicas the HROT side shows (<see cref="HrotDisSources.IsReplicaOrigin"/>); <see cref="SourceId.None"/> = the side introduces none.
    /// </summary>
    public SourceId Origin { get; set; }

    /// <summary>True when the side publishes HROT entities under the shared entity numbers (the one <c>dis.entity</c> map and pool of DIS and HLA, DD-009).</summary>
    public bool UsesEntityNumbers { get; set; }

    /// <summary>True for the HLA side: its origin is <see cref="HrotDisSources.Rpr"/> and the <c>rpr.handle</c> map exists (built-in sides only).</summary>
    internal bool IsRprSide { get; set; }

    internal IReadOnlyList<Action<IdentityRegistry>> MapRequests => _maps;

    /// <summary>Asks for an identity map named <paramref name="name"/> on the side's <see cref="Origin"/>, created after the shared maps in declaration order; read it from <see cref="SideContext.Map{TKey}"/>.</summary>
    public void RequestMap<TKey>(string name, IIdAllocator<TKey>? allocator = null)
        where TKey : notnull, IEquatable<TKey>
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _maps.Add(identities =>
        {
            if (Origin == SourceId.None)
            {
                throw new InvalidOperationException($"Side '{Key}' requested the map '{name}' but declared no origin.");
            }

            identities.Create(Origin, name, allocator);
        });
    }
}

/// <summary>What a running side gets in phase 2 (see <see cref="IProtocolSide.Register"/>): the gateway being built and everything the profile built from the declarations.</summary>
public sealed class SideContext
{
    internal SideContext(GatewayBuilder builder, GatewayConfiguration config, PivotSchema schema, HrotDisMaps maps, HrotDisSources sources,
        HrotDisServices services, IOutboundChannel<IGatewayToHrotMessage> hrotOut)
    {
        Builder = builder;
        Config = config;
        Schema = schema;
        Maps = maps;
        Sources = sources;
        Services = services;
        HrotOut = hrotOut;
    }

    /// <summary>The gateway being built: the side adds its modules here and finds the ports it declared (idempotent <c>RequireInbound</c> / <c>RequireOutbound</c>).</summary>
    public GatewayBuilder Builder { get; }

    /// <summary>The whole configuration (a side reads its own settings from the object passed to <see cref="IProtocolSide.Register"/>).</summary>
    public GatewayConfiguration Config { get; }

    /// <summary>The pivot vocabulary.</summary>
    public PivotSchema Schema { get; }

    /// <summary>The shared identity maps.</summary>
    public HrotDisMaps Maps { get; }

    /// <summary>The sources of the gateway.</summary>
    public HrotDisSources Sources { get; }

    /// <summary>The services shared by the modules.</summary>
    public HrotDisServices Services { get; }

    /// <summary>The outbound port to HROT.</summary>
    public IOutboundChannel<IGatewayToHrotMessage> HrotOut { get; }

    /// <summary>An identity map the side asked for with <see cref="SideDeclaration.RequestMap{TKey}"/>.</summary>
    public IdentityMap<TKey> Map<TKey>(string name)
        where TKey : notnull, IEquatable<TKey> =>
        Builder.Identities.Find(name) as IdentityMap<TKey>
        ?? throw new InvalidOperationException($"There is no identity map '{name}' with that key type.");
}

/// <summary>The sides a gateway can run, in order: the order of both configuration phases (declare, register) and so of ports, maps and modules.</summary>
public sealed class SideRegistry
{
    private readonly IProtocolSide[] _sides;

    private SideRegistry(IProtocolSide[] sides) => _sides = sides;

    /// <summary>The built-in sides: DIS, then HLA.</summary>
    public static SideRegistry Builtin { get; } = new([new DisSide(), new HlaSide()]);

    /// <summary>The sides in registration order.</summary>
    public IReadOnlyList<IProtocolSide> Sides => _sides;

    /// <summary>A registry with <paramref name="side"/> appended (this registry is unchanged); its key must be unused and not one of the reserved keys "shared" and "hrot".</summary>
    public SideRegistry With(IProtocolSide side)
    {
        ArgumentNullException.ThrowIfNull(side);
        if (string.IsNullOrEmpty(side.Key) || side.Key is "shared" or "hrot")
        {
            throw new ArgumentException($"'{side.Key}' is not a valid side key.", nameof(side));
        }

        if (Find(side.Key) is not null)
        {
            throw new ArgumentException($"A side with the key '{side.Key}' is already registered.", nameof(side));
        }

        return new SideRegistry([.. _sides, side]);
    }

    /// <summary>The side with the given key, or null.</summary>
    public IProtocolSide? Find(string key) => Array.Find(_sides, s => s.Key == key);
}
