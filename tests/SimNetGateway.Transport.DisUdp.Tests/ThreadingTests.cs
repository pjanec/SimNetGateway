using System.Diagnostics;
using SimNetGateway.Engine;
using DISnet;
using SimNetGateway.Protocol.Dis7;

namespace SimNetGateway.Transport.DisUdp.Tests;

/// <summary>R-018: the receive thread only decodes and enqueues; the engine's drain delivers. Plus the lifetime of the thread and the socket.</summary>
public class ThreadingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static Spec Es(uint serial, int exercise = 1) => new(Kind.EntityState, exercise, 5, 77, 5, 77, serial);

    [Fact]
    public void Nothing_reaches_the_sink_except_inside_the_drain_on_the_draining_thread()
    {
        var socket = new FakeSocket();
        using var transport = new DisUdpTransport(Pdus.Settings(), socket, new SourceId(1));
        transport.Start();
        var sink = new RecordingSink();

        // the sink is known to the transport from this drain on; whatever the receive thread does next must not touch it
        Assert.Equal(0, transport.Drain(sink, 10));
        for (uint i = 1; i <= 20; i++)
        {
            socket.Feed(Pdus.Encode(Es(i)));
        }

        Assert.True(transport.WaitForPdus(20, Bound), "the receive thread did not take the datagrams");
        Assert.Empty(sink.Items);
        Assert.Equal(0, transport.Counters.Delivered);
        Assert.Equal(20, transport.Counters.Pending);

        int me = System.Environment.CurrentManagedThreadId;
        Assert.NotEqual(me, transport.ReceiveThread!.ManagedThreadId);
        Assert.Equal(20, transport.Drain(sink, 100));
        Assert.All(sink.Items, i => Assert.Equal(me, i.Thread));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (uint)i), sink.Items.Select(i => i.Pdu.Timestamp));
    }

    [Fact]
    public void Concurrent_producers_and_drains_lose_and_duplicate_nothing()
    {
        const int producers = 8;
        const int perProducer = 1500;
        using var transport = new DisUdpTransport(Pdus.Settings(), new FakeSocket(), new SourceId(1));
        var sink = new RecordingSink();
        int running = producers;

        var threads = Enumerable.Range(0, producers).Select(p => new Thread(() =>
        {
            for (int i = 0; i < perProducer; i++)
            {
                // serial = producer * 100000 + sequence; every second datagram carries two PDUs
                uint serial = (uint)(p * 100000 + i);
                var one = Pdus.Encode(Es(serial));
                if (i % 2 == 0)
                {
                    transport.Ingest(one);
                }
                else
                {
                    // two PDUs: this one and a filtered other-exercise one that must vanish
                    transport.Ingest(Pdus.Concat(Pdus.Encode(Es(serial, exercise: 9)), one));
                }
            }

            Interlocked.Decrement(ref running);
        })).ToList();
        threads.ForEach(t => t.Start());

        var random = new Random(42);
        while (Volatile.Read(ref running) > 0)
        {
            transport.Drain(sink, random.Next(1, 200));
        }

        threads.ForEach(t => Assert.True(t.Join(Bound)));
        while (transport.Drain(sink, 500) > 0)
        {
        }

        var serials = sink.Items.Select(i => i.Pdu.Timestamp).ToList();
        Assert.Equal(producers * perProducer, serials.Count);
        Assert.Equal(serials.Count, serials.Distinct().Count());
        foreach (var group in serials.GroupBy(s => s / 100000))
        {
            Assert.Equal(Enumerable.Range(0, perProducer).Select(i => (uint)(group.Key * 100000 + i)), group);
        }

        var c = transport.Counters;
        Assert.Equal(producers * perProducer, c.Delivered);
        Assert.Equal(producers * perProducer / 2, c.FilteredExercise);
        Assert.Equal(0, c.Pending);
        Assert.True(c.Balanced);
    }

    [Fact]
    public void The_receive_loop_survives_socket_failures_and_malformed_datagrams()
    {
        var socket = new FakeSocket();
        using var transport = new DisUdpTransport(Pdus.Settings(), socket, new SourceId(1));
        socket.FailReceives(3);
        transport.Start();
        socket.Feed(Pdus.Truncated());
        socket.Feed([]);
        socket.Feed(Pdus.Encode(Es(1)));

        Assert.True(transport.WaitForPdus(3, Bound));
        Assert.True(transport.IsReceiving);
        var c = transport.Counters;
        Assert.Equal(3, c.ReceiveErrors);
        Assert.Equal(2, c.DecodeErrors);
        Assert.Equal(1, transport.Drain(new RecordingSink(), 10));
    }

    [Fact]
    public void Dispose_stops_the_thread_within_a_bounded_time_and_closes_the_socket()
    {
        var socket = new FakeSocket();
        var transport = new DisUdpTransport(Pdus.Settings(), socket, new SourceId(1));
        transport.Start();
        var thread = transport.ReceiveThread!;
        Assert.True(thread.IsAlive);

        var watch = Stopwatch.StartNew();
        transport.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Dispose took {watch.Elapsed}");
        Assert.False(thread.IsAlive);
        Assert.False(transport.IsReceiving);
        Assert.True(socket.IsDisposed);
        transport.Dispose();
        Assert.Throws<ObjectDisposedException>(() => transport.Start());
    }

    [Fact]
    public void Dispose_does_not_throw_when_the_socket_does_or_nothing_was_started()
    {
        var socket = new FakeSocket { ThrowOnDispose = true };
        var transport = new DisUdpTransport(Pdus.Settings(), socket, new SourceId(1));
        transport.Dispose();
        Assert.True(socket.IsDisposed);

        var started = new DisUdpTransport(Pdus.Settings(), new FakeSocket { ThrowOnDispose = true }, new SourceId(1));
        started.Start();
        started.Dispose();
        Assert.False(started.IsReceiving);
    }

    [Fact]
    public void Dispose_while_datagrams_are_arriving_ends_cleanly()
    {
        var socket = new FakeSocket();
        var transport = new DisUdpTransport(Pdus.Settings(), socket, new SourceId(1));
        transport.Start();
        for (uint i = 0; i < 5000; i++)
        {
            socket.Feed(Pdus.Encode(Es(i)));
        }

        transport.Dispose();
        Assert.False(transport.IsReceiving);
        Assert.True(transport.Counters.Balanced);
    }

    [Fact]
    public void Send_encodes_one_pdu_per_datagram_and_counts_failures_without_throwing()
    {
        var socket = new FakeSocket();
        using var transport = new DisUdpTransport(Pdus.Settings(), socket, new SourceId(1));
        Pdu a = Pdus.Build(Es(1));
        Pdu b = Pdus.Build(new Spec(Kind.Fire, 1, 1, 3001, 1, 3001, 2));
        transport.Send(in a);
        transport.Send(in b);

        Assert.Equal(2, socket.Sent.Count);
        Assert.Equal(PduCodec.Encode(a), socket.Sent.ToArray()[0]);
        Assert.Equal(PduCodec.Encode(b), socket.Sent.ToArray()[1]);
        Assert.Equal(2, transport.Counters.Sent);

        // cannot be encoded: 256 variable parameters
        var tooMany = new EntityStatePdu();
        for (int i = 0; i < 256; i++)
        {
            tooMany.VariableParameters.Add(new VariableParameter());
        }

        Pdu bad = tooMany;
        transport.Send(in bad);
        Assert.Equal(1, transport.Counters.EncodeErrors);

        socket.FailSends = true;
        transport.Send(in a);
        var c = transport.Counters;
        Assert.Equal(1, c.SendErrors);
        Assert.Equal(2, c.Sent);
    }
}
