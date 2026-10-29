using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Common.Tests;

/// <summary>
/// DD-009 section 8: the shared send policy against a brute-force oracle with its own dead-reckoning arithmetic (Static and FPW)
/// and its own angle measure (rotation matrices), over seeded random sequences: a send is due iff the held model drifted beyond a
/// threshold or the heartbeat is due, and a send just made is never due again in the same instant (one send per entity per tick).
/// </summary>
public class DeadReckonedSendPolicyTests
{
    private const int Sequences = 600;

    private static Vec3 V(Random r, double range) => new(r.NextDouble() * 2 * range - range, r.NextDouble() * 2 * range - range, r.NextDouble() * 2 * range - range);

    // ---- the oracle: own arithmetic, nothing shared with the policy but the data types -----------------------------------

    private static Vec3 OraclePosition(int algorithm, Vec3 p, Vec3 v, double dt) =>
        algorithm == 2 ? new Vec3(p.X + v.X * dt, p.Y + v.Y * dt, p.Z + v.Z * dt) : p;

    private static double[,] Matrix(Vec3 e)
    {
        double cp = Math.Cos(e.X), sp = Math.Sin(e.X), ct = Math.Cos(e.Y), st = Math.Sin(e.Y), cf = Math.Cos(e.Z), sf = Math.Sin(e.Z);
        return new[,]
        {
            { cp * ct, cp * st * sf - sp * cf, cp * st * cf + sp * sf },
            { sp * ct, sp * st * sf + cp * cf, sp * st * cf - cp * sf },
            { -st, ct * sf, ct * cf },
        };
    }

