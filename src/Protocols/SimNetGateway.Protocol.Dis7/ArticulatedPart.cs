#nullable enable
using System;
using DISnet;
using DISnet.DataStreamUtilities;

namespace SimNetGateway.Protocol.Dis7;

/// <summary>
/// An articulated part record (IEEE 1278.1-2012 section 6.2.94.2) as carried in the variable parameters of an Entity State
/// PDU: record type 0, change indicator, the id of the part it is attached to, the parameter type (type class plus
/// metric) and the value. The standard gives the value 64 bits; a value is a 32-bit IEEE float in the first four bytes
/// (big-endian) followed by four zero bytes. The vendored <see cref="VariableParameter"/> is a generic 16-byte record,
/// so packing goes through the wire layout: the bytes are identical to what <see cref="ArticulatedParts"/> marshals.
/// </summary>
/// <param name="ChangeIndicator">Incremented by the sender whenever the part changes.</param>
/// <param name="PartAttachedTo">Id of the part this one is attached to (0 = the entity itself).</param>
/// <param name="ParameterType">Type class plus metric, see <see cref="MakeParameterType"/>.</param>
/// <param name="Value">The parameter value (radians for azimuth/elevation/rotation metrics).</param>
public readonly record struct ArticulatedPart(byte ChangeIndicator, ushort PartAttachedTo, uint ParameterType, float Value)
{
    /// <summary>The record type (parameter type designator) of an articulated part.</summary>
    public const byte RecordType = 0;

    /// <summary>Size of the record in bytes.</summary>
    public const int Size = 16;

    // Articulated part type metrics (SISO-REF-010, "Articulated Parts - Type Metric"); the low five bits of the type.
    public const uint MetricPosition = 1;
    public const uint MetricAzimuth = 11;
    public const uint MetricAzimuthRate = 12;
    public const uint MetricElevation = 13;
    public const uint MetricElevationRate = 14;
    public const uint MetricRotation = 15;

    // Articulated part type classes (SISO-REF-010, "Articulated Parts - Type Class"): multiples of 32.
    public const uint ClassPrimaryTurret1 = 4096;
    public const uint ClassPrimaryGun1 = 4416;

    /// <summary>The parameter type of a type class and a metric: <c>class + metric</c> (the metric is the low five bits).</summary>
    public static uint MakeParameterType(uint typeClass, uint metric) => (typeClass & ~0x1Fu) | (metric & 0x1Fu);

    /// <summary>The type class of a parameter type.</summary>
    public static uint TypeClassOf(uint parameterType) => parameterType & ~0x1Fu;

    /// <summary>The metric of a parameter type.</summary>
    public static uint MetricOf(uint parameterType) => parameterType & 0x1Fu;

    /// <summary>The generic variable parameter record carrying this part (the 16 wire bytes are those of the standard).</summary>
    public VariableParameter ToVariableParameter()
    {
        var typed = new ArticulatedParts
        {
            RecordType = RecordType,
            ChangeIndicator = ChangeIndicator,
            PartAttachedTo = PartAttachedTo,
            ParameterType = ParameterType,
            ParameterValue = (ulong)BitConverter.SingleToUInt32Bits(Value) << 32,
        };
        var output = new DataOutputStream(Endian.Big);
        typed.Marshal(output);
        var parameter = new VariableParameter();
        parameter.Unmarshal(new DataInputStream(output.ConvertToBytes(), Endian.Big));
        return parameter;
    }

    /// <summary>
    /// Reads an articulated part from a variable parameter; false when the record is not an articulated part
    /// (record type other than 0, e.g. an attached part).
    /// </summary>
    public static bool TryFrom(VariableParameter parameter, out ArticulatedPart part)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        part = default;
        if (parameter.RecordType != RecordType)
        {
            return false;
        }

        var output = new DataOutputStream(Endian.Big);
        parameter.Marshal(output);
        var typed = new ArticulatedParts();
        typed.Unmarshal(new DataInputStream(output.ConvertToBytes(), Endian.Big));
        part = new ArticulatedPart(
            typed.ChangeIndicator,
            typed.PartAttachedTo,
            typed.ParameterType,
            BitConverter.UInt32BitsToSingle((uint)(typed.ParameterValue >> 32)));
        return true;
    }
}
