using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Common.Tests;

public class PredictorTests
{
    private static readonly PredictionBasis Basis = new(
        Position: new Vec3(1000, 2000, 3000),
        Velocity: new Vec3(10, -5, 2),
        Acceleration: new Vec3(1, 2, -1),
        Orientation: new Vec3(0.1, 0.2, 0.3),
        AngularVelocity: new Vec3(0, 0, 0.5),
        StateTime: 10);

    private static IPredictor Dis(DisDeadReckoning algorithm) => new DisPredictor(algorithm);

    private static void AssertVec(Vec3 expected, Vec3 actual, double tolerance = 1e-9)
    {
        Assert.True(Math.Abs(expected.X - actual.X) <= tolerance, $"X: expected {expected.X}, actual {actual.X}");
        Assert.True(Math.Abs(expected.Y - actual.Y) <= tolerance, $"Y: expected {expected.Y}, actual {actual.Y}");
        Assert.True(Math.Abs(expected.Z - actual.Z) <= tolerance, $"Z: expected {expected.Z}, actual {actual.Z}");
    }

    private static void AssertSameRotation(Vec3 expectedEuler, Vec3 actualEuler, double tolerance = 1e-9)
    {
        var e = Orientation.ToMatrix(expectedEuler);
        var a = Orientation.ToMatrix(actualEuler);
        for (int i = 0; i < 3; i++)
        {
            AssertVec(e.Column(i), a.Column(i), tolerance);
        }
    }