    private static double OracleAngle(Vec3 a, Vec3 b)
    {
        var ma = Matrix(a);
        var mb = Matrix(b);
        double trace = 0;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                trace += ma[j, i] * mb[j, i]; // trace(Ma^T Mb)
            }
        }

        return Math.Acos(Math.Clamp((trace - 1) / 2, -1, 1));
    }

    private static EntityId Create(Rig r, Vec3 pos, Vec3 vel, Vec3 ori, int alg, double stateTime)
    {
        var cs = r.Engine.BeginChange(r.Net);
        var id = cs.CreateEntity();
        cs.Set(id, r.Pos, AttrValue.From(pos));
        cs.Set(id, r.Vel, AttrValue.From(vel));
        cs.Set(id, r.Ori, AttrValue.From(ori));
        cs.Set(id, r.Alg, AttrValue.From((long)alg));
        cs.Set(id, r.StateTime, AttrValue.From(stateTime));
        Assert.True(cs.Commit().Succeeded);
        return id;
    }

    [Fact]
    public void Decision_equals_the_brute_force_oracle_over_seeded_sequences()
    {
        int tested = 0, drifts = 0, beats = 0, quiet = 0;
        for (int seed = 0; seed < Sequences; seed++)
        {
            var rng = new Random(seed);
            var rig = new Rig();
            double posThr = 0.2 + rng.NextDouble() * 4;
            double oriThrDeg = 0.5 + rng.NextDouble() * 8;
            double heartbeat = 1 + rng.NextDouble() * 6;
            var policy = new DeadReckonedSendPolicy(rig.Input, SendPolicySettings.FromDegrees(posThr, oriThrDeg, heartbeat), rig.Predictors);

            int alg = rng.Next(2) == 0 ? 1 : 2;
            var pos = V(rng, 1000);
            var vel = alg == 2 ? V(rng, 30) : Vec3.Zero;
            var ori = new Vec3(rng.NextDouble() * 6 - 3, rng.NextDouble() * 2.4 - 1.2, rng.NextDouble() * 6 - 3);
            double stateTime = rng.NextDouble() * 10;
            var entity = rig.Engine.State.Find(Create(rig, pos, vel, ori, alg, stateTime))!;

            // the held model: a send at sendTime of a state that differs from the truth by a random amount around the thresholds
            double sendTime = stateTime + rng.NextDouble() * 5;
            var heldAlg = alg;
            var heldVel = heldAlg == 2 ? vel + V(rng, 0.01) : Vec3.Zero;
            var truthAtSend = OraclePosition(alg, pos, vel, sendTime - stateTime);
            double scale = rng.Next(3) == 0 ? 1.2 : 0.2; // some sequences stay well inside the thresholds
            var heldPos = truthAtSend + V(rng, posThr * scale);
            var heldOri = ori + V(rng, oriThrDeg * Math.PI / 180.0 * scale);
            var model = SentModel.Of(heldPos, heldVel, Vec3.Zero, heldOri, Vec3.Zero, heldAlg, sendTime);
            double now = sendTime + rng.NextDouble() * heartbeat * 1.5;

            var truth = OraclePosition(alg, pos, vel, now - stateTime);
            var held = OraclePosition(heldAlg, heldPos, heldVel, now - sendTime);
            double dist = (truth - held).Length;
            double angle = OracleAngle(ori, heldOri);
            double age = now - sendTime;
            if (Math.Abs(dist - posThr) < 1e-7 || Math.Abs(angle - oriThrDeg * Math.PI / 180.0) < 1e-7 || Math.Abs(age - heartbeat) < 1e-9)
            {
                continue; // a decision on the boundary itself is not what this oracle can judge
            }

            bool expectDrift = dist > posThr || angle > oriThrDeg * Math.PI / 180.0;
            bool expectBeat = age >= heartbeat;
            Assert.True(expectDrift == policy.Drifted(entity, in model, now), $"seed {seed}: drift expected {expectDrift} (dist {dist}, thr {posThr}, angle {angle})");
            Assert.True(expectBeat == policy.HeartbeatDue(in model, now), $"seed {seed}: heartbeat expected {expectBeat}");
            Assert.True((expectDrift || expectBeat) == policy.NeedsSend(entity, in model, now), $"seed {seed}: needs-send");
            tested++;
            drifts += expectDrift ? 1 : 0;
            beats += expectBeat ? 1 : 0;
            quiet += !expectDrift && !expectBeat ? 1 : 0;
        }

        Assert.True(tested >= 500, $"only {tested} sequences were testable");
        Assert.True(drifts > 50 && beats > 50 && quiet > 50, $"the sequences do not cover every outcome: drift {drifts}, heartbeat {beats}, quiet {quiet}");
    }

    [Fact]
    public void A_send_just_made_is_not_due_again_in_the_same_instant()
    {
        for (int seed = 0; seed < Sequences; seed++)
        {
            var rng = new Random(1000 + seed);
            var rig = new Rig();
            var policy = new DeadReckonedSendPolicy(rig.Input, SendPolicySettings.FromDegrees(0.1 + rng.NextDouble(), 0.5 + rng.NextDouble(), 1 + rng.NextDouble() * 5), rig.Predictors);
            int alg = rng.Next(2) == 0 ? 1 : 2;
            double stateTime = rng.NextDouble() * 10;
            var entity = rig.Engine.State.Find(Create(rig, V(rng, 500), alg == 2 ? V(rng, 40) : Vec3.Zero, V(rng, 1), alg, stateTime))!;
            double now = stateTime + rng.NextDouble() * 20;

            // what a full send at "now" records: the entity's state predicted to now
            Assert.True(policy.TryRead(entity, out var basis, out int algorithm));
            var predicted = policy.Predict(in basis, algorithm, now);
            var model = SentModel.Of(predicted.Position, basis.Velocity, basis.Acceleration, predicted.Orientation, basis.AngularVelocity, algorithm, now);
            Assert.False(policy.NeedsSend(entity, in model, now), $"seed {seed}");
            Assert.False(policy.Drifted(entity, in model, now), $"seed {seed}");
            Assert.False(policy.HeartbeatDue(in model, now), $"seed {seed}");
        }
    }

    [Fact]
    public void Heartbeat_is_due_exactly_one_period_after_the_send_and_not_before()
    {
        var rig = new Rig();
        var policy = new DeadReckonedSendPolicy(rig.Input, SendPolicySettings.FromDegrees(1, 3, 5), rig.Predictors);
        var model = SentModel.Of(Vec3.Zero, Vec3.Zero, Vec3.Zero, Vec3.Zero, Vec3.Zero, 1, 10.0);
        Assert.False(policy.HeartbeatDue(in model, 14.999));
        Assert.True(policy.HeartbeatDue(in model, 15.0));
        Assert.True(policy.HeartbeatDue(in model, 100.0));
    }

    [Fact]
    public void Orientation_alone_triggers_a_send_and_equal_to_the_threshold_is_within()
    {
        var rig = new Rig();
        var policy = new DeadReckonedSendPolicy(rig.Input, SendPolicySettings.FromDegrees(1, 3, 5), rig.Predictors);
        var entity = rig.Engine.State.Find(Create(rig, Vec3.Zero, Vec3.Zero, new Vec3(0, 0, 0), 1, 0))!;
        double threeDeg = 3.0 * Math.PI / 180.0;
        var within = SentModel.Of(Vec3.Zero, Vec3.Zero, Vec3.Zero, new Vec3(threeDeg * 0.99, 0, 0), Vec3.Zero, 1, 0);
        var beyond = SentModel.Of(Vec3.Zero, Vec3.Zero, Vec3.Zero, new Vec3(threeDeg * 1.01, 0, 0), Vec3.Zero, 1, 0);
        Assert.False(policy.Drifted(entity, in within, 1.0));
        Assert.True(policy.Drifted(entity, in beyond, 1.0));
    }

    [Fact]
    public void An_entity_without_a_readable_state_never_drifts()
    {
        var rig = new Rig();
        var policy = new DeadReckonedSendPolicy(rig.Input, SendPolicySettings.FromDegrees(1, 3, 5), rig.Predictors);
        var cs = rig.Engine.BeginChange(rig.Net);
        var id = cs.CreateEntity();
        cs.Set(id, rig.Pos, AttrValue.From(new Vec3(1, 2, 3))); // no state time
        Assert.True(cs.Commit().Succeeded);
        var entity = rig.Engine.State.Find(id)!;
        var model = SentModel.Of(Vec3.Zero, Vec3.Zero, Vec3.Zero, Vec3.Zero, Vec3.Zero, 1, 0);
        Assert.False(policy.Drifted(entity, in model, 10));
    }

    [Fact]
    public void Invalid_settings_are_rejected()
    {
        var rig = new Rig();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadReckonedSendPolicy(rig.Input, new SendPolicySettings(-1, 0.1, 5), rig.Predictors));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadReckonedSendPolicy(rig.Input, new SendPolicySettings(1, 0.1, 0), rig.Predictors));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadReckonedSendPolicy(rig.Input, new SendPolicySettings(1, double.NaN, 5), rig.Predictors));
    }
}
