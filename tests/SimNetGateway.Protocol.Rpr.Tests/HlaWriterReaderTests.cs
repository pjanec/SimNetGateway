using SimNetGateway.Protocol.Rpr.Hla.Encoding;

namespace SimNetGateway.Protocol.Rpr.Tests;

/// <summary>The padded IEEE 1516.2 primitives (DD-008 section 3.3): alignment relative to the value start, composites, error state.</summary>
public class HlaWriterReaderTests
{
    [Fact]
    public void Basic_types_are_big_endian_and_aligned_to_their_size_from_the_start_of_the_value()
    {
        var w = new HlaWriter();
        w.Octet(0xAB);
        w.Int16BE(0x0102);        // @2 (1 pad)
        w.Int32BE(0x03040506);    // @4
        w.Octet(0xCD);            // @8
        w.Int64BE(0x0708090A0B0C0D0EL); // @16 (7 pad)
        w.Float32BE(1.0f);        // @24
        w.Float64BE(2.0);         // @32 (4 pad)

        var expected = new Ex().U8(0xAB).Pad(1).U16(0x0102).U32(0x03040506).U8(0xCD).Pad(7)
            .Raw(7, 8, 9, 10, 11, 12, 13, 14).F32(1.0f).Pad(4).F64(2.0).ToArray();

        Assert.Equal(expected, w.ToArray());
        Assert.Equal(40, w.Length);
    }

    [Fact]
    public void Padding_is_zero_and_the_buffer_grows()
    {
        var w = new HlaWriter();
        for (var i = 0; i < 300; i++)
        {
            w.Octet(0xFF);
            w.Align(8);
        }

        var bytes = w.ToArray();

        Assert.Equal(2400, bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            Assert.Equal(i % 8 == 0 ? 0xFF : 0, bytes[i]);
        }
    }

    [Fact]
    public void Align_to_a_boundary_already_met_writes_nothing()
    {
        var w = new HlaWriter();
        w.Int32BE(1);
        w.Align(4);
        w.Align(2);
        w.Align(1);

        Assert.Equal(4, w.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Align(0));
    }

    [Fact]
    public void NullTerminatedAscii_is_the_characters_and_one_zero_with_boundary_one()
    {
        var w = new HlaWriter();
        w.Octet(1);
        w.NullTerminatedAscii("ab");
        w.NullTerminatedAscii(string.Empty);

        Assert.Equal(new byte[] { 1, (byte)'a', (byte)'b', 0, 0 }, w.ToArray());
        Assert.Throws<ArgumentException>(() => w.NullTerminatedAscii("x\0"));
        Assert.Throws<ArgumentException>(() => w.NullTerminatedAscii("ü"));
    }

    [Fact]
    public void VariableArray_of_float64_has_four_pad_octets_after_the_count()
    {
        // HLAvariableArray boundary = max(4, element boundary) = 8: count@0..3, 4 pad, elements@8 and @16
        var w = new HlaWriter();
        w.VariableArrayHeader(2, 8);
        w.Float64BE(1.0);
        w.Float64BE(2.0);

        Assert.Equal(new Ex().U32(2).Pad(4).F64(1.0).F64(2.0).ToArray(), w.ToArray());
    }

    [Fact]
    public void VariableArray_of_octets_has_boundary_four_and_aligns_after_other_data()
    {
        var w = new HlaWriter();
        w.Octet(9);
        w.VariableArrayHeader(3, 1); // aligns to 4 first
        w.Octet(1);
        w.Octet(2);
        w.Octet(3);

        Assert.Equal(new Ex().U8(9).Pad(3).U32(3).Raw(1, 2, 3).ToArray(), w.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HlaWriter().VariableArrayHeader(-1, 1));
    }

    [Theory]
    [InlineData(0, 1)]  // alternative with boundary 1 chosen
    [InlineData(1, 8)]  // alternative with boundary 8 chosen
    [InlineData(2, 2)]  // alternative with boundary 2 chosen
    public void Variant_record_pads_to_the_largest_boundary_over_all_alternatives_not_the_chosen_one(int chosen, int chosenBoundary)
    {
        // alternatives: 0 -> one octet (boundary 1), 1 -> float64 (boundary 8), 2 -> int16 (boundary 2); discriminant: one octet
        ReadOnlySpan<int> alternatives = [1, 8, 2];
        var w = new HlaWriter();
        w.Octet(7); // some preceding value, so the record start is not offset 0
        w.VariantRecordStart(1, alternatives);
        w.Octet((byte)chosen);
        w.VariantAlternativeStart(alternatives);
        var alternativeOffset = w.Length;
        switch (chosen)
        {
            case 0: w.Octet(0xEE); break;
            case 1: w.Float64BE(1.0); break;
            default: w.Int16BE(0x0102); break;
        }

        Assert.Equal(16, alternativeOffset); // record at 8 (largest boundary), discriminant@8, alternative@16
        Assert.True(chosenBoundary <= 8);
        var bytes = w.ToArray();
        Assert.Equal(new Ex().U8(7).Pad(7).U8(chosen).Pad(7).ToArray(), bytes[..16]);

        var r = new HlaReader(bytes);
        Assert.True(r.TryOctet(out _));
        Assert.True(r.TryVariantRecordStart(1, alternatives));
        Assert.True(r.TryOctet(out var disc));
        Assert.Equal(chosen, disc);
        Assert.True(r.TryVariantAlternativeStart(alternatives));
        Assert.Equal(16, r.Position);
    }

