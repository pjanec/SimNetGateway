using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Transport.FakeRti;

namespace SimNetGateway.Transport.HlaRpr.Tests;

/// <summary>
/// The NED-RPR gateway running over the HLA transport, the fake RTI and a scripted peer federate (success condition g): the RPR
/// channels of the engine are the <see cref="RprHlaConnection"/> itself, the host tick is pump, engine tick, peer evoke.
/// </summary>
public class EndToEndTests
{
    private const ulong Hawk = 42;

    private sealed class Host
    {
        public const double Dt = 1.0 / 60.0;

        public Host()
        {
            Clock = new ManualClock();
            Hub = new FakeRtiHub();
            var builder = NedRprConfigurations.Default.CreateBuilder(
                Clock, new GatewayOptions { JournalCapacity = 16384, DiagnosticsCapacity = 8192, EventLogCapacity = 4096 });
            var ned = builder.RequireInbound("ned");
            var rpr = builder.RequireInbound("rpr");
            NedIn = new InMemoryInbound(ned.Source, "ned");
            Connection = new RprHlaConnection(
                Hub.CreateAmbassador(), new RprHlaSettings { FederationName = "E2E", FederateName = "NedRprGateway" }, Clock, rpr.Source);
            builder.BindInbound("ned", NedIn);
            builder.BindInbound("rpr", Connection);
            NedOut = new InMemoryOutbound<INedMessage>();
            DisplayOut = new InMemoryOutbound<IDisplayMessage>();
            builder.BindOutbound<INedMessage>("ned", NedOut);
            builder.BindOutbound<IRprMessage>("rpr", Connection);
            builder.BindOutbound<IDisplayMessage>("display", DisplayOut);
            Binding = builder.Build();
            Engine = Binding.Engine;
            NedOut.Context = Engine;
            DisplayOut.Context = Engine;
            Peer = new ScriptedRprFederate(Hub, "E2E", "Peer");
            Connection.Start();
        }

        public ManualClock Clock { get; }
        public FakeRtiHub Hub { get; }
        public RprHlaConnection Connection { get; }
        public GatewayHostBinding Binding { get; }
        public GatewayEngine Engine { get; }
        public InMemoryInbound NedIn { get; }
        public InMemoryOutbound<INedMessage> NedOut { get; }
        public InMemoryOutbound<IDisplayMessage> DisplayOut { get; }
        public ScriptedRprFederate Peer { get; }

        /// <summary>One host tick: advance the clock, pump the transport, tick the engine, let the peer receive.</summary>
        public void Step()
        {
            Clock.Advance(Dt);
            Connection.Pump();
            Engine.Tick();
            Peer.Evoke();
        }

        public void Steps(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Step();
            }
        }

        public void Ned<T>(T message)
            where T : INedMessage => NedIn.Enqueue(in message);

