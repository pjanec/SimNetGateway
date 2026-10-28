using System.Text;
using DISnet;

namespace SimNetGateway.Protocol.Dis7.Tests;

public class DisTimestampTests
{
    [Fact]
    public void Random_seconds_round_trip_within_one_unit_and_keep_the_absolute_bit()
    {
        var random = new Random(1);
        for (int i = 0; i < 1000; i++)
        {
            double seconds = random.NextDouble() * 3600.0;
            bool absolute = random.Next(2) == 1;
            uint stamp = DisTimestamp.Encode(seconds, absolute);

            Assert.Equal(absolute, DisTimestamp.IsAbsolute(stamp));
            double back = DisTimestamp.ToSecondsPastHour(stamp);
            Assert.InRange(back, 0.0, 3600.0);
            // rounding to the nearest unit: within half a unit, in particular within one (a wrap near 3600 counts mod 3600)
            double diff = Math.Abs(back - seconds);
            diff = Math.Min(diff, 3600.0 - diff);
            Assert.True(diff <= DisTimestamp.UnitSeconds, $"{seconds}: {back}");
        }
    }

    [Fact]
    public void Known_values()
    {
        Assert.Equal(0u, DisTimestamp.EncodeRelative(0));
        Assert.Equal(1u, DisTimestamp.EncodeAbsolute(0));
        // half an hour = 2^30 units; shifted left by one = 2^31
        Assert.Equal(0x80000000u, DisTimestamp.EncodeRelative(1800));
        Assert.Equal(0x80000001u, DisTimestamp.EncodeAbsolute(1800));
        // the largest unit count is 2^31 - 1 -> 0xFFFFFFFE (relative)
        Assert.Equal(0xFFFFFFFEu, DisTimestamp.EncodeRelative(3600.0 - DisTimestamp.UnitSeconds));
        Assert.Equal(1800.0, DisTimestamp.ToSecondsPastHour(0x80000000u), 9);
        Assert.False(DisTimestamp.IsAbsolute(0x80000000u));
        Assert.True(DisTimestamp.IsAbsolute(0x80000001u));
        Assert.Equal(1u << 30, DisTimestamp.Units(0x80000001u));
    }

    [Fact]
    public void An_hour_wraps_to_zero_and_negative_values_wrap_forward()
    {
        Assert.Equal(DisTimestamp.EncodeAbsolute(0), DisTimestamp.EncodeAbsolute(3600));
        Assert.Equal(DisTimestamp.EncodeAbsolute(1.5), DisTimestamp.EncodeAbsolute(3601.5));
        Assert.Equal(DisTimestamp.EncodeRelative(3599), DisTimestamp.EncodeRelative(-1));
        // rounding up to a full hour must not overflow into the absolute bit or the count
        Assert.Equal(0u, DisTimestamp.EncodeRelative(3600.0 - DisTimestamp.UnitSeconds / 4));
    }

    [Fact]
    public void Non_finite_input_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DisTimestamp.Encode(double.NaN, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisTimestamp.Encode(double.PositiveInfinity, true));
    }
}

public class EntityAppearanceTests
{
    private static uint RandomWord(Random random) => (uint)random.NextInt64(0, 1L << 32);

