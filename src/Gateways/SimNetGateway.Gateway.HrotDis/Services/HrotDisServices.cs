using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// The sources of the HROT DIS gateway: one per peer (R-061). <see cref="Rpr"/> is the HLA side's source, <see cref="SourceId.None"/>
/// while the HLA side does not run. Entities of origin dis and rpr are the replicas HROT shows (DD-009 section 3 item 4).
/// </summary>
public sealed record HrotDisSources(SourceId Hrot, SourceId Dis, SourceId Rpr = default)
{
    /// <summary>The origins declared by sides of other projects (<see cref="SideDeclaration.Origin"/>); entities of these origins are replicas too.</summary>
    public IReadOnlyList<SourceId> Others { get; init; } = Array.Empty<SourceId>();

    /// <summary>True for an entity origin that is a replica of a peer simulation (any declared side origin: dis, rpr or another side's), not the HROT side's own.</summary>
    public bool IsReplicaOrigin(SourceId origin)
    {
        if (origin == Dis || (Rpr != SourceId.None && origin == Rpr))
        {
            return true;
        }

        for (int i = 0; i < Others.Count; i++)
        {
            if (Others[i] == origin)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The identity maps of the gateway (DD-006 section 3.2, DD-007 section 3 item 3): HROT entity ids (<c>long</c>); wire ids from the
/// pack are bound, ids for entities introduced to HROT (DIS entities) come from the injected allocator port. With the DIS side
/// running there is a second map, <c>dis.entity</c> (<see cref="DisEntityKey"/>): the ids of DIS entities are bound from the PDUs,
/// the ids of the HROT entities published to DIS come from a number pool with quarantine under the gateway's site and application.
/// The same map and pool give the entity identifier of the HLA side (DIS and RPR entity identifiers are one identifier space: a hrot entity has ONE
/// number on both sides, so a DIS/HLA bridge never merges two vehicles); the map exists when the DIS or the HLA side runs.
/// </summary>
public sealed class HrotDisMaps
{
    private readonly IdentityMap<DisEntityKey>? _disEntity;
    private readonly IdentityMap<RprObjectHandle>? _rprHandle;

    public HrotDisMaps(IdentityMap<long> hrotEntity, IdentityMap<DisEntityKey>? disEntity = null,
        IdentityMap<RprObjectHandle>? rprHandle = null)
    {
        HrotEntity = hrotEntity;
        _disEntity = disEntity;
        _rprHandle = rprHandle;
    }

    /// <summary>"hrot.entity".</summary>
    public IdentityMap<long> HrotEntity { get; }

    /// <summary>True when the DIS side runs and so <see cref="DisEntity"/> exists.</summary>
    public bool HasDis => _disEntity is not null;

    /// <summary>"dis.entity": bound from the PDU for entities of origin dis, allocated (site, application, number pool) for the entities published to DIS.</summary>
    public IdentityMap<DisEntityKey> DisEntity =>
        _disEntity ?? throw new InvalidOperationException("The DIS side is not running: there is no dis.entity map.");

    /// <summary>True when the HLA side runs and so <see cref="RprHandle"/> exists.</summary>
    public bool HasRpr => _rprHandle is not null;

    /// <summary>"rpr.handle": bound when the RTI answers a registration of a hrot entity, or when a handle is first seen on the wire (rpr entities).</summary>
    public IdentityMap<RprObjectHandle> RprHandle =>
        _rprHandle ?? throw new InvalidOperationException("The HLA side is not running: there is no rpr.handle map.");

    public static HrotDisMaps Create(IdentityRegistry identities, HrotDisSources sources, IIdAllocator<long> hrotIds, DisIdentityOptions? dis = null,
        bool hla = false)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(hrotIds);
        var hrot = identities.Create<long>(sources.Hrot, "hrot.entity", hrotIds);
        var disMap = dis is null ? null : identities.Create<DisEntityKey>(sources.Dis, "dis.entity", dis.CreateAllocator());
        IdentityMap<RprObjectHandle>? rprHandle = null;
        if (hla)
        {
            rprHandle = identities.Create<RprObjectHandle>(sources.Rpr, "rpr.handle");
        }

        return new HrotDisMaps(hrot, disMap, rprHandle);
    }
}

/// <summary>What the DIS ingress ignored and why (diagnostics and tests; the engine has no counter for a message a handler chose to skip).</summary>
public sealed class DisIngressCounters
{
    /// <summary>PDUs that carried the gateway's own site and application (a looped-back echo).</summary>
    public long Echoes { get; internal set; }

    /// <summary>PDUs of another exercise.</summary>
    public long WrongExercise { get; internal set; }

    /// <summary>Start/Resume and Stop/Freeze PDUs not acted on (<c>dis.disControlsHrot</c> off, not addressed to the gateway, or no valid transition).</summary>
    public long SimanIgnored { get; internal set; }

    /// <summary>Remove Entity PDUs for an entity the DIS side does not own in the pivot.</summary>
    public long RemovesIgnored { get; internal set; }
}

/// <summary>The services the modules share (DD-006 section 2 "Services"), created once per gateway by the profile.</summary>
public sealed class HrotDisServices
{
    public HrotDisServices(GatewayConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
        Geo = new GeoFrame(config.Hrot.AllowZeroOrigin);
        EntityTypes = new EntityTypeCatalog(config.Shared.DefaultTkbForUnknown);
        Munitions = new MunitionCatalog();
        Damage = new DamageMapping(config.Shared);
        EventIds = new DisEventIds((ushort)config.Shared.Site, (ushort)config.Shared.Application);
        Correlator = new FireCorrelator();
        Predictors = PredictorRegistry.CreateDis();
        DisIngress = new DisIngressCounters();
    }

    public GatewayConfiguration Config { get; }

    public GeoFrame Geo { get; }

    public EntityTypeCatalog EntityTypes { get; }

    public MunitionCatalog Munitions { get; }

    public DamageMapping Damage { get; }

    public DisEventIds EventIds { get; }

    public FireCorrelator Correlator { get; }

    /// <summary>The DIS dead-reckoning algorithms (R-060): the DIS egress predicts with them, nothing else needs them in this gateway.</summary>
    public PredictorRegistry Predictors { get; }

    /// <summary>What the DIS ingress ignored and why.</summary>
    public DisIngressCounters DisIngress { get; }

    /// <summary>The gateway's own DIS site (<c>shared.site</c>).</summary>
    public ushort OwnSite => (ushort)Config.Shared.Site;

    /// <summary>The gateway's own DIS application (<c>shared.application</c>).</summary>
    public ushort OwnApplication => (ushort)Config.Shared.Application;

    /// <summary>True when the identifiers name the gateway itself: a looped-back echo of its own traffic (the transport filters it too, the gateway does not rely on that).</summary>
    public bool IsOwn(ushort site, ushort application) => site == OwnSite && application == OwnApplication;

    /// <summary>
    /// The DIS side shows its entities and effects to HROT only in an open session: a valid origin, a session that is not
    /// idle and not in replay mode (DD-007 section 6: Idle removes the replicas, Replay shows none).
    /// </summary>
    public bool ReplicasAllowed(ISessionView session) =>
        Geo.IsValid && session.State != SessionState.Idle && session.Mode == SessionMode.Live;

    /// <summary>DIS fire and detonation reach HROT only while the exercise runs (not in Edit, not paused, never in replay).</summary>
    public bool EffectsAllowed(ISessionView session) =>
        Geo.IsValid && session.State == SessionState.Running && session.Mode == SessionMode.Live;

    /// <summary>
    /// HROT entities are shown to DIS only while the exercise runs (DD-007 section 6): Running or Paused, in either mode (a replay
    /// still goes from HROT to DIS); not in Edit (Loaded: authoring positions are not an exercise) and not Idle.
    /// </summary>
    public bool DisSessionOpen(ISessionView session) =>
        session.State == SessionState.Running || session.State == SessionState.Paused;

    /// <summary>Fire and detonation reach DIS in an open session; in a replay only with <c>shared.sendEventsInReplay</c> (R-087).</summary>
    public bool DisEventsAllowed(ISessionView session) =>
        DisSessionOpen(session) && (session.Mode == SessionMode.Live || Config.Shared.SendEventsInReplay);

    /// <summary>The HLA side shows HROT entities under the same rule as the DIS side (<see cref="DisSessionOpen"/>).</summary>
    public bool HlaSessionOpen(ISessionView session) => DisSessionOpen(session);

    /// <summary>Fire and detonation reach HLA under the same rule as DIS (<see cref="DisEventsAllowed"/>, R-087).</summary>
    public bool HlaEventsAllowed(ISessionView session) => DisEventsAllowed(session);
}
