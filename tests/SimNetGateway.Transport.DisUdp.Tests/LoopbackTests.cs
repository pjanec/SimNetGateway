using System.Net;
using System.Net.Sockets;
using SimNetGateway.Engine;
using DISnet;
using SimNetGateway.Protocol.Dis7;

namespace SimNetGateway.Transport.DisUdp.Tests;

/// <summary>Probes whether this machine can do broadcast / multicast on the loopback; the tests below are skipped with the reason otherwise.</summary>
public static class NetworkProbe
{
    public static readonly string? BroadcastProblem = Probe("127.255.255.255", null);

    public static readonly string? MulticastProblem = Probe("239.255.42.99", "127.0.0.1");

    /// <summary>Two sockets on one port with SO_REUSEADDR, one sends to the address, both must hear it.</summary>
    private static string? Probe(string address, string? local)
    {
        try
        {
            int port = Pdus.FreePort();
            var settings = new DisUdpSettings { Address = address, Port = port, LocalAddress = local, ExerciseId = 1 };
            using var a = new UdpDatagramSocket(settings);
            using var b = new UdpDatagramSocket(settings);
            a.Send([1, 2, 3]);
            var buffer = new byte[64];
            foreach (var socket in new[] { a, b })
            {
                if (socket.Receive(buffer, 2000) != 3)
                {
                    return $"{address}: a datagram sent to it did not arrive on the loopback of this machine";
                }
            }

            return null;
        }
        catch (Exception e)
        {
            return $"{address}: {e.GetType().Name}: {e.Message}";
        }
    }
}

public sealed class BroadcastFactAttribute : FactAttribute
{
    public BroadcastFactAttribute() => Skip = NetworkProbe.BroadcastProblem is { } p ? "loopback broadcast unavailable - " + p : null;
}

public sealed class MulticastFactAttribute : FactAttribute
{
    public MulticastFactAttribute() => Skip = NetworkProbe.MulticastProblem is { } p ? "loopback multicast unavailable - " + p : null;
}

