using System.Collections.Concurrent;
using DISnet;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Dis7;

namespace SimNetGateway.Transport.DisUdp;

/// <summary>
/// The DIS network side of the HROT DIS gateway (DD-006 section 14, H3): the inbound channel (decoded PDUs, each delivered as its concrete class
/// because the gateway's modules route on it) and the outbound channel of <see cref="Pdu"/> over one UDP socket.
/// <para>
/// Threading (R-018): the receive thread only decodes (Protocol.Dis7 <see cref="PduCodec"/>), filters and ENQUEUES; nothing reaches the engine
/// except inside <see cref="Drain"/>, which the engine thread calls. <see cref="Send"/> runs on the caller's thread: one PDU per datagram
/// (no bundling), never throwing - encode and socket failures are counted.
/// </para>
/// <para>
/// Filters: PDUs of another exercise, PDUs originated by the own site and application (looped-back echo of what the gateway sent: the entity,
/// firing, issuing or originating id, or the event id), PDUs of a type the codec does not decode and malformed PDUs are counted and not delivered.
/// A datagram that carries several PDUs is processed PDU by PDU in order.
/// </para>
/// </summary>
public sealed class DisUdpTransport : IInboundChannel, IOutboundChannel<Pdu>, IDisposable
{
    private const int PollMs = 50;
    private const int JoinMs = 5000;

    private readonly DisUdpSettings _settings;
    private readonly IDatagramSocket _socket;
    private readonly ConcurrentQueue<Pdu> _queue = new();
    private readonly object _arrival = new();
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private readonly byte[] _buffer = new byte[65536];
    private readonly ushort _ownSite;
    private readonly ushort _ownApplication;
    private readonly byte _exercise;
    private Thread? _thread;
    private int _disposed;

    private long _datagrams;
    private long _pdus;
    private long _delivered;
    private long _pending;
    private long _filteredExercise;
    private long _filteredOwn;
    private long _unsupported;
    private long _decodeErrors;
    private long _overflow;
    private long _receiveErrors;
    private long _sent;
    private long _encodeErrors;
    private long _sendErrors;

