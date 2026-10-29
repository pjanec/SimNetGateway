using System.Net;
using System.Net.Sockets;

namespace SimNetGateway.Transport.DisUdp;

/// <summary>
/// The real UDP socket: SO_REUSEADDR (several processes on one host may share the DIS port), broadcast enabled, multicast joined when the
/// destination is a multicast group. Plain System.Net.Sockets, the same code on Linux and Windows.
/// </summary>
public sealed class UdpDatagramSocket : IDatagramSocket
{
    private readonly Socket _socket;
    private readonly IPEndPoint _destination;
    private readonly IPAddress? _group;
    private readonly IPAddress _local;
    private bool _disposed;

    public UdpDatagramSocket(DisUdpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _destination = new IPEndPoint(settings.DestinationAddress, settings.Port);
        _local = settings.LocalAddress is null ? IPAddress.Any : IPAddress.Parse(settings.LocalAddress);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.EnableBroadcast = true;
            if (OperatingSystem.IsWindows())
            {
                // Windows reports an ICMP "port unreachable" of an earlier send as a receive error; switch that off (SIO_UDP_CONNRESET).
                try
                {
                    _socket.IOControl(-1744830452, [0, 0, 0, 0], null);
                }
                catch (SocketException)
                {
                }
            }

            // multicast is received on the wildcard address (binding to a group address is not portable)
            var bindTo = settings.IsMulticast ? IPAddress.Any : _local;
            _socket.Bind(new IPEndPoint(bindTo, settings.EffectiveListenPort));
            if (settings.IsMulticast)
            {
                _group = _destination.Address;
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, settings.MulticastTtl);
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, settings.MulticastLoopback);
                if (settings.LocalAddress is not null)
                {
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, _local.GetAddressBytes());
                }

                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(_group, _local));
            }
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    /// <summary>The local port actually bound.</summary>
    public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public int Receive(byte[] buffer, int timeoutMs)
    {
        if (!_socket.Poll(Math.Max(0, timeoutMs) * 1000, SelectMode.SelectRead))
        {
            return -1;
        }

        return _socket.Receive(buffer, SocketFlags.None);
    }

    public void Send(ReadOnlySpan<byte> datagram) => _socket.SendTo(datagram, SocketFlags.None, _destination);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_group is not null)
        {
            try
            {
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(_group, _local));
            }
            catch (Exception)
            {
                // leaving a group on a dying socket is best effort
            }
        }

        _socket.Dispose();
    }
}
