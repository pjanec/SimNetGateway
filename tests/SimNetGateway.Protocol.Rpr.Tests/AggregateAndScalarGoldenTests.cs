using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Protocol.Rpr.Tests;

/// <summary>
/// Goldens of the codecs added in stage HLA-2 (hand-built bytes, independent of HlaWriter): AggregateMarkingStruct
/// (RPR-Aggregate_v2.0.xml: MarkingEncodingType + MarkingArray31), RTIobjectIdArray (HLAvariableArray of RPRnullTerminatedArray
/// RTIobjectId, RPR-Foundation_v2.0.xml) and the scalar parameter types of WeaponFire / MunitionDetonation.
/// </summary>
public class AggregateAndScalarGoldenTests
{
    [Fact]
    public void AggregateMarkingStruct_is_32_octets_one_encoding_octet_then_31_data_octets()
    {
        var expected = new Ex().U8(1).Raw((byte)'A', (byte)'l', (byte)'p', (byte)'h', (byte)'a').Pad(26).ToArray();

        var actual = RprFom2.EncodeAggregateMarking(AggregateMarkingStruct.FromAscii("Alpha"));

        Assert.Equal(expected, actual);
        Assert.Equal(32, actual.Length);
        Assert.True(RprFom2.TryDecodeAggregateMarking(expected, out var back));
        Assert.Equal("Alpha", back.AsciiText);
        Assert.Equal(new string('x', 31), AggregateMarkingStruct.FromAscii(new string('x', 40)).AsciiText);
    }

    [Fact]
    public void AggregateMarkingStruct_must_be_31_octets_and_a_known_encoding()
    {
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeAggregateMarking(new AggregateMarkingStruct(MarkingEncodingEnum8.ASCII, new byte[11])));
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeAggregateMarking(new AggregateMarkingStruct((MarkingEncodingEnum8)9, new byte[31])));
        Assert.False(RprFom2.TryDecodeAggregateMarking(new Ex().U8(1).Pad(30).ToArray(), out _)); // one octet short
        Assert.False(RprFom2.TryDecodeAggregateMarking(new Ex().U8(1).Pad(32).ToArray(), out _)); // one octet too many
        Assert.False(RprFom2.TryDecodeAggregateMarking(new Ex().U8(9).Pad(31).ToArray(), out _));
    }

    [Fact]
    public void RtiObjectIdArray_is_an_int32_count_followed_by_null_terminated_names_without_padding()
    {
        var expected = new Ex().U32(3).Asciiz("HLAobject_7").Asciiz("").Asciiz("T-72_3").ToArray();

        var actual = RprFom2.EncodeRtiObjectIdArray(["HLAobject_7", "", "T-72_3"]);

        Assert.Equal(expected, actual);
        Assert.True(RprFom2.TryDecodeRtiObjectIdArray(expected, out var names));
        Assert.Equal(new[] { "HLAobject_7", "", "T-72_3" }, names);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, RprFom2.EncodeRtiObjectIdArray([]));
        Assert.True(RprFom2.TryDecodeRtiObjectIdArray([0, 0, 0, 0], out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void RtiObjectIdArray_refuses_a_count_that_does_not_fit_a_missing_terminator_and_trailing_octets()
    {
        Assert.False(RprFom2.TryDecodeRtiObjectIdArray(new Ex().U32(2).Asciiz("a").ToArray(), out _)); // second name missing
        Assert.False(RprFom2.TryDecodeRtiObjectIdArray(new Ex().U32(1).Raw((byte)'a').ToArray(), out _)); // no terminator
        Assert.False(RprFom2.TryDecodeRtiObjectIdArray(new Ex().U32(0x7FFFFFFF).ToArray(), out _)); // hostile count
        Assert.False(RprFom2.TryDecodeRtiObjectIdArray(new Ex().U32(1).Asciiz("a").Raw(0).ToArray(), out _)); // trailing octet
        Assert.False(RprFom2.TryDecodeRtiObjectIdArray([0, 0, 0], out _)); // truncated count
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeRtiObjectIdArray(["caf\u00e9"]));
    }

    [Fact]
    public void The_scalar_parameter_types_are_big_endian()
    {
        Assert.Equal(new byte[] { 0x12, 0x34 }, RprFom2.EncodeUnsignedInteger16(0x1234));
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, RprFom2.EncodeUnsignedInteger32(0x12345678));
        Assert.Equal(new byte[] { 0x44, 0x7A, 0, 0 }, RprFom2.EncodeLengthMeterFloat32(1000f));
        Assert.True(RprFom2.TryDecodeUnsignedInteger16([0xFF, 0xFE], out var u16));
        Assert.Equal(0xFFFE, u16);
        Assert.True(RprFom2.TryDecodeLengthMeterFloat32([0x44, 0x7A, 0, 0], out var f));
        Assert.Equal(1000f, f);
        Assert.False(RprFom2.TryDecodeUnsignedInteger16([1], out _));
        Assert.False(RprFom2.TryDecodeUnsignedInteger32([1, 2, 3, 4, 5], out _));
        Assert.False(RprFom2.TryDecodeLengthMeterFloat32([1, 2, 3], out _));
    }
}
