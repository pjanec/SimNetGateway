using SimNetGateway.Protocol.Rpr.Hla.Encoding;

namespace SimNetGateway.Protocol.Rpr.Fom;

/// <summary>
/// The one place that knows the RPR FOM 2.0 (SISO-STD-001.1-2015) names and data layouts (DD-008 section 4): the FOM subset
/// the gateway uses as a code model (<see cref="Model"/>, name constants in the nested classes) and the codecs of its
/// data types over the padded IEEE 1516.2 encoding of <see cref="HlaWriter"/> / <see cref="HlaReader"/> (DD-008 section 3.3).
/// <para>
/// Codec contract: <c>EncodeX</c> returns the value of one attribute or parameter, built from offset 0, with zero padding;
/// it throws <see cref="ArgumentException"/> for a value the FOM cannot represent (an enumerator not in the FOM, a
/// dead reckoning algorithm without alternative, non-ASCII text, a marking that is not 11 octets). <c>TryDecodeX</c> never
/// throws and never returns a half value: it returns false for a truncated, over-long (trailing octets) or inconsistent value
/// (an enumerator not in the FOM, a boolean other than 0 and 1, a count that does not fit) and leaves the output default.
/// Padding octets are not checked on decode. Floating point values keep their exact bit pattern.
/// </para>
/// </summary>
public static partial class RprFom2
{
    /// <summary>Name of the module set the code model represents; the key of <see cref="FomModules"/>.</summary>
    public const string ModuleName = "RPR-FOM-2.0";

    private static readonly Lazy<FomModel> ModelValue = new(BuildModel);

    /// <summary>The RPR FOM 2.0 subset the gateway uses, built in code (R-054). The same immutable instance every time.</summary>
    public static FomModel Model() => ModelValue.Value;

    // --- boundaries (octets) of the RPR constructed types, derived from their fields (DD-008 section 3.3) ---

    private const int WorldLocationBoundary = 8;

    // Boundaries of the alternatives of the two variant records used: every Spatial*Struct starts with a WorldLocationStruct
    // (float64, 8); ArticulatedPartsStruct and AttachedPartsStruct hold uint32 (4).
    private static readonly int[] SpatialAlternatives = [8, 8, 8, 8, 8, 8, 8, 8, 8];
    private static readonly int[] ParameterValueAlternatives = [4, 4];
    private const int ArticulatedParameterBoundary = 4; // PartAttachedTo 2, ParameterType 4, the part structs 4

    private delegate bool Reader<T>(ref HlaReader reader, out T value);

    private static bool Decode<T>(ReadOnlySpan<byte> data, Reader<T> read, out T value)
    {
        var reader = new HlaReader(data);
        if (read(ref reader, out value) && !reader.Failed && reader.IsAtEnd)
        {
            return true;
        }
        value = default!;
        return false;
    }

    private static byte[] Encode(Action<HlaWriter> write)
    {
        var writer = new HlaWriter();
        write(writer);
        return writer.ToArray();
    }

    // --- RPRboolean ---

    public static byte[] EncodeBoolean(bool value) => Encode(w => WriteBoolean(w, value));

    public static bool TryDecodeBoolean(ReadOnlySpan<byte> data, out bool value) => Decode(data, TryReadBoolean, out value);

    internal static void WriteBoolean(HlaWriter w, bool value) => w.Octet(value ? (byte)1 : (byte)0);

    internal static bool TryReadBoolean(ref HlaReader r, out bool value)
    {
        value = false;
        if (!r.TryOctet(out var b) || b > 1)
        {
            return r.Fail();
        }
        value = b == 1;
        return true;
    }

    // --- enumerations (encoded as their representation type; only enumerators of the FOM are valid) ---

    public static byte[] EncodeForceIdentifier(ForceIdentifierEnum8 value) => Encode(w => WriteEnum8(w, value));

