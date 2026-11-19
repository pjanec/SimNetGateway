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
        VehicleLook = b.RequireOutbound<DpsItem<VehicleLook>>("dps.VehicleLook");
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

    public IOutboundChannel<DpsItem<VehicleLook>> VehicleLook { get; }

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
        b.BindOutbound<DpsItem<VehicleLook>>("dps.VehicleLook", t.VehicleLook);
    }
}
