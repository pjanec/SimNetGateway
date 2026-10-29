using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Showcase;
using SimNetGateway.Transport.FakeRti;
using SimNetGateway.Transport.HlaRpr;
using static SimNetGateway.Gateway.HrotDis.Tests.RprMessages;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// The HROT gateway with BOTH sides over their real transports' seam: the DIS side on in-memory channels (recorded PDUs), the HLA side on
/// <see cref="RprHlaConnection"/> over a <see cref="FakeRtiHub"/> with a <see cref="ScriptedRprFederate"/> as the rest of the federation
/// (DD-009 section 8; the host tick order of DD-008 section 6: pump the RTI, tick the engine, let the peer receive).
/// </summary>
public sealed class HrotDisHlaHost
{
    public const double Dt = 1.0 / 60.0;

    public HrotDisHlaHost(GatewayConfiguration? config = null)
    {
        Config = config ?? Both();
        Clock = new ManualClock();
        var ids = new HrotIdRangeAllocator(5_000_000, 5_000_999);
        var configuration = HrotDisConfigurations.For(Config, "e2e", ids);
        Profile = (HrotDisProfile)configuration.Profile;
        Builder = configuration.CreateBuilder(Clock, new GatewayOptions { JournalCapacity = 16384, DiagnosticsCapacity = 8192, EventLogCapacity = 4096 });

        var hrot = Builder.RequireInbound("hrot");
        HrotIn = new InMemoryInbound(hrot.Source, "hrot");
        Builder.BindInbound("hrot", HrotIn);
        HrotOut = new InMemoryOutbound<IGatewayToHrotMessage>();
        Builder.BindOutbound<IGatewayToHrotMessage>("hrot", HrotOut);

        var dis = Builder.RequireInbound("dis");
        DisIn = new InMemoryInbound(dis.Source, "dis");
        Builder.BindInbound("dis", DisIn);
        DisOut = new InMemoryOutbound<Pdu>();
        Builder.BindOutbound<Pdu>("dis", DisOut);

        var hla = Config.Hla!;
        var rpr = Builder.RequireInbound("rpr");
        Hub = new FakeRtiHub();
        var settings = new RprHlaSettings { FederationName = hla.Federation, FederateName = hla.FederateName, FederateType = hla.FederateType, CreateIfMissing = hla.CreateFederation };
        Connection = new RprHlaConnection(Hub.CreateAmbassador(), settings, Clock, rpr.Source, "rpr", NextEventId);
        Connection.Start();
        Peer = new ScriptedRprFederate(Hub, settings.FederationName, "RprPeer");
        Peer.DeclareForGateway();
        Builder.BindInbound("rpr", Connection);
        RprOut = new InMemoryOutbound<IRprMessage>();
        Builder.BindOutbound<IRprMessage>("rpr", new Tee(RprOut, Connection));

        Binding = Builder.Build();
        Engine = Binding.Engine;
        HrotOut.Context = Engine;
        DisOut.Context = Engine;
        RprOut.Context = Engine;
        Peer.TickSource = () => Engine.TickCount;
        Schema = new PivotSchema(Engine.Attributes, Engine.EventTypes);
        HrotSource = hrot.Source;
        RprSource = Engine.Sources.Register("rpr");
        HrotIds = (IdentityMap<long>)Engine.Identities.Find("hrot.entity")!;
        RprHandles = (IdentityMap<RprObjectHandle>)Engine.Identities.Find("rpr.handle")!;
    }

    private long _eventCounter;

    private Guid NextEventId() => new((int)++_eventCounter, 0x0E, 0x71, 0x4E, 0, 0, 0, 0, 0, 0, 2);

    public GatewayConfiguration Config { get; }

    public ManualClock Clock { get; }

    public HrotDisProfile Profile { get; }

    public GatewayBuilder Builder { get; }

    public GatewayHostBinding Binding { get; }

    public GatewayEngine Engine { get; }

    public PivotSchema Schema { get; }

    public FakeRtiHub Hub { get; }

    public RprHlaConnection Connection { get; }

    public ScriptedRprFederate Peer { get; }

    public InMemoryInbound HrotIn { get; }

    public InMemoryInbound DisIn { get; }

    public InMemoryOutbound<IGatewayToHrotMessage> HrotOut { get; }

    public InMemoryOutbound<Pdu> DisOut { get; }

    public InMemoryOutbound<IRprMessage> RprOut { get; }

    public SourceId HrotSource { get; }

    public SourceId RprSource { get; }

    public IdentityMap<long> HrotIds { get; }

    public IdentityMap<RprObjectHandle> RprHandles { get; }

