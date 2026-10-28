using System.Buffers.Binary;

namespace SimNetGateway.Protocol.Rpr.Hla.Encoding;

/// <summary>
/// Reader of the padded IEEE 1516.2-2010 encodings, the counterpart of <see cref="HlaWriter"/> (DD-008 section 3.3).
/// Every read is bounds-checked and returns false instead of throwing; the first failed read puts the reader in an error
/// state (<see cref="Failed"/>) in which every further read fails too and the position no longer moves, so a caller can
/// chain reads and test once. Padding octets are skipped without being checked (lenient on input, zero on output).
/// </summary>
public ref struct HlaReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;
    private bool _failed;

    public HlaReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _failed = false;
    }

    /// <summary>Offset of the next element before alignment.</summary>
    public readonly int Position => _position;

    public readonly int Remaining => _data.Length - _position;

    public readonly bool IsAtEnd => _position == _data.Length;

    /// <summary>True once any read has failed.</summary>
    public readonly bool Failed => _failed;

    /// <summary>Puts the reader in the error state (for a caller's own consistency check) and returns false.</summary>
    public bool Fail()
    {
        _failed = true;
        return false;
    }

    /// <summary>Skips padding up to the next multiple of <paramref name="boundary"/>; fails when the padding is not there.</summary>
    public bool TryAlign(int boundary)
    {
        if (_failed || boundary < 1)
        {
            return Fail();
        }
        var pad = (boundary - (_position % boundary)) % boundary;
        if (pad > Remaining)
        {
            return Fail();
        }
        _position += pad;
        return true;
    }

    /// <summary>Counterpart of <see cref="HlaWriter.VariantRecordStart"/>.</summary>
    public bool TryVariantRecordStart(int discriminantBoundary, ReadOnlySpan<int> alternativeBoundaries) =>
        TryAlign(Math.Max(discriminantBoundary, HlaWriter.LargestOf(alternativeBoundaries)));

    /// <summary>Counterpart of <see cref="HlaWriter.VariantAlternativeStart"/>.</summary>
    public bool TryVariantAlternativeStart(ReadOnlySpan<int> alternativeBoundaries) =>
        TryAlign(HlaWriter.LargestOf(alternativeBoundaries));

    public bool TryOctet(out byte value)
    {
        value = 0;
        if (_failed || Remaining < 1)
        {
            return Fail();
        }
        value = _data[_position++];
        return true;
    }

    public bool TryInt16BE(out short value)
    {
        value = 0;
        if (!TryAlign(2) || Remaining < 2)
        {
            return Fail();
        }
        value = BinaryPrimitives.ReadInt16BigEndian(_data.Slice(_position));
        _position += 2;
        return true;
    }

    public bool TryUInt16BE(out ushort value)
    {
        value = 0;
        if (!TryAlign(2) || Remaining < 2)
        {
            return Fail();
        }
        value = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(_position));
        _position += 2;
        return true;
    }

    public bool TryInt32BE(out int value)
    {
        value = 0;
        if (!TryAlign(4) || Remaining < 4)
        {
            return Fail();
        }
        value = BinaryPrimitives.ReadInt32BigEndian(_data.Slice(_position));
        _position += 4;
        return true;
    }

    public bool TryUInt32BE(out uint value)
    {
        value = 0;
        if (!TryAlign(4) || Remaining < 4)
        {
            return Fail();
        }
        value = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_position));
        _position += 4;
        return true;
    }

    public bool TryInt64BE(out long value)
    {
        value = 0;
        if (!TryAlign(8) || Remaining < 8)
        {
            return Fail();
        }
        value = BinaryPrimitives.ReadInt64BigEndian(_data.Slice(_position));
        _position += 8;
        return true;
    }

    public bool TryUInt64BE(out ulong value)
    {
        value = 0;
        if (!TryAlign(8) || Remaining < 8)
        {
            return Fail();
        }
        value = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(_position));
        _position += 8;
        return true;
    }

    public bool TryFloat32BE(out float value)
    {
        value = 0;
        if (!TryUInt32BE(out var bits))
        {
            return false;
        }
        value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
        return true;
    }

    public bool TryFloat64BE(out double value)
    {
        value = 0;
        if (!TryUInt64BE(out var bits))
        {
            return false;
        }
        value = BitConverter.Int64BitsToDouble(unchecked((long)bits));
        return true;
    }

    /// <summary>Reads <paramref name="count"/> raw octets (boundary 1).</summary>
    public bool TryOctets(int count, out ReadOnlySpan<byte> values)
    {
        values = default;
        if (_failed || count < 0 || Remaining < count)
        {
            return Fail();
        }
        values = _data.Slice(_position, count);
        _position += count;
        return true;
    }

    /// <summary>
    /// Reads an RPRnullTerminatedArray of HLAASCIIchar: characters up to the first 0x00, which is consumed. Fails when no
    /// terminator is in the data or a character is not ASCII.
    /// </summary>
    public bool TryNullTerminatedAscii(out string text)
    {
        text = string.Empty;
        if (_failed)
        {
            return false;
        }
        var rest = _data.Slice(_position);
        var end = rest.IndexOf((byte)0);
        if (end < 0)
        {
            return Fail();
        }
        var chars = new char[end];
        for (var i = 0; i < end; i++)
        {
            if (rest[i] > 0x7F)
            {
                return Fail();
            }
            chars[i] = (char)rest[i];
        }
        text = new string(chars);
        _position += end + 1;
        return true;
    }

    /// <summary>
    /// Reads the header of an HLAvariableArray (align to max(4, element boundary), HLAinteger32BE count). Fails on a negative
    /// count or one that cannot fit in the remaining data given the smallest possible encoded element size, so a hostile
    /// count never drives an allocation.
    /// </summary>
    public bool TryVariableArrayHeader(int elementBoundary, int minElementSize, out int count)
    {
        count = 0;
        if (!TryAlign(Math.Max(4, elementBoundary)) || !TryInt32BE(out var n))
        {
            return Fail();
        }
        if (n < 0 || minElementSize < 1 || n > Remaining / minElementSize)
        {
            return Fail();
        }
        count = n;
        return true;
    }
}
