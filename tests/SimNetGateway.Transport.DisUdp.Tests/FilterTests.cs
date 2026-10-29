using DISnet;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Dis7;

namespace SimNetGateway.Transport.DisUdp.Tests;

/// <summary>Filtering, queueing and counting of the inbound side, over no socket at all (Ingest) - seeded random traffic against an oracle.</summary>
public class FilterTests
{
    private static DisUdpTransport Create(int exercise = 1, int queue = 65536) =>
        new(Pdus.Settings(exercise, queue), new FakeSocket(), new SourceId(1), "dis");

    private static Spec Make(Kind kind, int exercise = 1, ushort site = 5, ushort app = 77, ushort eventSite = 5, ushort eventApp = 77, uint serial = 1) =>
        new(kind, exercise, site, app, eventSite, eventApp, serial);

    [Theory]
    [InlineData(Kind.EntityState)]
    [InlineData(Kind.Fire)]
    [InlineData(Kind.Detonation)]
    [InlineData(Kind.RemoveEntity)]
    [InlineData(Kind.StartResume)]
    public void A_pdu_is_delivered_as_its_concrete_class_not_as_the_base_type(Kind kind)
    {
        using var transport = Create();
        var spec = Make(kind);
        transport.Ingest(Pdus.Encode(spec));
        var sink = new RecordingSink();
        Assert.Equal(1, transport.Drain(sink, 10));

        var (declared, pdu, _) = Assert.Single(sink.Items);
        Assert.Equal(spec.ConcreteType, declared);
        Assert.Equal(spec.ConcreteType, pdu.GetType());
    }

    [Fact]
    public void Every_decoded_pdu_type_is_delivered_with_its_own_class()
    {
        using var transport = Create();
        Pdu[] pdus =
        [
            new EntityStatePdu(), new FirePdu(), new DetonationPdu(), new CollisionPdu(), new CreateEntityPdu(), new RemoveEntityPdu(),
            new StartResumePdu(), new StopFreezePdu(), new AcknowledgePdu(), new ActionRequestPdu(), new ActionResponsePdu(), new EntityStateUpdatePdu(),
        ];
        foreach (var pdu in pdus)
        {
            pdu.ExerciseID = 1;
            transport.Ingest(PduCodec.Encode(pdu));
        }

        var sink = new RecordingSink();
        transport.Drain(sink, 100);
        Assert.Equal(pdus.Select(p => p.GetType()), sink.Items.Select(i => i.Declared));
    }

    [Fact]
    public void Another_exercise_is_filtered_and_counted()
    {
        using var transport = Create(exercise: 4);
        transport.Ingest(Pdus.Encode(Make(Kind.EntityState, exercise: 3)));
        transport.Ingest(Pdus.Encode(Make(Kind.EntityState, exercise: 4)));

        var sink = new RecordingSink();
        Assert.Equal(1, transport.Drain(sink, 10));
        var c = transport.Counters;
        Assert.Equal(1, c.FilteredExercise);
        Assert.Equal(1, c.Delivered);
        Assert.True(c.Balanced);
    }

    [Theory]
    [InlineData(Kind.EntityState)]
    [InlineData(Kind.Fire)]
    [InlineData(Kind.Detonation)]
    [InlineData(Kind.RemoveEntity)]
    [InlineData(Kind.StartResume)]
    public void A_pdu_of_the_own_site_and_application_is_an_echo(Kind kind)
    {
        using var transport = Create();
        transport.Ingest(Pdus.Encode(Make(kind, site: Spec.OwnSite, app: Spec.OwnApplication, eventSite: Spec.OwnSite, eventApp: Spec.OwnApplication)));
        // same site, other application / other site, same application: not own
        transport.Ingest(Pdus.Encode(Make(kind, site: Spec.OwnSite, app: Spec.OwnApplication + 1, eventSite: 5, eventApp: 77)));
        transport.Ingest(Pdus.Encode(Make(kind, site: Spec.OwnSite + 1, app: Spec.OwnApplication, eventSite: 5, eventApp: 77)));

        Assert.Equal(1, transport.Counters.FilteredOwn);
        Assert.Equal(2, transport.Drain(new RecordingSink(), 10));
    }

    [Theory]
    [InlineData(Kind.Fire)]
    [InlineData(Kind.Detonation)]
    public void A_fire_or_detonation_with_an_own_event_id_is_an_echo_even_from_a_foreign_shooter(Kind kind)
    {
        using var transport = Create();
        transport.Ingest(Pdus.Encode(Make(kind, site: 5, app: 77, eventSite: Spec.OwnSite, eventApp: Spec.OwnApplication)));
        Assert.Equal(1, transport.Counters.FilteredOwn);
        Assert.Equal(0, transport.Drain(new RecordingSink(), 10));
    }

