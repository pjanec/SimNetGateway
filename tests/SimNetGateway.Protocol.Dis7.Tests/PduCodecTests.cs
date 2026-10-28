using DISnet;

namespace SimNetGateway.Protocol.Dis7.Tests;

/// <summary>
/// Property tests of <see cref="PduCodec"/> with seeded random PDUs: encode -> decode is field-identical, the encoded
/// size is the standard's, several PDUs per datagram split correctly, and every malformation is an error entry
/// (never an exception, never a partial PDU).
/// </summary>
public class PduCodecTests
{
    private const int Iterations = 250;

    private static readonly string[] AllNames =
    {
        "EntityState", "EntityStateWithParameters", "Fire", "Detonation", "DetonationWithParameters", "Collision",
        "CreateEntity", "RemoveEntity", "StartResume", "StopFreeze", "Acknowledge", "ActionRequest", "ActionResponse",
        "EntityStateUpdate",
    };

    public static TheoryData<string> TypeNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in AllNames)
            {
                data.Add(name);
            }

            return data;
        }
    }

    private static (Pdu Pdu, int Size) Create(string name, Random random)
    {
        Pdu pdu;
        int parameters = 0;
        int size;
        switch (name)
        {
            case "EntityState": pdu = new EntityStatePdu(); size = 144; break;
            case "EntityStateWithParameters": pdu = new EntityStatePdu(); parameters = random.Next(1, 5); size = 144 + 16 * parameters; break;
            case "Fire": pdu = new FirePdu(); size = 96; break;
            case "Detonation": pdu = new DetonationPdu(); size = 104; break;
            case "DetonationWithParameters": pdu = new DetonationPdu(); parameters = random.Next(1, 4); size = 104 + 16 * parameters; break;
            case "Collision": pdu = new CollisionPdu(); size = 60; break;
            case "CreateEntity": pdu = new CreateEntityPdu(); size = 28; break;
            case "RemoveEntity": pdu = new RemoveEntityPdu(); size = 28; break;
            case "StartResume": pdu = new StartResumePdu(); size = 44; break;
            case "StopFreeze": pdu = new StopFreezePdu(); size = 40; break;
            case "Acknowledge": pdu = new AcknowledgePdu(); size = 32; break;
            case "ActionRequest": pdu = new ActionRequestPdu(); size = 40; break;
            case "ActionResponse": pdu = new ActionResponsePdu(); size = 40; break;
            case "EntityStateUpdate": pdu = new EntityStateUpdatePdu(); size = 72; break;
            default: throw new ArgumentException(name);
        }

        RandomFill.Fill(pdu, random, parameters);
        return (pdu, size);
    }

    [Theory]
    [MemberData(nameof(TypeNames))]
    public void Encode_then_decode_is_field_identical_and_has_the_standard_size(string name)
    {
        var random = new Random(20261028);
        for (int i = 0; i < Iterations; i++)
        {
            var (pdu, size) = Create(name, random);
            var bytes = PduCodec.Encode(pdu);

            Assert.Equal(size, bytes.Length);
            Assert.Equal(size, (bytes[8] << 8) | bytes[9]); // Length field, big-endian
            Assert.Equal(pdu.PduType, bytes[2]);

            var decoded = PduCodec.Decode(bytes);
            Assert.False(decoded.HasErrors, $"{name} #{i}: " + string.Join("; ", decoded.Items.Select(x => x.Error)));
            var item = Assert.Single(decoded.Items);
            Assert.Equal(PduDecodeStatus.Decoded, item.Status);
            Assert.Equal(0, item.Offset);
            Assert.Equal(size, item.Length);
            Assert.Equal(pdu.GetType(), item.Pdu!.GetType());
            var differences = DeepCompare.Differences(pdu, item.Pdu);
            Assert.True(differences.Count == 0, $"{name} #{i}: " + string.Join("; ", differences));

            // and the decoded object encodes to the same bytes again
            Assert.Equal(bytes, PduCodec.Encode(item.Pdu));
        }
    }

    [Fact]
    public void A_datagram_with_n_pdus_decodes_to_exactly_n_pdus_in_order()
    {
        var random = new Random(7);
        var names = AllNames;
        for (int round = 0; round < Iterations; round++)
        {
            int n = random.Next(1, 8);
            var sent = new List<Pdu>();
            var datagram = new List<byte>();
            for (int k = 0; k < n; k++)
            {
                var (pdu, _) = Create(names[random.Next(names.Length)], random);
                sent.Add(pdu);
                datagram.AddRange(PduCodec.Encode(pdu));
            }

            var result = PduCodec.Decode(datagram.ToArray());
            Assert.False(result.HasErrors);
            Assert.Equal(n, result.Items.Count);
            int offset = 0;
            for (int k = 0; k < n; k++)
            {
                var item = result.Items[k];
                Assert.Equal(PduDecodeStatus.Decoded, item.Status);
                Assert.Equal(offset, item.Offset);
                Assert.Equal(sent[k].GetType(), item.Pdu!.GetType());
                Assert.Empty(DeepCompare.Differences(sent[k], item.Pdu));
                offset += item.Length;
            }

            Assert.Equal(datagram.Count, offset);
            Assert.Equal(n, result.Pdus.Count());
        }
    }

    [Theory]
    [MemberData(nameof(TypeNames))]
    public void Every_truncation_of_a_single_pdu_is_an_error_without_exception_or_partial_pdu(string name)
    {
        var random = new Random(99);
        var (pdu, _) = Create(name, random);
        var bytes = PduCodec.Encode(pdu);

        for (int cut = 0; cut < bytes.Length; cut++)
        {
            var result = PduCodec.Decode(bytes.AsSpan(0, cut));
            Assert.True(result.HasErrors, $"{name}: {cut} of {bytes.Length} bytes did not report an error");
            Assert.Empty(result.Pdus);
            Assert.DoesNotContain(result.Items, item => item.Pdu is not null);
        }
    }

    [Fact]
    public void Every_truncation_inside_a_multi_pdu_datagram_reports_an_error_and_keeps_the_complete_leading_pdus()
    {
        var random = new Random(5);
        var first = Create("Fire", random).Pdu;
        var second = Create("EntityStateWithParameters", random).Pdu;
        var third = Create("StopFreeze", random).Pdu;
        var a = PduCodec.Encode(first);
        var b = PduCodec.Encode(second);
        var c = PduCodec.Encode(third);
        var all = a.Concat(b).Concat(c).ToArray();
        int[] boundaries = { 0, a.Length, a.Length + b.Length, all.Length };

        for (int cut = 1; cut < all.Length; cut++)
        {
            var result = PduCodec.Decode(all.AsSpan(0, cut));
            bool atBoundary = boundaries.Contains(cut);
            Assert.Equal(!atBoundary, result.HasErrors); // a cut exactly between PDUs is a valid shorter datagram
            int complete = boundaries.Count(x => x > 0 && x <= cut);
            Assert.Equal(complete, result.Pdus.Count());
        }
    }

    [Fact]
    public void Length_smaller_than_12_or_beyond_the_datagram_is_an_error()
    {
        var bytes = PduCodec.Encode(new CreateEntityPdu());

        foreach (int bad in new[] { 0, 1, 11 })
        {
            var copy = (byte[])bytes.Clone();
            copy[8] = 0; copy[9] = (byte)bad;
            var result = PduCodec.Decode(copy);
            Assert.True(result.HasErrors, $"Length {bad}");
            Assert.Empty(result.Pdus);
        }

        foreach (int bad in new[] { 29, 100, 65535 })
        {
            var copy = (byte[])bytes.Clone();
            copy[8] = (byte)(bad >> 8); copy[9] = (byte)bad;
            var result = PduCodec.Decode(copy);
            Assert.True(result.HasErrors, $"Length {bad}");
            Assert.Empty(result.Pdus);
        }
    }

    [Fact]
    public void Length_that_disagrees_with_the_content_is_an_error_not_a_half_object()
    {
        // A longer Length with matching trailing bytes: the content is 28 bytes, the PDU claims 32.
        var bytes = PduCodec.Encode(new CreateEntityPdu()).Concat(new byte[4]).ToArray();
        bytes[9] = 32;
        var result = PduCodec.Decode(bytes);
        Assert.True(result.HasErrors);
        Assert.Empty(result.Pdus);

        // A shorter Length than a fixed-size PDU needs: the next PDU starts inside the first.
        var entity = PduCodec.Encode(new EntityStatePdu());
        entity[9] = 100;
        var result2 = PduCodec.Decode(entity);
        Assert.True(result2.HasErrors);
        Assert.Empty(result2.Pdus);
    }

    [Fact]
    public void A_variable_parameter_count_larger_than_the_data_is_an_error()
    {
        var bytes = PduCodec.Encode(new EntityStatePdu());
        bytes[19] = 5; // number of variable parameters (header 12 + entity id 6 + force id 1)
        var result = PduCodec.Decode(bytes);
        Assert.True(result.HasErrors);
        Assert.Empty(result.Pdus);
    }

    [Fact]
    public void Unknown_types_are_reported_as_unsupported_and_decoding_continues()
    {
        var before = PduCodec.Encode(new CreateEntityPdu { RequestID = 1 });
        var unknown = new ByteBuilder().Header(1, 200, 9, 0, 20).Zeros(8).ToArray(); // type 200, 20 bytes
        var after = PduCodec.Encode(new RemoveEntityPdu { RequestID = 2 });

        var result = PduCodec.Decode(before.Concat(unknown).Concat(after).ToArray());

        Assert.False(result.HasErrors);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(PduDecodeStatus.Decoded, result.Items[0].Status);
        Assert.Equal(PduDecodeStatus.Unsupported, result.Items[1].Status);
        Assert.Equal(200, result.Items[1].PduType);
        Assert.Equal(20, result.Items[1].Length);
        Assert.Equal(before.Length, result.Items[1].Offset);
        Assert.Null(result.Items[1].Pdu);
        Assert.Equal(PduDecodeStatus.Decoded, result.Items[2].Status);
        Assert.Equal(2u, ((RemoveEntityPdu)result.Items[2].Pdu!).RequestID);
        Assert.Equal(2, result.Pdus.Count());
    }

    [Fact]
    public void A_known_pdu_type_that_is_not_decoded_is_unsupported_too()
    {
        // Type 25 (Transmitter) is valid DIS but not in the vendored classes.
        var bytes = new ByteBuilder().Header(1, 25, 4, 0, 12).ToArray();
        var result = PduCodec.Decode(bytes);
        Assert.False(result.HasErrors);
        Assert.Equal(PduDecodeStatus.Unsupported, Assert.Single(result.Items).Status);
    }

    [Fact]
    public void A_malformed_body_with_a_sound_length_is_an_error_and_decoding_continues()
    {
        // Entity state claims 5 variable parameters but is only 144 bytes long.
        var bad = PduCodec.Encode(new EntityStatePdu());
        bad[19] = 5;
        var good = PduCodec.Encode(new CreateEntityPdu { RequestID = 9 });

        var result = PduCodec.Decode(bad.Concat(good).ToArray());

        Assert.True(result.HasErrors);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(PduDecodeStatus.Error, result.Items[0].Status);
        Assert.Null(result.Items[0].Pdu);
        Assert.NotNull(result.Items[0].Error);
        Assert.Equal(PduDecodeStatus.Decoded, result.Items[1].Status);
    }

    [Fact]
    public void An_empty_datagram_is_an_error()
    {
        var result = PduCodec.Decode(ReadOnlySpan<byte>.Empty);
        Assert.True(result.HasErrors);
        Assert.Empty(result.Pdus);
    }

    [Fact]
    public void Random_garbage_never_throws_and_never_yields_a_pdu_unless_it_is_well_formed()
    {
        var random = new Random(31337);
        for (int i = 0; i < 2000; i++)
        {
            var garbage = new byte[random.Next(0, 200)];
            random.NextBytes(garbage);
            if (garbage.Length >= 12 && random.Next(2) == 0)
            {
                garbage[2] = (byte)new[] { 1, 2, 3, 11, 12, 13, 14, 15, 16 }[random.Next(9)]; // a known type
            }

            var result = PduCodec.Decode(garbage);
            foreach (var item in result.Items)
            {
                if (item.Status == PduDecodeStatus.Decoded)
                {
                    // whatever was accepted re-encodes to exactly the bytes it came from
                    Assert.Equal(garbage.AsSpan(item.Offset, item.Length).ToArray(), PduCodec.Encode(item.Pdu!));
                }
                else
                {
                    Assert.Null(item.Pdu);
                }
            }
        }
    }

    [Fact]
    public void Variable_datum_records_are_refused_in_both_directions()
    {
        var request = new ActionRequestPdu();
        request.VariableDatums.Add(new VariableDatum());
        Assert.Throws<ArgumentException>(() => PduCodec.Encode(request));

        var bytes = PduCodec.Encode(new ActionRequestPdu());
        bytes[35] = 1; // number of variable datum records (header 12 + ids 12 + request 4 + action 4 + fixed 4)
        Assert.True(PduCodec.Decode(bytes).HasErrors);
    }

    [Fact]
    public void Encode_rejects_more_variable_parameters_than_the_count_byte_holds()
    {
        var pdu = new EntityStatePdu();
        for (int i = 0; i < 256; i++)
        {
            pdu.VariableParameters.Add(new VariableParameter());
        }

        Assert.Throws<ArgumentException>(() => PduCodec.Encode(pdu));
    }

    [Fact]
    public void Simulation_management_sizes_do_not_include_the_duplicate_ids_any_more()
    {
        Assert.Equal(28, new CreateEntityPdu().GetMarshalledSize());
        Assert.Equal(28, new RemoveEntityPdu().GetMarshalledSize());
        Assert.Equal(44, new StartResumePdu().GetMarshalledSize());
        Assert.Equal(40, new StopFreezePdu().GetMarshalledSize());
        Assert.Equal(32, new AcknowledgePdu().GetMarshalledSize());
        Assert.Equal(40, new ActionRequestPdu().GetMarshalledSize());
        Assert.Equal(40, new ActionResponsePdu().GetMarshalledSize());
    }
}