    public static bool TryDecodeForceIdentifier(ReadOnlySpan<byte> data, out ForceIdentifierEnum8 value) =>
        Decode(data, TryReadForceIdentifier, out value);

    public static byte[] EncodeDamageStatus(DamageStatusEnum32 value) => Encode(w => WriteEnum32(w, value));

    public static bool TryDecodeDamageStatus(ReadOnlySpan<byte> data, out DamageStatusEnum32 value) =>
        Decode(data, TryReadDamageStatus, out value);

    public static byte[] EncodeDetonationResultCode(DetonationResultCodeEnum8 value) => Encode(w => WriteEnum8(w, value));

    public static bool TryDecodeDetonationResultCode(ReadOnlySpan<byte> data, out DetonationResultCodeEnum8 value) =>
        Decode(data, TryReadDetonationResultCode, out value);

    public static byte[] EncodeFuseType(FuseTypeEnum16 value) => Encode(w => WriteEnum16(w, value));

    public static bool TryDecodeFuseType(ReadOnlySpan<byte> data, out FuseTypeEnum16 value) =>
        Decode(data, TryReadFuseType, out value);

    public static byte[] EncodeWarheadType(WarheadTypeEnum16 value) => Encode(w => WriteEnum16(w, value));

    public static bool TryDecodeWarheadType(ReadOnlySpan<byte> data, out WarheadTypeEnum16 value) =>
        Decode(data, TryReadWarheadType, out value);

    public static byte[] EncodeMarkingEncoding(MarkingEncodingEnum8 value) => Encode(w => WriteEnum8(w, value));

    public static bool TryDecodeMarkingEncoding(ReadOnlySpan<byte> data, out MarkingEncodingEnum8 value) =>
        Decode(data, TryReadMarkingEncoding, out value);

    public static byte[] EncodeDeadReckoningAlgorithm(DeadReckoningAlgorithmEnum8 value) => Encode(w => WriteEnum8(w, value));

    public static bool TryDecodeDeadReckoningAlgorithm(ReadOnlySpan<byte> data, out DeadReckoningAlgorithmEnum8 value) =>
        Decode(data, TryReadDeadReckoningAlgorithm, out value);

    private static void WriteEnum8<T>(HlaWriter w, T value) where T : struct, Enum
    {
        RequireDefined(value);
        w.Octet(Convert.ToByte(value));
    }

    private static void WriteEnum16<T>(HlaWriter w, T value) where T : struct, Enum
    {
        RequireDefined(value);
        w.UInt16BE(Convert.ToUInt16(value));
    }

    private static void WriteEnum32<T>(HlaWriter w, T value) where T : struct, Enum
    {
        RequireDefined(value);
        w.UInt32BE(Convert.ToUInt32(value));
    }

