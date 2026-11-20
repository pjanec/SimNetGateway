using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>DPS round 3 in the plugin (DD-019 3.1, 3.2, 3.4, 3.5): the suggested view with its change detection and cuts, impacts, the round's launch and burn, airborne animals.</summary>
public class DpsRoundThreeTests
{
    private static HrotView View(double simTime = 1.0, string source = "Director", string mode = "Shoulder", int target = 12, string shot = "Take 1", double x = 10, float distance = 2.2f,
        HrotQuat? rotation = null) =>
        new(source, mode, target, distance, -1f, new HrotVec3(x, 20, 3), rotation ?? HrotQuat.Identity, 60f, shot, simTime);

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
}
