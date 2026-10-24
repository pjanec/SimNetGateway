using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// What a predictor extrapolates from: the last known state, all in the frames of the pivot (DD-001 section 9).
/// </summary>
/// <param name="Position">ECEF position in metres.</param>
/// <param name="Velocity">Velocity in m/s: ECEF for the world-axis algorithms, body axes for the body-axis ones.</param>
/// <param name="Acceleration">Acceleration in m/s^2, in the same axes as the velocity.</param>
/// <param name="Orientation">Geocentric Euler angles psi/theta/phi in radians (see <see cref="SimNetGateway.Common.Orientation"/>).</param>
/// <param name="AngularVelocity">Angular velocity in body axes, rad/s.</param>
/// <param name="StateTime">Simulation time at which the values were valid, in seconds.</param>
public readonly record struct PredictionBasis(
    Vec3 Position, Vec3 Velocity, Vec3 Acceleration, Vec3 Orientation, Vec3 AngularVelocity, double StateTime);

/// <summary>A predicted state: ECEF position and velocity, and geocentric Euler orientation.</summary>
public readonly record struct PredictedState(Vec3 Position, Vec3 Velocity, Vec3 Orientation);

/// <summary>
/// A dead-reckoning strategy (R-060, DD-004 P-37). Implementations must be allocation-free and side-effect free.
/// </summary>
public interface IPredictor
{
    /// <summary>The key in the <see cref="PredictorRegistry"/>; the value of the pivot's algorithm attribute.</summary>
    int StrategyId { get; }

    string Name { get; }

    /// <summary>
    /// Predicts the state at the absolute simulation <paramref name="time"/>; <c>dt = time - basis.StateTime</c>.
    /// A negative dt extrapolates backwards.
    /// </summary>
    PredictedState Predict(in PredictionBasis basis, double time);
}
