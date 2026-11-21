using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using Xunit.Abstractions;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>DPS round 3 in the plugin (DD-019 3.1, 3.2, 3.4, 3.5): the suggested view with its change detection and cuts, impacts, the round's launch and burn, airborne animals.</summary>
public class DpsRoundThreeTests(ITestOutputHelper output)
{
    private static HrotView View(double simTime = 1.0, string source = "Director", string mode = "Shoulder", int target = 12, string shot = "Take 1", double x = 10, float distance = 2.2f,
        HrotQuat? rotation = null, int cut = 0) =>
        new(source, mode, target, distance, -1f, new HrotVec3(x, 20, 3), rotation ?? HrotQuat.Identity, 60f, shot, simTime, cut);

    private static HrotQuat Yaw(double deg)
    {
        double h = deg * Math.PI / 360.0;
        return new HrotQuat(0, 0, Math.Sin(h), Math.Cos(h));
    }

    [Fact]
    public void A_view_is_mapped_and_a_cut_grows_the_cut_sequence_only_on_a_new_binding_or_shot()
    {
        uint domain = Watch.NewDomain();
        using var views = new Watch.Watch<D.View>(domain, "station-1", v => "view");
        using var rig = new Rig(domain);

        rig.Send(View());
        Assert.True(views.Until(w => w.Events.Count == 1));
        var first = views.Events[0];
        Assert.Equal((D.ViewSource.ViewSourceDirector, D.ViewMode.ViewModeShoulder, 12, 2.2f, -1f, 60f, "Take 1", 1u, 1.0),
            (first.Source, first.Mode, first.TargetId, first.Distance, first.Side, first.FovDeg, first.Shot, first.CutSeq, first.SimTime));
        Assert.Equal((10.0, 20.0, 3.0), (first.Position.X, first.Position.Y, first.Position.Z));

        // the same shot moving: new samples, the same cut
        rig.Send(View(simTime: 1.1, x: 12));
        Assert.True(views.Until(w => w.Events.Count == 2));
        Assert.Equal(1u, views.Events[1].CutSeq);

        // a turn of the camera beyond the angle threshold is a change as well
        rig.Send(View(simTime: 1.2, x: 12, rotation: Yaw(10)));
        Assert.True(views.Until(w => w.Events.Count == 3));
        Assert.Equal(1u, views.Events[2].CutSeq);

        // a new shot, then a new target and a new mode, are cuts
        rig.Send(View(simTime: 1.3, x: 12, rotation: Yaw(10), shot: "Take 2"));
        Assert.True(views.Until(w => w.Events.Count == 4));
        Assert.Equal((2u, "Take 2"), (views.Events[3].CutSeq, views.Events[3].Shot));
        rig.Send(View(simTime: 1.4, x: 12, rotation: Yaw(10), shot: "Take 2", target: 14));
        Assert.True(views.Until(w => w.Events.Count == 5));   // a state topic keeps one sample: wait for each before the next
        rig.Send(View(simTime: 1.5, x: 12, rotation: Yaw(10), shot: "Take 2", target: 14, mode: "Chase"));
        Assert.True(views.Until(w => w.Events.Count == 6));
        Assert.Equal(new[] { 3u, 4u }, new[] { views.Events[4].CutSeq, views.Events[5].CutSeq });
        Assert.Equal(D.ViewMode.ViewModeChase, views.Events[5].Mode);
    }

    [Fact]
    public void A_hard_cut_HROT_counts_is_a_cut_in_the_view_even_when_nothing_else_about_the_binding_changed()
    {
        uint domain = Watch.NewDomain();
        using var views = new Watch.Watch<D.View>(domain, "station-1", v => "view");
        using var rig = new Rig(domain);

        rig.Send(View(cut: 7));
        Assert.True(views.Until(w => w.Events.Count == 1));
        rig.Send(View(simTime: 1.1, x: 12, cut: 7));   // the same HROT cut, the camera moving: the same CutSeq
        Assert.True(views.Until(w => w.Events.Count == 2));
        Assert.Equal(1u, views.Events[1].CutSeq);

        rig.Send(View(simTime: 1.2, x: 12, cut: 8));   // the director cut to a camera with the same source, mode, target and shot
        Assert.True(views.Until(w => w.Events.Count == 3));
        Assert.Equal(2u, views.Events[2].CutSeq);
    }

