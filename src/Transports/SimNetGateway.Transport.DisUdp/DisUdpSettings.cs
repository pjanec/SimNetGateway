using System.Net;
using System.Net.Sockets;

namespace SimNetGateway.Transport.DisUdp;

/// <summary>
/// How <see cref="DisUdpTransport"/> talks to the DIS network (DD-006 section 14, H3). Built by the host from the gateway's
/// configuration (dis.address, dis.port, dis.exerciseId, shared.site, shared.application); this project does not reference the gateway.
/// </summary>
public sealed record DisUdpSettings
{
    /// <summary>Where PDUs are sent: a broadcast address, a multicast group (224.0.0.0/4, joined for receiving) or a unicast address.</summary>
    public string Address { get; init; } = "255.255.255.255";

    /// <summary>The port PDUs are sent to and, unless <see cref="ListenPort"/> is set, received on (DIS: 3000).</summary>
    public int Port { get; init; } = 3000;

    /// <summary>The port to receive on when it differs from <see cref="Port"/> (two peers on one host that must not share a unicast port); null: <see cref="Port"/>.</summary>
    public int? ListenPort { get; init; }

    /// <summary>The local interface address to bind to (unicast, broadcast) and to join / send multicast on; null: any.</summary>
    public string? LocalAddress { get; init; }

    /// <summary>Only PDUs of this exercise are delivered (0 .. 255).</summary>
    public int ExerciseId { get; init; }

    /// <summary>Own site: a PDU originated by (<see cref="OwnSite"/>, <see cref="OwnApplication"/>) is a looped-back echo and is not delivered.</summary>
    public int OwnSite { get; init; } = 1;

    public int OwnApplication { get; init; } = 3001;

    /// <summary>The most decoded PDUs waiting for the engine's drain; further ones are dropped and counted.</summary>
    public int QueueCapacity { get; init; } = 65536;

    /// <summary>Multicast time to live of sent datagrams.</summary>
    public int MulticastTtl { get; init; } = 1;

    /// <summary>True: multicast datagrams sent here are looped back to local receivers (needed for peers on the same host).</summary>
    public bool MulticastLoopback { get; init; } = true;

    public IPAddress DestinationAddress => IPAddress.Parse(Address);

    public bool IsMulticast => DestinationAddress.AddressFamily == AddressFamily.InterNetwork &&
                               (DestinationAddress.GetAddressBytes()[0] & 0xF0) == 0xE0;

    public int EffectiveListenPort => ListenPort ?? Port;

    /// <summary>Throws <see cref="ArgumentException"/> naming the first invalid value.</summary>
    public void Validate()
    {
        if (!IPAddress.TryParse(Address, out var destination) || destination.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"address '{Address}' is not an IPv4 address", nameof(Address));
        }

        if (LocalAddress is not null && (!IPAddress.TryParse(LocalAddress, out var local) || local.AddressFamily != AddressFamily.InterNetwork))
        {
            throw new ArgumentException($"local address '{LocalAddress}' is not an IPv4 address", nameof(LocalAddress));
        }

        CheckRange(Port, 1, 65535, nameof(Port));
        if (ListenPort is { } listen)
        {
            CheckRange(listen, 1, 65535, nameof(ListenPort));
        }

        CheckRange(ExerciseId, 0, 255, nameof(ExerciseId));
        CheckRange(OwnSite, 0, 65535, nameof(OwnSite));
        CheckRange(OwnApplication, 0, 65535, nameof(OwnApplication));
        CheckRange(QueueCapacity, 1, int.MaxValue, nameof(QueueCapacity));
        CheckRange(MulticastTtl, 1, 255, nameof(MulticastTtl));
    }

    private static void CheckRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentException($"{name} {value} is outside {min} .. {max}", name);
        }
    }
}
