using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// The thresholds of a dead-reckoned send policy: a protocol side that publishes full updates sends again when the receiver's
/// dead-reckoning prediction of the last update drifted beyond <see cref="PositionThresholdM"/> or
/// <see cref="OrientationThresholdRad"/> from the truth, or when nothing was sent for <see cref="HeartbeatS"/> (DD-009 section 3
/// item 1; the IEEE 1278.1 dead-reckoning threshold and heartbeat).
/// </summary>
public readonly record struct SendPolicySettings(double PositionThresholdM, double OrientationThresholdRad, double HeartbeatS)
{
    /// <summary>The same settings with the orientation threshold given in degrees (as the configuration file has it).</summary>
    public static SendPolicySettings FromDegrees(double positionThresholdM, double orientationThresholdDeg, double heartbeatS) =>
        new(positionThresholdM, orientationThresholdDeg * Math.PI / 180.0, heartbeatS);
}

/// <summary>
/// The dead-reckoning model a receiver holds after the last full update (the values as they went on the wire, so rounding is
/// part of the model) and the time at which it was sent. The caller keeps one per published entity (in its own entity table, so
/// the model lives and dies with the entity) and passes it to <see cref="DeadReckonedSendPolicy"/>.
/// </summary>
public struct SentModel
{
    public Vec3 Position;
    public Vec3 Velocity;
    public Vec3 Acceleration;
    public Vec3 Orientation;
    public Vec3 AngularVelocity;
    public int Algorithm;

    /// <summary>Time of the send (also the state time of the held model), seconds on the caller's clock.</summary>
    public double Time;

    /// <summary>Builds the model a receiver holds after a send of the given values at <paramref name="now"/>.</summary>
    public static SentModel Of(Vec3 position, Vec3 velocity, Vec3 acceleration, Vec3 orientation, Vec3 angularVelocity, int algorithm, double now) =>
        new()
        {
            Position = position,
            Velocity = velocity,
            Acceleration = acceleration,
            Orientation = orientation,
            AngularVelocity = angularVelocity,
            Algorithm = algorithm,
            Time = now,
        };
}

/// <summary>
/// The send policy of a full-update protocol side, shared by every side that publishes dead-reckoned entities (DD-009 section 3
/// item 1, R-114): is a send due now? A send is due when the dead-reckoning prediction of the held <see cref="SentModel"/> is
/// farther from the entity's predicted truth than a threshold (<see cref="Drifted"/>), or when nothing was sent for the heartbeat
/// period (<see cref="HeartbeatDue"/>); <see cref="NeedsSend"/> is the disjunction. The policy is stateless apart from its
/// configuration: the caller owns the per-entity <see cref="SentModel"/> and the decision of what a send contains. Vocabulary-free:
/// the attribute ids come from the gateway's schema (the same <see cref="DrAttributeSet"/> the dead-reckoning processor takes), the
/// predictors from the <see cref="PredictorRegistry"/> (R-060).
/// </summary>
public sealed class DeadReckonedSendPolicy
{
    private readonly DrAttributeSet _attributes;

    public DeadReckonedSendPolicy(DrAttributeSet attributes, SendPolicySettings settings, PredictorRegistry predictors)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(predictors);
        if (!(settings.PositionThresholdM >= 0) || !(settings.OrientationThresholdRad >= 0) || !(settings.HeartbeatS > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Thresholds must be >= 0 and the heartbeat > 0.");
        }

        _attributes = attributes;
        Settings = settings;
        Predictors = predictors;
    }

    public SendPolicySettings Settings { get; }

    public PredictorRegistry Predictors { get; }

    /// <summary>
    /// Reads the entity's spatial attributes. Position and state time are required; velocity, acceleration, orientation and
    /// angular velocity default to zero, the algorithm to <paramref name="defaultAlgorithm"/> (DIS numbering: 1 = Static).
    /// </summary>
    public bool TryRead(EntityRecord entity, out PredictionBasis basis, out int algorithm, int defaultAlgorithm = 1)
    {
        var a = _attributes;
        basis = default;
        algorithm = defaultAlgorithm;
        if (!entity.TryGet(a.Position, out var position) || !entity.TryGet(a.StateTime, out var stateTime))
        {
            return false;
        }

        basis = new PredictionBasis(
            position.AsVec3(),
            Vec(entity, a.Velocity),
            Vec(entity, a.Acceleration),
            Vec(entity, a.Orientation),
            Vec(entity, a.AngularVelocity),
            stateTime.AsDouble());
        if (entity.TryGet(a.Algorithm, out var alg))
        {
            algorithm = (int)alg.AsInt();
        }

        return true;
    }

    /// <summary>The state at <paramref name="time"/>; an algorithm without a predictor holds the basis (as Static, with no velocity claim).</summary>
    public PredictedState Predict(in PredictionBasis basis, int algorithm, double time)
    {
        if (Predictors.TryGet(algorithm, out var predictor))
        {
            return predictor.Predict(in basis, time);
        }

        return new PredictedState(basis.Position, Vec3.Zero, basis.Orientation);
    }

    /// <summary>The state a receiver predicts at <paramref name="time"/> from the held model.</summary>
    public PredictedState Held(in SentModel model, double time)
    {
        var basis = new PredictionBasis(model.Position, model.Velocity, model.Acceleration, model.Orientation, model.AngularVelocity, model.Time);
        return Predict(in basis, model.Algorithm, time);
    }

    /// <summary>The angle in radians between two geocentric Euler orientations.</summary>
    public static double AngleBetween(Vec3 a, Vec3 b) =>
        Quaternion.AngleBetween(Quaternion.FromEuler(a.X, a.Y, a.Z), Quaternion.FromEuler(b.X, b.Y, b.Z));

    /// <summary>True when the receiver's prediction is farther from the truth than a threshold (strictly: equal is within).</summary>
    public bool Exceeds(in PredictedState truth, in PredictedState held) =>
        (truth.Position - held.Position).Length > Settings.PositionThresholdM ||
        AngleBetween(truth.Orientation, held.Orientation) > Settings.OrientationThresholdRad;

    /// <summary>
    /// True when the held model's prediction is beyond a threshold from the entity's state predicted to <paramref name="now"/>;
    /// false when the entity has no readable spatial state.
    /// </summary>
    public bool Drifted(EntityRecord entity, in SentModel model, double now)
    {
        if (!TryRead(entity, out var basis, out int algorithm))
        {
            return false;
        }

        var truth = Predict(in basis, algorithm, now);
        var held = Held(in model, now);
        return Exceeds(in truth, in held);
    }

    /// <summary>True when nothing was sent for the heartbeat period (an update sent exactly one heartbeat ago is due).</summary>
    public bool HeartbeatDue(in SentModel model, double now) => now - model.Time >= Settings.HeartbeatS;

    /// <summary>A send is due: the heartbeat, or drift beyond a threshold.</summary>
    public bool NeedsSend(EntityRecord entity, in SentModel model, double now) =>
        HeartbeatDue(in model, now) || Drifted(entity, in model, now);

    private static Vec3 Vec(EntityRecord entity, AttrId attr) => entity.TryGet(attr, out var v) ? v.AsVec3() : Vec3.Zero;
}
