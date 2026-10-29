using SimNetGateway.Protocol.Rpr.Hla.Encoding;

namespace SimNetGateway.Protocol.Rpr.Fom;

// Codecs added in stage HLA-2 (DD-008 section 12): the AggregateEntity attributes the transport decodes and the scalar
// parameter types of WeaponFire / MunitionDetonation. Same contract as the rest of RprFom2 (see the class summary).

/// <summary>AggregateMarkingStruct: an encoding type and exactly 31 data octets (MarkingArray31, RPR-Aggregate_v2.0.xml).</summary>
public readonly record struct AggregateMarkingStruct(MarkingEncodingEnum8 EncodingType, byte[] Data)
{
    public const int DataLength = 31;

    /// <summary>An ASCII marking of at most 31 characters, zero padded.</summary>
    public static AggregateMarkingStruct FromAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var data = new byte[DataLength];
        for (var i = 0; i < Math.Min(text.Length, DataLength); i++)
        {
            data[i] = (byte)(text[i] > 0x7F ? '?' : text[i]);
        }
        return new AggregateMarkingStruct(MarkingEncodingEnum8.ASCII, data);
    }

    /// <summary>The data octets up to the first zero as ASCII text.</summary>
    public string AsciiText
    {
        get
        {
            var end = Array.IndexOf(Data, (byte)0);
            return System.Text.Encoding.ASCII.GetString(Data, 0, end < 0 ? Data.Length : end);
        }
    }

    public bool Equals(AggregateMarkingStruct other) => EncodingType == other.EncodingType && Data.AsSpan().SequenceEqual(other.Data);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EncodingType);
        hash.AddBytes(Data);
        return hash.ToHashCode();
    }
}

public static partial class RprFom2
{
    // --- AggregateMarkingStruct: encoding type octet + 31 octets, 32 octets, boundary 1 ---

    public static byte[] EncodeAggregateMarking(AggregateMarkingStruct value) => Encode(w => WriteAggregateMarking(w, value));

    public static bool TryDecodeAggregateMarking(ReadOnlySpan<byte> data, out AggregateMarkingStruct value) =>
        Decode(data, TryReadAggregateMarking, out value);

    internal static void WriteAggregateMarking(HlaWriter w, AggregateMarkingStruct value)
    {
        if (value.Data is null || value.Data.Length != AggregateMarkingStruct.DataLength)
        {
            throw new ArgumentException($"AggregateMarkingStruct data must be exactly {AggregateMarkingStruct.DataLength} octets", nameof(value));
        }
        WriteEnum8(w, value.EncodingType);
        w.Octets(value.Data);
    }

    internal static bool TryReadAggregateMarking(ref HlaReader r, out AggregateMarkingStruct value)
    {
        value = default;
        if (!TryReadMarkingEncoding(ref r, out var encoding) || !r.TryOctets(AggregateMarkingStruct.DataLength, out var data))
        {
            return false;
        }
        value = new AggregateMarkingStruct(encoding, data.ToArray());
        return true;
    }

    // --- RTIobjectIdArray: HLAvariableArray of RTIobjectId (count int32 BE, then each id null terminated, boundary 1) ---

    public static byte[] EncodeRtiObjectIdArray(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return Encode(w =>
        {
            w.VariableArrayHeader(names.Count, 1);
            foreach (var name in names)
            {
                w.NullTerminatedAscii(name);
            }
        });
    }

    public static bool TryDecodeRtiObjectIdArray(ReadOnlySpan<byte> data, out string[] names)
    {
        var reader = new HlaReader(data);
        // every element takes at least its terminator
        if (reader.TryVariableArrayHeader(1, 1, out var count))
        {
            var list = new string[count];
            for (var i = 0; i < count; i++)
            {
                if (!reader.TryNullTerminatedAscii(out list[i]))
                {
                    names = Array.Empty<string>();
                    return false;
                }
            }
            if (!reader.Failed && reader.IsAtEnd)
            {
                names = list;
                return true;
            }
        }
        names = Array.Empty<string>();
        return false;
    }

    // --- scalar parameter types ---

    /// <summary>UnsignedInteger16 (HLAinteger16BE as unsigned): QuantityFired, RateOfFire.</summary>
    public static byte[] EncodeUnsignedInteger16(ushort value) => Encode(w => w.UInt16BE(value));

    public static bool TryDecodeUnsignedInteger16(ReadOnlySpan<byte> data, out ushort value) =>
        Decode(data, TryReadUnsignedInteger16, out value);

    private static bool TryReadUnsignedInteger16(ref HlaReader r, out ushort value) => r.TryUInt16BE(out value);

    /// <summary>UnsignedInteger32: FireMissionIndex.</summary>
    public static byte[] EncodeUnsignedInteger32(uint value) => Encode(w => w.UInt32BE(value));

    public static bool TryDecodeUnsignedInteger32(ReadOnlySpan<byte> data, out uint value) =>
        Decode(data, TryReadUnsignedInteger32, out value);

    private static bool TryReadUnsignedInteger32(ref HlaReader r, out uint value) => r.TryUInt32BE(out value);

    /// <summary>LengthMeterFloat32 (HLAfloat32BE, metres): FireControlSolutionRange.</summary>
    public static byte[] EncodeLengthMeterFloat32(float value) => Encode(w => w.Float32BE(value));

    public static bool TryDecodeLengthMeterFloat32(ReadOnlySpan<byte> data, out float value) =>
        Decode(data, TryReadFloat32, out value);

    private static bool TryReadFloat32(ref HlaReader r, out float value) => r.TryFloat32BE(out value);
}
