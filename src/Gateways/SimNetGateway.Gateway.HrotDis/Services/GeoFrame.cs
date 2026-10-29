using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// The geodetic frame service (DD-006 section 5, DD-007 section 3 item 8): the origin of HROT's local ENU frame, its
/// generation, and the conversions between local ENU / HROT body quaternions and the ECEF / geocentric Euler values of the pivot.
/// Invalid until a valid <see cref="HrotOrigin"/> arrived; an origin of exactly (0, 0, 0) means "not set" unless
/// <c>hrot.allowZeroOrigin</c>. Orientation conversions use the frame at the origin (the flat-world assumption of DD-006 section 5).
/// </summary>
public sealed class GeoFrame
{
    private readonly bool _allowZeroOrigin;
    private LocalTangentFrame? _frame;

    public GeoFrame(bool allowZeroOrigin = false) => _allowZeroOrigin = allowZeroOrigin;

    /// <summary>A valid origin has arrived; nothing positional enters or leaves before.</summary>
    public bool IsValid => _frame is not null;

    /// <summary>The generation of the current origin (0 while invalid).</summary>
    public int Generation { get; private set; }

    /// <summary>The origin as received; null while invalid.</summary>
    public HrotOrigin? Origin { get; private set; }

    /// <summary>The tangent frame of the current origin.</summary>
    public LocalTangentFrame Frame => _frame ?? throw new InvalidOperationException("The geodetic origin is not valid yet.");

    /// <summary>
    /// Takes the origin. Returns true when the frame changed (a first origin or a different one: a re-base); false with
    /// <paramref name="problem"/> null when it equals the current one, and false with the reason when it is not acceptable
    /// (the previous frame stays).
    /// </summary>
    public bool TryApply(in HrotOrigin origin, out string? problem)
    {
        problem = null;
        if (!double.IsFinite(origin.LatitudeDeg) || !double.IsFinite(origin.LongitudeDeg) || !double.IsFinite(origin.AltitudeM))
        {
            problem = "origin is not finite";
            return false;
        }

        if (origin.LatitudeDeg < -90.0 || origin.LatitudeDeg > 90.0 || origin.LongitudeDeg < -180.0 || origin.LongitudeDeg > 180.0)
        {
            problem = $"origin latitude {origin.LatitudeDeg} / longitude {origin.LongitudeDeg} out of range";
            return false;
        }

        if (!_allowZeroOrigin && origin.LatitudeDeg == 0.0 && origin.LongitudeDeg == 0.0 && origin.AltitudeM == 0.0)
        {
            problem = "origin (0, 0, 0) means 'not set' (hrot.allowZeroOrigin is false)";
            return false;
        }

        if (Origin is { } current && current == origin)
        {
            return false;
        }

        _frame = new LocalTangentFrame(Geodetic.FromDegrees(origin.LatitudeDeg, origin.LongitudeDeg, origin.AltitudeM));
        Origin = origin;
        Generation = origin.Generation;
        return true;
    }

    // ---- points and vectors -----------------------------------------------------------------------------------------

    public Vec3 PointToEcef(HrotVec3 enu) => Frame.PointToEcef(ToVec(enu));

    public HrotVec3 PointFromEcef(Vec3 ecef) => FromVec(Frame.PointFromEcef(ecef));

    public Vec3 VectorToEcef(HrotVec3 enu) => Frame.VectorToEcef(ToVec(enu));

    public HrotVec3 VectorFromEcef(Vec3 ecef) => FromVec(Frame.VectorFromEcef(ecef));

    // ---- orientation ------------------------------------------------------------------------------------------------

    /// <summary>HROT body quaternion (body-to-ENU, FLU) to the geocentric Euler angles (psi, theta, phi) of the FRD body.</summary>
    public Vec3 ToEuler(HrotQuat rotation) => LocalToGeocentricOrientation.ToDisEuler(ToQuaternion(rotation), Frame);

    /// <summary>Geocentric Euler angles to the HROT body quaternion (unit, w &gt;= 0).</summary>
    public HrotQuat FromEuler(Vec3 psiThetaPhi)
    {
        var q = LocalToGeocentricOrientation.FromDisEuler(psiThetaPhi, Frame);
        return new HrotQuat(q.X, q.Y, q.Z, q.W);
    }

    /// <summary>Angular velocity in ENU axes to the body (FRD) axes of the pivot.</summary>
    public Vec3 AngularVelocityToBody(HrotQuat rotation, HrotVec3 omegaEnu) =>
        LocalToGeocentricOrientation.AngularVelocityEnuToBodyFrd(ToQuaternion(rotation), ToVec(omegaEnu));

    /// <summary>Angular velocity in body (FRD) axes to ENU axes.</summary>
    public HrotVec3 AngularVelocityFromBody(HrotQuat rotation, Vec3 omegaBody) =>
        FromVec(LocalToGeocentricOrientation.AngularVelocityBodyFrdToEnu(ToQuaternion(rotation), omegaBody));

    // ---- plain conversions ------------------------------------------------------------------------------------------

    public static Vec3 ToVec(HrotVec3 v) => new(v.X, v.Y, v.Z);

    public static HrotVec3 FromVec(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The unit quaternion (Common: w first) of a HROT quaternion; a zero quaternion is the identity.</summary>
    public static Quaternion ToQuaternion(HrotQuat q) => new Quaternion(q.W, q.X, q.Y, q.Z).Normalized();

    /// <summary>True when every component is a finite number.</summary>
    public static bool IsFinite(HrotVec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    public static bool IsFinite(HrotQuat q) => double.IsFinite(q.X) && double.IsFinite(q.Y) && double.IsFinite(q.Z) && double.IsFinite(q.W);
}