    public LocalTangentFrame? Frame { get; private set; }

    /// <summary>The live entities of an origin.</summary>
    public List<EntityRecord> EntitiesOf(SourceId origin)
    {
        var list = new List<EntityRecord>();
        foreach (var e in Engine.State.Entities)
        {
            if (e.Origin == origin)
            {
                list.Add(e);
            }
        }

        return list;
    }

    /// <summary>One host tick: advance the clock, the transport evokes the RTI's callbacks, the engine ticks, the peer receives.</summary>
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

    public void Run(double seconds) => Steps((int)Math.Round(seconds / Dt));

    public void Hrot<T>(T message)
        where T : IHrotToGatewayMessage => HrotIn.Enqueue(in message);

    public void HrotStep<T>(T message)
        where T : IHrotToGatewayMessage
    {
        Hrot(message);
        Step();
    }

    public void GoLive(double lat = 50.0, double lon = 14.0, double alt = 300.0)
    {
        Frame = new LocalTangentFrame(Geodetic.FromDegrees(lat, lon, alt));
        HrotStep(new HrotOrigin(lat, lon, alt, 1));
        HrotStep(new HrotSession(ClusterPhase.Live, true, 1.0, 0.0));
    }

    private sealed class Tee(IOutboundChannel<IRprMessage> first, RprHlaConnection transport) : IOutboundChannel<IRprMessage>
    {
        public void Send(in IRprMessage message)
        {
            first.Send(in message);
            transport.Send(in message);
        }
    }
}

public class HlaEndToEndTests
{
    private static EntityTypeStruct TankStruct => new(1, 1, 225, 1, 1, 3, 0);

    private static PeerObject OnlyObject(HrotDisHlaHost h) => Assert.Single(h.Peer.Objects);

    [Fact]
    public void A_hrot_entity_appears_at_the_federate_with_the_right_class_type_identifier_and_spatial_and_as_an_espdu()
    {
        var h = new HrotDisHlaHost();
        h.GoLive();
        var enu = new HrotVec3(100, 200, 10);
        h.HrotStep(HrotDisHarness.State(7, enu, tkb: 100, marking: "TANK-01", force: 2, velocity: new HrotVec3(3, 4, 0)));
        h.Steps(4);

        var obj = OnlyObject(h);
        // the class the object is registered as follows from the type the gateway registered it with (the peer subscribed to the superclass)
        Assert.Equal(RprFom2.ObjectClasses.GroundVehicle, RprHlaClasses.NameFor(h.RprOut.OfType<RprRegisterObject>().Single().EntityType));
        Assert.Equal(TankStruct, obj.EntityType);
        Assert.Equal("TANK-01", obj.Marking!.Value.AsciiText);
        Assert.Equal(ForceIdentifierEnum8.Opposing, obj.Force);
        Assert.Equal(new EntityIdentifierStruct(new FederateIdentifierStruct(1, 3001), 1), obj.EntityIdentifier);
        Assert.Equal(DamageStatusEnum32.NoDamage, obj.Damage);
        var spatial = obj.Spatial!.Value;
        Assert.Equal(DeadReckoningAlgorithmEnum8.DRM_FPW, spatial.Algorithm);
        var expected = h.Frame!.PointToEcef(new Vec3(100, 200, 10));
        var at = new Vec3(spatial.WorldLocation.X, spatial.WorldLocation.Y, spatial.WorldLocation.Z);
        Assert.True((at - expected).Length < 1.0, $"location {at} is not near {expected}");
        var v = h.Frame.VectorToEcef(new Vec3(3, 4, 0));
        Assert.True((new Vec3(spatial.Velocity.X, spatial.Velocity.Y, spatial.Velocity.Z) - v).Length < 1e-3);

        // the same entity as an Entity State PDU on the DIS side
        var pdu = Assert.IsType<EntityStatePdu>(h.DisOut.Messages.Select(m => m.Message).First(m => m is EntityStatePdu));
        Assert.Equal("TANK-01", Protocol.Dis7.EntityMarkingText.Get(pdu.Marking));
        Assert.Equal(new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack(), DisConversions.PackType(pdu.EntityType));
        Assert.True((DisConversions.ToVec(pdu.EntityLocation) - expected).Length < 1.0);
        Assert.Equal(0, h.Connection.SendErrors + h.Connection.EncodeErrors + h.Connection.RejectedOutbound);
    }

