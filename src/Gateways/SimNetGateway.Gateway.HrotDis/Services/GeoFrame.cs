using SimNetGateway.Common;
using SimNetGateway.Common.Geo;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// The geodetic frame service (DD-006 section 5, DD-014): HROT's local metres are UTM grid metres minus an origin (geo reference protocol
/// version 2). Converts between local points / vectors / HROT body quaternions and the ECEF / geocentric Euler values of the pivot. Positions
/// are exact (the projection); directions are rotated by the grid convergence at the point they belong to, so every direction conversion takes
/// that local position (the UTM scale factor, under 0.05 percent, is ignored for vectors).
/// Valid only after a UTM <see cref="HrotOrigin"/> arrived; a local-only terrain (<see cref="HrotOrigin.FrameNone"/>) is accepted but not valid.
/// An origin of exactly (0, 0, 0) means "not set" unless <c>hrot.allowZeroOrigin</c>.
/// </summary>
public sealed class GeoFrame
{
    private readonly bool _allowZeroOrigin;
    private UtmFrame? _utm;

    public GeoFrame(bool allowZeroOrigin = false) => _allowZeroOrigin = allowZeroOrigin;

    /// <summary>A UTM frame is known; nothing positional enters or leaves before.</summary>
    public bool IsValid => _utm is not null;

    /// <summary>The terrain is local-only (frame 2 received): its metres have no place on the Earth, nothing positional crosses.</summary>
    public bool IsLocalOnly { get; private set; }

    /// <summary>The generation of the current origin (0 until one was accepted).</summary>
    public int Generation { get; private set; }

    /// <summary>The origin as received (a UTM or a none frame); null until one was accepted.</summary>
    public HrotOrigin? Origin { get; private set; }

    /// <summary>The UTM frame of the current origin.</summary>
    public UtmFrame Utm => _utm ?? throw new InvalidOperationException("The geodetic frame is not valid yet.");

    /// <summary>
    /// Takes the geo reference. Returns true when the frame changed (a first one, a different one, or a switch to / from a local-only
    /// terrain: a re-base); false with <paramref name="problem"/> null when it equals the current one, and false with the reason when it is not
    /// acceptable (a protocol version other than 2, an unknown frame, a bad origin; the previous frame stays).
    /// </summary>
    public bool TryApply(in HrotOrigin origin, out string? problem)
    {
        problem = null;
        if (origin.ProtocolVersion != HrotOrigin.CurrentProtocolVersion)
        {
            problem = $"geo reference protocol version {origin.ProtocolVersion} is not supported (expected {HrotOrigin.CurrentProtocolVersion})";
            return false;
        }

        if (origin.Frame != HrotOrigin.FrameUtm && origin.Frame != HrotOrigin.FrameNone)
        {
            problem = $"geo reference frame {origin.Frame} is unknown (expected {HrotOrigin.FrameUtm} = UTM or {HrotOrigin.FrameNone} = none)";
            return false;
        }

        if (origin.Frame == HrotOrigin.FrameUtm)
        {
            problem = Check(origin);
            if (problem is not null)
            {
                return false;
            }
        }

        if (Origin is { } current && current == origin)
        {
            return false;
        }

        if (origin.Frame == HrotOrigin.FrameNone)
        {
            _utm = null;
            IsLocalOnly = true;
        }
        else
        {
            _utm = UtmFrame.AtOrigin(origin.LatitudeDeg, origin.LongitudeDeg, origin.AltitudeM, origin.UtmZoneOverride, origin.UtmSouthOverride);
            IsLocalOnly = false;
        }

        Origin = origin;
        Generation = origin.Generation;
        return true;
    }

    private string? Check(in HrotOrigin origin)
    {
        if (!double.IsFinite(origin.LatitudeDeg) || !double.IsFinite(origin.LongitudeDeg) || !double.IsFinite(origin.AltitudeM))
        {
            return "origin is not finite";
        }

        if (origin.LatitudeDeg < -90.0 || origin.LatitudeDeg > 90.0 || origin.LongitudeDeg < -180.0 || origin.LongitudeDeg > 180.0)
        {
            return $"origin latitude {origin.LatitudeDeg} / longitude {origin.LongitudeDeg} out of range";
        }

        if (origin.UtmZoneOverride < 0 || origin.UtmZoneOverride > 60)
        {
            return $"UTM zone override {origin.UtmZoneOverride} out of range (0 = none, 1 .. 60)";
        }

        if (!_allowZeroOrigin && origin.LatitudeDeg == 0.0 && origin.LongitudeDeg == 0.0 && origin.AltitudeM == 0.0)
        {
            return "origin (0, 0, 0) means 'not set' (hrot.allowZeroOrigin is false)";
        }

        return null;
    }

