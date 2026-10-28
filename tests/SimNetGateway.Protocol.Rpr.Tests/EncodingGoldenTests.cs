using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Protocol.Rpr.Tests;

/// <summary>
/// Success condition 1 (DD-008 section 8): hand-built expected bytes, independent of HlaWriter, for every struct and every
/// Spatial alternative, with the sizes the design pins. Every padding octet is spelled out and is zero.
/// </summary>
public class EncodingGoldenTests
{
    private static readonly EntityTypeStruct Tank = new(1, 1, 225, 1, 1, 3, 0);

    private static Ex EntityTypeBytes(EntityTypeStruct t) =>
        // boundary 2; kind@0 domain@1 country@2..3 category@4 subcategory@5 specific@6 extra@7 -> 8 octets, no padding
        new Ex().U8(t.EntityKind).U8(t.Domain).U16(t.CountryCode).U8(t.Category).U8(t.Subcategory).U8(t.Specific).U8(t.Extra);

    [Fact]
    public void EntityTypeStruct_is_8_octets()
    {
        var expected = new byte[] { 1, 1, 0x00, 0xE1, 1, 1, 3, 0 };

        Assert.Equal(expected, RprFom2.EncodeEntityType(Tank));
        Assert.Equal(8, EntityTypeBytes(Tank).Length);
    }

    [Fact]
    public void FederateIdentifier_and_EntityIdentifierStruct_are_4_and_6_octets()
    {
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, RprFom2.EncodeFederateIdentifier(new(0x1234, 0x5678)));

        var id = new EntityIdentifierStruct(new FederateIdentifierStruct(1, 2), 3);