    [Fact]
    public void Reader_mirrors_the_writer_for_random_sequences_of_elements()
    {
        for (var seed = 0; seed < 500; seed++)
        {
            var rnd = new Random(seed);
            var kinds = Enumerable.Range(0, rnd.Next(1, 30)).Select(_ => rnd.Next(9)).ToArray();
            var longs = kinds.Select(_ => rnd.NextInt64(long.MinValue, long.MaxValue)).ToArray();
            var w = new HlaWriter();
            for (var i = 0; i < kinds.Length; i++)
            {
                switch (kinds[i])
                {
                    case 0: w.Octet((byte)longs[i]); break;
                    case 1: w.Int16BE((short)longs[i]); break;
                    case 2: w.UInt16BE((ushort)longs[i]); break;
                    case 3: w.Int32BE((int)longs[i]); break;
                    case 4: w.UInt32BE((uint)longs[i]); break;
                    case 5: w.Int64BE(longs[i]); break;
                    case 6: w.UInt64BE((ulong)longs[i]); break;
                    case 7: w.Float32BE(BitConverter.Int32BitsToSingle((int)longs[i])); break;
                    default: w.Float64BE(BitConverter.Int64BitsToDouble(longs[i])); break;
                }
            }
            var bytes = w.ToArray();

            var r = new HlaReader(bytes);
            for (var i = 0; i < kinds.Length; i++)
            {
                switch (kinds[i])
                {
                    case 0: Assert.True(r.TryOctet(out var a)); Assert.Equal((byte)longs[i], a); break;
                    case 1: Assert.True(r.TryInt16BE(out var b)); Assert.Equal((short)longs[i], b); break;
                    case 2: Assert.True(r.TryUInt16BE(out var c)); Assert.Equal((ushort)longs[i], c); break;
                    case 3: Assert.True(r.TryInt32BE(out var d)); Assert.Equal((int)longs[i], d); break;
                    case 4: Assert.True(r.TryUInt32BE(out var e)); Assert.Equal((uint)longs[i], e); break;
                    case 5: Assert.True(r.TryInt64BE(out var f)); Assert.Equal(longs[i], f); break;
                    case 6: Assert.True(r.TryUInt64BE(out var g)); Assert.Equal((ulong)longs[i], g); break;
                    case 7:
                        Assert.True(r.TryFloat32BE(out var h));
                        Assert.Equal((int)longs[i], BitConverter.SingleToInt32Bits(h));
                        break;
                    default:
                        Assert.True(r.TryFloat64BE(out var k));
                        Assert.Equal(longs[i], BitConverter.DoubleToInt64Bits(k));
                        break;
                }
            }
            Assert.True(r.IsAtEnd);
            Assert.False(r.Failed);
        }
    }

    [Fact]
    public void A_failed_read_leaves_an_error_state_and_never_throws()
    {
        var r = new HlaReader(new byte[] { 1, 2, 3 });
        Assert.True(r.TryOctet(out _));

        Assert.False(r.TryInt32BE(out var value)); // alignment pad 3 is not available
        Assert.Equal(0, value);
        Assert.True(r.Failed);
        var position = r.Position;
        Assert.False(r.TryOctet(out _)); // data is there, but the reader is in the error state
        Assert.False(r.TryOctets(1, out _));
        Assert.False(r.TryAlign(1));
        Assert.Equal(position, r.Position);

        var empty = new HlaReader(ReadOnlySpan<byte>.Empty);
        Assert.False(empty.TryFloat64BE(out _));
        Assert.True(empty.Failed);
        Assert.False(new HlaReader().TryOctet(out _));
    }

    [Fact]
    public void VariableArrayHeader_rejects_counts_beyond_the_data_and_negative_counts()
    {
        var w = new HlaWriter();
        w.VariableArrayHeader(3, 4);
        w.UInt32BE(1);
        w.UInt32BE(2);
        w.UInt32BE(3);
        var good = w.ToArray();

        var ok = new HlaReader(good);
        Assert.True(ok.TryVariableArrayHeader(4, 4, out var count));
        Assert.Equal(3, count);

        var tooShort = new HlaReader(good[..^1]); // the third element is cut
        Assert.False(tooShort.TryVariableArrayHeader(4, 4, out _));

        var huge = new Ex().U32(int.MaxValue).ToArray();
        Assert.False(new HlaReader(huge).TryVariableArrayHeader(1, 1, out _));
        var negative = new Ex().U32(0xFFFFFFFF).ToArray();
        Assert.False(new HlaReader(negative).TryVariableArrayHeader(1, 1, out _));
        Assert.True(new HlaReader(new Ex().U32(0).ToArray()).TryVariableArrayHeader(1, 1, out var zero));
        Assert.Equal(0, zero);
    }

    [Fact]
    public void NullTerminatedAscii_read_needs_a_terminator_and_ascii()
    {
        var r = new HlaReader(new byte[] { (byte)'o', (byte)'k', 0, 9 });
        Assert.True(r.TryNullTerminatedAscii(out var text));
        Assert.Equal("ok", text);
        Assert.Equal(3, r.Position);

        Assert.False(new HlaReader(new byte[] { (byte)'o', (byte)'k' }).TryNullTerminatedAscii(out _));
        Assert.False(new HlaReader(new byte[] { 0x80, 0 }).TryNullTerminatedAscii(out _));
    }
}