    // ---- points -----------------------------------------------------------------------------------------------------

    public Vec3 PointToEcef(HrotVec3 p) => Utm.PointToEcef(ToVec(p));

    public HrotVec3 PointFromEcef(Vec3 e) => FromVec(Utm.PointFromEcef(e));

    // ---- directions: rotated by the convergence at the point they belong to ------------------------------------------

    /// <summary>The tangent frame (true east / north / up) at a local point and the rotation from grid ENU to true ENU there.</summary>
    private (LocalTangentFrame Tangent, Matrix3 GridToTrue) At(HrotVec3 at)
    {
        var utm = Utm;
        var local = ToVec(at);
        return (new LocalTangentFrame(utm.ToGeodetic(local)), Matrix3.RotationZ(-utm.ConvergenceRad(local)));
    }

    /// <summary>A vector in grid ENU components at <paramref name="at"/> to ECEF components.</summary>
    public Vec3 VectorToEcef(HrotVec3 at, HrotVec3 v)
    {
        var (tangent, gridToTrue) = At(at);
        return tangent.VectorToEcef(gridToTrue * ToVec(v));
    }

    /// <summary>A vector in ECEF components at <paramref name="at"/> to grid ENU components.</summary>
    public HrotVec3 VectorFromEcef(HrotVec3 at, Vec3 v)
    {
        var (tangent, gridToTrue) = At(at);
        return FromVec(gridToTrue.Transpose() * tangent.VectorFromEcef(v));
    }

    /// <summary>HROT body quaternion (body-to-grid-ENU, FLU) at <paramref name="at"/> to the geocentric Euler angles (psi, theta, phi) of the FRD body.</summary>
    public Vec3 ToEuler(HrotVec3 at, HrotQuat q)
    {
        var (tangent, gridToTrue) = At(at);
        var trueEnu = Quaternion.FromMatrix(gridToTrue * ToQuaternion(q).ToMatrix());
        return LocalToGeocentricOrientation.ToDisEuler(trueEnu, tangent);
    }

    /// <summary>Geocentric Euler angles at <paramref name="at"/> to the HROT body quaternion (unit, w &gt;= 0).</summary>
    public HrotQuat FromEuler(HrotVec3 at, Vec3 psiThetaPhi)
    {
        var (tangent, gridToTrue) = At(at);
        var trueEnu = LocalToGeocentricOrientation.FromDisEuler(psiThetaPhi, tangent);
        var q = Quaternion.FromMatrix(gridToTrue.Transpose() * trueEnu.ToMatrix());
        return new HrotQuat(q.X, q.Y, q.Z, q.W);
    }

    /// <summary>
    /// Angular velocity in grid ENU axes to the body (FRD) axes of the pivot. Done through true ENU like the other direction conversions; the
    /// convergence cancels between the rotated velocity and the rotated quaternion.
    /// </summary>
    public Vec3 AngularVelocityToBody(HrotVec3 at, HrotQuat q, HrotVec3 w)
    {
        var (_, gridToTrue) = At(at);
        var trueEnu = Quaternion.FromMatrix(gridToTrue * ToQuaternion(q).ToMatrix());
        return LocalToGeocentricOrientation.AngularVelocityEnuToBodyFrd(trueEnu, gridToTrue * ToVec(w));
    }

    /// <summary>Angular velocity in body (FRD) axes to grid ENU axes at <paramref name="at"/>.</summary>
    public HrotVec3 AngularVelocityFromBody(HrotVec3 at, HrotQuat q, Vec3 w)
    {
        var (_, gridToTrue) = At(at);
        var trueEnu = Quaternion.FromMatrix(gridToTrue * ToQuaternion(q).ToMatrix());
        return FromVec(gridToTrue.Transpose() * LocalToGeocentricOrientation.AngularVelocityBodyFrdToEnu(trueEnu, w));
    }

    // ---- plain conversions ------------------------------------------------------------------------------------------

    public static Vec3 ToVec(HrotVec3 v) => new(v.X, v.Y, v.Z);

    public static HrotVec3 FromVec(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The unit quaternion (Common: w first) of a HROT quaternion; a zero quaternion is the identity.</summary>
    public static Quaternion ToQuaternion(HrotQuat q) => new Quaternion(q.W, q.X, q.Y, q.Z).Normalized();

    /// <summary>True when every component is a finite number.</summary>
    public static bool IsFinite(HrotVec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    public static bool IsFinite(HrotQuat q) => double.IsFinite(q.X) && double.IsFinite(q.Y) && double.IsFinite(q.Z) && double.IsFinite(q.W);
}
