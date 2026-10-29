using System.Net.Sockets;
using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Dis7;
using SimNetGateway.Protocol.Hrot;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.Transport.DisUdp.Tests;

/// <summary>
/// DD-006 H3 end to end: the Gateway.HrotDis profile on the engine, the DIS UDP transport on the 127.0.0.1 loopback and a scripted DIS peer (a plain
/// UDP socket that decodes and encodes with Protocol.Dis7). HROT messages are fed in memory (Protocol.Hrot); no HROT transport is involved.
/// The test project is allowed to reference both the transport and the gateway (DD-003 rule 6 is about src projects); the wiring below is
/// what a host does.
/// </summary>
public sealed class HrotDisUdpLoopbackTests : IDisposable
{
    private const double Lat = 50.0, Lon = 14.0, Alt = 300.0;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly ManualClock _clock = new();
    private readonly GatewayEngine _engine;
    private readonly InMemoryInbound _hrotIn;
    private readonly InMemoryOutbound<IGatewayToHrotMessage> _hrotOut = new();
    private readonly DisUdpTransport _transport;
    private readonly Socket _peer;
    private readonly int _gatewayPort;
    private readonly LocalTangentFrame _frame = new(Geodetic.FromDegrees(Lat, Lon, Alt));

    public HrotDisUdpLoopbackTests()
    {
        int peerPort = Pdus.FreePort();
        _gatewayPort = Pdus.FreePort();
        var config = GatewayConfiguration.CreateDefault(1) with
        {
            Dis = new DisConfiguration { ExerciseId = 1, Address = "127.0.0.1", Port = peerPort },
        };

        _peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _peer.Bind(Pdus.Loopback(peerPort));

        var configuration = HrotDisConfigurations.For(config, "h3-e2e", new HrotIdRangeAllocator(5_000_000, 5_000_999));
        var builder = configuration.CreateBuilder(_clock, new GatewayOptions { JournalCapacity = 4096, DiagnosticsCapacity = 4096, EventLogCapacity = 1024 });

        var hrot = builder.RequireInbound("hrot");
        _hrotIn = new InMemoryInbound(hrot.Source, "hrot");
        builder.BindInbound("hrot", _hrotIn);
        builder.BindOutbound<IGatewayToHrotMessage>("hrot", _hrotOut);

        // what a host does with the gateway's configuration: its dis.* and shared.* values become the transport's settings
        var dis = config.Dis!;
        var settings = new DisUdpSettings
        {
            Address = dis.Address,
            Port = dis.Port,
            ListenPort = _gatewayPort,
            LocalAddress = "127.0.0.1",
            ExerciseId = dis.ExerciseId,
            OwnSite = config.Shared.Site,
            OwnApplication = config.Shared.Application,
        };
        _transport = DisUdpTransport.Open(settings, builder.RequireInbound("dis").Source);
        builder.BindInbound("dis", _transport);
        builder.BindOutbound<Pdu>("dis", _transport);

        _engine = builder.Build().Engine;
        _hrotOut.Context = _engine;
    }

    public void Dispose()
    {
        _transport.Dispose();
        _peer.Dispose();
    }

