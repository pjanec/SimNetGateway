using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// IEEE 1278.1 dead-reckoning algorithm numbers (DIS and RPR). The strategy id of a DIS predictor is this number.
/// </summary>
public enum DisDeadReckoning
{
    Other = 0,
    Static = 1,
    Fpw = 2,
    Rpw = 3,
    Rvw = 4,
    Fvw = 5,
    Fpb = 6,
    Rpb = 7,
    Rvb = 8,
    Fvb = 9,
}

/// <summary>
/// One DIS dead-reckoning algorithm (IEEE 1278.1 Annex E): Static, FPW, RPW, RVW, FVW and the body-axis FPB, RPB, RVB,
/// FVB. "Other" (0) names no algorithm and has no predictor.
/// <para>
/// F = fixed orientation, R = rotating (constant angular velocity in body axes); P = constant velocity, V = constant
/// acceleration in addition; W = velocity and acceleration in world (ECEF) axes, B = in body axes. The body-axis
/// variants rotate the body-axis vectors into ECEF with the orientation; for the rotating ones the body turns while
/// moving, so the position is the exact integral of <c>R0 * exp(w t) * (v + a t)</c> (closed forms below), and the
/// predicted velocity is the body-axis velocity rotated by the predicted orientation. The orientation of a rotating
/// variant is <c>R0 * exp([w] dt)</c> converted back to Euler angles.
/// </para>
/// </summary>
public sealed class DisPredictor : IPredictor
{
    /// <summary>The algorithms that have a predictor, in numeric order.</summary>
    public static readonly IReadOnlyList<DisDeadReckoning> Supported = new[]
    {
        DisDeadReckoning.Static, DisDeadReckoning.Fpw, DisDeadReckoning.Rpw, DisDeadReckoning.Rvw, DisDeadReckoning.Fvw,
        DisDeadReckoning.Fpb, DisDeadReckoning.Rpb, DisDeadReckoning.Rvb, DisDeadReckoning.Fvb,
    };

    private readonly DisDeadReckoning _algorithm;

    public DisPredictor(DisDeadReckoning algorithm)
    {
        if (algorithm < DisDeadReckoning.Static || algorithm > DisDeadReckoning.Fvb)
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "No predictor exists for this DIS algorithm.");
        }

        _algorithm = algorithm;
        Name = algorithm == DisDeadReckoning.Static ? "Static" : algorithm.ToString().ToUpperInvariant();
    }

    public int StrategyId => (int)_algorithm;

    public string Name { get; }

    public PredictedState Predict(in PredictionBasis basis, double time)
    {
        double dt = time - basis.StateTime;
        var p = basis.Position;
        var v = basis.Velocity;
        var a = basis.Acceleration;
        switch (_algorithm)
        {
            case DisDeadReckoning.Static:
                return new PredictedState(p, Vec3.Zero, basis.Orientation);

            case DisDeadReckoning.Fpw:
                return new PredictedState(p + v * dt, v, basis.Orientation);

            case DisDeadReckoning.Rpw:
                return new PredictedState(p + v * dt, v, Rotated(in basis, dt));

            case DisDeadReckoning.Rvw:
                return new PredictedState(p + v * dt + a * (0.5 * dt * dt), v + a * dt, Rotated(in basis, dt));

            case DisDeadReckoning.Fvw:
                return new PredictedState(p + v * dt + a * (0.5 * dt * dt), v + a * dt, basis.Orientation);

            case DisDeadReckoning.Fpb:
            {
                var r0 = Orientation.ToMatrix(basis.Orientation);
                return new PredictedState(p + r0 * (v * dt), r0 * v, basis.Orientation);
            }

            case DisDeadReckoning.Fvb:
            {
                var r0 = Orientation.ToMatrix(basis.Orientation);
                var dist = v * dt + a * (0.5 * dt * dt);
                return new PredictedState(p + r0 * dist, r0 * (v + a * dt), basis.Orientation);
            }

            case DisDeadReckoning.Rpb:
                return RotatingBody(in basis, dt, withAcceleration: false);

            default:
                return RotatingBody(in basis, dt, withAcceleration: true);
        }
    }

    /// <summary>The orientation rotated by the body-axis angular velocity over dt.</summary>
    private static Vec3 Rotated(in PredictionBasis basis, double dt)
    {
        var w = basis.AngularVelocity;
        if (dt == 0.0 || (w.X == 0.0 && w.Y == 0.0 && w.Z == 0.0))
        {
            return basis.Orientation;
        }

        return Orientation.FromMatrix(Orientation.ToMatrix(basis.Orientation) * Matrix3.FromRotationVector(w * dt));
    }

    private static PredictedState RotatingBody(in PredictionBasis basis, double dt, bool withAcceleration)
    {
        var w = basis.AngularVelocity;
        var v = basis.Velocity;
        var a = withAcceleration ? basis.Acceleration : Vec3.Zero;
        var r0 = Orientation.ToMatrix(basis.Orientation);
        var rt = r0 * Matrix3.FromRotationVector(w * dt);
        var orientation = (dt == 0.0 || (w.X == 0.0 && w.Y == 0.0 && w.Z == 0.0)) ? basis.Orientation : Orientation.FromMatrix(rt);

        // Body-axis displacement: integral over 0..dt of exp([w] s) (v + a s) ds, with
        //   exp([w] s) = I + sin(ws)/w K + (1 - cos(ws))/w^2 K^2,   K = [w]x.
        double wn = w.Length;
        var k = Matrix3.Skew(w);
        var k2 = k * k;
        BodyCoefficients(wn, dt, out double c2, out double c3, out double d2, out double d3);
        var disp = v * dt + k * v * c2 + k2 * v * c3;
        if (withAcceleration)
        {
            disp = disp + a * (0.5 * dt * dt) + k * a * d2 + k2 * a * d3;
        }

        var velocityBody = v + a * dt;
        return new PredictedState(basis.Position + r0 * disp, rt * velocityBody, orientation);
    }

    /// <summary>
    /// With x = w dt: c2 = (1 - cos x)/w^2, c3 = (x - sin x)/w^3, d2 = (sin x - x cos x)/w^3,
    /// d3 = (x^2/2 - x sin x + 1 - cos x)/w^4. Series for a small x (the closed forms cancel), exact elsewhere.
    /// </summary>
    private static void BodyCoefficients(double w, double t, out double c2, out double c3, out double d2, out double d3)
    {
        double x = w * t;
        if (Math.Abs(x) < 0.05)
        {
            double w2 = w * w, w4 = w2 * w2;
            double t2 = t * t, t3 = t2 * t, t4 = t2 * t2, t5 = t4 * t, t6 = t4 * t2, t7 = t6 * t, t8 = t4 * t4;
            c2 = t2 / 2 - w2 * t4 / 24 + w4 * t6 / 720;
            c3 = t3 / 6 - w2 * t5 / 120 + w4 * t7 / 5040;
            d2 = t3 / 3 - w2 * t5 / 30 + w4 * t7 / 840;
            d3 = t4 / 8 - w2 * t6 / 144 + w4 * t8 / 5760;
            return;
        }

        double sin = Math.Sin(x), cos = Math.Cos(x);
        double ww = w * w;
        c2 = (1 - cos) / ww;
        c3 = (x - sin) / (ww * w);
        d2 = (sin - x * cos) / (ww * w);
        d3 = (x * x / 2 - x * sin + 1 - cos) / (ww * ww);
    }
}