    [Fact]
    public void Setting_one_field_never_changes_any_other_bit()
    {
        var random = new Random(2);
        for (int i = 0; i < 2000; i++)
        {
            uint word = RandomWord(random);
            var damage = (DamageState)random.Next(4);
            var smoke = (SmokeState)random.Next(4);
            bool flag = random.Next(2) == 1;

            uint changed = EntityAppearance.SetDamage(word, damage);
            Assert.Equal(damage, EntityAppearance.GetDamage(changed));
            Assert.Equal(word & ~EntityAppearance.DamageMask, changed & ~EntityAppearance.DamageMask);

            changed = EntityAppearance.SetSmoke(word, smoke);
            Assert.Equal(smoke, EntityAppearance.GetSmoke(changed));
            Assert.Equal(word & ~EntityAppearance.SmokeMask, changed & ~EntityAppearance.SmokeMask);

            changed = EntityAppearance.SetFrozen(word, flag);
            Assert.Equal(flag, EntityAppearance.GetFrozen(changed));
            Assert.Equal(word & ~EntityAppearance.FrozenMask, changed & ~EntityAppearance.FrozenMask);

            changed = EntityAppearance.SetDeactivated(word, flag);
            Assert.Equal(flag, EntityAppearance.GetDeactivated(changed));
            Assert.Equal(word & ~EntityAppearance.DeactivatedMask, changed & ~EntityAppearance.DeactivatedMask);
        }
    }

    [Fact]
    public void The_fields_do_not_overlap_and_sit_on_the_bits_of_the_standard()
    {
        Assert.Equal(0x00000018u, EntityAppearance.DamageMask); // bits 3-4
        Assert.Equal(0x00000060u, EntityAppearance.SmokeMask); // bits 5-6
        Assert.Equal(0x00200000u, EntityAppearance.FrozenMask); // bit 21
        Assert.Equal(0x00800000u, EntityAppearance.DeactivatedMask); // bit 23
        Assert.Equal(0u, EntityAppearance.DamageMask & EntityAppearance.SmokeMask);

        Assert.Equal(0x08u, EntityAppearance.SetDamage(0, DamageState.SlightDamage));
        Assert.Equal(0x10u, EntityAppearance.SetDamage(0, DamageState.ModerateDamage));
        Assert.Equal(0x18u, EntityAppearance.SetDamage(0, DamageState.Destroyed));
        Assert.Equal(0x00800000u, EntityAppearance.SetDeactivated(0, true));
        Assert.Equal(0x00200000u, EntityAppearance.SetFrozen(0, true));
    }

    [Fact]
    public void Damage_values_zero_to_three_round_trip_from_any_base()
    {
        var random = new Random(3);
        for (int i = 0; i < 200; i++)
        {
            uint word = RandomWord(random);
            for (uint d = 0; d <= 3; d++)
            {
                Assert.Equal((DamageState)d, EntityAppearance.GetDamage(EntityAppearance.SetDamage(word, (DamageState)d)));
            }
        }
    }

    [Fact]
    public void Flags_clear_again()
    {
        uint all = uint.MaxValue;
        Assert.False(EntityAppearance.GetFrozen(EntityAppearance.SetFrozen(all, false)));
        Assert.False(EntityAppearance.GetDeactivated(EntityAppearance.SetDeactivated(all, false)));
        Assert.Equal(DamageState.NoDamage, EntityAppearance.GetDamage(EntityAppearance.SetDamage(all, DamageState.NoDamage)));
    }
}

