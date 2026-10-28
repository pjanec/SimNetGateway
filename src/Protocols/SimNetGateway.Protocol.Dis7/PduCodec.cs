#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DISnet;
using DISnet.DataStreamUtilities;

namespace SimNetGateway.Protocol.Dis7;

/// <summary>PDU type numbers (SISO-REF-010, "PDU Type") of the PDUs this codec decodes.</summary>
public static class PduTypes
{
    public const byte EntityState = 1;
    public const byte Fire = 2;
    public const byte Detonation = 3;
    public const byte Collision = 4;
    public const byte CreateEntity = 11;
    public const byte RemoveEntity = 12;
    public const byte StartResume = 13;
    public const byte StopFreeze = 14;
    public const byte Acknowledge = 15;
    public const byte ActionRequest = 16;
    public const byte ActionResponse = 17;
    public const byte EntityStateUpdate = 67;
}

/// <summary>Outcome of decoding one PDU of a datagram.</summary>
public enum PduDecodeStatus
{
    /// <summary>The PDU was decoded completely.</summary>
    Decoded,

    /// <summary>A well-framed PDU of a type this codec does not decode; it was skipped.</summary>
    Unsupported,

    /// <summary>The PDU or the framing is malformed; no PDU object is returned.</summary>
    Error,
}

/// <summary>One PDU slot of a decoded datagram: the PDU, or why there is none.</summary>
/// <param name="Status">Decoded, Unsupported or Error.</param>
/// <param name="Offset">Byte offset of the PDU in the datagram.</param>
/// <param name="Length">Byte length of the PDU (header Length) when it could be determined, else the bytes left.</param>
/// <param name="PduType">The PDU type number of the header, or 0 when the header itself is incomplete.</param>
/// <param name="Pdu">The decoded PDU (only when <paramref name="Status"/> is Decoded).</param>
/// <param name="Error">Description (only when <paramref name="Status"/> is Error).</param>
public sealed record DecodedPdu(PduDecodeStatus Status, int Offset, int Length, byte PduType, Pdu? Pdu, string? Error);

/// <summary>The result of <see cref="PduCodec.Decode(ReadOnlySpan{byte})"/>: one entry per PDU found, in datagram order.</summary>
public sealed class PduDecodeResult
{
    internal PduDecodeResult(IReadOnlyList<DecodedPdu> items)
    {
        Items = items;
        foreach (var item in items)
        {
            if (item.Status == PduDecodeStatus.Error)
            {
                HasErrors = true;
            }
        }
    }

    public IReadOnlyList<DecodedPdu> Items { get; }

    /// <summary>True when any entry is an error (a datagram with an error entry is not fully understood).</summary>
    public bool HasErrors { get; }

    /// <summary>The decoded PDUs only, in order.</summary>
    public IEnumerable<Pdu> Pdus
    {
        get
        {
            foreach (var item in Items)
            {
                if (item.Pdu is not null)
                {
                    yield return item.Pdu;
                }
            }
        }
    }
}

/// <summary>
/// Big-endian encoding and decoding of DIS 7 PDUs on top of the vendored classes. Decoding splits a datagram that
/// carries several PDUs by the header <c>Length</c>, reports unknown types as Unsupported and every malformation as an
/// error entry - it never throws and never returns a half-filled object (the vendored classes swallow read errors;
/// the codec reads each PDU from an exactly sized slice and checks that it consumed exactly its Length).
/// </summary>
public static class PduCodec
{
    /// <summary>Size of the PDU header in bytes.</summary>
    public const int HeaderSize = 12;

    private static readonly Dictionary<byte, Func<Pdu>> Factories = new()
    {
        [PduTypes.EntityState] = () => new EntityStatePdu(),
        [PduTypes.Fire] = () => new FirePdu(),
        [PduTypes.Detonation] = () => new DetonationPdu(),
        [PduTypes.Collision] = () => new CollisionPdu(),
        [PduTypes.CreateEntity] = () => new CreateEntityPdu(),
        [PduTypes.RemoveEntity] = () => new RemoveEntityPdu(),
        [PduTypes.StartResume] = () => new StartResumePdu(),
        [PduTypes.StopFreeze] = () => new StopFreezePdu(),
        [PduTypes.Acknowledge] = () => new AcknowledgePdu(),
        [PduTypes.ActionRequest] = () => new ActionRequestPdu(),
        [PduTypes.ActionResponse] = () => new ActionResponsePdu(),
        [PduTypes.EntityStateUpdate] = () => new EntityStateUpdatePdu(),
    };

    /// <summary>True when <see cref="Decode(ReadOnlySpan{byte})"/> decodes this PDU type.</summary>
    public static bool IsSupported(byte pduType) => Factories.ContainsKey(pduType);