    [Fact]
    public void A_federate_entity_appears_in_hrot_as_a_replica_and_nothing_of_it_goes_to_dis_or_back_to_the_federation()
    {
        var h = new HrotDisHlaHost();
        h.GoLive();
        var enu = new Vec3(500, -250, 20);
        h.Peer.Inject(Update(h.Frame!, Peer(1), enu, marking: "FED-TANK", force: RprForceId.Friendly));
        h.Steps(3);

        var replica = Assert.Single(h.HrotOut.OfType<ReplicaState>());
        Support.Near(enu, replica.State.Position, 1e-3, "replica position");
        Assert.Equal("FED-TANK", replica.Marking);
        Assert.Equal(1, replica.Force);
        Assert.Equal(RprConversions.PackType(Tank), replica.DisType);
        Assert.Single(h.EntitiesOf(h.RprSource));
        Assert.Empty(h.DisOut.OfType<EntityStatePdu>());       // never rpr -> DIS
        Assert.Empty(h.RprOut.OfType<RprRegisterObject>());    // no echo back to the federation
        Assert.Empty(h.RprOut.OfType<RprObjectUpdate>());
        Assert.Empty(h.EntitiesOf(h.HrotSource));

        // and the federate removing it removes the replica
        h.Peer.Inject(new RprObjectRemoved(Peer(1)));
        h.Steps(3);
        Assert.Single(h.HrotOut.OfType<ReplicaRemoved>());
        Assert.Empty(h.EntitiesOf(h.RprSource));
    }

    [Fact]
    public void A_dis_entity_appears_in_hrot_but_never_at_the_federate()
    {
        var h = new HrotDisHlaHost();
        h.GoLive();
        h.DisIn.Enqueue(DisPdus.EntityState(5, h.Frame!.PointToEcef(new Vec3(10, 10, 0))));
        h.Steps(4);
        Assert.Single(h.HrotOut.OfType<ReplicaState>());
        Assert.Empty(h.Peer.Objects);                  // never dis -> HLA
        Assert.Empty(h.RprOut.OfType<RprRegisterObject>());
    }

