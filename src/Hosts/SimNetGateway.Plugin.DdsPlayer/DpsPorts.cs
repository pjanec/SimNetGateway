using SimNetGateway.Engine;
using SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The outbound ports of the dps side: one per published topic, named "dps.&lt;Topic&gt;". The side declares them (phase 1), its module writes to them, and the
/// plugin binds each to the writer of the runtime's <see cref="DpsTransport"/>.
/// </summary>
internal sealed class DpsPorts
{
    private DpsPorts(GatewayBuilder b)
    {
        Session = b.RequireOutbound<DpsItem<Session>>("dps.Session");
        Entity = b.RequireOutbound<DpsItem<Entity>>("dps.Entity");
        EntityState = b.RequireOutbound<DpsItem<EntityState>>("dps.EntityState");
        Articulation = b.RequireOutbound<DpsItem<Articulation>>("dps.Articulation");
        PersonState = b.RequireOutbound<DpsItem<PersonState>>("dps.PersonState");
        Appearance = b.RequireOutbound<DpsItem<Appearance>>("dps.Appearance");
        Fire = b.RequireOutbound<DpsItem<Fire>>("dps.Fire");
        Detonation = b.RequireOutbound<DpsItem<Detonation>>("dps.Detonation");
        Effect = b.RequireOutbound<DpsItem<Effect>>("dps.Effect");
        Impact = b.RequireOutbound<DpsItem<Impact>>("dps.Impact");
        VehicleLook = b.RequireOutbound<DpsItem<VehicleLook>>("dps.VehicleLook");
        Round = b.RequireOutbound<DpsItem<Round>>("dps.Round");
        MunitionLook = b.RequireOutbound<DpsItem<MunitionLook>>("dps.MunitionLook");
        FlameJet = b.RequireOutbound<DpsItem<FlameJet>>("dps.FlameJet");
        FireSpots = b.RequireOutbound<DpsItem<FireSpots>>("dps.FireSpots");
        GroundMarks = b.RequireOutbound<DpsItem<GroundMarks>>("dps.GroundMarks");
        Animal = b.RequireOutbound<DpsItem<Animal>>("dps.Animal");
        BuildingDamage = b.RequireOutbound<DpsItem<BuildingDamage>>("dps.BuildingDamage");
        Mover = b.RequireOutbound<DpsItem<Mover>>("dps.Mover");
        MoverTracks = b.RequireOutbound<DpsItem<MoverTracks>>("dps.MoverTracks");
        TrafficLights = b.RequireOutbound<DpsItem<TrafficLights>>("dps.TrafficLights");
        TkbType = b.RequireOutbound<DpsItem<TkbType>>("dps.TkbType");
        ManDowned = b.RequireOutbound<DpsItem<ManDowned>>("dps.ManDowned");
        PlayerMessage = b.RequireOutbound<DpsItem<PlayerMessage>>("dps.PlayerMessage");
        Status = b.RequireOutbound<DpsItem<Status>>("dps.Status");
        Exercise = b.RequireOutbound<DpsItem<Exercise>>("dps.Exercise");
        View = b.RequireOutbound<DpsItem<View>>("dps.View");
    }

    public IOutboundChannel<DpsItem<Session>> Session { get; }

    public IOutboundChannel<DpsItem<Entity>> Entity { get; }

    public IOutboundChannel<DpsItem<EntityState>> EntityState { get; }

    public IOutboundChannel<DpsItem<Articulation>> Articulation { get; }

    public IOutboundChannel<DpsItem<PersonState>> PersonState { get; }

    public IOutboundChannel<DpsItem<Appearance>> Appearance { get; }

    public IOutboundChannel<DpsItem<Fire>> Fire { get; }

    public IOutboundChannel<DpsItem<Detonation>> Detonation { get; }

    public IOutboundChannel<DpsItem<Effect>> Effect { get; }

    public IOutboundChannel<DpsItem<Impact>> Impact { get; }

    public IOutboundChannel<DpsItem<VehicleLook>> VehicleLook { get; }

    public IOutboundChannel<DpsItem<Round>> Round { get; }

    public IOutboundChannel<DpsItem<MunitionLook>> MunitionLook { get; }