        public IEnumerable<DiagnosticEntry> Problems()
        {
            foreach (var entry in Engine.Diagnostics.Recent)
            {
                if (entry.Severity >= DiagnosticSeverity.Warning)
                {
                    yield return entry;
                }
            }
        }
    }

    private static readonly TypeCatalog Catalog = new();

    private static EntityTypeStruct Dis(int tkb)
    {
        Assert.True(Catalog.TryToDis(tkb, out var dis));
        return Gen.ToStruct(dis);
    }

    private static void PublishHawk(Host h, double speed = 100)
    {
        h.Ned(new NedEntityMaster(Hawk, "Hawk", 100, NedForce.Friendly));
        h.Step();
        h.Ned(new NedGeoSpatial(Hawk, 50.0, 14.0, 300.0, 90.0, 0.0, 0.0, speed));
        h.Steps(4);
    }

    [Fact]
    public void A_NED_entity_appears_at_the_peer_with_type_identifier_marking_force_and_spatial()
    {
        var h = new Host();
        h.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);

        PublishHawk(h);

        var o = Assert.Single(h.Peer.Discovered);
        Assert.Equal(RprFom2.ObjectClasses.PhysicalEntity, o.ClassName);
        Assert.Equal(Dis(100), o.EntityType);
        Assert.Equal(new EntityIdentifierStruct(new FederateIdentifierStruct(1, 3001), 1), o.EntityIdentifier);
        Assert.Equal("Hawk", o.Marking!.Value.AsciiText);
        Assert.Equal(ForceIdentifierEnum8.Friendly, o.Force);
        NedRprConversions.GeoToPivot(50.0, 14.0, 300.0, 90.0, 0, 0, 100, out var p, out var orientation, out var v);
        var spatial = o.Spatial!.Value;
        Assert.Equal(DeadReckoningAlgorithmEnum8.DRM_FPW, spatial.Algorithm);
        Assert.Equal(new WorldLocationStruct(p.X, p.Y, p.Z), spatial.WorldLocation);
        Assert.Equal(new OrientationStruct((float)orientation.X, (float)orientation.Y, (float)orientation.Z), spatial.Orientation);
        Assert.Equal(new VelocityVectorStruct((float)v.X, (float)v.Y, (float)v.Z), spatial.Velocity);
        Assert.Empty(h.Problems());
        Assert.Equal(0, h.Connection.DecodeErrors + h.Connection.EncodeErrors + h.Connection.SendErrors + h.Connection.CallbackErrors);
    }

    [Fact]
    public void A_peer_that_subscribed_at_the_registered_class_knows_the_entity_as_that_class_and_a_Human_peer_does_not_see_it()
    {
        var h = new Host();
        var vehicles = h.Peer;
        vehicles.SubscribePhysical(RprFom2.ObjectClasses.GroundVehicle);
        var lifeforms = new ScriptedRprFederate(h.Hub, "E2E", "Lifeforms");
        lifeforms.SubscribePhysical(RprFom2.ObjectClasses.Lifeform);

        PublishHawk(h); // TKB 100 is a ground vehicle (1/1)
        lifeforms.Evoke();

        Assert.Equal(RprFom2.ObjectClasses.GroundVehicle, Assert.Single(vehicles.Discovered).ClassName);
        Assert.Empty(lifeforms.Discovered);
    }

    [Fact]
    public void A_peer_entity_appears_on_the_NED_side_and_follows_its_updates()
    {
        var h = new Host();
        h.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
        var wolf = h.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
        NedRprConversions.GeoToPivot(50.2, 14.2, 200, 270, 0, 0, 20, out var p, out var ori, out var vel);
        var spatial = new SpatialVariantStruct(
            DeadReckoningAlgorithmEnum8.DRM_FPW, new WorldLocationStruct(p.X, p.Y, p.Z), false,
            new OrientationStruct((float)ori.X, (float)ori.Y, (float)ori.Z), new VelocityVectorStruct((float)vel.X, (float)vel.Y, (float)vel.Z), default, default);
        h.Steps(2); // the gateway discovers the object and asks for its values

        h.Peer.Update(wolf, RprFom2.ObjectClasses.GroundVehicle, new ScriptedRprFederate.Attributes()
            .Marking("Wolf").EntityType(Dis(400)).Force(ForceIdentifierEnum8.Opposing)
            .EntityIdentifier(new EntityIdentifierStruct(new FederateIdentifierStruct(9, 9), 5)).Spatial(spatial));
        h.Steps(2);

        var master = Assert.IsType<NedEntityMaster>(Assert.Single(h.NedOut.OfType<NedEntityMaster>()));
        Assert.Equal("Wolf", master.Name);
        Assert.Equal(400, master.TkbType);
        Assert.Equal(NedForce.Opposing, master.Force);
        Assert.InRange(master.Id, 1_000_000UL, 1_999_999UL);
        var geo = h.NedOut.OfType<NedGeoSpatial>().Last();
        Assert.Equal(master.Id, geo.Id);
        Assert.Equal(50.2, geo.LatitudeDeg, 6);
        Assert.Equal(14.2, geo.LongitudeDeg, 6);
        Assert.Equal(200, geo.Altitude, 2);

        // a later partial update (only the position moves) reaches NED as the matching descriptor
        h.NedOut.Clear();
        NedRprConversions.GeoToPivot(50.3, 14.2, 200, 270, 0, 0, 20, out var moved, out _, out _);
        h.Peer.Update(wolf, RprFom2.ObjectClasses.GroundVehicle, new ScriptedRprFederate.Attributes()
            .Spatial(spatial with { WorldLocation = new WorldLocationStruct(moved.X, moved.Y, moved.Z) }));
        h.Steps(2);
        Assert.Equal(50.3, Assert.Single(h.NedOut.OfType<NedGeoSpatial>()).LatitudeDeg, 6);

        // removal by the peer removes the entity on the NED side
        h.Peer.Rti.DeleteObjectInstance(wolf, Array.Empty<byte>());
        h.Steps(2);
        Assert.Contains(h.NedOut.OfType<NedEntityDisposed>(), d => d.Id == master.Id);
        Assert.Empty(h.Problems());
    }

    [Fact]
    public void A_peer_weapon_fire_reaches_the_NED_side_with_shooter_target_and_munition_and_a_detonation_is_delivered_to_the_engine()
    {
        var h = new Host();
        h.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        h.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
        PublishHawk(h);
        var wolf = h.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
        h.Steps(2);
        NedRprConversions.GeoToPivot(50.2, 14.2, 200, 270, 0, 0, 0, out var p, out var ori, out _);
        h.Peer.Update(wolf, RprFom2.ObjectClasses.GroundVehicle, new ScriptedRprFederate.Attributes()
            .Marking("Wolf").EntityType(Dis(400)).Force(ForceIdentifierEnum8.Opposing)
            .EntityIdentifier(new EntityIdentifierStruct(new FederateIdentifierStruct(9, 9), 5))
            .Spatial(new SpatialVariantStruct(DeadReckoningAlgorithmEnum8.Static, new WorldLocationStruct(p.X, p.Y, p.Z), false,
                new OrientationStruct((float)ori.X, (float)ori.Y, (float)ori.Z), default, default, default)));
        h.Steps(2);
        var wolfNedId = h.NedOut.OfType<NedEntityMaster>().Single(m => m.Name == "Wolf").Id;
        var hawkName = h.Peer.Discovered.Single().Name;
        h.Peer.SubscribeInteractions();
        h.Peer.PublishInteractions();
        h.NedOut.Clear();

        h.Peer.SendFire(ScriptedRprFederate.FireParameters(
            3, h.Peer.NameOf(wolf), h.Peer.NameOf(wolf), hawkName, "", Dis(501), new WorldLocationStruct(p.X, p.Y, p.Z), 2, 0,
            FuseTypeEnum16.Other, WarheadTypeEnum16.Other, 0, 0, default));
        h.Steps(2);

        var fire = Assert.Single(h.NedOut.OfType<NedWeaponFire>());
        Assert.Equal(wolfNedId, fire.Shooter);
        Assert.Equal(Hawk, fire.Target);
        Assert.Equal(501, fire.MunitionTkb);
        Assert.Equal(2, fire.Quantity);
        Assert.NotEqual(Guid.Empty, fire.FireId);

        // the detonation of the same event: the transport delivers it; the NED-RPR gateway has no ingress for it (unchanged by HLA-2)
        h.Peer.SendDetonation(ScriptedRprFederate.DetonationParameters(
            3, h.Peer.NameOf(wolf), h.Peer.NameOf(wolf), hawkName, "", Dis(501), new WorldLocationStruct(p.X, p.Y, p.Z), 2, 0,
            FuseTypeEnum16.Other, WarheadTypeEnum16.Other, DetonationResultCodeEnum8.EntityImpact, default, default));
        h.Steps(2);
        Assert.Equal(1, h.Engine.Diagnostics.UnhandledCount(typeof(RprMunitionDetonation)));
        Assert.Empty(h.Problems());
    }

    [Fact]
    public void A_NED_fire_and_detonation_reach_the_peer_as_interactions_with_object_names_and_one_event_identifier()
    {
        var h = new Host();
        h.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        h.Peer.SubscribeInteractions();
        PublishHawk(h);
        h.Ned(new NedEntityMaster(43, "Viper", 100, NedForce.Friendly));
        h.Ned(new NedGeoSpatial(43, 50.1, 14.1, 300, 90, 0, 0, 0));
        h.Steps(4);
        var fireId = Guid.NewGuid();

        h.Ned(new NedWeaponFire(fireId, 43, Hawk, 501, 50.1, 14.1, 0, 1));
        h.Steps(2);
        h.Ned(new NedDetonation(fireId, Hawk, 501, 50.1, 14.1, 0, NedDetonationResult.GroundImpact, -1));
        h.Steps(2);

        var fire = Assert.Single(h.Peer.Fires);
        var detonation = Assert.Single(h.Peer.Detonations);
        Assert.Equal(fire.Event, detonation.Event);
        Assert.Equal(Dis(501), fire.Munition);
        Assert.NotEqual(fire.Firing, fire.Target);
        Assert.Contains(fire.Firing, h.Peer.Discovered.Select(d => d.Name));
        Assert.Contains(fire.Target, h.Peer.Discovered.Select(d => d.Name));
        Assert.Equal(DetonationResultCodeEnum8.GroundImpact, detonation.Result); // pivot 2 -> FOM 3
        Assert.Equal((byte)3, (byte)detonation.Result);
        Assert.Empty(h.Problems());
    }

    [Fact]
    public void A_value_request_from_the_peer_makes_the_gateway_send_a_full_update_the_peer_reflects()
    {
        var h = new Host();
        h.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        PublishHawk(h, speed: 0);
        var o = Assert.Single(h.Peer.Discovered);
        var before = new[] { "EntityType", "EntityIdentifier", "Marking", "ForceIdentifier", "Spatial" }.ToDictionary(a => a, a => o.Count(a));
        var spatialBefore = o.Spatial;

        h.Peer.RequestAll(o);
        h.Steps(3);

        foreach (var (attribute, count) in before)
        {
            Assert.True(o.Count(attribute) == count + 1, $"{attribute} was reflected {o.Count(attribute) - count} times by the answer");
        }

        Assert.Equal(spatialBefore, o.Spatial); // a stationary entity: the same state again
        Assert.Equal(0, o.Count("DamageState")); // the entity has no damage state to report
        Assert.Empty(h.Problems());
    }

    [Fact]
    public void The_gateway_heartbeat_keeps_reaching_the_peer_over_the_transport()
    {
        var h = new Host();
        h.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        PublishHawk(h, speed: 0);
        var o = Assert.Single(h.Peer.Discovered);
        int spatial = o.Count("Spatial");

        h.Steps((int)(6 / Host.Dt)); // heartbeat period 5 s

        Assert.True(o.Count("Spatial") > spatial);
    }
}