    /// <summary>
    /// Decodes all PDUs of a datagram. A framing error (header cut off, Length below 12 or beyond the datagram) ends the
    /// scan with one error entry, since the next PDU cannot be located; a malformed body with a sound Length is an error
    /// entry and the scan continues with the next PDU. An empty datagram is an error.
    /// </summary>
    public static PduDecodeResult Decode(ReadOnlySpan<byte> datagram)
    {
        var items = new List<DecodedPdu>();
        if (datagram.Length == 0)
        {
            items.Add(new DecodedPdu(PduDecodeStatus.Error, 0, 0, 0, null, "empty datagram"));
            return new PduDecodeResult(items);
        }

        int offset = 0;
        while (offset < datagram.Length)
        {
            int remaining = datagram.Length - offset;
            if (remaining < HeaderSize)
            {
                items.Add(Fail(offset, remaining, 0, $"truncated header: {remaining} bytes left, {HeaderSize} needed"));
                break;
            }

            byte type = datagram[offset + 2];
            int length = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(offset + 8, 2));
            if (length < HeaderSize)
            {
                items.Add(Fail(offset, remaining, type, $"PDU type {type}: Length {length} is smaller than the header ({HeaderSize})"));
                break;
            }

            if (length > remaining)
            {
                items.Add(Fail(offset, remaining, type, $"PDU type {type}: Length {length} exceeds the {remaining} bytes left"));
                break;
            }

            if (!Factories.TryGetValue(type, out var create))
            {
                items.Add(new DecodedPdu(PduDecodeStatus.Unsupported, offset, length, type, null, null));
            }
            else
            {
                items.Add(DecodeOne(datagram.Slice(offset, length), offset, type, create));
            }

            offset += length;
        }

        return new PduDecodeResult(items);
    }

    /// <summary>Convenience overload for a byte array.</summary>
    public static PduDecodeResult Decode(byte[] datagram) => Decode(new ReadOnlySpan<byte>(datagram));

    /// <summary>
    /// Encodes a PDU big-endian. Sets <c>Length</c> to the marshalled size first. Throws
    /// <see cref="ArgumentException"/> when the PDU cannot be encoded consistently (more than 65535 bytes, more variable
    /// parameters than the count field holds, a marshalling error).
    /// </summary>
    public static byte[] Encode(Pdu pdu)
    {
        ArgumentNullException.ThrowIfNull(pdu);
        int size = pdu.GetMarshalledSize();
        if (size > ushort.MaxValue)
        {
            throw new ArgumentException($"PDU is {size} bytes, more than the 65535 the Length field holds", nameof(pdu));
        }

        if (VariableDatumsIn(pdu) is { } datums && datums != 0)
        {
            throw new ArgumentException($"variable datum records are not supported ({datums})", nameof(pdu));
        }

        int? parameters = pdu switch
        {
            EntityStatePdu entity => entity.VariableParameters.Count,
            DetonationPdu detonation => detonation.VariableParameters.Count,
            EntityStateUpdatePdu update => update.VariableParameters.Count,
            _ => null,
        };
        if (parameters > byte.MaxValue)
        {
            throw new ArgumentException($"{parameters} variable parameters, the count field holds at most 255", nameof(pdu));
        }

        pdu.Length = (ushort)size;
        string? failure = null;
        void OnError(Exception e) => failure ??= e.Message;
        pdu.Exception += OnError;
        byte[] bytes;
        try
        {
            var stream = new DataOutputStream(Endian.Big);
            pdu.Marshal(stream);
            bytes = stream.ConvertToBytes();
        }
        finally
        {
            pdu.Exception -= OnError;
        }

        if (failure is not null)
        {
            throw new ArgumentException("PDU could not be marshalled: " + failure, nameof(pdu));
        }

        if (bytes.Length != size)
        {
            throw new ArgumentException(
                $"PDU marshalled to {bytes.Length} bytes but its size is {size} (a record count field disagrees with its list?)",
                nameof(pdu));
        }

        return bytes;
    }

    // The vendored VariableDatum record has no payload bytes (it marshals a fixed 16 bytes), so it cannot carry the
    // variable-length data of the standard: PDUs with variable datum records are refused instead of mis-framed.
    private static uint? VariableDatumsIn(Pdu pdu) => pdu switch
    {
        ActionRequestPdu request => (uint)request.VariableDatums.Count | request.NumberOfVariableDatumRecords,
        ActionResponsePdu response => (uint)response.VariableDatums.Count | response.NumberOfVariableDatumRecords,
        _ => null,
    };

    private static DecodedPdu Fail(int offset, int length, byte type, string message) =>
        new(PduDecodeStatus.Error, offset, length, type, null, message);

    private static DecodedPdu DecodeOne(ReadOnlySpan<byte> bytes, int offset, byte type, Func<Pdu> create)
    {
        try
        {
            var pdu = create();
            string? failure = null;
            pdu.Exception += e => failure ??= e.Message;
            var input = new DataInputStream(bytes.ToArray(), Endian.Big);
            pdu.Unmarshal(input);

            if (failure is not null)
            {
                return Fail(offset, bytes.Length, type, $"PDU type {type}: {failure}");
            }

            if (VariableDatumsIn(pdu) is { } datums && datums != 0)
            {
                return Fail(offset, bytes.Length, type, $"PDU type {type}: variable datum records are not supported ({datums})");
            }

            if (input.Position != bytes.Length)
            {
                return Fail(offset, bytes.Length, type,
                    $"PDU type {type}: read {input.Position} of {bytes.Length} bytes (Length does not match the content)");
            }

            int size = pdu.GetMarshalledSize();
            if (size != bytes.Length)
            {
                return Fail(offset, bytes.Length, type, $"PDU type {type}: content is {size} bytes but Length says {bytes.Length}");
            }

            return new DecodedPdu(PduDecodeStatus.Decoded, offset, bytes.Length, type, pdu, null);
        }
        catch (Exception e)
        {
            return Fail(offset, bytes.Length, type, $"PDU type {type}: {e.GetType().Name}: {e.Message}");
        }
    }
}
