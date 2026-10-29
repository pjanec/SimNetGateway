using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// The pivot's kinematic state of an entity as a dead-reckoning basis, and the DIS-side threshold arithmetic (DD-006 sections 5 and
/// 6.1). Used by the DIS egress to predict a HROT entity's state to now, to remember what a receiver holds after an Entity State
/// PDU, and to measure how far that receiver's prediction has drifted from the truth. Pure functions over the pivot values.
/// </summary>
public static class DisKinematics
{
    /// <summary>
    /// Reads the entity's spatial attributes. Position and state time are required; velocity, acceleration, orientation and
    /// angular velocity default to zero, the algorithm to Static.
    /// </summary>
    public static bool TryRead(PivotSchema s, EntityRecord entity, out PredictionBasis basis, out int algorithm)
    {
        basis = default;
        algorithm = DrAlgorithms.Static;
        if (!entity.TryGet(s.Position, out var position) || !entity.TryGet(s.StateTime, out var stateTime))
        {
            return false;
        }

        basis = new PredictionBasis(
            position.AsVec3(),
            Vec(entity, s.Velocity),
            Vec(entity, s.Acceleration),
            Vec(entity, s.Orientation),
            Vec(entity, s.AngularVelocity),
            stateTime.AsDouble());
        if (entity.TryGet(s.DrAlgorithm, out var a))
        {
            algorithm = (int)a.AsInt();
        }

        return true;
    }

    /// <summary>The state at <paramref name="time"/>; an algorithm without a predictor holds the basis (as Static, with no velocity claim).</summary>
    public static PredictedState Predict(PredictorRegistry predictors, in PredictionBasis basis, int algorithm, double time)
    {
        if (predictors.TryGet(algorithm, out var predictor))
        {
            return predictor.Predict(in basis, time);
        }

        return new PredictedState(basis.Position, Vec3.Zero, basis.Orientation);
    }

    /// <summary>The angle in radians between two geocentric Euler orientations.</summary>
    public static double AngleBetween(Vec3 a, Vec3 b) =>
        Quaternion.AngleBetween(Quaternion.FromEuler(a.X, a.Y, a.Z), Quaternion.FromEuler(b.X, b.Y, b.Z));

    /// <summary>True when the receiver's prediction is farther from the truth than a threshold (strictly: equal is within).</summary>
    public static bool Exceeds(in PredictedState truth, in PredictedState held, double positionThresholdM, double orientationThresholdRad) =>
        (truth.Position - held.Position).Length > positionThresholdM ||
        AngleBetween(truth.Orientation, held.Orientation) > orientationThresholdRad;

    private static Vec3 Vec(EntityRecord entity, AttrId attr) => entity.TryGet(attr, out var v) ? v.AsVec3() : Vec3.Zero;
}