    [Fact]
    public void An_unchanged_view_is_held_back_until_the_heartbeat_and_a_stop_is_one_sample_with_source_none()
    {
        uint domain = Watch.NewDomain();
        using var views = new Watch.Watch<D.View>(domain, "station-1", v => "view");
        using var rig = new Rig(domain);   // heartbeat 5 s of simulation time

        rig.Send(View(simTime: 10.0));
        Assert.True(views.Until(w => w.Events.Count == 1));
        rig.Send(View(simTime: 10.5));   // nothing changed: not sent
        rig.Send(View(simTime: 12.0));   // not sent
        rig.Send(View(simTime: 15.0));   // 5 s after the last one sent: the heartbeat
        Assert.True(views.Until(w => w.Events.Count == 2));
        Assert.Equal([10.0, 15.0], views.Events.Select(e => e.SimTime).ToArray());
        Assert.All(views.Events, e => Assert.Equal(1u, e.CutSeq));

        rig.Send(new HrotView("None", string.Empty, 0, 0f, 0f, HrotVec3.Zero, HrotQuat.Identity, 0f, string.Empty, 16.0));
        Assert.True(views.Until(w => w.Events.Count == 3));
        Assert.Equal(D.ViewSource.ViewSourceNone, views.Events[2].Source);
    }

    [Fact]
    public void A_view_with_a_non_finite_pose_and_a_long_shot_name_are_handled()
    {
        uint domain = Watch.NewDomain();
        using var views = new Watch.Watch<D.View>(domain, "station-1", v => "view");
        using var rig = new Rig(domain);

        rig.Send(View() with { Position = new HrotVec3(double.NaN, 0, 0) });
        rig.Send(View(simTime: 2.0, shot: new string('s', 100), source: "Hologram", mode: "Teleport"));

        Assert.True(views.Until(w => w.Events.Count == 1));
        Assert.Single(views.Events);
        Assert.Equal(64, views.Events[0].Shot.Length);
        Assert.Equal((D.ViewSource.ViewSourceNone, D.ViewMode.ViewModeUnknown), (views.Events[0].Source, views.Events[0].Mode));   // names the contract does not have are 0
    }

    [Fact]
    public void An_impact_is_mapped_with_its_surface_and_kind_and_unknown_names_are_unknown()
    {
        uint domain = Watch.NewDomain();
        using var impacts = new Watch.Watch<D.Impact>(domain, "station-1", i => "impact");
        using var rig = new Rig(domain);

        rig.Send(new HrotImpact(5.5, new HrotVec3(1, 2, 3), new HrotVec3(0, 0, 1), "Building", "Stop", 6_000_000_001L, 2001, "Brick"));
        rig.Send(new HrotImpact(5.6, new HrotVec3(1, 2, 3), HrotVec3.Zero, string.Empty, "Exit", 6_000_000_001L, 0, new string('m', 50)));
        rig.Send(new HrotImpact(5.7, new HrotVec3(1, 2, 3), HrotVec3.Zero, "Ground", "Entry", 7, 0, string.Empty));
        rig.Send(new HrotImpact(5.8, new HrotVec3(double.NaN, 2, 3), HrotVec3.Zero, "Ground", "Entry", 7, 0, string.Empty));   // not finite: dropped

        Assert.True(impacts.Until(w => w.Events.Count == 3));
        var stop = impacts.Events[0];
        Assert.Equal((5.5, D.EffectSurface.EffectSurfaceBuilding, D.ImpactKind.ImpactKindStopped, 6_000_000_001L, 2001, "Brick", 1f),
            (stop.SimTime, stop.Surface, stop.Kind, stop.MunitionTypeId, stop.ShooterId, stop.Material, stop.Normal.Z));
        Assert.Equal((D.EffectSurface.EffectSurfaceUnknown, D.ImpactKind.ImpactKindLeft, 32), (impacts.Events[1].Surface, impacts.Events[1].Kind, impacts.Events[1].Material.Length));
        Assert.Equal((D.EffectSurface.EffectSurfaceGround, D.ImpactKind.ImpactKindEntered), (impacts.Events[2].Surface, impacts.Events[2].Kind));
        Assert.Equal(D.ImpactKind.ImpactKindUnknown, LookNames.ImpactKindOf("Ricochet"));
    }

    [Fact]
    public void A_round_carries_its_launch_time_and_motor_burn_and_an_animal_its_airborne_state()
    {
        uint domain = Watch.NewDomain();
        using var rounds = new Watch.Watch<D.Round>(domain, "station-1", r => r.EntityId.ToString());
        using var animals = new Watch.Watch<D.Animal>(domain, "station-1", a => a.EntityId.ToString());
        using var rig = new Rig(domain);

        rig.Send(new HrotRound(3001, 6_000_000_001L, 2001, new HrotVec3(1, 2, 3), false, -1f, false, 123.5, 5f));
        rig.Send(new HrotRound(3002, 6_000_000_002L, 0, new HrotVec3(1, 2, 3), false, -1f, false));   // from before round 3: no launch time, no motor
        rig.Send(new HrotAnimal(4001, "Flee", 3, false, false, false, 12, 20, 777, "Flying", true));
        rig.Send(new HrotAnimal(4002, "Calm", 0, false, false, false, 0, 0, 0, string.Empty));

        Assert.True(rounds.Until(w => w.Alive.Count == 2));
        Assert.Equal((123.5, 5f), (rounds.Alive["3001"].LaunchedAt, rounds.Alive["3001"].BurnSeconds));
        Assert.Equal((0.0, 0f), (rounds.Alive["3002"].LaunchedAt, rounds.Alive["3002"].BurnSeconds));
        Assert.True(animals.Until(w => w.Alive.Count == 2));
        Assert.True(animals.Alive["4001"].Airborne);
        Assert.False(animals.Alive["4002"].Airborne);
    }

