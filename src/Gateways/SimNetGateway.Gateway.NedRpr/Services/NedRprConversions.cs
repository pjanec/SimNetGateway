using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Static, allocation-free conversions of the NED-RPR gateway, built on Common only (Wgs84, Orientation, Velocity,
/// Angles). Geo conventions:
/// <list type="bullet">
/// <item>NED angles are degrees: heading from north clockwise, pitch up positive, roll right positive; altitude and
/// speed in metres and m/s, speed along heading and pitch.</item>
/// <item>The pivot is ECEF metres, geocentric Euler angles (psi, theta, phi) in radians and ECEF m/s.</item>
/// <item>Back from the pivot, heading is in [0, 360), pitch in [-90, 90], roll in (-180, 180]. At speed 0 the
/// velocity has no direction, so nothing is derived from it: heading, pitch and roll always come from the
/// orientation and the speed from the velocity magnitude; a zero velocity is therefore a stopped entity whatever
/// its heading was.</item>
/// <item>On the polar axis the longitude is undefined and comes back as 0; the local frame (and so the heading) there is
/// the one of the 0 meridian.</item>
/// </list>
/// The numbering of damage, force and detonation result is the same on all three sides (NED, pivot, RPR); each mapping
/// is a range-checked identity that returns false outside its valid range.
/// </summary>
public static class NedRprConversions
{
    /// <summary>The valid DIS entity numbers of the gateway's own entities: 0 and 65535 are reserved.</summary>
    public const int MinEntityNumber = 1;

    public const int MaxEntityNumber = 65534;

    // ---- geo ------------------------------------------------------------------------------------------------------

    /// <summary>Geodetic degrees and metres to ECEF metres.</summary>
    public static Vec3 GeodeticToEcef(double latitudeDeg, double longitudeDeg, double altitude) =>
        Wgs84.ToEcef(Geodetic.FromDegrees(latitudeDeg, longitudeDeg, altitude));

    /// <summary>ECEF metres to geodetic degrees (longitude in (-180, 180]) and altitude.</summary>
    public static void EcefToGeodetic(Vec3 ecef, out double latitudeDeg, out double longitudeDeg, out double altitude)
    {
        var g = Wgs84.ToGeodetic(ecef);
        latitudeDeg = g.LatitudeDegrees;
        longitudeDeg = g.LongitudeDegrees;
        altitude = g.Altitude;
    }

    /// <summary>A NED geo-spatial state to the pivot: ECEF position, geocentric orientation and ECEF velocity.</summary>
    public static void GeoToPivot(double latitudeDeg, double longitudeDeg, double altitude, double headingDeg,
        double pitchDeg, double rollDeg, double speed, out Vec3 position, out Vec3 orientation, out Vec3 velocity)
    {
        var at = Geodetic.FromDegrees(latitudeDeg, longitudeDeg, altitude);
        double heading = Angles.ToRadians(headingDeg);
        double pitch = Angles.ToRadians(pitchDeg);
        position = Wgs84.ToEcef(at);
        orientation = Orientation.ToGeocentric(heading, pitch, Angles.ToRadians(rollDeg), at);
        velocity = Velocity.ToEcef(speed, heading, pitch, at);
    }

    /// <inheritdoc cref="GeoToPivot(double,double,double,double,double,double,double,out Vec3,out Vec3,out Vec3)"/>
    public static void GeoToPivot(in NedGeoSpatial g, out Vec3 position, out Vec3 orientation, out Vec3 velocity) =>
        GeoToPivot(g.LatitudeDeg, g.LongitudeDeg, g.Altitude, g.HeadingDeg, g.PitchDeg, g.RollDeg, g.Speed,
            out position, out orientation, out velocity);

    /// <summary>The pivot state of an entity back to a NED geo-spatial message (see the class remarks for the conventions).</summary>
    public static NedGeoSpatial PivotToGeo(ulong id, Vec3 position, Vec3 orientation, Vec3 velocity)
    {
        var at = Wgs84.ToGeodetic(position);
        var (heading, pitch, roll) = Orientation.ToLocal(orientation, at);
        double speed = velocity.Length;
        return new NedGeoSpatial(id, at.LatitudeDegrees, at.LongitudeDegrees, at.Altitude,
            NormalizeHeadingDeg(Angles.ToDegrees(heading)), Angles.ToDegrees(pitch), Angles.ToDegrees(roll), speed);
    }

    /// <summary>The local heading [0, 360) of a geocentric orientation at an ECEF position (for the display).</summary>
    public static double LocalHeadingDeg(Vec3 position, Vec3 orientation)
    {
        var (heading, _, _) = Orientation.ToLocal(orientation, Wgs84.ToGeodetic(position));
        return NormalizeHeadingDeg(Angles.ToDegrees(heading));
    }

    /// <summary>Any angle in degrees to [0, 360).</summary>
    public static double NormalizeHeadingDeg(double degrees)
    {
        double d = degrees % 360.0;
        if (d < 0)
        {
            d += 360.0;
        }

        // a tiny negative input rounds d + 360 up to exactly 360
        return d >= 360.0 ? 0.0 : d;
    }

    // ---- value structs ---------------------------------------------------------------------------------------------

    public static WorldLocation ToWorldLocation(Vec3 v) => new(v.X, v.Y, v.Z);

    public static Vec3 ToVec3(WorldLocation w) => new(w.X, w.Y, w.Z);

    public static RprOrientation ToRprOrientation(Vec3 psiThetaPhi) => new(psiThetaPhi.X, psiThetaPhi.Y, psiThetaPhi.Z);

    public static Vec3 ToVec3(RprOrientation o) => new(o.Psi, o.Theta, o.Phi);

    public static RprVector ToRprVector(Vec3 v) => new(v.X, v.Y, v.Z);

    public static Vec3 ToVec3(RprVector v) => new(v.X, v.Y, v.Z);

    // ---- damage (pivot scale = NED level = RPR state, 0..3) ---------------------------------------------------------

    public static bool IsValidDamage(int level) => level is >= 0 and <= 3;

    public static bool TryDamageToRpr(int level, out RprDamageState state)
    {
        state = default;
        if (!IsValidDamage(level))
        {
            return false;
        }

        state = (RprDamageState)level;
        return true;
    }

    public static bool TryDamageFromRpr(RprDamageState state, out int level)
    {
        level = (byte)state;
        if (IsValidDamage(level))
        {
            return true;
        }

        level = 0;
        return false;
    }

    // ---- force (pivot = NedForce numbering) -------------------------------------------------------------------------

    public static bool TryForceToPivot(NedForce force, out int pivot)
    {
        pivot = (byte)force;
        if (pivot <= (int)NedForce.Neutral)
        {
            return true;
        }

        pivot = 0;
        return false;
    }

    public static bool TryForceFromPivot(int pivot, out NedForce force)
    {
        force = default;
        if (pivot < 0 || pivot > (int)NedForce.Neutral)
        {
            return false;
        }

        force = (NedForce)pivot;
        return true;
    }

    public static bool TryForceToRpr(int pivot, out RprForceId force)
    {
        force = default;
        if (pivot < 0 || pivot > (int)RprForceId.Neutral)
        {
            return false;
        }

        force = (RprForceId)pivot;
        return true;
    }

    public static bool TryForceFromRpr(RprForceId force, out int pivot)
    {
        pivot = (byte)force;
        if (pivot <= (int)RprForceId.Neutral)
        {
            return true;
        }

        pivot = 0;
        return false;
    }

    // ---- detonation result (pivot and NED numbering identical; RPR takes the FOM numbering, R-105) ---------------------

    public static bool IsValidDetonationResult(int result) => result is >= 0 and <= 3;

    public static bool TryResultToPivot(NedDetonationResult result, out int pivot)
    {
        pivot = (byte)result;
        if (IsValidDetonationResult(pivot))
        {
            return true;
        }

        pivot = 0;
        return false;
    }

    public static bool TryResultFromPivot(int pivot, out NedDetonationResult result)
    {
        result = default;
        if (!IsValidDetonationResult(pivot))
        {
            return false;
        }

        result = (NedDetonationResult)pivot;
        return true;
    }

    /// <summary>Pivot (Other 0, EntityImpact 1, GroundImpact 2, Detonation 3) to the FOM numbering (0, 1, 3, 5).</summary>
    public static bool TryResultToRpr(int pivot, out RprDetonationResult result)
    {
        switch (pivot)
        {
            case 0: result = RprDetonationResult.Other; return true;
            case 1: result = RprDetonationResult.EntityImpact; return true;
            case 2: result = RprDetonationResult.GroundImpact; return true;
            case 3: result = RprDetonationResult.Detonation; return true;
            default: result = default; return false;
        }
    }

    /// <summary>The inverse of <see cref="TryResultToRpr"/>; the FOM codes the pivot has no counterpart for are rejected.</summary>
    public static bool TryResultFromRpr(RprDetonationResult result, out int pivot)
    {
        switch (result)
        {
            case RprDetonationResult.Other: pivot = 0; return true;
            case RprDetonationResult.EntityImpact: pivot = 1; return true;
            case RprDetonationResult.GroundImpact: pivot = 2; return true;
            case RprDetonationResult.Detonation: pivot = 3; return true;
            default: pivot = 0; return false;
        }
    }

    // ---- DIS ids ---------------------------------------------------------------------------------------------------

    public static bool IsValidEntityNumber(int number) => number is >= MinEntityNumber and <= MaxEntityNumber;

    /// <summary>The DIS entity id of an allocated entity number; the number must be valid (1..65534).</summary>
    public static bool TryMakeEntityId(ushort site, ushort application, int number, out DisEntityId id)
    {
        id = default;
        if (!IsValidEntityNumber(number))
        {
            return false;
        }

        id = new DisEntityId(site, application, (ushort)number);
        return true;
    }

    /// <summary>The entity number of a DIS id (the key the number pool allocated).</summary>
    public static int EntityNumber(DisEntityId id) => id.Entity;
}