        Assert.Equal(new byte[] { 0, 1, 0, 2, 0, 3 }, RprFom2.EncodeEntityIdentifier(id));
    }

    [Fact]
    public void MarkingStruct_is_12_octets()
    {
        var marking = MarkingStruct.FromAscii("T-72_3");
        var expected = new Ex().U8(1).Raw((byte)'T', (byte)'-', (byte)'7', (byte)'2', (byte)'_', (byte)'3').Pad(5).ToArray();

        var actual = RprFom2.EncodeMarking(marking);

        Assert.Equal(expected, actual);
        Assert.Equal(12, actual.Length);
    }

    [Fact]
    public void WorldLocationStruct_is_24_octets_of_big_endian_float64()
    {
        var expected = new Ex().F64(4_000_000.5).F64(-3_000_000.25).F64(5_000_000.125).ToArray();

        var actual = RprFom2.EncodeWorldLocation(new WorldLocationStruct(4_000_000.5, -3_000_000.25, 5_000_000.125));

        Assert.Equal(expected, actual);
        Assert.Equal(24, actual.Length);
        // 4_000_000.5 = 0x414E8480_40000000 as an IEEE 754 double
        Assert.Equal(new byte[] { 0x41, 0x4E, 0x84, 0x80, 0x40, 0, 0, 0 }, actual[..8]);
    }

    [Fact]
    public void Vectors_and_orientation_are_12_octets_of_float32()
    {
        Assert.Equal(new Ex().F32(0.5f).F32(-2f).F32(1e10f).ToArray(), RprFom2.EncodeOrientation(new(0.5f, -2f, 1e10f)));
        Assert.Equal(new Ex().F32(1f).F32(2f).F32(3f).ToArray(), RprFom2.EncodeVelocityVector(new(1f, 2f, 3f)));
        Assert.Equal(new Ex().F32(4f).F32(5f).F32(6f).ToArray(), RprFom2.EncodeAccelerationVector(new(4f, 5f, 6f)));
        Assert.Equal(new Ex().F32(7f).F32(8f).F32(9f).ToArray(), RprFom2.EncodeAngularVelocityVector(new(7f, 8f, 9f)));
        Assert.Equal(new Ex().F32(-1f).F32(0f).F32(1f).ToArray(), RprFom2.EncodeRelativePosition(new(-1f, 0f, 1f)));
        Assert.Equal(new byte[] { 0x3F, 0x80, 0, 0 }, RprFom2.EncodeVelocityVector(new(1f, 0, 0))[..4]);
    }

    /// <summary>
    /// SpatialVariantStruct layout, derived by hand (record boundary 8 because WorldLocationStruct holds float64):
    /// disc@0 (1 octet), 7 pad to the largest boundary over ALL alternatives, WorldLocation@8..31, IsFrozen@32, 3 pad to the
    /// float32 boundary, Orientation@36..47 = 48 (Static); FP adds Velocity 12 = 60; RP adds Velocity and AngularVelocity =
    /// 72; FV adds Velocity and Acceleration = 72; RV adds all three = 84.
    /// </summary>
    [Theory]
    [InlineData(DeadReckoningAlgorithmEnum8.Static, 48)]
    [InlineData(DeadReckoningAlgorithmEnum8.DRM_FPW, 60)]
    [InlineData(DeadReckoningAlgorithmEnum8.DRM_FPB, 60)]
    [InlineData(DeadReckoningAlgorithmEnum8.DRM_RPW, 72)]
    [InlineData(DeadReckoningAlgorithmEnum8.DRM_RPB, 72)]
    [InlineData(DeadReckoningAlgorithmEnum8.DRM_FVW, 72)]
    [InlineData(DeadReckoningAlgorithmEnum8.DRM_FVB, 72)]
    [InlineData(DeadReckoningAlgorithmEnum8.DRM_RVW, 84)]
    [InlineData(DeadReckoningAlgorithmEnum8.DRM_RVB, 84)]
    public void SpatialVariantStruct_alternative_has_the_designed_size_and_layout(DeadReckoningAlgorithmEnum8 algorithm, int size)
    {
        var value = new SpatialVariantStruct(
            algorithm,
            new WorldLocationStruct(1.5, 2.5, 3.5),
            true,
            new OrientationStruct(0.1f, 0.2f, 0.3f),
            new VelocityVectorStruct(10f, 11f, 12f),
            new AccelerationVectorStruct(20f, 21f, 22f),
            new AngularVelocityVectorStruct(30f, 31f, 32f));
        var probe = value;
        var expected = new Ex().U8((byte)algorithm).Pad(7)
            .F64(1.5).F64(2.5).F64(3.5)
            .U8(1).Pad(3)
            .F32(0.1f).F32(0.2f).F32(0.3f);
        var carried = value with
        {
            Velocity = probe.HasVelocity ? value.Velocity : default,
            Acceleration = probe.HasAcceleration ? value.Acceleration : default,
            AngularVelocity = probe.HasAngularVelocity ? value.AngularVelocity : default,
        };
        // order of the optional parts in the FOM records: Velocity, then Acceleration (FV/RV), then AngularVelocity (RP/RV)
        if (probe.HasVelocity)
        {
            expected.F32(10f).F32(11f).F32(12f);
        }
        if (probe.HasAcceleration)
        {
            expected.F32(20f).F32(21f).F32(22f);
        }
        if (probe.HasAngularVelocity)
        {
            expected.F32(30f).F32(31f).F32(32f);
        }

        var actual = RprFom2.EncodeSpatial(value);

        Assert.Equal(size, expected.Length);
        Assert.Equal(size, actual.Length);
        Assert.Equal(expected.ToArray(), actual);
        Assert.True(RprFom2.TryDecodeSpatial(actual, out var decoded));
        Assert.Equal(carried, decoded);
    }

    [Fact]
    public void SpatialVariantStruct_padding_after_the_discriminant_is_the_same_for_every_alternative()
    {
        // the largest boundary over all alternatives (8) pads every variant, so WorldLocation always starts at offset 8
        foreach (var algorithm in Enum.GetValues<DeadReckoningAlgorithmEnum8>().Where(a => a != DeadReckoningAlgorithmEnum8.Other))
        {
            var bytes = RprFom2.EncodeSpatial(Gen.Spatial(new Random(5), algorithm) with { WorldLocation = new(1.0, 1.0, 1.0) });

            Assert.Equal((byte)algorithm, bytes[0]);
            Assert.All(bytes[1..8], b => Assert.Equal(0, b));
            Assert.Equal(new byte[] { 0x3F, 0xF0, 0, 0, 0, 0, 0, 0 }, bytes[8..16]);
            Assert.All(bytes[33..36], b => Assert.Equal(0, b)); // padding between IsFrozen@32 and Orientation@36
        }
    }

    [Fact]
    public void SpatialVariantStruct_Other_has_no_alternative_encode_is_refused_and_decode_fails()
    {
        var other = Gen.Spatial(new Random(1), DeadReckoningAlgorithmEnum8.Static) with { Algorithm = DeadReckoningAlgorithmEnum8.Other };
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeSpatial(other));
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeSpatial(other with { Algorithm = (DeadReckoningAlgorithmEnum8)200 }));

        var bytes = RprFom2.EncodeSpatial(Gen.Spatial(new Random(2), DeadReckoningAlgorithmEnum8.DRM_RVB));
        foreach (var bad in new byte[] { 0, 10, 11, 255 })
        {
            bytes[0] = bad;
            Assert.False(RprFom2.TryDecodeSpatial(bytes, out _));
        }
    }

    [Fact]
    public void RtiObjectId_is_ascii_plus_a_zero_without_length_or_padding()
    {
        Assert.Equal(new Ex().Raw((byte)'H', (byte)'L', (byte)'A', (byte)'o', (byte)'_', (byte)'7').U8(0).ToArray(),
            RprFom2.EncodeRtiObjectId("HLAo_7"));
        Assert.Equal(new byte[] { 0 }, RprFom2.EncodeRtiObjectId(string.Empty));
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeRtiObjectId("a\0b"));
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeRtiObjectId("caf\u00e9"));
    }

    [Fact]
    public void EventIdentifierStruct_is_2_plus_length_plus_1_octets()
    {
        const string name = "T-72_3";
        var expected = new Ex().U16(12).Asciiz(name).ToArray();

        var actual = RprFom2.EncodeEventIdentifier(new EventIdentifierStruct(12, name));

        Assert.Equal(expected, actual);
        Assert.Equal(2 + name.Length + 1, actual.Length);
        Assert.Equal(3, RprFom2.EncodeEventIdentifier(new EventIdentifierStruct(0, string.Empty)).Length);
    }

    /// <summary>
    /// ArticulatedParameterStruct, derived by hand (record boundary 4: PartAttachedTo 2, ParameterType 4, parts 4):
    /// Change@0, 1 pad, PartAttachedTo@2..3, ParameterType@4..7 (both alternatives have boundary 4, so no extra variant
    /// padding), alternative@8..19: ArticulatedPartsStruct = 3 x 4 octets, AttachedPartsStruct = Station 4 + EntityTypeStruct 8.
    /// Both give 20 octets.
    /// </summary>
    [Fact]
    public void ArticulatedParameterStruct_is_20_octets_for_both_alternatives()
    {
        var parts = new ArticulatedParameterStruct(
            1, 0x0203, new ParameterValueVariantStruct(ParameterTypeEnum32.ArticulatedPart, new ArticulatedPartsStruct(4096, 11, 0.5f), default));
        var partsExpected = new Ex().U8(1).Pad(1).U16(0x0203).U32(0).U32(4096).U32(11).F32(0.5f).ToArray();
        var attached = new ArticulatedParameterStruct(
            2, 7, new ParameterValueVariantStruct(ParameterTypeEnum32.AttachedPart, default, new AttachedPartsStruct(9, Tank)));
        var attachedExpected = EntityTypeBytes(Tank);
        var attachedBytes = new Ex().U8(2).Pad(1).U16(7).U32(1).U32(9).Raw(attachedExpected.ToArray()).ToArray();

        Assert.Equal(partsExpected, RprFom2.EncodeArticulatedParameter(parts));
        Assert.Equal(attachedBytes, RprFom2.EncodeArticulatedParameter(attached));
        Assert.Equal(20, partsExpected.Length);
        Assert.Equal(20, attachedBytes.Length);
    }

    [Fact]
    public void ArticulatedParameterStructLengthlessArray_is_the_elements_only()
    {
        var one = new ArticulatedParameterStruct(
            1, 2, new ParameterValueVariantStruct(ParameterTypeEnum32.ArticulatedPart, new ArticulatedPartsStruct(1, 2, 3f), default));
        var two = new ArticulatedParameterStruct(
            3, 4, new ParameterValueVariantStruct(ParameterTypeEnum32.AttachedPart, default, new AttachedPartsStruct(5, Tank)));

        var bytes = RprFom2.EncodeArticulatedParameters([one, two, one]);

        Assert.Equal(60, bytes.Length); // 3 x 20, no count, no padding between elements (boundary 4 divides 20)
        Assert.Equal(RprFom2.EncodeArticulatedParameter(one), bytes[..20]);
        Assert.Equal(RprFom2.EncodeArticulatedParameter(two), bytes[20..40]);
        Assert.Empty(RprFom2.EncodeArticulatedParameters([]));
        Assert.True(RprFom2.TryDecodeArticulatedParameters(bytes, out var decoded));
        Assert.Equal(new[] { one, two, one }, decoded);
        Assert.True(RprFom2.TryDecodeArticulatedParameters([], out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void Booleans_and_enumerations_encode_as_their_representation_type()
    {
        Assert.Equal(new byte[] { 0 }, RprFom2.EncodeBoolean(false));
        Assert.Equal(new byte[] { 1 }, RprFom2.EncodeBoolean(true));
        Assert.Equal(new byte[] { 2 }, RprFom2.EncodeForceIdentifier(ForceIdentifierEnum8.Opposing));
        Assert.Equal(new byte[] { 30 }, RprFom2.EncodeForceIdentifier(ForceIdentifierEnum8.Neutral_10));
        Assert.Equal(new byte[] { 0, 0, 0, 3 }, RprFom2.EncodeDamageStatus(DamageStatusEnum32.Destroyed));
        Assert.Equal(new byte[] { 1 }, RprFom2.EncodeDetonationResultCode(DetonationResultCodeEnum8.EntityImpact));
        Assert.Equal(new byte[] { 33 }, RprFom2.EncodeDetonationResultCode(DetonationResultCodeEnum8.Miss_due_to_fly_out_and_end_game_failure));
        Assert.Equal(new byte[] { 0x03, 0xE8 }, RprFom2.EncodeFuseType(FuseTypeEnum16.Contact)); // 1000
        Assert.Equal(new byte[] { 0x03, 0xE8 }, RprFom2.EncodeWarheadType(WarheadTypeEnum16.HighExplosive)); // 1000
        Assert.Equal(new byte[] { 1 }, RprFom2.EncodeMarkingEncoding(MarkingEncodingEnum8.ASCII));
        Assert.Equal(new byte[] { 2 }, RprFom2.EncodeDeadReckoningAlgorithm(DeadReckoningAlgorithmEnum8.DRM_FPW));
    }

    [Fact]
    public void Enumerators_outside_the_FOM_are_refused_and_do_not_decode()
    {
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeForceIdentifier((ForceIdentifierEnum8)31));
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeDamageStatus((DamageStatusEnum32)4));
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeFuseType((FuseTypeEnum16)11));
        Assert.False(RprFom2.TryDecodeForceIdentifier([31], out _));
        Assert.False(RprFom2.TryDecodeDamageStatus([0, 0, 0, 4], out _));
        Assert.False(RprFom2.TryDecodeFuseType([0, 11], out _));
        Assert.False(RprFom2.TryDecodeBoolean([2], out _));
        Assert.False(RprFom2.TryDecodeMarkingEncoding([4], out _));
    }

    [Fact]
    public void MarkingStruct_must_be_11_octets_and_a_known_encoding()
    {
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeMarking(new MarkingStruct(MarkingEncodingEnum8.ASCII, new byte[10])));
        Assert.Throws<ArgumentException>(() => RprFom2.EncodeMarking(new MarkingStruct((MarkingEncodingEnum8)9, new byte[11])));
        Assert.False(RprFom2.TryDecodeMarking(new Ex().U8(9).Pad(11).ToArray(), out _));
        Assert.Equal("T-72_3", MarkingStruct.FromAscii("T-72_3").AsciiText);
        Assert.Equal("ABCDEFGHIJK", MarkingStruct.FromAscii("ABCDEFGHIJKLMN").AsciiText);
    }
}