    private void Tick(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            _clock.Advance(1.0 / 60.0);
            _engine.Tick();
        }
    }

    private void GoLive()
    {
        var origin = new HrotOrigin(Lat, Lon, Alt, 1);
        var session = new HrotSession(ClusterPhase.Live, true, 1.0, 0.0);
        _hrotIn.Enqueue(in origin);
        _hrotIn.Enqueue(in session);
        Tick(2);
    }

    private List<Pdu> PeerReceive(Func<List<Pdu>, bool> done)
    {
        var result = new List<Pdu>();
        var buffer = new byte[65536];
        var deadline = DateTime.UtcNow + Bound;
        while (!done(result) && DateTime.UtcNow < deadline)
        {
            var left = deadline - DateTime.UtcNow;
            if (!_peer.Poll((int)Math.Max(1, left.TotalMilliseconds) * 1000, SelectMode.SelectRead))
            {
                break;
            }

            int n = _peer.Receive(buffer);
            var decoded = PduCodec.Decode(new ReadOnlySpan<byte>(buffer, 0, n));
            Assert.False(decoded.HasErrors);
            result.AddRange(decoded.Pdus);
        }

        return result;
    }

    [Fact]
    public void A_hrot_entity_appears_as_entity_state_pdus_at_the_peer()
    {
        GoLive();
        var enu = new HrotVec3(100, 200, 5);
        var state = new HrotEntityState(1, 100, new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack(), "T1", 1, 0, enu, HrotQuat.Identity,
            default, default, 100, 100, 1.0);
        _hrotIn.Enqueue(in state);
        Tick(3);

        // the session going Live also announces itself with a Start/Resume (dis.hrotControlsDis), before the entity
        var received = PeerReceive(r => r.OfType<EntityStatePdu>().Any());
        Assert.Single(received.OfType<StartResumePdu>());
        var espdu = Assert.Single(received.OfType<EntityStatePdu>());
        Assert.Equal(1, espdu.ExerciseID);
        Assert.Equal(1, espdu.EntityID.SimulationAddress.Site);
        Assert.Equal(3001, espdu.EntityID.SimulationAddress.Application);
        var expected = _frame.PointToEcef(new Vec3(enu.X, enu.Y, enu.Z));
        Assert.True(Math.Abs(espdu.EntityLocation.X - expected.X) < 1.0 && Math.Abs(espdu.EntityLocation.Y - expected.Y) < 1.0 &&
                    Math.Abs(espdu.EntityLocation.Z - expected.Z) < 1.0,
            $"location {espdu.EntityLocation.X}, {espdu.EntityLocation.Y}, {espdu.EntityLocation.Z} but expected {expected}");
        Assert.Equal(received.Count, _transport.Counters.Sent);
        Assert.Equal(0, _transport.Counters.SendErrors);
    }

    [Fact]
    public void A_peer_entity_state_pdu_yields_a_replica_state_and_the_gateways_own_pdus_are_not_taken_back()
    {
        GoLive();
        // a HROT entity first, so that the gateway has PDUs on the wire (to the peer only)
        var state = new HrotEntityState(1, 100, 0, "T1", 1, 0, default, HrotQuat.Identity, default, default, 100, 100, 1.0);
        _hrotIn.Enqueue(in state);
        Tick(2);

        var enu = new Vec3(30, 40, 0);
        var pdu = new EntityStatePdu
        {
            ExerciseID = 1,
            Timestamp = DisTimestamp.EncodeRelative(1.0),
            EntityID = Pdus.Id(5, 77, 1),
            ForceId = 2,
            EntityType = DisConversions.ToEntityType(new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack()),
            EntityLocation = DisConversions.ToDouble(_frame.PointToEcef(enu)),
        };
        EntityMarkingText.Set(pdu.Marking, "PEER");
        pdu.DeadReckoningParameters.DeadReckoningAlgorithm = 1;
        var bytes = PduCodec.Encode(pdu);
        _peer.SendTo(bytes, Pdus.Loopback(_gatewayPort));

        Assert.True(_transport.WaitForPdus(1, Bound), "the datagram did not arrive");
        Tick(3);

        var replica = Assert.Single(_hrotOut.OfType<ReplicaState>());
        Assert.Equal("PEER", replica.Marking);
        Assert.InRange(replica.Id, 5_000_000, 5_000_999);
        Assert.True(Math.Abs(replica.State.Position.X - enu.X) < 1e-3 && Math.Abs(replica.State.Position.Y - enu.Y) < 1e-3,
            $"replica at {replica.State.Position}");
        Assert.Equal(1, _transport.Counters.Delivered);
        Assert.True(_transport.Counters.Balanced);
    }

    [Fact]
    public void An_exercise_mismatch_and_an_echo_from_the_peer_do_not_reach_the_gateway()
    {
        GoLive();
        var wrongExercise = new EntityStatePdu { ExerciseID = 2, EntityID = Pdus.Id(5, 77, 2) };
        var echo = new EntityStatePdu { ExerciseID = 1, EntityID = Pdus.Id(1, 3001, 9) };
        _peer.SendTo(PduCodec.Encode(wrongExercise), Pdus.Loopback(_gatewayPort));
        _peer.SendTo(PduCodec.Encode(echo), Pdus.Loopback(_gatewayPort));

        Assert.True(_transport.WaitForPdus(2, Bound));
        Tick(3);
        Assert.Empty(_hrotOut.OfType<ReplicaState>());
        var c = _transport.Counters;
        Assert.Equal(1, c.FilteredExercise);
        Assert.Equal(1, c.FilteredOwn);
        Assert.Equal(0, c.Delivered);
    }
}