    [Fact]
    public void Unsupported_and_malformed_pdus_are_counted_not_delivered_and_never_throw()
    {
        using var transport = Create();
        transport.Ingest(Pdus.UnsupportedPdu());
        transport.Ingest(Pdus.BadBodyPdu());
        transport.Ingest(Pdus.Truncated());
        transport.Ingest([]);
        transport.Ingest(new byte[] { 0xFF });

        var c = transport.Counters;
        Assert.Equal(5, c.DatagramsReceived);
        Assert.Equal(1, c.Unsupported);
        Assert.Equal(4, c.DecodeErrors);
        Assert.Equal(0, transport.Drain(new RecordingSink(), 10));
        Assert.True(c.Balanced);
    }

    [Fact]
    public void A_datagram_with_several_pdus_delivers_all_of_them_in_order()
    {
        using var transport = Create();
        var specs = new[]
        {
            Make(Kind.EntityState, serial: 1), Make(Kind.Fire, serial: 2), Make(Kind.Detonation, serial: 3),
            Make(Kind.RemoveEntity, serial: 4), Make(Kind.StartResume, serial: 5),
        };
        transport.Ingest(Pdus.Concat(specs.Select(Pdus.Encode).ToArray()));

        var sink = new RecordingSink();
        Assert.Equal(5, transport.Drain(sink, 10));
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, sink.Items.Select(i => i.Pdu.Timestamp));
        Assert.Equal(1, transport.Counters.DatagramsReceived);
        Assert.Equal(5, transport.Counters.PdusReceived);
    }

    [Fact]
    public void Pdus_around_a_bad_one_in_the_same_datagram_still_pass()
    {
        using var transport = Create();
        var datagram = Pdus.Concat(
            Pdus.Encode(Make(Kind.EntityState, serial: 1)), Pdus.BadBodyPdu(), Pdus.UnsupportedPdu(),
            Pdus.Encode(Make(Kind.EntityState, serial: 2)), Pdus.Truncated());
        transport.Ingest(datagram);

        var sink = new RecordingSink();
        transport.Drain(sink, 10);
        Assert.Equal(new uint[] { 1, 2 }, sink.Items.Select(i => i.Pdu.Timestamp));
        var c = transport.Counters;
        Assert.Equal(1, c.Unsupported);
        Assert.Equal(2, c.DecodeErrors);
        Assert.True(c.Balanced);
    }

    [Fact]
    public void Drain_respects_its_budget_and_keeps_the_rest_in_order()
    {
        using var transport = Create();
        for (uint i = 1; i <= 10; i++)
        {
            transport.Ingest(Pdus.Encode(Make(Kind.EntityState, serial: i)));
        }

        var sink = new RecordingSink();
        Assert.Equal(3, transport.Drain(sink, 3));
        Assert.Equal(7, transport.Counters.Pending);
        Assert.Equal(0, transport.Drain(sink, 0));
        Assert.Equal(7, transport.Drain(sink, 100));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (uint)i), sink.Items.Select(i => i.Pdu.Timestamp));
        Assert.Equal(0, transport.Drain(sink, 100));
    }

    [Fact]
    public void A_full_queue_drops_and_counts_the_overflow()
    {
        using var transport = Create(queue: 4);
        for (uint i = 1; i <= 10; i++)
        {
            transport.Ingest(Pdus.Encode(Make(Kind.EntityState, serial: i)));
        }

        var c = transport.Counters;
        Assert.Equal(4, c.Pending);
        Assert.Equal(6, c.QueueOverflow);
        Assert.True(c.Balanced);
        var sink = new RecordingSink();
        transport.Drain(sink, 100);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, sink.Items.Select(i => i.Pdu.Timestamp));
        // room again after the drain
        transport.Ingest(Pdus.Encode(Make(Kind.EntityState, serial: 11)));
        Assert.Equal(1, transport.Counters.Pending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void The_exercise_id_range_ends_work(int exercise)
    {
        using var transport = Create(exercise);
        transport.Ingest(Pdus.Encode(Make(Kind.EntityState, exercise: exercise)));
        transport.Ingest(Pdus.Encode(Make(Kind.EntityState, exercise: exercise == 0 ? 1 : exercise - 1)));
        Assert.Equal(1, transport.Drain(new RecordingSink(), 10));
        Assert.Equal(1, transport.Counters.FilteredExercise);
    }

    [Fact]
    public void Settings_are_validated()
    {
        Assert.Throws<ArgumentException>(() => Create(exercise: 256));
        Assert.Throws<ArgumentException>(() => new DisUdpTransport(Pdus.Settings() with { Port = 0 }, new FakeSocket(), new SourceId(1)));
        Assert.Throws<ArgumentException>(() => new DisUdpTransport(Pdus.Settings() with { Address = "not an address" }, new FakeSocket(), new SourceId(1)));
        Assert.Throws<ArgumentException>(() => new DisUdpTransport(Pdus.Settings() with { OwnSite = 70000 }, new FakeSocket(), new SourceId(1)));
        Assert.Throws<ArgumentException>(() => new DisUdpTransport(Pdus.Settings() with { LocalAddress = "x" }, new FakeSocket(), new SourceId(1)));
    }

    public static TheoryData<int> Seeds() => new() { 1, 2, 3, 4, 5, 6, 7, 8 };

    /// <summary>
    /// Random datagrams of 1 .. 4 PDUs (random kind, exercise, origin, event id) plus unsupported and malformed slots: after every datagram the
    /// counters balance and match the oracle; at the end the delivered PDUs are exactly the right-exercise, not-own ones, in arrival order, each as
    /// its concrete class.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_traffic_is_filtered_exactly_as_the_oracle_says(int seed)
    {
        var random = new Random(seed);
        using var transport = Create();
        var expected = new List<Spec>();
        long pdus = 0, filteredExercise = 0, filteredOwn = 0, unsupported = 0, errors = 0;
        uint serial = 0;
        ushort[] sites = [Spec.OwnSite, 5, 6];
        ushort[] apps = [Spec.OwnApplication, 77, 78];

        for (int d = 0; d < 400; d++)
        {
            var parts = new List<byte[]>();
            int slots = random.Next(1, 5);
            for (int i = 0; i < slots; i++)
            {
                int roll = random.Next(12);
                if (roll == 0)
                {
                    parts.Add(Pdus.UnsupportedPdu(12 + random.Next(0, 30)));
                    unsupported++;
                }
                else if (roll == 1)
                {
                    parts.Add(Pdus.BadBodyPdu());
                    errors++;
                }
                else
                {
                    var spec = new Spec((Kind)random.Next(5), random.Next(3), sites[random.Next(3)], apps[random.Next(3)],
                        sites[random.Next(3)], apps[random.Next(3)], ++serial);
                    parts.Add(Pdus.Encode(spec));
                    if (spec.Exercise != 1)
                    {
                        filteredExercise++;
                    }
                    else if (spec.IsOwn())
                    {
                        filteredOwn++;
                    }
                    else
                    {
                        expected.Add(spec);
                    }
                }

                pdus++;
            }

            if (random.Next(10) == 0)
            {
                parts.Add(Pdus.Truncated());
                errors++;
                pdus++;
            }

            transport.Ingest(Pdus.Concat(parts.ToArray()));

            var c = transport.Counters;
            Assert.True(c.Balanced, $"datagram {d}: counters do not add up");
            Assert.Equal(pdus, c.PdusReceived);
            Assert.Equal(filteredExercise, c.FilteredExercise);
            Assert.Equal(filteredOwn, c.FilteredOwn);
            Assert.Equal(unsupported, c.Unsupported);
            Assert.Equal(errors, c.DecodeErrors);
            Assert.Equal(d + 1, c.DatagramsReceived);
        }

        var sink = new RecordingSink();
        while (transport.Drain(sink, random.Next(1, 40)) > 0)
        {
        }

        Assert.Equal(expected.Select(s => s.Serial), sink.Items.Select(i => i.Pdu.Timestamp));
        Assert.Equal(expected.Select(s => s.ConcreteType), sink.Items.Select(i => i.Declared));
        Assert.Equal(expected.Select(s => s.ConcreteType), sink.Items.Select(i => i.Pdu.GetType()));
        var final = transport.Counters;
        Assert.Equal(expected.Count, final.Delivered);
        Assert.Equal(0, final.Pending);
        Assert.True(final.Balanced);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_garbage_never_throws_and_always_balances(int seed)
    {
        var random = new Random(seed);
        using var transport = Create();
        var valid = Pdus.Encode(Make(Kind.Fire));
        for (int i = 0; i < 2000; i++)
        {
            byte[] data;
            if (random.Next(2) == 0)
            {
                data = new byte[random.Next(0, 200)];
                random.NextBytes(data);
            }
            else
            {
                // a valid PDU with some bytes flipped or cut off
                data = (byte[])valid.Clone();
                for (int k = random.Next(0, 4); k > 0; k--)
                {
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                }

                data = data[..random.Next(0, data.Length + 1)];
            }

            transport.Ingest(data);
            Assert.True(transport.Counters.Balanced);
        }

        transport.Drain(new RecordingSink(), int.MaxValue);
        Assert.True(transport.Counters.Balanced);
    }
}
