using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>The spatial values of a full update as they go on the wire of a DIS-aligned side (DIS Entity State PDU, RPR spatial attributes).</summary>
public readonly record struct WireKinematics(Vec3 Position, Vec3 Velocity, Vec3 Acceleration, Vec3 Orientation, Vec3 AngularVelocity, int Algorithm);

/// <summary>
/// What a full update of a hrot entity carries, shared by the DIS and the RPR publication (DD-009 section 3: one way to project the
/// entity to now): the position and orientation dead-reckoned from the pivot's state time to now, the velocity as the wire
/// carries it (for the body-axis algorithms the body-axis velocity, which the predictor's ECEF velocity is not), acceleration
/// and angular velocity as stored.
/// </summary>
public static class DrWire
{
    public static bool TryProject(DeadReckonedSendPolicy policy, EntityRecord entity, double now, out WireKinematics kinematics)
    {
        if (!policy.TryRead(entity, out var basis, out int algorithm, DrAlgorithms.Static))
        {
            kinematics = default;
            return false;
        }

        var predicted = policy.Predict(in basis, algorithm, now);
        double dt = now - basis.StateTime;
        var velocity = predicted.Velocity;
        if (DrAlgorithms.IsBodyAxis(algorithm))
        {
            // the wire carries body-axis velocity for the body-axis algorithms (the predictor's is rotated into ECEF)
            velocity = algorithm >= 8 ? basis.Velocity + basis.Acceleration * dt : basis.Velocity;
        }

        kinematics = new WireKinematics(predicted.Position, velocity, basis.Acceleration, predicted.Orientation, basis.AngularVelocity, algorithm);
        return true;
    }
}

/// <summary>Which hrot entities may be published to a protocol side (DIS or HLA): what a full update needs, and the unit rule (R-088).</summary>
public static class Publishable
{
    /// <summary>DIS entity kind 11: HROT units, not a SISO kind; not sent unless <c>shared.forwardUnitEntities</c> (R-088).</summary>
    public const int UnitKind = 11;

    /// <summary>The entity has what a published entity needs (type and position with their time), the origin is valid and a unit is allowed.</summary>
    public static bool IsEligible(PivotSchema schema, HrotDisServices services, EntityRecord entity)
    {
        if (!services.Geo.IsValid || !entity.TryGet(schema.EntityType, out var type) || !entity.Has(schema.Position) || !entity.Has(schema.StateTime))
        {
            return false;
        }

        return services.Config.Shared.ForwardUnitEntities || DisTypeCode.Unpack(type.AsInt()).Kind != UnitKind;
    }
}
