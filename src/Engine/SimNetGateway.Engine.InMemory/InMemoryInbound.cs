using SimNetGateway.Engine;

namespace SimNetGateway.Engine.InMemory;

/// <summary>
/// An in-memory inbound channel for one message type; any thread may <see cref="Enqueue"/>, the engine thread drains.
/// A growable ring buffer under a lock: once its storage has grown, Enqueue and Drain allocate nothing.
/// </summary>
public sealed class InMemoryInbound<T> : IInboundChannel
{
    private readonly object _lock = new();
    private Entry[] _ring = new Entry[16];
    private int _head;
    private int _count;

    private struct Entry
    {
        public T Message;
        public double? ReceivedAt;
    }

    public InMemoryInbound(SourceId source, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Source = source;
        Name = name;
    }

    public SourceId Source { get; }

    public string Name { get; }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public void Enqueue(in T message, double? receivedAt = null)
    {
        lock (_lock)
        {
            if (_count == _ring.Length)
            {
                var bigger = new Entry[_ring.Length * 2];
                for (int i = 0; i < _count; i++)
                {
                    bigger[i] = _ring[(_head + i) % _ring.Length];
                }

                _ring = bigger;
                _head = 0;
            }

            ref var slot = ref _ring[(_head + _count) % _ring.Length];
            slot.Message = message;
            slot.ReceivedAt = receivedAt;
            _count++;
        }
    }

    public int Drain(IIngressSink sink, int budget)
    {
        ArgumentNullException.ThrowIfNull(sink);
        int n = 0;
        while (n < budget)
        {
            T message;
            double? receivedAt;
            lock (_lock)
            {
                if (_count == 0)
                {
                    break;
                }

                ref var slot = ref _ring[_head];
                message = slot.Message;
                receivedAt = slot.ReceivedAt;
                slot = default;
                _head = (_head + 1) % _ring.Length;
                _count--;
            }

            // Delivered outside the lock: a handler (or a loopback tap) may enqueue again.
            sink.Deliver(in message, receivedAt);
            n++;
        }

        return n;
    }
}

/// <summary>
/// An in-memory inbound channel for many message types that keeps the arrival order across types. Messages travel in
/// pooled per-type envelopes and are delivered with their concrete static type, so the engine routes by the real
/// message type (struct messages are not boxed). Allocates only while the pools warm up.
/// </summary>
public sealed class InMemoryInbound : IInboundChannel
{
    private readonly object _lock = new();
    private readonly Queue<Envelope> _queue = new();

    public InMemoryInbound(SourceId source, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Source = source;
        Name = name;
    }

    public SourceId Source { get; }

    public string Name { get; }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    public void Enqueue<T>(in T message, double? receivedAt = null)
    {
        var envelope = Envelope<T>.Rent(in message, receivedAt);
        lock (_lock)
        {
            _queue.Enqueue(envelope);
        }
    }

    public int Drain(IIngressSink sink, int budget)
    {
        ArgumentNullException.ThrowIfNull(sink);
        int n = 0;
        while (n < budget)
        {
            Envelope envelope;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    break;
                }

                envelope = _queue.Dequeue();
            }

            try
            {
                envelope.Deliver(sink);
            }
            finally
            {
                envelope.Release();
            }

            n++;
        }

        return n;
    }

    private abstract class Envelope
    {
        public abstract void Deliver(IIngressSink sink);

        public abstract void Release();
    }

    private sealed class Envelope<T> : Envelope
    {
        private static readonly Stack<Envelope<T>> s_pool = new();

        private T _message = default!;
        private double? _receivedAt;

        public static Envelope<T> Rent(in T message, double? receivedAt)
        {
            Envelope<T>? envelope = null;
            lock (s_pool)
            {
                if (s_pool.Count > 0)
                {
                    envelope = s_pool.Pop();
                }
            }

            envelope ??= new Envelope<T>();
            envelope._message = message;
            envelope._receivedAt = receivedAt;
            return envelope;
        }

        public override void Deliver(IIngressSink sink) => sink.Deliver(in _message, _receivedAt);

        public override void Release()
        {
            _message = default!;
            _receivedAt = null;
            lock (s_pool)
            {
                s_pool.Push(this);
            }
        }
    }
}