    // ---- DD-022 3d: the camera is dead-reckoned like an entity ------------------------------------------------------------------------

    private const double Hz = 60.0;

    /// <summary>Feeds a camera path at 60 frames a second of simulation time; returns every sample the station published and checks, at every frame, that the client's prediction of the newest published sample is within the thresholds.</summary>
    private List<D.View> Fly(int frames, Func<double, (HrotVec3 Position, HrotQuat Rotation, float Fov)> path, string name, Func<int, int>? cut = null, int firstFrame = 0)
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var sent = new List<D.View>();
        rig.Transport.View.Tap = sent.Add;
        for (int k = firstFrame; k < firstFrame + frames; k++)
        {
            double t = k / Hz;
            var (position, rotation, fov) = path(t);
            rig.Send(new HrotView("Director", "Free", 0, 0f, 0f, position, rotation, fov, "shot", t, cut?.Invoke(k) ?? 0));
            var last = sent[^1];
            double dt = t - last.SimTime;
            var p = new Vec3(last.Position.X, last.Position.Y, last.Position.Z);
            var v = new Vec3(last.Velocity.X, last.Velocity.Y, last.Velocity.Z);
            var a = new Vec3(last.Acceleration.X, last.Acceleration.Y, last.Acceleration.Z);
            var predicted = new Vec3(p.X + v.X * dt + 0.5 * a.X * dt * dt, p.Y + v.Y * dt + 0.5 * a.Y * dt * dt, p.Z + v.Z * dt + 0.5 * a.Z * dt * dt);
            double miss = Math.Sqrt(Math.Pow(predicted.X - position.X, 2) + Math.Pow(predicted.Y - position.Y, 2) + Math.Pow(predicted.Z - position.Z, 2));
            Assert.True(miss <= 0.05 + 1e-3 || (cut?.Invoke(k) ?? 0) != (cut?.Invoke(k - 1) ?? 0), $"{name}: frame {k} the client's position prediction misses by {miss:F4} m");

            var q0 = new Quaternion(last.Orientation.W, last.Orientation.X, last.Orientation.Y, last.Orientation.Z);
            double wl = Math.Sqrt(last.AngularVelocity.X * last.AngularVelocity.X + last.AngularVelocity.Y * last.AngularVelocity.Y + last.AngularVelocity.Z * last.AngularVelocity.Z);
            var turn = wl < 1e-9 ? Quaternion.Identity : new Quaternion(Math.Cos(wl * dt / 2), Math.Sin(wl * dt / 2) * last.AngularVelocity.X / wl, Math.Sin(wl * dt / 2) * last.AngularVelocity.Y / wl, Math.Sin(wl * dt / 2) * last.AngularVelocity.Z / wl);
            double angle = Quaternion.AngleBetween(q0 * turn, new Quaternion(rotation.W, rotation.X, rotation.Y, rotation.Z)) * 180.0 / Math.PI;
            Assert.True(angle <= 1.0 + 0.1 || (cut?.Invoke(k) ?? 0) != (cut?.Invoke(k - 1) ?? 0), $"{name}: frame {k} the client's orientation prediction misses by {angle:F3} degrees");
        }

