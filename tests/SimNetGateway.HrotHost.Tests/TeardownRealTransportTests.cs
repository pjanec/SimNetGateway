using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SimNetGateway.Gateway.HrotDis;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>Teardown on the real DIS transport (a real UDP socket on loopback): Dispose frees the port and stops the receive thread.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class TeardownRealTransportTests
{
    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    [Fact]
    public void Dispose_frees_the_udp_port_and_stops_the_receive_thread()
    {
        int port = FreePort();
        var h = new PackHarness(dis: true, realDis: true,
            disConfiguration: new DisConfiguration { ExerciseId = 1, Address = "127.0.0.1", Port = port, HeartbeatS = 5, TimeoutS = 12 });
        h.Steps(3);
        var transport = h.DisTransport;
        var thread = (Thread?)typeof(SimNetGateway.Transport.DisUdp.DisUdpTransport).GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(transport)
            ?? throw new InvalidOperationException("the receive thread field of DisUdpTransport was not found");
        Assert.True(thread.IsAlive);
        using (var busy = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            Assert.ThrowsAny<SocketException>(() => busy.Bind(new IPEndPoint(IPAddress.Any, port)));   // the gateway holds it
        }

        h.System.Dispose();

        Assert.False(thread.IsAlive, "the receive thread must be joined");
        using var again = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        again.Bind(new IPEndPoint(IPAddress.Any, port));   // bindable right after, without SO_REUSEADDR
        Assert.Empty(h.Errors);
        h.World.Dispose();
    }
}