    [Fact]
    public void Strategy_ids_are_the_dis_numbers()
    {
        Assert.Equal(1, Dis(DisDeadReckoning.Static).StrategyId);
        Assert.Equal(2, Dis(DisDeadReckoning.Fpw).StrategyId);
        Assert.Equal(3, Dis(DisDeadReckoning.Rpw).StrategyId);
        Assert.Equal(4, Dis(DisDeadReckoning.Rvw).StrategyId);
        Assert.Equal(5, Dis(DisDeadReckoning.Fvw).StrategyId);
        Assert.Equal(6, Dis(DisDeadReckoning.Fpb).StrategyId);
        Assert.Equal(7, Dis(DisDeadReckoning.Rpb).StrategyId);
        Assert.Equal(8, Dis(DisDeadReckoning.Rvb).StrategyId);
        Assert.Equal(9, Dis(DisDeadReckoning.Fvb).StrategyId);
        Assert.Equal("FPW", Dis(DisDeadReckoning.Fpw).Name);
        Assert.Equal("Static", Dis(DisDeadReckoning.Static).Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DisPredictor((DisDeadReckoning)10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DisPredictor((DisDeadReckoning)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DisPredictor(DisDeadReckoning.Other));
    }

    [Fact]
    public void Static_keeps_position_and_orientation_and_has_no_velocity()
    {
        var s = Dis(DisDeadReckoning.Static).Predict(in Basis, 14);
        Assert.Equal(Basis.Position, s.Position);
        Assert.Equal(Basis.Orientation, s.Orientation);
        Assert.Equal(Vec3.Zero, s.Velocity);
    }

    [Fact]
    public void Fpw_moves_with_constant_velocity_and_ignores_acceleration_and_rotation()
    {
        var s = Dis(DisDeadReckoning.Fpw).Predict(in Basis, 14);
        AssertVec(new Vec3(1040, 1980, 3008), s.Position);
        Assert.Equal(Basis.Velocity, s.Velocity);
        Assert.Equal(Basis.Orientation, s.Orientation);
    }

    [Fact]
    public void Rpw_also_rotates_by_the_body_angular_velocity()
    {
        var s = Dis(DisDeadReckoning.Rpw).Predict(in Basis, 14);
        AssertVec(new Vec3(1040, 1980, 3008), s.Position);
        Assert.Equal(Basis.Velocity, s.Velocity);

        // Rotation by 0.5 rad/s * 4 s = 2 rad about the body z axis: R = R0 * Rz(2).
        var expected = Orientation.ToMatrix(Basis.Orientation) * Matrix3.RotationZ(2.0);
        AssertSameRotation(Orientation.FromMatrix(expected), s.Orientation);
    }

    [Fact]
    public void Rpw_with_a_yaw_rate_from_level_flight_adds_to_psi()
    {
        var level = Basis with { Orientation = Vec3.Zero };
        var s = Dis(DisDeadReckoning.Rpw).Predict(in level, 14);
        AssertVec(new Vec3(2.0, 0, 0), s.Orientation, 1e-12);
    }

    [Fact]
    public void Rvw_adds_acceleration_to_position_and_velocity_and_rotates()
    {
        var s = Dis(DisDeadReckoning.Rvw).Predict(in Basis, 14);
        AssertVec(new Vec3(1048, 1996, 3000), s.Position);
        AssertVec(new Vec3(14, 3, -2), s.Velocity);
        var expected = Orientation.ToMatrix(Basis.Orientation) * Matrix3.RotationZ(2.0);
        AssertSameRotation(Orientation.FromMatrix(expected), s.Orientation);
    }

    [Fact]
    public void Fvw_is_rvw_without_rotation()
    {
        var s = Dis(DisDeadReckoning.Fvw).Predict(in Basis, 14);
        AssertVec(new Vec3(1048, 1996, 3000), s.Position);
        AssertVec(new Vec3(14, 3, -2), s.Velocity);
        Assert.Equal(Basis.Orientation, s.Orientation);
    }

    [Fact]
    public void Zero_dt_returns_the_basis_for_every_algorithm()
    {
        foreach (var algorithm in DisPredictor.Supported)
        {
            var s = Dis(algorithm).Predict(in Basis, 10);
            AssertVec(Basis.Position, s.Position, 1e-12);
            AssertVec(Basis.Orientation, s.Orientation, 1e-12);
        }
    }

    [Fact]
    public void Negative_dt_extrapolates_backwards()
    {
        var fpw = Dis(DisDeadReckoning.Fpw).Predict(in Basis, 8);
        AssertVec(new Vec3(980, 2010, 2996), fpw.Position);

        var rvw = Dis(DisDeadReckoning.Rvw).Predict(in Basis, 8);
        AssertVec(new Vec3(982, 2014, 2994), rvw.Position);
        AssertVec(new Vec3(8, -9, 4), rvw.Velocity);

        var level = Basis with { Orientation = Vec3.Zero };
        var back = Dis(DisDeadReckoning.Rpw).Predict(in level, 8);
        AssertVec(new Vec3(-1.0, 0, 0), back.Orientation, 1e-12); // 0.5 rad/s * -2 s
    }

    [Fact]
    public void Rotating_then_rotating_back_restores_the_orientation()
    {
        var forward = Dis(DisDeadReckoning.Rpw).Predict(in Basis, 13);
        var again = Basis with { Orientation = forward.Orientation, StateTime = 13 };
        var back = Dis(DisDeadReckoning.Rpw).Predict(in again, 10);
        AssertSameRotation(Basis.Orientation, back.Orientation, 1e-12);
    }

    [Fact]
    public void Orientation_stays_in_the_euler_ranges_after_rotating()
    {
        var s = Dis(DisDeadReckoning.Rpw).Predict(in Basis, 100);
        Assert.InRange(s.Orientation.X, -Math.PI, Math.PI);
        Assert.InRange(s.Orientation.Y, -Math.PI / 2, Math.PI / 2);
        Assert.InRange(s.Orientation.Z, -Math.PI, Math.PI);
    }

    // ---- body-axis algorithms ------------------------------------------------------------------------------

    private static readonly PredictionBasis YawedEast = new(
        Position: new Vec3(1000, 2000, 3000),
        Velocity: new Vec3(10, 0, 0),
        Acceleration: new Vec3(1, 0, 0),
        Orientation: new Vec3(Math.PI / 2, 0, 0), // body x points along ECEF +y
        AngularVelocity: Vec3.Zero,
        StateTime: 10);

    [Fact]
    public void Fpb_rotates_the_body_velocity_into_ecef()
    {
        var s = Dis(DisDeadReckoning.Fpb).Predict(in YawedEast, 14);
        AssertVec(new Vec3(1000, 2040, 3000), s.Position);
        AssertVec(new Vec3(0, 10, 0), s.Velocity);
        Assert.Equal(YawedEast.Orientation, s.Orientation);
    }

    [Fact]
    public void Fvb_adds_the_body_acceleration()
    {
        var s = Dis(DisDeadReckoning.Fvb).Predict(in YawedEast, 14);
        AssertVec(new Vec3(1000, 2048, 3000), s.Position);   // 10*4 + 0.5*1*16 = 48 along ECEF y
        AssertVec(new Vec3(0, 14, 0), s.Velocity);
        Assert.Equal(YawedEast.Orientation, s.Orientation);
    }

    [Fact]
    public void Rpb_moving_on_a_circle()
    {
        // Level, body x = ECEF x at the start, yaw rate 0.5 rad/s, body speed 10 m/s: a circle of radius 20.
        var basis = new PredictionBasis(Vec3.Zero, new Vec3(10, 0, 0), Vec3.Zero, Vec3.Zero, new Vec3(0, 0, 0.5), 0);
        var s = Dis(DisDeadReckoning.Rpb).Predict(in basis, 4);
        double x = 20 * Math.Sin(2.0), y = 20 * (1 - Math.Cos(2.0));
        AssertVec(new Vec3(x, y, 0), s.Position, 1e-9);
        AssertVec(new Vec3(10 * Math.Cos(2.0), 10 * Math.Sin(2.0), 0), s.Velocity, 1e-9);
        AssertVec(new Vec3(2.0, 0, 0), s.Orientation, 1e-12);
    }

    [Fact]
    public void Rvb_with_acceleration_along_the_body_x_axis_while_turning()
    {
        // v = 0, a = (1,0,0) in body axes, yaw rate w: position = integral of s (cos ws, sin ws, 0) ds.
        double w = 0.5, t = 4, xw = w * t;
        var basis = new PredictionBasis(Vec3.Zero, Vec3.Zero, new Vec3(1, 0, 0), Vec3.Zero, new Vec3(0, 0, w), 0);
        var s = Dis(DisDeadReckoning.Rvb).Predict(in basis, t);
        double ex = (Math.Cos(xw) - 1) / (w * w) + t * Math.Sin(xw) / w;
        double ey = Math.Sin(xw) / (w * w) - t * Math.Cos(xw) / w;
        AssertVec(new Vec3(ex, ey, 0), s.Position, 1e-9);
        AssertVec(new Vec3(t * Math.Cos(xw), t * Math.Sin(xw), 0), s.Velocity, 1e-9);
    }

    [Fact]
    public void Rvb_without_rotation_equals_fvb_and_rpb_without_rotation_equals_fpb()
    {
        var still = YawedEast with { AngularVelocity = Vec3.Zero };
        AssertVec(Dis(DisDeadReckoning.Fvb).Predict(in still, 14).Position, Dis(DisDeadReckoning.Rvb).Predict(in still, 14).Position, 1e-12);
        AssertVec(Dis(DisDeadReckoning.Fpb).Predict(in still, 14).Position, Dis(DisDeadReckoning.Rpb).Predict(in still, 14).Position, 1e-12);
        Assert.Equal(still.Orientation, Dis(DisDeadReckoning.Rvb).Predict(in still, 14).Orientation);
    }

    [Theory]
    [InlineData(0.5, 4.0)]     // closed forms
    [InlineData(2.0, -3.0)]    // closed forms, backwards
    [InlineData(1e-3, 1.0)]    // series branch (w * dt below 0.05)
    [InlineData(0.01, 4.0)]    // series branch
    [InlineData(0.0125, 4.0)]  // just below the series/closed-form switch (x = 0.05)
    [InlineData(0.0126, 4.0)]  // just above
    public void Rotating_body_displacement_matches_numerical_integration(double rate, double dt)
    {
        var axis = new Vec3(0.3, -0.4, 0.5);
        var basis = new PredictionBasis(
            new Vec3(100, -200, 300), new Vec3(10, 3, -2), new Vec3(0.5, -1, 0.25), new Vec3(0.3, -0.2, 0.9),
            axis * (rate / axis.Length), 5);
        var r0 = Orientation.ToMatrix(basis.Orientation);

        // Composite Simpson over R0 * exp([w] s) * (v + a s), s in [0, dt] (dt may be negative).
        const int n = 4000;
        Vec3 Integrand(double s) => r0 * (Matrix3.FromRotationVector(basis.AngularVelocity * s) * (basis.Velocity + basis.Acceleration * s));
        double h = dt / n;
        var sum = Integrand(0) + Integrand(dt);
        for (int i = 1; i < n; i++)
        {
            sum = sum + Integrand(i * h) * (i % 2 == 1 ? 4.0 : 2.0);
        }

        var expected = basis.Position + sum * (h / 3.0);
        var actual = Dis(DisDeadReckoning.Rvb).Predict(in basis, 5 + dt);
        AssertVec(expected, actual.Position, 1e-8);

        // The velocity is the body velocity at dt rotated by the predicted orientation.
        var rt = Orientation.ToMatrix(actual.Orientation);
        AssertVec(rt * (basis.Velocity + basis.Acceleration * dt), actual.Velocity, 1e-9);

        // Rpb ignores the acceleration.
        var noAcc = basis with { Acceleration = Vec3.Zero };
        var expectedPb = noAcc.Position + IntegratePb(noAcc, r0, dt);
        AssertVec(expectedPb, Dis(DisDeadReckoning.Rpb).Predict(in noAcc, 5 + dt).Position, 1e-8);
        AssertVec(Dis(DisDeadReckoning.Rpb).Predict(in basis, 5 + dt).Position, Dis(DisDeadReckoning.Rpb).Predict(in noAcc, 5 + dt).Position, 1e-12);
    }

    [Fact]
    public void Rpb_velocity_ignores_the_acceleration_and_is_the_body_velocity_in_the_predicted_orientation()
    {
        var s = Dis(DisDeadReckoning.Rpb).Predict(in Basis, 14);
        var rt = Orientation.ToMatrix(s.Orientation);
        AssertVec(rt * Basis.Velocity, s.Velocity, 1e-9);
        var noAcc = Dis(DisDeadReckoning.Rpb).Predict(Basis with { Acceleration = Vec3.Zero }, 14);
        AssertVec(noAcc.Velocity, s.Velocity, 1e-12);
    }

    private static Vec3 IntegratePb(PredictionBasis b, Matrix3 r0, double dt)
    {
        const int n = 4000;
        Vec3 Integrand(double s) => r0 * (Matrix3.FromRotationVector(b.AngularVelocity * s) * b.Velocity);
        double h = dt / n;
        var sum = Integrand(0) + Integrand(dt);
        for (int i = 1; i < n; i++)
        {
            sum = sum + Integrand(i * h) * (i % 2 == 1 ? 4.0 : 2.0);
        }

        return sum * (h / 3.0);
    }

    // ---- registry -------------------------------------------------------------------------------------------

    [Fact]
    public void The_dis_registry_holds_the_nine_algorithms()
    {
        var registry = PredictorRegistry.CreateDis();
        Assert.Equal(9, registry.Count);
        Assert.Equal(9, registry.All.Count);
        for (int id = 1; id <= 9; id++)
        {
            Assert.True(registry.TryGet(id, out var predictor));
            Assert.Equal(id, predictor.StrategyId);
        }

        Assert.False(registry.TryGet(0, out _));   // Other
        Assert.False(registry.TryGet(10, out _));
        Assert.False(registry.TryGet(-1, out _));
    }

    [Fact]
    public void Registering_a_duplicate_strategy_id_throws()
    {
        var registry = PredictorRegistry.CreateDis();
        Assert.Throws<InvalidOperationException>(() => registry.Register(new DisPredictor(DisDeadReckoning.Fpw)));
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
    }

    private sealed class Custom : IPredictor
    {
        public int StrategyId => 1000;
        public string Name => "route";
        public PredictedState Predict(in PredictionBasis basis, double time) =>
            new(basis.Position + new Vec3(1, 1, 1) * (time - basis.StateTime), basis.Velocity, basis.Orientation);
    }

    [Fact]
    public void Custom_predictors_can_be_registered_next_to_the_dis_ones()
    {
        var registry = PredictorRegistry.CreateDis();
        registry.Register(new Custom());
        Assert.True(registry.TryGet(1000, out var predictor));
        Assert.Equal("route", predictor.Name);
        Assert.Equal(10, registry.All.Count);
    }

    [Fact]
    public void Predicting_allocates_nothing()
    {
        var predictors = PredictorRegistry.CreateDis().All.ToArray();
        var basis = Basis;
        for (int warm = 0; warm < 100; warm++)
        {
            foreach (var p in predictors)
            {
                p.Predict(in basis, 12);
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            for (int k = 0; k < predictors.Length; k++)
            {
                predictors[k].Predict(in basis, 12 + i);
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