    private static void RequireDefined<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException($"{value} ({typeof(T).Name}) is not an enumerator of the RPR FOM 2.0", nameof(value));
        }
    }

    private static bool TryReadForceIdentifier(ref HlaReader r, out ForceIdentifierEnum8 value) =>
        TryReadEnum8(ref r, out value);

    private static bool TryReadDetonationResultCode(ref HlaReader r, out DetonationResultCodeEnum8 value) =>
        TryReadEnum8(ref r, out value);

    private static bool TryReadMarkingEncoding(ref HlaReader r, out MarkingEncodingEnum8 value) =>
        TryReadEnum8(ref r, out value);

    private static bool TryReadDeadReckoningAlgorithm(ref HlaReader r, out DeadReckoningAlgorithmEnum8 value) =>
        TryReadEnum8(ref r, out value);

    private static bool TryReadFuseType(ref HlaReader r, out FuseTypeEnum16 value) => TryReadEnum16(ref r, out value);

    private static bool TryReadWarheadType(ref HlaReader r, out WarheadTypeEnum16 value) => TryReadEnum16(ref r, out value);

    private static bool TryReadDamageStatus(ref HlaReader r, out DamageStatusEnum32 value) => TryReadEnum32(ref r, out value);

    private static bool TryReadEnum8<T>(ref HlaReader r, out T value) where T : struct, Enum
    {
        value = default;
        if (!r.TryOctet(out var raw))
        {
            return false;
        }
        var candidate = (T)Enum.ToObject(typeof(T), raw);
        if (!Enum.IsDefined(candidate))
        {
            return r.Fail();
        }
        value = candidate;
        return true;
    }

    private static bool TryReadEnum16<T>(ref HlaReader r, out T value) where T : struct, Enum
    {
        value = default;
        if (!r.TryUInt16BE(out var raw))
        {
            return false;
        }
        var candidate = (T)Enum.ToObject(typeof(T), raw);
        if (!Enum.IsDefined(candidate))
        {
            return r.Fail();
        }
        value = candidate;
        return true;
    }

    private static bool TryReadEnum32<T>(ref HlaReader r, out T value) where T : struct, Enum
    {
        value = default;
        if (!r.TryUInt32BE(out var raw))
        {
            return false;
        }
        var candidate = (T)Enum.ToObject(typeof(T), raw);
        if (!Enum.IsDefined(candidate))
        {
            return r.Fail();
        }
        value = candidate;
        return true;
    }

    // --- identifiers ---

    public static byte[] EncodeFederateIdentifier(FederateIdentifierStruct value) => Encode(w => WriteFederateIdentifier(w, value));

    public static bool TryDecodeFederateIdentifier(ReadOnlySpan<byte> data, out FederateIdentifierStruct value) =>
        Decode(data, TryReadFederateIdentifier, out value);

    internal static void WriteFederateIdentifier(HlaWriter w, FederateIdentifierStruct value)
    {
        w.Align(2);
        w.UInt16BE(value.SiteId);
        w.UInt16BE(value.ApplicationId);
    }

    internal static bool TryReadFederateIdentifier(ref HlaReader r, out FederateIdentifierStruct value)
    {
        value = default;
        if (!r.TryAlign(2) || !r.TryUInt16BE(out var site) || !r.TryUInt16BE(out var app))
        {
            return false;
        }
        value = new FederateIdentifierStruct(site, app);
        return true;
    }

    public static byte[] EncodeEntityIdentifier(EntityIdentifierStruct value) => Encode(w => WriteEntityIdentifier(w, value));

    public static bool TryDecodeEntityIdentifier(ReadOnlySpan<byte> data, out EntityIdentifierStruct value) =>
        Decode(data, TryReadEntityIdentifier, out value);

    internal static void WriteEntityIdentifier(HlaWriter w, EntityIdentifierStruct value)
    {
        w.Align(2);
        WriteFederateIdentifier(w, value.FederateIdentifier);
        w.UInt16BE(value.EntityNumber);
    }

    internal static bool TryReadEntityIdentifier(ref HlaReader r, out EntityIdentifierStruct value)
    {
        value = default;
        if (!r.TryAlign(2) || !TryReadFederateIdentifier(ref r, out var federate) || !r.TryUInt16BE(out var number))
        {
            return false;
        }
        value = new EntityIdentifierStruct(federate, number);
        return true;
    }

    public static byte[] EncodeEntityType(EntityTypeStruct value) => Encode(w => WriteEntityType(w, value));

    public static bool TryDecodeEntityType(ReadOnlySpan<byte> data, out EntityTypeStruct value) =>
        Decode(data, TryReadEntityType, out value);

    internal static void WriteEntityType(HlaWriter w, EntityTypeStruct value)
    {
        w.Align(2);
        w.Octet(value.EntityKind);
        w.Octet(value.Domain);
        w.UInt16BE(value.CountryCode);
        w.Octet(value.Category);
        w.Octet(value.Subcategory);
        w.Octet(value.Specific);
        w.Octet(value.Extra);
    }

    internal static bool TryReadEntityType(ref HlaReader r, out EntityTypeStruct value)
    {
        value = default;
        if (!r.TryAlign(2) || !r.TryOctet(out var kind) || !r.TryOctet(out var domain) || !r.TryUInt16BE(out var country)
            || !r.TryOctet(out var category) || !r.TryOctet(out var subcategory) || !r.TryOctet(out var specific)
            || !r.TryOctet(out var extra))
        {
            return false;
        }
        value = new EntityTypeStruct(kind, domain, country, category, subcategory, specific, extra);
        return true;
    }

    /// <summary>RTIobjectId: the object instance name as RPRnullTerminatedArray of ASCII characters.</summary>
    public static byte[] EncodeRtiObjectId(string objectName) => Encode(w => w.NullTerminatedAscii(objectName));

    public static bool TryDecodeRtiObjectId(ReadOnlySpan<byte> data, out string objectName) =>
        Decode(data, TryReadRtiObjectId, out objectName);

    private static bool TryReadRtiObjectId(ref HlaReader r, out string value) => r.TryNullTerminatedAscii(out value);

    public static byte[] EncodeEventIdentifier(EventIdentifierStruct value) => Encode(w => WriteEventIdentifier(w, value));

    public static bool TryDecodeEventIdentifier(ReadOnlySpan<byte> data, out EventIdentifierStruct value) =>
        Decode(data, TryReadEventIdentifier, out value);

    internal static void WriteEventIdentifier(HlaWriter w, EventIdentifierStruct value)
    {
        w.Align(2);
        w.UInt16BE(value.EventCount);
        w.NullTerminatedAscii(value.IssuingObjectIdentifier);
    }

    internal static bool TryReadEventIdentifier(ref HlaReader r, out EventIdentifierStruct value)
    {
        value = default;
        if (!r.TryAlign(2) || !r.TryUInt16BE(out var count) || !r.TryNullTerminatedAscii(out var name))
        {
            return false;
        }
        value = new EventIdentifierStruct(count, name);
        return true;
    }

    // --- marking ---

    public static byte[] EncodeMarking(MarkingStruct value) => Encode(w => WriteMarking(w, value));

    public static bool TryDecodeMarking(ReadOnlySpan<byte> data, out MarkingStruct value) =>
        Decode(data, TryReadMarking, out value);

    internal static void WriteMarking(HlaWriter w, MarkingStruct value)
    {
        if (value.Data is null || value.Data.Length != MarkingStruct.DataLength)
        {
            throw new ArgumentException($"MarkingStruct data must be exactly {MarkingStruct.DataLength} octets", nameof(value));
        }
        WriteEnum8(w, value.EncodingType);
        w.Octets(value.Data);
    }

    internal static bool TryReadMarking(ref HlaReader r, out MarkingStruct value)
    {
        value = default;
        if (!TryReadMarkingEncoding(ref r, out var encoding) || !r.TryOctets(MarkingStruct.DataLength, out var data))
        {
            return false;
        }
        value = new MarkingStruct(encoding, data.ToArray());
        return true;
    }

    // --- vectors ---

    public static byte[] EncodeWorldLocation(WorldLocationStruct value) => Encode(w => WriteWorldLocation(w, value));

    public static bool TryDecodeWorldLocation(ReadOnlySpan<byte> data, out WorldLocationStruct value) =>
        Decode(data, TryReadWorldLocation, out value);

    internal static void WriteWorldLocation(HlaWriter w, WorldLocationStruct value)
    {
        w.Align(WorldLocationBoundary);
        w.Float64BE(value.X);
        w.Float64BE(value.Y);
        w.Float64BE(value.Z);
    }

    internal static bool TryReadWorldLocation(ref HlaReader r, out WorldLocationStruct value)
    {
        value = default;
        if (!r.TryAlign(WorldLocationBoundary) || !r.TryFloat64BE(out var x) || !r.TryFloat64BE(out var y)
            || !r.TryFloat64BE(out var z))
        {
            return false;
        }
        value = new WorldLocationStruct(x, y, z);
        return true;
    }

    public static byte[] EncodeOrientation(OrientationStruct value) => Encode(w => WriteOrientation(w, value));

    public static bool TryDecodeOrientation(ReadOnlySpan<byte> data, out OrientationStruct value) =>
        Decode(data, TryReadOrientation, out value);

    internal static void WriteOrientation(HlaWriter w, OrientationStruct value) => WriteVector3(w, value.Psi, value.Theta, value.Phi);

    internal static bool TryReadOrientation(ref HlaReader r, out OrientationStruct value)
    {
        value = default;
        if (!TryReadVector3(ref r, out var a, out var b, out var c))
        {
            return false;
        }
        value = new OrientationStruct(a, b, c);
        return true;
    }

    public static byte[] EncodeVelocityVector(VelocityVectorStruct value) => Encode(w => WriteVector3(w, value.X, value.Y, value.Z));

    public static bool TryDecodeVelocityVector(ReadOnlySpan<byte> data, out VelocityVectorStruct value) =>
        Decode(data, TryReadVelocity, out value);

    internal static bool TryReadVelocity(ref HlaReader r, out VelocityVectorStruct value)
    {
        value = default;
        if (!TryReadVector3(ref r, out var a, out var b, out var c))
        {
            return false;
        }
        value = new VelocityVectorStruct(a, b, c);
        return true;
    }

    public static byte[] EncodeAccelerationVector(AccelerationVectorStruct value) =>
        Encode(w => WriteVector3(w, value.X, value.Y, value.Z));

    public static bool TryDecodeAccelerationVector(ReadOnlySpan<byte> data, out AccelerationVectorStruct value) =>
        Decode(data, TryReadAcceleration, out value);

    internal static bool TryReadAcceleration(ref HlaReader r, out AccelerationVectorStruct value)
    {
        value = default;
        if (!TryReadVector3(ref r, out var a, out var b, out var c))
        {
            return false;
        }
        value = new AccelerationVectorStruct(a, b, c);
        return true;
    }

    public static byte[] EncodeAngularVelocityVector(AngularVelocityVectorStruct value) =>
        Encode(w => WriteVector3(w, value.X, value.Y, value.Z));

    public static bool TryDecodeAngularVelocityVector(ReadOnlySpan<byte> data, out AngularVelocityVectorStruct value) =>
        Decode(data, TryReadAngularVelocity, out value);

    internal static bool TryReadAngularVelocity(ref HlaReader r, out AngularVelocityVectorStruct value)
    {
        value = default;
        if (!TryReadVector3(ref r, out var a, out var b, out var c))
        {
            return false;
        }
        value = new AngularVelocityVectorStruct(a, b, c);
        return true;
    }

    public static byte[] EncodeRelativePosition(RelativePositionStruct value) =>
        Encode(w => WriteVector3(w, value.BodyX, value.BodyY, value.BodyZ));

    public static bool TryDecodeRelativePosition(ReadOnlySpan<byte> data, out RelativePositionStruct value) =>
        Decode(data, TryReadRelativePosition, out value);

    internal static bool TryReadRelativePosition(ref HlaReader r, out RelativePositionStruct value)
    {
        value = default;
        if (!TryReadVector3(ref r, out var a, out var b, out var c))
        {
            return false;
        }
        value = new RelativePositionStruct(a, b, c);
        return true;
    }

    /// <summary>A record of three float32 (boundary 4, 12 octets).</summary>
    private static void WriteVector3(HlaWriter w, float a, float b, float c)
    {
        w.Align(4);
        w.Float32BE(a);
        w.Float32BE(b);
        w.Float32BE(c);
    }

    private static bool TryReadVector3(ref HlaReader r, out float a, out float b, out float c)
    {
        a = b = c = 0;
        return r.TryAlign(4) && r.TryFloat32BE(out a) && r.TryFloat32BE(out b) && r.TryFloat32BE(out c);
    }

    // --- spatial variant ---

    public static byte[] EncodeSpatial(SpatialVariantStruct value) => Encode(w => WriteSpatial(w, value));

    public static bool TryDecodeSpatial(ReadOnlySpan<byte> data, out SpatialVariantStruct value) =>
        Decode(data, TryReadSpatial, out value);

    /// <summary>
    /// SpatialVariantStruct: a variant record. The record boundary is the largest boundary over the discriminant and ALL
    /// alternatives, 8 (WorldLocationStruct holds float64); the padding after the 1 octet discriminant therefore always
    /// goes up to the next multiple of that largest boundary, whichever alternative is chosen (DD-008 section 3.3).
    /// Sizes: Static 48, FP 60, RP and FV 72, RV 84.
    /// </summary>
    internal static void WriteSpatial(HlaWriter w, SpatialVariantStruct value)
    {
        if (value.Algorithm is DeadReckoningAlgorithmEnum8.Other || !Enum.IsDefined(value.Algorithm))
        {
            throw new ArgumentException($"{value.Algorithm} has no alternative in SpatialVariantStruct", nameof(value));
        }
        w.VariantRecordStart(1, SpatialAlternatives);
        w.Octet((byte)value.Algorithm);
        w.VariantAlternativeStart(SpatialAlternatives); // the largest boundary over all alternatives, not the chosen one's
        WriteWorldLocation(w, value.WorldLocation);
        WriteBoolean(w, value.IsFrozen);
        WriteOrientation(w, value.Orientation);
        if (value.HasVelocity)
        {
            WriteVector3(w, value.Velocity.X, value.Velocity.Y, value.Velocity.Z);
        }
        if (value.HasAcceleration)
        {
            WriteVector3(w, value.Acceleration.X, value.Acceleration.Y, value.Acceleration.Z);
        }
        if (value.HasAngularVelocity)
        {
            WriteVector3(w, value.AngularVelocity.X, value.AngularVelocity.Y, value.AngularVelocity.Z);
        }
    }

    internal static bool TryReadSpatial(ref HlaReader r, out SpatialVariantStruct value)
    {
        value = default;
        if (!r.TryVariantRecordStart(1, SpatialAlternatives) || !r.TryOctet(out var raw) || raw is 0 or > 9
            || !r.TryVariantAlternativeStart(SpatialAlternatives))
        {
            return r.Fail();
        }
        var algorithm = (DeadReckoningAlgorithmEnum8)raw;
        if (!TryReadWorldLocation(ref r, out var location) || !TryReadBoolean(ref r, out var frozen)
            || !TryReadOrientation(ref r, out var orientation))
        {
            return false;
        }
        var probe = new SpatialVariantStruct(algorithm, location, frozen, orientation, default, default, default);
        VelocityVectorStruct velocity = default;
        AccelerationVectorStruct acceleration = default;
        AngularVelocityVectorStruct angular = default;
        if (probe.HasVelocity && !TryReadVelocity(ref r, out velocity))
        {
            return false;
        }
        if (probe.HasAcceleration && !TryReadAcceleration(ref r, out acceleration))
        {
            return false;
        }
        if (probe.HasAngularVelocity && !TryReadAngularVelocity(ref r, out angular))
        {
            return false;
        }
        value = probe with { Velocity = velocity, Acceleration = acceleration, AngularVelocity = angular };
        return true;
    }

    // --- articulated parameters ---

    public static byte[] EncodeArticulatedParameter(ArticulatedParameterStruct value) =>
        Encode(w => WriteArticulatedParameter(w, value));

    public static bool TryDecodeArticulatedParameter(ReadOnlySpan<byte> data, out ArticulatedParameterStruct value) =>
        Decode(data, TryReadArticulatedParameter, out value);

    /// <summary>
    /// ArticulatedParameterStruct: Change (octet, offset 0), PartAttachedTo (uint16, offset 2 after 1 pad), then the variant
    /// at offset 4: ParameterType (uint32) and, since both alternatives have boundary 4, no extra padding, then
    /// ArticulatedPartsStruct (uint32, uint32, float32 = 12) or AttachedPartsStruct (uint32 + EntityTypeStruct 8 = 12).
    /// Both alternatives give 4 + 4 + 12 = 20 octets; the record boundary is 4.
    /// </summary>
    internal static void WriteArticulatedParameter(HlaWriter w, ArticulatedParameterStruct value)
    {
        var type = value.Value.ParameterType;
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentException($"{type} has no alternative in ParameterValueVariantStruct", nameof(value));
        }
        w.Align(ArticulatedParameterBoundary);
        w.Octet(value.Change);
        w.UInt16BE(value.PartAttachedTo);
        w.UInt32BE((uint)type);
        w.VariantAlternativeStart(ParameterValueAlternatives); // largest boundary over both alternatives
        if (type == ParameterTypeEnum32.ArticulatedPart)
        {
            w.UInt32BE(value.Value.ArticulatedParts.Class);
            w.UInt32BE(value.Value.ArticulatedParts.TypeMetric);
            w.Float32BE(value.Value.ArticulatedParts.Value);
        }
        else
        {
            w.UInt32BE(value.Value.AttachedParts.Station);
            WriteEntityType(w, value.Value.AttachedParts.StoreType);
        }
    }

    internal static bool TryReadArticulatedParameter(ref HlaReader r, out ArticulatedParameterStruct value)
    {
        value = default;
        if (!r.TryAlign(ArticulatedParameterBoundary) || !r.TryOctet(out var change) || !r.TryUInt16BE(out var attachedTo)
            || !r.TryUInt32BE(out var rawType) || rawType > 1 || !r.TryVariantAlternativeStart(ParameterValueAlternatives))
        {
            return r.Fail();
        }
        var type = (ParameterTypeEnum32)rawType;
        ArticulatedPartsStruct parts = default;
        AttachedPartsStruct attached = default;
        if (type == ParameterTypeEnum32.ArticulatedPart)
        {
            if (!r.TryUInt32BE(out var cls) || !r.TryUInt32BE(out var metric) || !r.TryFloat32BE(out var v))
            {
                return false;
            }
            parts = new ArticulatedPartsStruct(cls, metric, v);
        }
        else
        {
            if (!r.TryUInt32BE(out var station) || !TryReadEntityType(ref r, out var store))
            {
                return false;
            }
            attached = new AttachedPartsStruct(station, store);
        }
        value = new ArticulatedParameterStruct(change, attachedTo, new ParameterValueVariantStruct(type, parts, attached));
        return true;
    }

    /// <summary>
    /// ArticulatedParameterStructLengthlessArray (RPRlengthlessArray): the elements only, each aligned to its own boundary
    /// (4); with 20 octet elements that is no padding at all. An empty array is an empty value.
    /// </summary>
    public static byte[] EncodeArticulatedParameters(IReadOnlyList<ArticulatedParameterStruct> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Encode(w =>
        {
            foreach (var v in values)
            {
                WriteArticulatedParameter(w, v);
            }
        });
    }

    /// <summary>
    /// Decodes a lengthless array: elements until the data ends. Data that ends inside an element is a failure; data that
    /// ends between elements is a valid shorter array (the encoding has no count, so that cannot be told apart).
    /// </summary>
    public static bool TryDecodeArticulatedParameters(ReadOnlySpan<byte> data, out ArticulatedParameterStruct[] values)
    {
        var list = new List<ArticulatedParameterStruct>();
        var reader = new HlaReader(data);
        while (!reader.IsAtEnd)
        {
            if (!TryReadArticulatedParameter(ref reader, out var element))
            {
                values = Array.Empty<ArticulatedParameterStruct>();
                return false;
            }
            list.Add(element);
        }
        values = list.ToArray();
        return true;
    }
}
