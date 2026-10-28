namespace SimNetGateway.Protocol.Rpr.Fom;

// Plain value types of the RPR FOM 2.0 data types (SISO-STD-001.1-2015) that the gateway encodes and decodes. They carry the
// FOM field names and units (metres, radians, m/s, m/s^2, rad/s); the wire layout lives in RprFom2 (DD-008 section 3.3).
// They are independent of the gateway's own message types in RprMessages.cs.

/// <summary>FederateIdentifierStruct: the (site, application) pair that is also the DIS site and application number.</summary>
public readonly record struct FederateIdentifierStruct(ushort SiteId, ushort ApplicationId);

/// <summary>EntityIdentifierStruct: the federate identifier plus the entity number (a DIS entity id).</summary>
public readonly record struct EntityIdentifierStruct(FederateIdentifierStruct FederateIdentifier, ushort EntityNumber);

/// <summary>EntityTypeStruct: the DIS entity type (kind, domain, country, category, subcategory, specific, extra).</summary>
public readonly record struct EntityTypeStruct(
    byte EntityKind, byte Domain, ushort CountryCode, byte Category, byte Subcategory, byte Specific, byte Extra);

/// <summary>WorldLocationStruct: geocentric position in metres.</summary>
public readonly record struct WorldLocationStruct(double X, double Y, double Z);

/// <summary>OrientationStruct: Euler angles psi, theta, phi in radians.</summary>
public readonly record struct OrientationStruct(float Psi, float Theta, float Phi);

/// <summary>VelocityVectorStruct in m/s.</summary>
public readonly record struct VelocityVectorStruct(float X, float Y, float Z);

/// <summary>AccelerationVectorStruct in m/s^2.</summary>
public readonly record struct AccelerationVectorStruct(float X, float Y, float Z);

/// <summary>AngularVelocityVectorStruct in rad/s.</summary>
public readonly record struct AngularVelocityVectorStruct(float X, float Y, float Z);

/// <summary>RelativePositionStruct: body-frame distances in metres.</summary>
public readonly record struct RelativePositionStruct(float BodyX, float BodyY, float BodyZ);

/// <summary>MarkingStruct: an encoding type and exactly 11 data octets (MarkingArray11).</summary>
public readonly record struct MarkingStruct(MarkingEncodingEnum8 EncodingType, byte[] Data)
{
    public const int DataLength = 11;

    /// <summary>An ASCII marking of at most 11 characters, zero padded.</summary>
    public static MarkingStruct FromAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var data = new byte[DataLength];
        for (var i = 0; i < Math.Min(text.Length, DataLength); i++)
        {
            data[i] = (byte)(text[i] > 0x7F ? '?' : text[i]);
        }
        return new MarkingStruct(MarkingEncodingEnum8.ASCII, data);
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

    public bool Equals(MarkingStruct other) => EncodingType == other.EncodingType && Data.AsSpan().SequenceEqual(other.Data);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EncodingType);
        hash.AddBytes(Data);
        return hash.ToHashCode();
    }
}

/// <summary>
/// SpatialVariantStruct: the discriminant (dead reckoning algorithm) and the fields that algorithm carries. Fields an
/// algorithm does not carry are zero after a decode and ignored by an encode:
/// Static carries none of velocity / acceleration / angular velocity; FPW, FPB carry velocity; RPW, RPB velocity and angular
/// velocity; FVW, FVB velocity and acceleration; RVW, RVB all three. <see cref="DeadReckoningAlgorithmEnum8.Other"/> has no
/// alternative in the FOM and cannot be encoded or decoded.
/// </summary>
public readonly record struct SpatialVariantStruct(
    DeadReckoningAlgorithmEnum8 Algorithm,
    WorldLocationStruct WorldLocation,
    bool IsFrozen,
    OrientationStruct Orientation,
    VelocityVectorStruct Velocity,
    AccelerationVectorStruct Acceleration,
    AngularVelocityVectorStruct AngularVelocity)
{
    public bool HasVelocity => Algorithm is not (DeadReckoningAlgorithmEnum8.Static or DeadReckoningAlgorithmEnum8.Other);

    public bool HasAcceleration => Algorithm is DeadReckoningAlgorithmEnum8.DRM_RVW or DeadReckoningAlgorithmEnum8.DRM_RVB
        or DeadReckoningAlgorithmEnum8.DRM_FVW or DeadReckoningAlgorithmEnum8.DRM_FVB;

    public bool HasAngularVelocity => Algorithm is DeadReckoningAlgorithmEnum8.DRM_RPW or DeadReckoningAlgorithmEnum8.DRM_RPB
        or DeadReckoningAlgorithmEnum8.DRM_RVW or DeadReckoningAlgorithmEnum8.DRM_RVB;
}

/// <summary>EventIdentifierStruct: the event count of the issuing federate and the RTI object name of the issuing object.</summary>
public readonly record struct EventIdentifierStruct(ushort EventCount, string IssuingObjectIdentifier);

/// <summary>ParameterTypeEnum32 as far as ParameterValueVariantStruct has alternatives for it.</summary>
public enum ParameterTypeEnum32 : uint
{
    ArticulatedPart = 0,
    AttachedPart = 1,
}

/// <summary>ArticulatedPartsStruct.</summary>
public readonly record struct ArticulatedPartsStruct(uint Class, uint TypeMetric, float Value);

/// <summary>AttachedPartsStruct.</summary>
public readonly record struct AttachedPartsStruct(uint Station, EntityTypeStruct StoreType);

/// <summary>
/// ParameterValueVariantStruct: <see cref="ParameterType"/> selects which of the two parts is meaningful; the other is zero
/// after a decode and ignored by an encode.
/// </summary>
public readonly record struct ParameterValueVariantStruct(
    ParameterTypeEnum32 ParameterType, ArticulatedPartsStruct ArticulatedParts, AttachedPartsStruct AttachedParts);

/// <summary>ArticulatedParameterStruct: an articulated or attached part.</summary>
public readonly record struct ArticulatedParameterStruct(byte Change, ushort PartAttachedTo, ParameterValueVariantStruct Value);