        output.WriteLine($"{name}: {sent.Count} samples for {frames} frames");
        return sent;
    }

    private static HrotQuat YawQ(double rad) => new(0, 0, Math.Sin(rad / 2), Math.Cos(rad / 2));

    [Fact]
    public void A_camera_flying_straight_at_a_constant_speed_is_predicted_by_the_rates_and_sends_far_fewer_samples_than_frames()
    {
        var sent = Fly(600, t => (new HrotVec3(10 + 5 * t, 20, 3), HrotQuat.Identity, 60f), "straight");

        Assert.Equal(5.0, sent[^1].Velocity.X, 0.05);
        Assert.Equal(0.0, sent[^1].Acceleration.X, 0.2);
        Assert.Equal(0.0, sent[^1].AngularVelocity.Z, 0.01);
        // the old rule sent a sample whenever the camera moved 5 cm: at 5 m/s that is every frame (600); the rates make a steady move nearly free
        Assert.True(sent.Count < 20, $"{sent.Count} samples for 600 frames");
    }

    [Fact]
    public void A_camera_turning_at_a_constant_rate_carries_its_body_frame_angular_velocity()
    {
        var sent = Fly(300, t => (new HrotVec3(10, 20, 3), YawQ(0.5 * t), 60f), "turn");

        Assert.Equal((0.0, 0.0, 0.5), (Math.Round(sent[^1].AngularVelocity.X, 2), Math.Round(sent[^1].AngularVelocity.Y, 2), Math.Round(sent[^1].AngularVelocity.Z, 2)));
        Assert.True(sent.Count < 30, $"{sent.Count} samples for 300 frames");
    }

    [Fact]
    public void A_camera_orbiting_a_point_carries_its_speed_its_centripetal_acceleration_and_its_turn_rate()
    {
        // radius 10 m at 0.5 rad/s: 5 m/s tangential, 2.5 m/s^2 towards the centre, turning 0.5 rad/s about its own up axis
        var sent = Fly(600, t => (new HrotVec3(10 * Math.Cos(0.5 * t), 10 * Math.Sin(0.5 * t), 3), YawQ(0.5 * t + Math.PI / 2), 60f), "orbit");
        var last = sent[^1];

        Assert.Equal(5.0, Math.Sqrt(last.Velocity.X * last.Velocity.X + last.Velocity.Y * last.Velocity.Y), 0.15);
        Assert.Equal(2.5, Math.Sqrt(last.Acceleration.X * last.Acceleration.X + last.Acceleration.Y * last.Acceleration.Y), 0.4);
        Assert.Equal(0.5, last.AngularVelocity.Z, 0.02);
        Assert.True(sent.Count < 150, $"{sent.Count} samples for 600 frames");
    }

    [Fact]
    public void A_cut_is_never_blended_it_resets_the_rates_and_the_next_rates_come_from_the_new_shot_alone()
    {
        // fast flight, then at frame 100 the director cuts 1000 m away (HROT's own cut counter grows); the new shot moves at 1 m/s
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var sent = new List<D.View>();
        rig.Transport.View.Tap = sent.Add;
        for (int k = 0; k < 100; k++)
        {
            rig.Send(new HrotView("Director", "Free", 0, 0f, 0f, new HrotVec3(10 + 30.0 * k / Hz, 0, 3), HrotQuat.Identity, 60f, "shot", k / Hz, 0));
        }

        Assert.Equal(30.0, sent[^1].Velocity.X, 0.5);
        uint cutBefore = sent[^1].CutSeq;
        for (int k = 100; k < 130; k++)
        {
            rig.Send(new HrotView("Director", "Free", 0, 0f, 0f, new HrotVec3(1000 + 1.0 * (k - 100) / Hz, 0, 3), HrotQuat.Identity, 60f, "shot", k / Hz, 1));
        }

        var atCut = sent.First(v => v.CutSeq == cutBefore + 1);
        Assert.Equal((0f, 0f, 0f), (atCut.Velocity.X, atCut.Acceleration.X, atCut.AngularVelocity.Z));   // nothing carries over the cut
        Assert.Equal(1000.0, atCut.Position.X, 3);
        Assert.Equal(1.0, sent[^1].Velocity.X, 0.1);   // not 1000 m / dt from mixing the two shots
        Assert.Equal(cutBefore + 1, sent[^1].CutSeq);
    }

    [Fact]
    public void A_view_whose_simulation_time_stands_still_has_no_rates()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var sent = new List<D.View>();
        rig.Transport.View.Tap = sent.Add;
        for (int k = 0; k < 30; k++)
        {
            rig.Send(new HrotView("Director", "Free", 0, 0f, 0f, new HrotVec3(10 + 5.0 * k / Hz, 0, 3), HrotQuat.Identity, 60f, "shot", k / Hz));
        }

        // paused: the pose stays and so does simulation time, more samples arrive
        for (int k = 0; k < 10; k++)
        {
            rig.Send(new HrotView("Director", "Free", 0, 0f, 0f, new HrotVec3(10 + 5.0 * 29 / Hz, 0, 3), HrotQuat.Identity, 60f, "shot", 29 / Hz));
        }

        // nothing is sent while nothing moves on simulation time (the client's clock is held); a sample that goes out then (a binding value changed) says no motion
        int before = sent.Count;
        rig.Send(new HrotView("Director", "Free", 0, 2f, 0f, new HrotVec3(10 + 5.0 * 29 / Hz, 0, 3), HrotQuat.Identity, 60f, "shot", 29 / Hz));
        Assert.Equal(before + 1, sent.Count);
        Assert.Equal((0f, 0f), (sent[^1].Velocity.X, sent[^1].Acceleration.X));
    }
}
