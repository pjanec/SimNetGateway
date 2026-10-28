using System.Buffers.Binary;

namespace SimNetGateway.Protocol.Rpr.Hla.Encoding;

/// <summary>
/// Writer of the padded IEEE 1516.2-2010 encodings (DD-008 section 3.3). One writer builds one attribute or parameter value:
/// offset 0 is the start of the value, every element is aligned to its own octet boundary relative to that start by zero
/// padding inserted BEFORE the element (none after the last one). The multi-octet basic types are big-endian and align to
/// their size. Composite types are built by the caller from <see cref="Align"/> and the basic writers (see <c>RprFom2</c>).
/// </summary>
public sealed class HlaWriter
{
    private byte[] _buffer = new byte[64];
    private int _length;

    /// <summary>Number of octets written so far (= the offset of the next element before alignment).</summary>
    public int Length => _length;

    /// <summary>Pads with zero octets until <see cref="Length"/> is a multiple of <paramref name="boundary"/>.</summary>
    public void Align(int boundary)
    {
        if (boundary < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(boundary));
        }
        var pad = (boundary - (_length % boundary)) % boundary;
        if (pad > 0)
        {
            Reserve(pad);
            _buffer.AsSpan(_length, pad).Clear();
            _length += pad;
        }
    }

    /// <summary>
    /// Start of an HLAvariantRecord: aligns to the record boundary, the largest boundary over the discriminant and ALL
    /// alternatives. The discriminant is written next by the caller.
    /// </summary>
    public void VariantRecordStart(int discriminantBoundary, ReadOnlySpan<int> alternativeBoundaries) =>
        Align(Math.Max(discriminantBoundary, LargestOf(alternativeBoundaries)));

    /// <summary>
    /// Between the discriminant and the chosen alternative of an HLAvariantRecord: pads up to the largest boundary over ALL
    /// alternatives (not the chosen one's), so the alternative's offset does not depend on which one is chosen.
    /// </summary>
    public void VariantAlternativeStart(ReadOnlySpan<int> alternativeBoundaries) => Align(LargestOf(alternativeBoundaries));

    internal static int LargestOf(ReadOnlySpan<int> boundaries)
    {
        var largest = 1;
        foreach (var b in boundaries)
        {
            largest = Math.Max(largest, b);
        }
        return largest;
    }

    public void Octet(byte value)
    {
        Reserve(1);
        _buffer[_length++] = value;
    }

    public void Int16BE(short value)
    {
        Align(2);
        Reserve(2);
        BinaryPrimitives.WriteInt16BigEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    public void UInt16BE(ushort value)
    {
        Align(2);
        Reserve(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    public void Int32BE(int value)
    {
        Align(4);
        Reserve(4);
        BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void UInt32BE(uint value)
    {
        Align(4);
        Reserve(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void Int64BE(long value)
    {
        Align(8);
        Reserve(8);
        BinaryPrimitives.WriteInt64BigEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    public void UInt64BE(ulong value)
    {
        Align(8);
        Reserve(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    /// <summary>HLAfloat32BE: the IEEE 754 bit pattern, big-endian, bit-exact (NaN payloads survive).</summary>
    public void Float32BE(float value) => UInt32BE(unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    /// <summary>HLAfloat64BE: the IEEE 754 bit pattern, big-endian, bit-exact (NaN payloads survive).</summary>
    public void Float64BE(double value) => UInt64BE(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

    /// <summary>Raw octets with boundary 1 (a fixed array of HLAoctet).</summary>
    public void Octets(ReadOnlySpan<byte> values)
    {
        Reserve(values.Length);
        values.CopyTo(_buffer.AsSpan(_length));
        _length += values.Length;
    }

    /// <summary>
    /// RPRnullTerminatedArray of HLAASCIIchar (the RPR <c>RTIobjectId</c>): the characters followed by one 0x00, no length
    /// prefix, boundary 1. Only printable-or-control ASCII except NUL can be encoded.
    /// </summary>
    public void NullTerminatedAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var c in text)
        {
            if (c == '\0' || c > 0x7F)
            {
                throw new ArgumentException("RTIobjectId text must be ASCII without NUL", nameof(text));
            }
        }
        Reserve(text.Length + 1);
        foreach (var c in text)
        {
            _buffer[_length++] = (byte)c;
        }
        _buffer[_length++] = 0;
    }

    /// <summary>
    /// Starts an HLAvariableArray: aligns to the array boundary max(4, element boundary) and writes the element count as
    /// HLAinteger32BE. The elements follow, each aligned to its own boundary.
    /// </summary>
    public void VariableArrayHeader(int count, int elementBoundary)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        Align(Math.Max(4, elementBoundary));
        Int32BE(count);
    }

    /// <summary>The octets written so far.</summary>
    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    private void Reserve(int extra)
    {
        if (_length + extra > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + extra));
        }
    }
}