/// <summary>Real UDP sockets on 127.0.0.1.</summary>
public class LoopbackTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static Spec Es(uint serial, ushort site = 5, ushort app = 77, int exercise = 1) => new(Kind.EntityState, exercise, site, app, site, app, serial);

    private static DisUdpTransport Open(DisUdpSettings s, int source = 1) => DisUdpTransport.Open(s, new SourceId(source));

    [Fact]
    public void Unicast_pdus_arrive_byte_identical_and_in_order()
    {
        int portA = Pdus.FreePort();
        int portB = Pdus.FreePort();
        using var a = Open(Pdus.Settings() with { Port = portB, ListenPort = portA, LocalAddress = "127.0.0.1", OwnSite = 5, OwnApplication = 77 });
        using var b = Open(Pdus.Settings() with { Port = portA, ListenPort = portB, LocalAddress = "127.0.0.1", OwnSite = 6, OwnApplication = 78 });

        var sent = new List<Pdu>();
        for (uint i = 1; i <= 30; i++)
        {
            Pdu pdu = Pdus.Build(Es(i, 5, 77));
            sent.Add(pdu);
            a.Send(in pdu);
        }

        Assert.True(b.WaitForPdus(30, Bound), $"only {b.Counters.PdusReceived} of 30 arrived");
        var sink = new RecordingSink();
        b.Drain(sink, 100);
        Assert.Equal(30, sink.Items.Count);
        Assert.Equal(sent.Select(p => PduCodec.Encode(p)), sink.Items.Select(i => PduCodec.Encode(i.Pdu)), new ByteArrayComparer());
        Assert.All(sink.Items, i => Assert.Equal(typeof(EntityStatePdu), i.Declared));
        Assert.Equal(30, a.Counters.Sent);
        Assert.True(b.Counters.Balanced);
    }

    [Fact]
    public void A_pdu_sent_to_itself_is_an_echo_and_is_filtered_while_a_foreign_one_passes()
    {
        int port = Pdus.FreePort();
        using var t = Open(Pdus.Settings() with { Port = port, LocalAddress = "127.0.0.1" });
        Pdu own = Pdus.Build(Es(1, Spec.OwnSite, Spec.OwnApplication));
        Pdu foreign = Pdus.Build(Es(2, 5, 77));
        t.Send(in own);
        t.Send(in foreign);

        Assert.True(t.WaitForPdus(2, Bound));
        var sink = new RecordingSink();
        Assert.Equal(1, t.Drain(sink, 10));
        Assert.Equal(2u, sink.Items.Single().Pdu.Timestamp);
        Assert.Equal(1, t.Counters.FilteredOwn);
    }

    [Fact]
    public void A_datagram_from_a_raw_socket_with_several_pdus_and_junk_is_handled()
    {
        int port = Pdus.FreePort();
        using var t = Open(Pdus.Settings() with { Port = port, LocalAddress = "127.0.0.1" });
        using var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        raw.SendTo(Pdus.Concat(Pdus.Encode(Es(1)), Pdus.Encode(Es(2)), Pdus.Encode(Es(3))), Pdus.Loopback(port));
        raw.SendTo(new byte[] { 1, 2, 3 }, Pdus.Loopback(port));
        raw.SendTo(Pdus.Encode(Es(4, exercise: 2)), Pdus.Loopback(port));
        raw.SendTo(Pdus.Encode(Es(5)), Pdus.Loopback(port));

        Assert.True(t.WaitForPdus(6, Bound));
        var sink = new RecordingSink();
        t.Drain(sink, 100);
        Assert.Equal(new uint[] { 1, 2, 3, 5 }, sink.Items.Select(i => i.Pdu.Timestamp));
        Assert.True(t.Counters.Balanced);
    }

    [BroadcastFact]
    public void Two_transports_on_one_port_both_hear_a_broadcast_and_the_sender_filters_its_echo()
    {
        int port = Pdus.FreePort();
        // bound to the wildcard address: a socket bound to a unicast address does not receive broadcasts on Linux
        var common = Pdus.Settings() with { Address = "127.255.255.255", Port = port };
        using var sender = Open(common with { OwnSite = 5, OwnApplication = 77 });
        using var one = Open(common with { OwnSite = 6, OwnApplication = 78 });
        using var two = Open(common with { OwnSite = 7, OwnApplication = 79 });

        Pdu pdu = Pdus.Build(Es(1, 5, 77));
        sender.Send(in pdu);

        Assert.True(one.WaitForPdus(1, Bound));
        Assert.True(two.WaitForPdus(1, Bound));
        Assert.True(sender.WaitForPdus(1, Bound));
        Assert.Equal(1, one.Drain(new RecordingSink(), 10));
        Assert.Equal(1, two.Drain(new RecordingSink(), 10));
        Assert.Equal(0, sender.Drain(new RecordingSink(), 10));
        Assert.Equal(1, sender.Counters.FilteredOwn);
    }

    [MulticastFact]
    public void Two_transports_on_one_port_both_hear_a_multicast_group()
    {
        int port = Pdus.FreePort();
        var common = Pdus.Settings() with { Address = "239.255.42.99", Port = port, LocalAddress = "127.0.0.1" };
        using var sender = Open(common with { OwnSite = 5, OwnApplication = 77 });
        using var one = Open(common with { OwnSite = 6, OwnApplication = 78 });
        using var two = Open(common with { OwnSite = 7, OwnApplication = 79 });

        for (uint i = 1; i <= 5; i++)
        {
            Pdu pdu = Pdus.Build(Es(i, 5, 77));
            sender.Send(in pdu);
        }

        Assert.True(one.WaitForPdus(5, Bound));
        Assert.True(two.WaitForPdus(5, Bound));
        var a = new RecordingSink();
        var b = new RecordingSink();
        one.Drain(a, 10);
        two.Drain(b, 10);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, a.Items.Select(i => i.Pdu.Timestamp));
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, b.Items.Select(i => i.Pdu.Timestamp));
    }

    [Fact]
    public void Dispose_closes_the_real_socket_and_frees_the_port()
    {
        int port = Pdus.FreePort();
        var t = Open(Pdus.Settings() with { Port = port, LocalAddress = "127.0.0.1" });
        var thread = t.ReceiveThread!;
        t.Dispose();
        Assert.False(thread.IsAlive);

        // an exclusive bind (no SO_REUSEADDR) succeeds only if the transport's socket is really closed
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
        // sending after dispose is counted, not thrown
        Pdu pdu = Pdus.Build(Es(1));
        t.Send(in pdu);
        Assert.Equal(1, t.Counters.SendErrors);
    }

    [Fact]
    public void Open_with_invalid_settings_throws_and_leaks_nothing()
    {
        Assert.Throws<ArgumentException>(() => Open(Pdus.Settings() with { Port = 70000 }));
        Assert.Throws<ArgumentException>(() => Open(Pdus.Settings() with { Address = "bad" }));
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x!.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => obj.Length;
    }
}