    /// <param name="settings">Exercise, own site / application and queue capacity (the socket was built from the same settings).</param>
    /// <param name="socket">The datagram socket; owned by the transport from now on (disposed with it).</param>
    /// <param name="source">The engine source of the inbound port this channel is bound to.</param>
    /// <param name="name">The channel name.</param>
    public DisUdpTransport(DisUdpSettings settings, IDatagramSocket socket, SourceId source, string name = "dis")
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentException.ThrowIfNullOrEmpty(name);
        settings.Validate();
        _settings = settings;
        _socket = socket;
        Source = source;
        Name = name;
        _ownSite = (ushort)settings.OwnSite;
        _ownApplication = (ushort)settings.OwnApplication;
        _exercise = (byte)settings.ExerciseId;
    }

    /// <summary>Opens the real UDP socket for <paramref name="settings"/> and starts the receive thread.</summary>
    public static DisUdpTransport Open(DisUdpSettings settings, SourceId source, string name = "dis")
    {
        var socket = new UdpDatagramSocket(settings);
        try
        {
            var transport = new DisUdpTransport(settings, socket, source, name);
            transport.Start();
            return transport;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public SourceId Source { get; }

    public string Name { get; }

    /// <summary>True while the receive thread runs.</summary>
    public bool IsReceiving => _thread is { IsAlive: true };

    /// <summary>The receive thread; null before <see cref="Start"/> (tests: its managed id and liveness).</summary>
    public Thread? ReceiveThread => _thread;

    public DisUdpCounters Counters => new()
    {
        DatagramsReceived = Interlocked.Read(ref _datagrams),
        PdusReceived = Interlocked.Read(ref _pdus),
        Delivered = Interlocked.Read(ref _delivered),
        Pending = Interlocked.Read(ref _pending),
        FilteredExercise = Interlocked.Read(ref _filteredExercise),
        FilteredOwn = Interlocked.Read(ref _filteredOwn),
        Unsupported = Interlocked.Read(ref _unsupported),
        DecodeErrors = Interlocked.Read(ref _decodeErrors),
        QueueOverflow = Interlocked.Read(ref _overflow),
        ReceiveErrors = Interlocked.Read(ref _receiveErrors),
        Sent = Interlocked.Read(ref _sent),
        EncodeErrors = Interlocked.Read(ref _encodeErrors),
        SendErrors = Interlocked.Read(ref _sendErrors),
    };

    /// <summary>Starts the receive thread (once).</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "DisUdp-receive-" + Name };
        _thread.Start();
    }

    // ---- inbound ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Decodes and filters one received datagram and enqueues the PDUs that pass, in order. This is what the receive thread calls for each
    /// datagram; it never throws and never touches the engine. Public so that the filtering can be tested without a socket.
    /// </summary>
    public void Ingest(ReadOnlySpan<byte> datagram)
    {
        try
        {
            IngestCore(datagram);
        }
        finally
        {
            lock (_arrival)
            {
                Monitor.PulseAll(_arrival);
            }
        }
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> PDU slots were received in total, or the timeout passes (a bounded wait on a signal, for
    /// tests and tools that must not sleep); true when the count was reached.
    /// </summary>
    public bool WaitForPdus(long count, TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        lock (_arrival)
        {
            while (Interlocked.Read(ref _pdus) < count)
            {
                var left = timeout - watch.Elapsed;
                if (left <= TimeSpan.Zero)
                {
                    return false;
                }

                Monitor.Wait(_arrival, left);
            }
        }

        return true;
    }

    private void IngestCore(ReadOnlySpan<byte> datagram)
    {
        Interlocked.Increment(ref _datagrams);
        PduDecodeResult result;
        try
        {
            result = PduCodec.Decode(datagram);
        }
        catch (Exception)
        {
            // the codec does not throw; this keeps the receive loop alive even if it ever did
            Interlocked.Increment(ref _pdus);
            Interlocked.Increment(ref _decodeErrors);
            return;
        }

        foreach (var item in result.Items)
        {
            Interlocked.Increment(ref _pdus);
            switch (item.Status)
            {
                case PduDecodeStatus.Unsupported:
                    Interlocked.Increment(ref _unsupported);
                    continue;
                case PduDecodeStatus.Error:
                    Interlocked.Increment(ref _decodeErrors);
                    continue;
            }

            var pdu = item.Pdu!;
            if (pdu.ExerciseID != _exercise)
            {
                Interlocked.Increment(ref _filteredExercise);
                continue;
            }

            if (IsOwn(pdu, _ownSite, _ownApplication))
            {
                Interlocked.Increment(ref _filteredOwn);
                continue;
            }

            if (Interlocked.Read(ref _pending) >= _settings.QueueCapacity)
            {
                Interlocked.Increment(ref _overflow);
                continue;
            }

            Interlocked.Increment(ref _pending);
            _queue.Enqueue(pdu);
        }
    }

    /// <summary>Hands at most <paramref name="budget"/> queued PDUs to the engine, each as its concrete class; engine thread only.</summary>
    public int Drain(IIngressSink sink, int budget)
    {
        ArgumentNullException.ThrowIfNull(sink);
        int count = 0;
        while (count < budget && _queue.TryDequeue(out var pdu))
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _delivered);
            count++;
            DeliverTyped(sink, pdu);
        }

        return count;
    }

    private static void DeliverTyped(IIngressSink sink, Pdu pdu)
    {
        // the gateway's modules register for the concrete class (OnMessage<EntityStatePdu>), so the generic argument must be that class
        switch (pdu)
        {
            case EntityStatePdu p: sink.Deliver(in p); break;
            case FirePdu p: sink.Deliver(in p); break;
            case DetonationPdu p: sink.Deliver(in p); break;
            case CollisionPdu p: sink.Deliver(in p); break;
            case CreateEntityPdu p: sink.Deliver(in p); break;
            case RemoveEntityPdu p: sink.Deliver(in p); break;
            case StartResumePdu p: sink.Deliver(in p); break;
            case StopFreezePdu p: sink.Deliver(in p); break;
            case AcknowledgePdu p: sink.Deliver(in p); break;
            case ActionRequestPdu p: sink.Deliver(in p); break;
            case ActionResponsePdu p: sink.Deliver(in p); break;
            case EntityStateUpdatePdu p: sink.Deliver(in p); break;
            default: sink.Deliver(in pdu); break;
        }
    }

    /// <summary>True when the PDU was originated by the given site and application (the echo filter, per PDU kind as in the gateway's ingress).</summary>
    public static bool IsOwn(Pdu pdu, ushort site, ushort application)
    {
        bool Is(EntityID? id) => id?.SimulationAddress is { } a && a.Site == site && a.Application == application;
        bool IsEvent(EventIdentifier? id) => id?.SimulationAddress is { } a && a.Site == site && a.Application == application;

        return pdu switch
        {
            EntityStatePdu p => Is(p.EntityID),
            EntityStateUpdatePdu p => Is(p.EntityID),
            FirePdu p => Is(p.FiringEntityID) || IsEvent(p.EventID),
            DetonationPdu p => Is(p.FiringEntityID) || IsEvent(p.EventID),
            CollisionPdu p => Is(p.IssuingEntityID) || IsEvent(p.EventID),
            SimulationManagementFamilyPdu p => Is(p.OriginatingEntityID),
            _ => false,
        };
    }

    private void ReceiveLoop()
    {
        while (!_stopSignal.IsSet)
        {
            try
            {
                int length = _socket.Receive(_buffer, PollMs);
                if (length >= 0)
                {
                    Ingest(new ReadOnlySpan<byte>(_buffer, 0, length));
                }
            }
            catch (Exception)
            {
                if (_stopSignal.IsSet)
                {
                    return;
                }

                Interlocked.Increment(ref _receiveErrors);
                // do not spin on a failing socket
                _stopSignal.Wait(10);
            }
        }
    }

    // ---- outbound --------------------------------------------------------------------------------------------------------

    /// <summary>Encodes the PDU and sends it as one datagram; failures are counted, never thrown.</summary>
    public void Send(in Pdu message)
    {
        byte[] bytes;
        try
        {
            bytes = PduCodec.Encode(message);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _encodeErrors);
            return;
        }

        try
        {
            _socket.Send(bytes);
            Interlocked.Increment(ref _sent);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _sendErrors);
        }
    }

    // ---- lifetime --------------------------------------------------------------------------------------------------------

    /// <summary>Stops the receive thread (bounded wait) and closes the socket; safe to call twice, never throws.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopSignal.Set();
        try
        {
            _thread?.Join(JoinMs);
        }
        catch (Exception)
        {
        }

        try
        {
            _socket.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