public class EntityMarkingTextTests
{
    private static string RandomAscii(Random random, int length)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < length; i++)
        {
            builder.Append((char)random.Next(0x20, 0x7F));
        }

        return builder.ToString();
    }

    [Fact]
    public void Up_to_eleven_ascii_characters_round_trip_and_the_rest_is_zero_padding()
    {
        var random = new Random(4);
        for (int i = 0; i < 500; i++)
        {
            string text = RandomAscii(random, random.Next(0, 12));
            var bytes = EntityMarkingText.Encode(text);

            Assert.Equal(11, bytes.Length);
            Assert.Equal(Encoding.ASCII.GetBytes(text), bytes.Take(text.Length).ToArray());
            Assert.All(bytes.Skip(text.Length), b => Assert.Equal(0, b));
            Assert.Equal(text, EntityMarkingText.Decode(bytes));
        }
    }

    [Fact]
    public void Longer_text_is_truncated_at_eleven_characters()
    {
        var bytes = EntityMarkingText.Encode("ABCDEFGHIJKLMNOP");
        Assert.Equal("ABCDEFGHIJK", Encoding.ASCII.GetString(bytes));
        Assert.Equal("ABCDEFGHIJK", EntityMarkingText.Decode(bytes));
    }

    [Fact]
    public void Non_ascii_and_control_characters_become_question_marks()
    {
        Assert.Equal("a?b?c?", EntityMarkingText.Decode(EntityMarkingText.Encode("aéb€c\n")));
        // a surrogate pair is one character
        Assert.Equal("x?y", EntityMarkingText.Decode(EntityMarkingText.Encode("x\U0001F600y")));
        Assert.Equal(string.Empty, EntityMarkingText.Decode(EntityMarkingText.Encode(null)));
        Assert.Equal(11, EntityMarkingText.Encode(string.Empty).Length);
    }

    [Fact]
    public void Decoding_stops_at_the_first_zero_and_masks_foreign_bytes()
    {
        var bytes = new byte[] { (byte)'A', 0, (byte)'B', 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal("A", EntityMarkingText.Decode(bytes));
        Assert.Equal("?A", EntityMarkingText.Decode(new byte[] { 0xE9, (byte)'A' }));
    }

    [Fact]
    public void The_marking_record_uses_character_set_one()
    {
        var marking = new EntityMarking();
        EntityMarkingText.Set(marking, "Wolf 2-1");
        Assert.Equal(1, marking.CharacterSet);
        Assert.Equal("Wolf 2-1", EntityMarkingText.Get(marking));
        marking.CharacterSet = 0;
        Assert.Equal(string.Empty, EntityMarkingText.Get(marking));
    }
}

public class ArticulatedPartTests
{
    [Fact]
    public void Packing_round_trips_over_random_parts_and_uses_the_standard_layout()
    {
        var random = new Random(6);
        for (int i = 0; i < 500; i++)
        {
            var part = new ArticulatedPart(
                (byte)random.Next(256), (ushort)random.Next(65536), (uint)random.NextInt64(0, 1L << 32),
                (float)((random.NextDouble() - 0.5) * 20));

            var parameter = part.ToVariableParameter();
            Assert.Equal(0, parameter.RecordType);
            Assert.True(ArticulatedPart.TryFrom(parameter, out var back));
            Assert.Equal(part, back);

            // the 16 wire bytes: type 0, change, attached(2), parameter type(4), float(4), padding(4)
            var expected = new ByteBuilder()
                .U8(0).U8(part.ChangeIndicator).U16(part.PartAttachedTo).U32(part.ParameterType).F32(part.Value).Zeros(4)
                .ToArray();
            var pdu = new EntityStatePdu();
            pdu.VariableParameters.Add(parameter);
            pdu.NumberOfVariableParameters = 1;
            var bytes = PduCodec.Encode(pdu);
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(bytes.AsSpan(144, 16)));

            var decoded = Assert.Single(PduCodec.Decode(bytes).Items);
            Assert.True(ArticulatedPart.TryFrom(((EntityStatePdu)decoded.Pdu!).VariableParameters[0], out var viaWire));
            Assert.Equal(part, viaWire);
        }
    }

    [Fact]
    public void Other_record_types_are_not_articulated_parts()
    {
        var attached = new VariableParameter { RecordType = 1 };
        Assert.False(ArticulatedPart.TryFrom(attached, out _));
    }

    [Fact]
    public void Parameter_type_is_class_plus_metric()
    {
        uint azimuth = ArticulatedPart.MakeParameterType(ArticulatedPart.ClassPrimaryTurret1, ArticulatedPart.MetricAzimuth);
        Assert.Equal(4096u + 11u, azimuth);
        Assert.Equal(ArticulatedPart.ClassPrimaryTurret1, ArticulatedPart.TypeClassOf(azimuth));
        Assert.Equal(ArticulatedPart.MetricAzimuth, ArticulatedPart.MetricOf(azimuth));
    }
}
