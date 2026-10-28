using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using DISnet;

namespace SimNetGateway.Protocol.Dis7.Tests;

/// <summary>
/// Hand-built big-endian byte sequences: the expected side of the golden tests, written straight from the layouts of
/// IEEE 1278.1-2012 and independent of the vendored marshalling code.
/// </summary>
public sealed class ByteBuilder
{
    private readonly List<byte> _bytes = new();

    public int Count => _bytes.Count;

    public ByteBuilder U8(int value) { _bytes.Add((byte)value); return this; }

    public ByteBuilder U16(int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value);
        _bytes.AddRange(b.ToArray());
        return this;
    }

    public ByteBuilder U32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        _bytes.AddRange(b.ToArray());
        return this;
    }

    public ByteBuilder U64(ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        _bytes.AddRange(b.ToArray());
        return this;
    }

    public ByteBuilder F32(float value) => U32(BitConverter.SingleToUInt32Bits(value));

    public ByteBuilder F64(double value) => U64(BitConverter.DoubleToUInt64Bits(value));

    public ByteBuilder Zeros(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _bytes.Add(0);
        }

        return this;
    }

    public ByteBuilder Raw(params byte[] bytes) { _bytes.AddRange(bytes); return this; }

    /// <summary>PDU header: protocol version 7, exercise, type, family, timestamp, length, status 0, padding 0.</summary>
    public ByteBuilder Header(int exercise, int type, int family, uint timestamp, int length) =>
        U8(7).U8(exercise).U8(type).U8(family).U32(timestamp).U16(length).U8(0).U8(0);

    public ByteBuilder EntityId(int site, int application, int entity) => U16(site).U16(application).U16(entity);

    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>Fills every settable field of a vendored PDU object graph with seeded random values.</summary>
public static class RandomFill
{
    // Header fields the codec sets or the class fixes itself.
    private static readonly HashSet<string> Skipped = new() { "PduType", "ProtocolFamily", "Length" };

    public static void Fill(object target, Random random, int variableParameters = 0)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0 || Skipped.Contains(property.Name))
            {
                continue;
            }

            var type = property.PropertyType;
            if (property.Name.StartsWith("NumberOf", StringComparison.Ordinal))
            {
                continue; // derived from the list lengths below
            }

            if (type == typeof(List<VariableParameter>))
            {
                var list = (List<VariableParameter>)property.GetValue(target)!;
                list.Clear();
                for (int i = 0; i < variableParameters; i++)
                {
                    var parameter = new VariableParameter();
                    Fill(parameter, random);
                    list.Add(parameter);
                }

                continue;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                continue; // other lists stay empty
            }

            if (type == typeof(byte[]))
            {
                var array = (byte[])property.GetValue(target)!;
                random.NextBytes(array);
                continue;
            }

            if (type.IsClass && type.Namespace == "DISnet")
            {
                var value = property.GetValue(target);
                if (value is not null)
                {
                    Fill(value, random);
                }

                continue;
            }

            if (!property.CanWrite)
            {
                continue;
            }

            var generated = Random(type, random);
            if (generated is not null)
            {
                property.SetValue(target, generated);
            }
        }

        var count = target.GetType().GetProperty("NumberOfVariableParameters");
        if (count is { CanWrite: true } && count.PropertyType == typeof(byte))
        {
            count.SetValue(target, (byte)variableParameters);
        }
    }

    private static object? Random(Type type, Random random)
    {
        if (type == typeof(byte)) return (byte)random.Next(256);
        if (type == typeof(sbyte)) return (sbyte)random.Next(-128, 128);
        if (type == typeof(ushort)) return (ushort)random.Next(65536);
        if (type == typeof(short)) return (short)random.Next(-32768, 32768);
        if (type == typeof(uint)) return (uint)random.NextInt64(0, 1L << 32);
        if (type == typeof(int)) return random.Next(int.MinValue, int.MaxValue);
        if (type == typeof(ulong)) return (ulong)random.NextInt64() << 1 ^ (ulong)random.NextInt64();
        if (type == typeof(long)) return random.NextInt64(long.MinValue, long.MaxValue);
        if (type == typeof(float)) return (float)((random.NextDouble() - 0.5) * 2e6);
        if (type == typeof(double)) return (random.NextDouble() - 0.5) * 2e7;
        if (type == typeof(bool)) return random.Next(2) == 1;
        return null;
    }
}

/// <summary>Field-by-field comparison of two vendored object graphs through their public properties.</summary>
public static class DeepCompare
{
    public static List<string> Differences(object expected, object actual)
    {
        var differences = new List<string>();
        Compare(expected, actual, expected.GetType().Name, differences);
        return differences;
    }

    private static void Compare(object? expected, object? actual, string path, List<string> differences)
    {
        if (expected is null || actual is null)
        {
            if (!ReferenceEquals(expected, actual))
            {
                differences.Add($"{path}: {expected ?? "null"} vs {actual ?? "null"}");
            }

            return;
        }

        var type = expected.GetType();
        if (type != actual.GetType())
        {
            differences.Add($"{path}: type {type.Name} vs {actual.GetType().Name}");
            return;
        }

        if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
        {
            if (!expected.Equals(actual))
            {
                differences.Add($"{path}: {expected} vs {actual}");
            }

            return;
        }

        if (expected is byte[] expectedBytes)
        {
            if (!expectedBytes.AsSpan().SequenceEqual((byte[])actual))
            {
                differences.Add($"{path}: byte arrays differ");
            }

            return;
        }

        if (expected is IList expectedList)
        {
            var actualList = (IList)actual;
            if (expectedList.Count != actualList.Count)
            {
                differences.Add($"{path}: count {expectedList.Count} vs {actualList.Count}");
                return;
            }

            for (int i = 0; i < expectedList.Count; i++)
            {
                Compare(expectedList[i], actualList[i], $"{path}[{i}]", differences);
            }

            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            Compare(property.GetValue(expected), property.GetValue(actual), $"{path}.{property.Name}", differences);
        }
    }
}
