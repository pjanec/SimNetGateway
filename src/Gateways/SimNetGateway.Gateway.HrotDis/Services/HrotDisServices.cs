using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>The sources of the HROT DIS gateway: the two sides (R-061: one per peer). Nothing else writes the pivot yet.</summary>
public sealed record HrotDisSources(SourceId Hrot, SourceId Dis);

/// <summary>
/// The identity maps of the gateway (DD-006 section 3.2, DD-007 section 3 item 3): HROT entity ids (<c>long</c>); wire ids from the
/// pack are bound, ids for entities introduced to HROT (DIS entities) come from the injected allocator port.
/// </summary>
public sealed class HrotDisMaps
{
    public HrotDisMaps(IdentityMap<long> hrotEntity) => HrotEntity = hrotEntity;

    /// <summary>"hrot.entity".</summary>
    public IdentityMap<long> HrotEntity { get; }

    public static HrotDisMaps Create(IdentityRegistry identities, HrotDisSources sources, IIdAllocator<long> hrotIds)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(hrotIds);
        return new HrotDisMaps(identities.Create<long>(sources.Hrot, "hrot.entity", hrotIds));
    }
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
    }

    public GatewayConfiguration Config { get; }

    public GeoFrame Geo { get; }

    public EntityTypeCatalog EntityTypes { get; }

    public MunitionCatalog Munitions { get; }

    public DamageMapping Damage { get; }

    public DisEventIds EventIds { get; }

    public FireCorrelator Correlator { get; }

    /// <summary>
    /// The DIS side shows its entities and effects to HROT only in an open session: a valid origin, a session that is not
    /// idle and not in replay mode (DD-007 section 6: Idle removes the replicas, Replay shows none).
    /// </summary>
    public bool ReplicasAllowed(ISessionView session) =>
        Geo.IsValid && session.State != SessionState.Idle && session.Mode == SessionMode.Live;

    /// <summary>DIS fire and detonation reach HROT only while the exercise runs (not in Edit, not paused, never in replay).</summary>
    public bool EffectsAllowed(ISessionView session) =>
        Geo.IsValid && session.State == SessionState.Running && session.Mode == SessionMode.Live;
}
