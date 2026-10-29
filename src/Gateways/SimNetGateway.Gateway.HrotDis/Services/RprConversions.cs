using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// Plain conversions between the <c>Protocol.Rpr</c> values and the values of the pivot (DD-009 section 3 item 3). The pivot is RPR
/// FOM aligned (DD-006 section 3.1), so most are 1:1; no state, shared by the RPR ingress and egress modules so that both
/// directions agree by construction.
/// </summary>
public static class RprConversions
{
    // ---- entity type --------------------------------------------------------------------------------------------------

    public static long PackType(DisEntityType type) =>
        new DisTypeCode(type.Kind, type.Domain, type.Country, type.Category, type.Subcategory, type.Specific, type.Extra).Pack();

    public static DisEntityType ToRprType(long packed)
    {
        var code = DisTypeCode.Unpack(packed);
        return new DisEntityType(code.Kind, code.Domain, code.Country, code.Category, code.Subcategory, code.Specific, code.Extra);
    }

    // ---- enumerations -------------------------------------------------------------------------------------------------

    /// <summary>The RPR force of a DIS force id; an id the FOM enumeration does not have (4 .. 255) is Other.</summary>
    public static RprForceId ForceOf(long dis) => dis is >= 1 and <= 3 ? (RprForceId)(byte)dis : RprForceId.Other;

    /// <summary>The RPR damage state of a pivot damage level (0 none .. 3 destroyed; anything else is clamped).</summary>
    public static RprDamageState DamageOf(long level) => (RprDamageState)(byte)Math.Clamp(level, 0, 3);

    public static bool IsValid(RprDamageState state) => (byte)state <= (byte)RprDamageState.Destroyed;

    /// <summary>The RPR detonation result of a pivot (DIS numbered) result; a number the FOM does not have is Other.</summary>
    public static RprDetonationResult ResultOf(long dis) => dis is >= 0 and <= 6 ? (RprDetonationResult)(byte)dis : RprDetonationResult.Other;

    // ---- vectors ------------------------------------------------------------------------------------------------------

    public static Vec3 ToVec(WorldLocation v) => new(v.X, v.Y, v.Z);

    public static Vec3 ToVec(RprVector v) => new(v.X, v.Y, v.Z);

    public static Vec3 ToVec(RprOrientation v) => new(v.Psi, v.Theta, v.Phi);

    public static WorldLocation ToLocation(Vec3 v) => new(v.X, v.Y, v.Z);

    public static RprVector ToVector(Vec3 v) => new(v.X, v.Y, v.Z);

    public static RprOrientation ToOrientation(Vec3 v) => new(v.X, v.Y, v.Z);

    public static bool IsFinite(WorldLocation v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    public static bool IsFinite(RprVector v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    public static bool IsFinite(RprOrientation v) => double.IsFinite(v.Psi) && double.IsFinite(v.Theta) && double.IsFinite(v.Phi);

    /// <summary>The value a federate decodes of a float32 attribute (velocity, acceleration, orientation, angular velocity travel as single precision).</summary>
    public static Vec3 Single(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    // ---- events -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The event guid of a pivot event id: deterministic, so a fire and the detonation that the correlator gave the same
    /// <see cref="DisEventId"/> carry the same guid on the RPR side too.
    /// </summary>
    public static Guid EventGuid(DisEventId id) =>
        new(id.Event, unchecked((short)id.Site), unchecked((short)id.Application), 0x48, 0x52, 0x4F, 0x54, 0x45, 0x56, 0x54, 0x01);
}
