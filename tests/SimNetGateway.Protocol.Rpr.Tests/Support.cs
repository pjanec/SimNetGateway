using System.Buffers.Binary;
using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Protocol.Rpr.Tests;

/// <summary>
/// Expected-byte builder for the golden vectors. It is deliberately independent of HlaWriter: it knows nothing about
/// alignment, every padding octet is spelled out with <see cref="Pad"/> in the test, derived by hand from the rules of
/// DD-008 section 3.3.
/// </summary>
internal sealed class Ex
{
    private readonly List<byte> _bytes = new();

    public int Length => _bytes.Count;

    public Ex U8(int value)
    {
        _bytes.Add(checked((byte)value));
        return this;
    }

    public Ex U16(int value)
    {
        Span<byte> s = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(s, checked((ushort)value));
        _bytes.AddRange(s.ToArray());
        return this;
    }

    public Ex U32(uint value)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(s, value);
        _bytes.AddRange(s.ToArray());
        return this;
    }

    public Ex F32(float value) => U32(unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    public Ex F64(double value)
    {
        Span<byte> s = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(s, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
        _bytes.AddRange(s.ToArray());
        return this;
    }

    public Ex Pad(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _bytes.Add(0);
        }
        return this;
    }

    public Ex Raw(params byte[] values)
    {
        _bytes.AddRange(values);
        return this;
    }

    public Ex Asciiz(string text)
    {
        foreach (var c in text)
        {
            _bytes.Add((byte)c);
        }
        _bytes.Add(0);
        return this;
    }

    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>Seeded random values of the RPR struct types; floats and doubles take arbitrary bit patterns (NaN included).</summary>
internal static class Gen
{
    public static float Float(Random r) => BitConverter.Int32BitsToSingle(r.Next(int.MinValue, int.MaxValue) ^ (r.Next() & 1));

    public static double Double(Random r) => BitConverter.Int64BitsToDouble(r.NextInt64(long.MinValue, long.MaxValue));

    public static byte U8(Random r) => (byte)r.Next(256);

    public static ushort U16(Random r) => (ushort)r.Next(65536);

    public static uint U32(Random r) => (uint)r.NextInt64(0, 1L << 32);

    public static T Pick<T>(Random r, T[] values) => values[r.Next(values.Length)];

    public static T PickEnum<T>(Random r) where T : struct, Enum => Pick(r, Enum.GetValues<T>());

    public static FederateIdentifierStruct Federate(Random r) => new(U16(r), U16(r));

    public static EntityIdentifierStruct EntityId(Random r) => new(Federate(r), U16(r));

    public static EntityTypeStruct EntityType(Random r) => new(U8(r), U8(r), U16(r), U8(r), U8(r), U8(r), U8(r));

    public static WorldLocationStruct World(Random r) => new(Double(r), Double(r), Double(r));

    public static OrientationStruct Orientation(Random r) => new(Float(r), Float(r), Float(r));

    public static VelocityVectorStruct Velocity(Random r) => new(Float(r), Float(r), Float(r));

    public static AccelerationVectorStruct Acceleration(Random r) => new(Float(r), Float(r), Float(r));

    public static AngularVelocityVectorStruct AngularVelocity(Random r) => new(Float(r), Float(r), Float(r));

    public static RelativePositionStruct Relative(Random r) => new(Float(r), Float(r), Float(r));

    public static MarkingStruct Marking(Random r)
    {
        var data = new byte[MarkingStruct.DataLength];
        r.NextBytes(data);
        return new MarkingStruct(PickEnum<MarkingEncodingEnum8>(r), data);
    }

    public static AggregateMarkingStruct AggregateMarking(Random r)
    {
        var data = new byte[AggregateMarkingStruct.DataLength];
        r.NextBytes(data);
        return new AggregateMarkingStruct(PickEnum<MarkingEncodingEnum8>(r), data);
    }

    public static string[] ObjectNames(Random r) => Enumerable.Range(0, r.Next(0, 6)).Select(_ => ObjectName(r)).ToArray();

    public static string ObjectName(Random r)
    {
        var length = r.Next(0, 40);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)r.Next(1, 128); // ASCII without NUL
        }
        return new string(chars);
    }

    public static EventIdentifierStruct Event(Random r) => new(U16(r), ObjectName(r));

    /// <summary>A spatial value of a random algorithm; the fields the algorithm does not carry stay zero.</summary>
    public static SpatialVariantStruct Spatial(Random r, DeadReckoningAlgorithmEnum8? algorithm = null)
    {
        var a = algorithm ?? Pick(r, Enum.GetValues<DeadReckoningAlgorithmEnum8>().Where(x => x != DeadReckoningAlgorithmEnum8.Other).ToArray());
        var probe = new SpatialVariantStruct(a, default, false, default, default, default, default);
        return new SpatialVariantStruct(
            a, World(r), r.Next(2) == 1, Orientation(r),
            probe.HasVelocity ? Velocity(r) : default,
            probe.HasAcceleration ? Acceleration(r) : default,
            probe.HasAngularVelocity ? AngularVelocity(r) : default);
    }

    public static ArticulatedParameterStruct Articulated(Random r)
    {
        var type = PickEnum<ParameterTypeEnum32>(r);
        return new ArticulatedParameterStruct(
            U8(r), U16(r),
            new ParameterValueVariantStruct(
                type,
                type == ParameterTypeEnum32.ArticulatedPart ? new ArticulatedPartsStruct(U32(r), U32(r), Float(r)) : default,
                type == ParameterTypeEnum32.AttachedPart ? new AttachedPartsStruct(U32(r), EntityType(r)) : default));
    }
}
