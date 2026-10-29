using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DISnet;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Dis7;

namespace SimNetGateway.Transport.DisUdp.Tests;

/// <summary>A sink that records what the transport delivered: the generic argument of each Deliver call, the PDU and the thread.</summary>
public sealed class RecordingSink : IIngressSink
{
    public List<(Type Declared, Pdu Pdu, int Thread)> Items { get; } = new();

    public void Deliver<T>(in T message, double? receivedAt = null)
    {
        Items.Add((typeof(T), (Pdu)(object)message!, System.Environment.CurrentManagedThreadId));
    }
}

/// <summary>An in-memory socket: tests feed datagrams, the receive loop takes them with a bounded wait.</summary>
public sealed class FakeSocket : IDatagramSocket
{
    private readonly BlockingCollection<byte[]> _incoming = new();
    private int _receiveFailures;

    public ConcurrentQueue<byte[]> Sent { get; } = new();

    public bool IsDisposed { get; private set; }

    public bool FailSends { get; set; }

    public bool ThrowOnDispose { get; set; }

    /// <summary>The next <paramref name="count"/> receives throw.</summary>
    public void FailReceives(int count) => Interlocked.Exchange(ref _receiveFailures, count);

    public void Feed(byte[] datagram) => _incoming.Add(datagram);

    public int Receive(byte[] buffer, int timeoutMs)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(FakeSocket));
        }

        if (Interlocked.Decrement(ref _receiveFailures) >= 0)
        {
            throw new SocketException((int)SocketError.NetworkDown);
        }

        if (!_incoming.TryTake(out var datagram, timeoutMs))
        {
            return -1;
        }

        datagram.CopyTo(buffer, 0);
        return datagram.Length;
    }

    public void Send(ReadOnlySpan<byte> datagram)
    {
        if (FailSends)
        {
            throw new SocketException((int)SocketError.NetworkUnreachable);
        }

        Sent.Enqueue(datagram.ToArray());
    }

    public void Dispose()
    {
        IsDisposed = true;
        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("dispose failed");
        }
    }
}

/// <summary>What a generated PDU is, by construction (the oracle of the filter tests does not look at the transport's code).</summary>
public enum Kind
{
    EntityState,
    Fire,
    Detonation,
    RemoveEntity,
    StartResume,
}

public sealed record Spec(Kind Kind, int Exercise, ushort Site, ushort Application, ushort EventSite, ushort EventApplication, uint Serial)
{
    public const ushort OwnSite = 1;
    public const ushort OwnApplication = 3001;

    /// <summary>The oracle: originated by the own site and application (the entity / firing / originating id, or the event id of Fire and Detonation).</summary>
    public bool IsOwn() =>
        (Site == OwnSite && Application == OwnApplication) ||
        (Kind is Kind.Fire or Kind.Detonation && EventSite == OwnSite && EventApplication == OwnApplication);

    public Type ConcreteType => Kind switch
    {
        Kind.EntityState => typeof(EntityStatePdu),
        Kind.Fire => typeof(FirePdu),
        Kind.Detonation => typeof(DetonationPdu),
        Kind.RemoveEntity => typeof(RemoveEntityPdu),
        _ => typeof(StartResumePdu),
    };
}

public static class Pdus
{
    public static DisUdpSettings Settings(int exercise = 1, int queue = 65536) => new()
    {
        Address = "127.0.0.1",
        Port = 3000,
        ExerciseId = exercise,
        OwnSite = Spec.OwnSite,
        OwnApplication = Spec.OwnApplication,
        QueueCapacity = queue,
    };

    public static EntityID Id(ushort site, ushort application, ushort number = 1) => new()
    {
        SimulationAddress = new SimulationAddress { Site = site, Application = application },
        EntityNumber = number,
    };

    public static EventIdentifier Event(ushort site, ushort application, ushort number = 1) => new()
    {
        SimulationAddress = new SimulationAddress { Site = site, Application = application },
        EventNumber = number,
    };

    public static Pdu Build(Spec s)
    {
        switch (s.Kind)
        {
            case Kind.EntityState:
                return new EntityStatePdu { ExerciseID = (byte)s.Exercise, Timestamp = s.Serial, EntityID = Id(s.Site, s.Application, 7) };
            case Kind.Fire:
                return new FirePdu
                {
                    ExerciseID = (byte)s.Exercise, Timestamp = s.Serial, FiringEntityID = Id(s.Site, s.Application, 7),
                    TargetEntityID = Id(9, 9, 9), MunitionExpendibleID = Id(0, 0, 0), EventID = Event(s.EventSite, s.EventApplication),
                };
            case Kind.Detonation:
                return new DetonationPdu
                {
                    ExerciseID = (byte)s.Exercise, Timestamp = s.Serial, FiringEntityID = Id(s.Site, s.Application, 7),
                    TargetEntityID = Id(9, 9, 9), ExplodingEntityID = Id(0, 0, 0), EventID = Event(s.EventSite, s.EventApplication),
                };
            case Kind.RemoveEntity:
                return new RemoveEntityPdu
                {
                    ExerciseID = (byte)s.Exercise, Timestamp = s.Serial, OriginatingEntityID = Id(s.Site, s.Application, 0),
                    ReceivingEntityID = Id(65535, 65535, 65535), RequestID = 1,
                };
            default:
                return new StartResumePdu
                {
                    ExerciseID = (byte)s.Exercise, Timestamp = s.Serial, OriginatingEntityID = Id(s.Site, s.Application, 0),
                    ReceivingEntityID = Id(65535, 65535, 65535), RequestID = 1,
                };
        }
    }

    public static byte[] Encode(Spec s) => PduCodec.Encode(Build(s));

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>A well framed PDU of a type the codec does not decode (type 99).</summary>
    public static byte[] UnsupportedPdu(int length = 20)
    {
        var bytes = new byte[length];
        bytes[0] = 7;
        bytes[2] = 99;
        bytes[8] = (byte)(length >> 8);
        bytes[9] = (byte)length;
        return bytes;
    }

    /// <summary>A header of an Entity State claiming 12 bytes: well framed, malformed body.</summary>
    public static byte[] BadBodyPdu()
    {
        var bytes = new byte[12];
        bytes[0] = 7;
        bytes[2] = PduTypes.EntityState;
        bytes[9] = 12;
        return bytes;
    }

    public static byte[] Truncated() => [7, 1, 1, 1, 0];

    public static IPEndPoint Loopback(int port) => new(IPAddress.Loopback, port);

    /// <summary>A currently free UDP port (the socket is closed again; sockets of the tests share ports with SO_REUSEADDR, so a rare race is harmless).</summary>
    public static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