    public IOutboundChannel<DpsItem<FlameJet>> FlameJet { get; }

    public IOutboundChannel<DpsItem<FireSpots>> FireSpots { get; }

    public IOutboundChannel<DpsItem<GroundMarks>> GroundMarks { get; }

    public IOutboundChannel<DpsItem<Animal>> Animal { get; }

    public IOutboundChannel<DpsItem<BuildingDamage>> BuildingDamage { get; }

    public IOutboundChannel<DpsItem<Mover>> Mover { get; }

    public IOutboundChannel<DpsItem<MoverTracks>> MoverTracks { get; }

    public IOutboundChannel<DpsItem<TrafficLights>> TrafficLights { get; }

    public IOutboundChannel<DpsItem<TkbType>> TkbType { get; }

    public IOutboundChannel<DpsItem<ManDowned>> ManDowned { get; }

    public IOutboundChannel<DpsItem<PlayerMessage>> PlayerMessage { get; }

    public IOutboundChannel<DpsItem<Status>> Status { get; }

    public IOutboundChannel<DpsItem<Exercise>> Exercise { get; }

    public IOutboundChannel<DpsItem<View>> View { get; }

    /// <summary>Declares (idempotently) every port on the builder.</summary>
    public static DpsPorts Require(GatewayBuilder builder) => new(builder);

    /// <summary>Binds every port to the writers of the transport.</summary>
    public static void Bind(GatewayBuilder b, DpsTransport t)
    {
        b.BindOutbound<DpsItem<Session>>("dps.Session", t.Session);
        b.BindOutbound<DpsItem<Entity>>("dps.Entity", t.EntityTopic);
        b.BindOutbound<DpsItem<EntityState>>("dps.EntityState", t.EntityState);
        b.BindOutbound<DpsItem<Articulation>>("dps.Articulation", t.Articulation);
        b.BindOutbound<DpsItem<PersonState>>("dps.PersonState", t.PersonState);
        b.BindOutbound<DpsItem<Appearance>>("dps.Appearance", t.Appearance);
        b.BindOutbound<DpsItem<Fire>>("dps.Fire", t.Fire);
        b.BindOutbound<DpsItem<Detonation>>("dps.Detonation", t.Detonation);
        b.BindOutbound<DpsItem<Effect>>("dps.Effect", t.Effect);
        b.BindOutbound<DpsItem<Impact>>("dps.Impact", t.Impact);
        b.BindOutbound<DpsItem<VehicleLook>>("dps.VehicleLook", t.VehicleLook);
        b.BindOutbound<DpsItem<Round>>("dps.Round", t.Round);
        b.BindOutbound<DpsItem<MunitionLook>>("dps.MunitionLook", t.MunitionLook);
        b.BindOutbound<DpsItem<FlameJet>>("dps.FlameJet", t.FlameJet);
        b.BindOutbound<DpsItem<FireSpots>>("dps.FireSpots", t.FireSpots);
        b.BindOutbound<DpsItem<GroundMarks>>("dps.GroundMarks", t.GroundMarks);
        b.BindOutbound<DpsItem<Animal>>("dps.Animal", t.Animal);
        b.BindOutbound<DpsItem<BuildingDamage>>("dps.BuildingDamage", t.BuildingDamage);
        b.BindOutbound<DpsItem<Mover>>("dps.Mover", t.Mover);
        b.BindOutbound<DpsItem<MoverTracks>>("dps.MoverTracks", t.MoverTracks);
        b.BindOutbound<DpsItem<TrafficLights>>("dps.TrafficLights", t.TrafficLights);
        b.BindOutbound<DpsItem<TkbType>>("dps.TkbType", t.TkbType);
        b.BindOutbound<DpsItem<ManDowned>>("dps.ManDowned", t.ManDowned);
        b.BindOutbound<DpsItem<PlayerMessage>>("dps.PlayerMessage", t.PlayerMessage);
        b.BindOutbound<DpsItem<Status>>("dps.Status", t.Status);
        b.BindOutbound<DpsItem<Exercise>>("dps.Exercise", t.Exercise);
        b.BindOutbound<DpsItem<View>>("dps.View", t.View);
    }
}