    [Fact]
    public void A_federate_weapon_fire_and_detonation_become_inject_fire_and_inject_detonation_and_a_hrot_fire_reaches_the_federate()
    {
        var h = new HrotDisHlaHost();
        h.GoLive();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 0, 0), tkb: 100, marking: "OURS"));
        h.Steps(4);
        var ours = h.RprHandles.TryGetKey(h.HrotIds.TryResolve(1, out var e) ? e : default, out var handle) ? handle : throw new InvalidOperationException("not registered");
        h.Peer.Inject(Update(h.Frame!, Peer(1), new Vec3(-300, 0, 0)));
        h.Steps(2);
        long shooter = h.HrotOut.OfType<ReplicaState>().Single().Id;

        // the federate fires at our entity (the target is named by our object's RTI handle), then the munition detonates on it
        h.Peer.Inject(new RprWeaponFire(Guid.NewGuid(), Peer(1), ours, RprConversions.ToLocation(h.Frame!.PointToEcef(new Vec3(-300, 0, 2))), MunitionKind, 2));
        h.Steps(3);
        var fire = Assert.Single(h.HrotOut.OfType<InjectFire>());
        Assert.Equal(shooter, fire.ShooterId);
        Assert.Equal(1, fire.TargetId);
        Assert.True(fire.TargetIsHrot);
        Assert.Equal(2, fire.Quantity);
        Support.Near(new Vec3(-300, 0, 2), fire.Origin, 1e-3, "fire origin");

        h.Peer.Inject(new RprMunitionDetonation(Guid.NewGuid(), Peer(1), ours, RprConversions.ToLocation(h.Frame.PointToEcef(new Vec3(99, 0, 0))), MunitionKind, RprDetonationResult.EntityImpact));
        h.Steps(3);
        var detonation = Assert.Single(h.HrotOut.OfType<InjectDetonation>());
        Assert.Equal(shooter, detonation.ShooterId);
        Assert.True(detonation.TargetIsHrot);

        // the other way: our entity fires at the federate's
        h.HrotStep(new HrotFire(1, shooter, 501, 0, new HrotVec3(100, 0, 2), new HrotVec3(400, 0, 0), 1));
        h.HrotStep(new HrotDetonation(1, shooter, 501, 0, new HrotVec3(-299, 0, 0), default));
        h.Steps(3);
        var peerFire = Assert.Single(h.Peer.Fires);
        Assert.Equal(h.Peer.NameOfHandle(ours.Value), peerFire.Firing);
        Assert.Equal(h.Peer.NameOfHandle(h.Peer.ActualHandle(Peer(1).Value)!.Value.Value), peerFire.Target);
        var peerDetonation = Assert.Single(h.Peer.Detonations);
        Assert.Equal(peerFire.Event, peerDetonation.Event); // one fire, its detonation: one event identifier
        Assert.Empty(h.Peer.InteractionDecodeFailures);
        Assert.Empty(h.Peer.AttributeDecodeFailures);
    }

    [Fact]
    public void A_hrot_removal_deletes_the_object_at_the_federate_and_a_late_registration_answer_is_cleaned_up()
    {
        var h = new HrotDisHlaHost();
        h.GoLive();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 5, 0)));
        h.Steps(4);
        Assert.Single(h.Peer.Objects);
        h.HrotStep(new HrotEntityRemoved(1));
        h.Steps(3);
        Assert.Empty(h.Peer.Objects);
        Assert.Single(h.Peer.RemovedNames);

        // removed in the same tick it was created: the registration answer arrives for a dead entity and the object is deleted again
        h.HrotStep(HrotDisHarness.State(2, new HrotVec3(5, 5, 0)));
        h.HrotStep(new HrotEntityRemoved(2));
        h.Steps(4);
        Assert.Empty(h.Peer.Objects);
        Assert.Equal(2, h.Peer.RemovedNames.Count);
        Assert.Equal(0, h.Connection.SendErrors);
    }

    [Fact]
    public void The_federate_asking_for_an_object_gets_a_full_update_again()
    {
        var h = new HrotDisHlaHost();
        h.GoLive();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 5, 0), marking: "ASKED"));
        h.Steps(4);
        var obj = OnlyObject(h);
        int before = obj.Count("Marking");
        h.Peer.RequestAll(obj);
        h.Steps(3);
        Assert.True(obj.Count("Marking") > before, "the request was not answered with a full update");
    }

    [Fact]
    public void The_federates_view_never_drifts_beyond_the_hla_thresholds_while_an_entity_moves_and_jumps()
    {
        var config = Both(hla: x => x with { PositionThresholdM = 1.0, HeartbeatS = 3.0 }, dis: x => x with { PositionThresholdM = 4.0 });
        var h = new HrotDisHlaHost(config);
        h.GoLive();
        double x = 0;
        double vx = 10;
        var random = new Random(77);
        int checkedTicks = 0;
        for (int i = 0; i < 1500; i++)
        {
            if (i % 40 == 0)
            {
                // HROT reports its state: mostly following its model, sometimes jumping or changing speed
                x += random.Next(4) == 0 ? random.NextDouble() * 6 - 3 : 0;
                vx = random.Next(5) == 0 ? random.NextDouble() * 20 : vx;
                h.Hrot(HrotDisHarness.State(1, new HrotVec3(x, 0, 0), velocity: new HrotVec3(vx, 0, 0)));
            }

            x += vx * HrotDisHlaHost.Dt;
            h.Step();
            var entity = h.EntitiesOf(h.HrotSource).FirstOrDefault();
            var obj = h.Peer.Objects.FirstOrDefault();
            var lastSend = h.RprOut.Messages.LastOrDefault(m => m.Message is RprObjectUpdate);
            if (entity is null || obj is null || lastSend.Message is null || !entity.Has(h.Schema.StateTime) || obj.Spatial is null)
            {
                continue;
            }

            var spatial = obj.Spatial.Value;

            // the federate's dead-reckoned position now (what it decoded from the wire, FPW from the time of the update) against the pivot's truth now
            double now = h.Engine.WallTime;
            var held = new Vec3(spatial.WorldLocation.X, spatial.WorldLocation.Y, spatial.WorldLocation.Z)
                       + new Vec3(spatial.Velocity.X, spatial.Velocity.Y, spatial.Velocity.Z) * (now - lastSend.WallTime);
            var truth = entity.Get(h.Schema.Position).AsVec3() + entity.Get(h.Schema.Velocity).AsVec3() * (now - entity.Get(h.Schema.StateTime).AsDouble());
            Assert.True((held - truth).Length <= 1.0 + 1e-3, $"tick {i}: the federate's view is {(held - truth).Length:F4} m from the truth (hla threshold 1 m)");
            Assert.True(now - lastSend.WallTime < 3.0 + 1e-6, $"tick {i}: the federate heard nothing for {now - lastSend.WallTime:F3} s (heartbeat 3 s)");
            checkedTicks++;
        }

        Assert.True(checkedTicks > 1400);
        Assert.Single(h.Peer.Objects);
        Assert.Empty(h.Peer.AttributeDecodeFailures);
        // DIS has the laxer 4 m threshold: it was sent to less often
        Assert.True(h.DisOut.OfType<EntityStatePdu>().Count() < h.RprOut.OfType<RprObjectUpdate>().Count());
    }
}
