using SimNetGateway.Engine;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// DD-001 section 12, "Dead reckoning" (R-020, R-021, R-024, R-028, R-029): DR parameters are forwarded, predictions only go
/// into derived attributes, the display understands no DR, the heartbeat sends the position predicted to now.
/// </summary>
public class DeadReckoningTests
{
    private static uint Version(EntityRecord e, AttrId attr) => e.TryGetSlot(attr, out var slot) ? slot.Meta.Version : 0;

    private static GatewayConfiguration WithSettings(NedRprSettings settings) => NedRprConfigurations.Default with { Settings = settings };

    private static GatewayConfiguration Disabling(params string[] rules) =>
        WithSettings(new NedRprSettings { DisabledRules = rules });

    /// <summary>Starts a scenario so that simulation time runs from 0 (the tests then know the state time of each input).</summary>
    private static void StartSession(GatewayHarness h)
    {
        h.NedStep(new NedSimControl(NedControlKind.Load, "dr"));
        h.NedStep(new NedSimControl(NedControlKind.Start, null));
    }

    // ---- forwarding to RPR ------------------------------------------------------------------------------------------

    [Fact]
    public void DR_parameters_are_forwarded_to_RPR_unchanged_and_the_state_time_stays_the_sim_time_of_the_input()
    {
        var h = new GatewayHarness();
        StartSession(h);
        h.Run(2);
        var geo = T.Geo(T.Hawk, heading: 30, pitch: 5, roll: 2, speed: 80);
        var entity = T.PublishNed(h, T.Hawk, "Hawk", 200, geo);
        var update = Assert.Single(h.RprSent<RprObjectUpdate>());

        Assert.Equal((byte)2, update.DeadReckoningAlgorithm);
        Assert.Equal(NedRprConversions.ToRprVector(T.VelocityOf(geo)), update.VelocityVector);
        Assert.Equal(NedRprConversions.ToRprOrientation(T.OrientationOf(geo)), update.Orientation);
        Assert.Equal(NedRprConversions.ToWorldLocation(T.Position(geo)), update.WorldLocation);
        Assert.True(entity.TryGetSlot(h.Schema.StateTime, out var stateTime));
        Assert.Equal(stateTime.Meta.SimTime, stateTime.Value.AsDouble(), 9); // the sim time of the input that carried the state
        Assert.True(stateTime.Value.AsDouble() > 2.0);
        Assert.Equal(h.NedSource, stateTime.Meta.Source);

        h.Run(8); // no predictions, heartbeats or anything else changes the stored state time
        Assert.Equal(stateTime.Value.AsDouble(), entity.Get(h.Schema.StateTime).AsDouble());
        Assert.Equal(1u, Version(entity, h.Schema.StateTime));
    }

    [Fact]
    public void Acceleration_and_angular_velocity_of_an_RPR_entity_are_stored_and_reach_nothing_on_NED()
    {
        var h = new GatewayHarness();
        var full = T.RprFull(0x9001, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5) with
        {
            AccelerationVector = new RprVector(0.5, 0, 0),
            AngularVelocity = new RprVector(0, 0, 0.01),
            DeadReckoningAlgorithm = 4,
        };

        h.RprStep(full);

        var entity = h.EntityOfHandle(full.Handle)!;
        Assert.Equal(new Vec3(0.5, 0, 0), entity.Get(h.Schema.Acceleration).AsVec3());
        Assert.Equal(new Vec3(0, 0, 0.01), entity.Get(h.Schema.AngularVelocity).AsVec3());
        Assert.Equal(4, entity.Get(h.Schema.DrAlgorithm).AsInt());
        Assert.True(entity.TryGetSlot(h.Schema.StateTime, out var stateTime));
        Assert.Equal(h.RprSource, stateTime.Meta.Source);
        Assert.Equal(2, h.NedOut.Count); // master + geo-spatial: NED has no acceleration
    }

    // ---- the processor --------------------------------------------------------------------------------------------

    [Fact]
    public void The_processor_writes_at_10_Hz_for_moving_named_entities_once_for_static_ones_and_never_for_unnamed_ones()
    {
        var h = new GatewayHarness();
        T.PublishNed(h, T.Hawk, "Hawk", 200, T.Geo(T.Hawk, speed: 100));
        T.PublishNed(h, T.Gate, "Gate", 300, T.Geo(T.Gate, lat: 50.5, speed: 0));
        h.NedStep(T.Geo(0x77, speed: 50)); // moving, but without a name: the display cannot show it
        h.NedStep(T.Master(0x78, "NoPosition")); // named, but nowhere
        h.Run(0.5);
        var hawk = h.EntityOfNed(T.Hawk)!;
        var gate = h.EntityOfNed(T.Gate)!;
        uint hawkBefore = Version(hawk, h.Schema.PredictedPosition);

        h.Run(3);

        Assert.InRange(Version(hawk, h.Schema.PredictedPosition) - hawkBefore, 29u, 31u);
        Assert.Equal(1u, Version(gate, h.Schema.PredictedPosition)); // static: predicted once, then unchanged
        Assert.False(h.EntityOfNed(0x77)!.Has(h.Schema.PredictedPosition));
        Assert.False(h.EntityOfNed(0x78)!.Has(h.Schema.PredictedPosition));
        var proc = h.Engine.Sources.Register("proc.dr");
        Assert.True(hawk.TryGetSlot(h.Schema.PredictedPosition, out var slot));
        Assert.Equal(proc, slot.Meta.Source);
        Assert.True(hawk.TryGetSlot(h.Schema.PredictedAt, out var at));
        Assert.Equal(proc, at.Meta.Source);
    }

    [Fact]
    public void The_processor_runs_at_its_configured_period()
    {
        var h = new GatewayHarness(WithSettings(new NedRprSettings { DrProcessorPeriod = TimeSpan.FromSeconds(0.5) }));
        T.PublishNed(h, T.Hawk, "Hawk", 200, T.Geo(T.Hawk, speed: 100));
        var hawk = h.EntityOfNed(T.Hawk)!;
        uint before = Version(hawk, h.Schema.PredictedPosition);

        h.Run(5);

        Assert.InRange(Version(hawk, h.Schema.PredictedPosition) - before, 9u, 11u);
    }

    [Fact]
    public void An_FPW_prediction_equals_the_analytic_position_within_one_centimetre_at_every_write()
    {
        var h = new GatewayHarness();
        StartSession(h);
        var geo = T.Geo(T.Hawk, lat: 50.0, lon: 14.0, alt: 500, heading: 123, pitch: 3, speed: 150);
        T.PublishNed(h, T.Hawk, "Hawk", 200, geo);
        var hawk = h.EntityOfNed(T.Hawk)!;
        double stateTime = hawk.Get(h.Schema.StateTime).AsDouble();
        int checkedWrites = 0;
        uint lastVersion = 0;

        for (int i = 0; i < 20 * 60; i++)
        {
            h.Step();
            uint version = Version(hawk, h.Schema.PredictedPosition);
            if (version == lastVersion)
            {
                continue;
            }

            lastVersion = version;
            double predictedAt = hawk.Get(h.Schema.PredictedAt).AsDouble();
            var analytic = T.Fpw(geo, predictedAt - stateTime);
            Assert.True(T.Distance(analytic, hawk.Get(h.Schema.PredictedPosition).AsVec3()) < 0.01,
                $"prediction at sim time {predictedAt} is off");
            checkedWrites++;
        }

        Assert.InRange(checkedWrites, 195, 205);
    }

    [Fact]
    public void A_new_NED_input_restarts_the_prediction_from_the_new_state()
    {
        var h = new GatewayHarness();
        StartSession(h);
        T.PublishNed(h, T.Hawk, "Hawk", 200, T.Geo(T.Hawk, heading: 90, speed: 100));
        h.Run(5);
        var turn = T.Geo(T.Hawk, lat: 50.004, lon: 14.007, heading: 0, speed: 100);

        h.NedStep(turn);
        double stateTime = h.EntityOfNed(T.Hawk)!.Get(h.Schema.StateTime).AsDouble();
        h.Run(4);

        var hawk = h.EntityOfNed(T.Hawk)!;
        double predictedAt = hawk.Get(h.Schema.PredictedAt).AsDouble();
        Assert.True(predictedAt > stateTime + 3.5);
        Assert.True(T.Distance(T.Fpw(turn, predictedAt - stateTime), hawk.Get(h.Schema.PredictedPosition).AsVec3()) < 0.01);
    }

    [Fact]
    public void An_RVW_prediction_of_an_RPR_entity_follows_the_constant_acceleration_formula()
    {
        var h = new GatewayHarness();
        StartSession(h);
        var full = T.RprFull(0x9001, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5) with
        {
            AccelerationVector = new RprVector(0.5, -0.25, 0.1),
            DeadReckoningAlgorithm = 4, // RVW
        };
        h.RprStep(full);
        var wolf = h.EntityOfHandle(full.Handle)!;
        double stateTime = wolf.Get(h.Schema.StateTime).AsDouble();
        var p0 = T.ToVec3(full.WorldLocation!.Value);
        var v0 = NedRprConversions.ToVec3(full.VelocityVector!.Value);

        h.Run(6);

        double dt = wolf.Get(h.Schema.PredictedAt).AsDouble() - stateTime;
        Assert.True(dt > 5.5);
        var expected = new Vec3(
            p0.X + v0.X * dt + 0.5 * 0.5 * dt * dt,
            p0.Y + v0.Y * dt + 0.5 * -0.25 * dt * dt,
            p0.Z + v0.Z * dt + 0.5 * 0.1 * dt * dt);
        Assert.True(T.Distance(expected, wolf.Get(h.Schema.PredictedPosition).AsVec3()) < 0.01);
    }

    [Fact]
    public void A_static_entity_is_predicted_at_its_stored_position()
    {
        var h = new GatewayHarness();
        var geo = T.Geo(T.Gate, speed: 0);
        T.PublishNed(h, T.Gate, "Gate", 300, geo);

        h.Run(2);

        var gate = h.EntityOfNed(T.Gate)!;
        Assert.Equal(T.Position(geo), gate.Get(h.Schema.PredictedPosition).AsVec3());
        Assert.Equal(1, gate.Get(h.Schema.DrAlgorithm).AsInt());
    }

    [Fact]
    public void Received_Spatial_attributes_are_never_changed_by_predictions()
    {
        var h = new GatewayHarness();
        StartSession(h);
        var geo = T.Geo(T.Hawk, speed: 120);
        var entity = T.PublishNed(h, T.Hawk, "Hawk", 200, geo);
        var attrs = new[] { h.Schema.Position, h.Schema.Orientation, h.Schema.Velocity, h.Schema.DrAlgorithm, h.Schema.StateTime };
        var before = attrs.Select(a => (a, Slot: entity.TryGetSlot(a, out var s) ? s : default)).ToList();

        h.Run(15);

        foreach (var (attr, slot) in before)
        {
            Assert.True(entity.TryGetSlot(attr, out var now));
            Assert.Equal(slot.Value, now.Value);
            Assert.Equal(slot.Meta, now.Meta); // same version, source, correlation and times: not even written
            Assert.Equal(h.NedSource, now.Meta.Source);
        }

        Assert.True(Version(entity, h.Schema.PredictedPosition) > 100);
        Assert.False(entity.Has(h.Schema.Acceleration));
        Assert.False(entity.Has(h.Schema.AngularVelocity));
    }

    [Fact]
    public void No_predictions_and_no_display_positions_are_produced_while_the_session_is_paused()
    {
        var h = new GatewayHarness();
        StartSession(h);
        T.PublishNed(h, T.Hawk, "Hawk", 200, T.Geo(T.Hawk, speed: 100));
        var hawk = h.EntityOfNed(T.Hawk)!;
        h.Run(2);
        h.NedStep(new NedSimControl(NedControlKind.Pause, null));
        h.Run(0.3); // lets the last pre-pause timer firing settle
        uint versionAtPause = Version(hawk, h.Schema.PredictedPosition);
        int displayAtPause = h.DisplayOut.Count;
        var positionAtPause = hawk.Get(h.Schema.PredictedPosition).AsVec3();

        h.Run(5);

        Assert.Equal(versionAtPause, Version(hawk, h.Schema.PredictedPosition));
        Assert.Equal(displayAtPause, h.DisplayOut.Count);
        Assert.Equal(positionAtPause, hawk.Get(h.Schema.PredictedPosition).AsVec3());

        h.NedStep(new NedSimControl(NedControlKind.Resume, null));
        h.Run(1);

        Assert.True(Version(hawk, h.Schema.PredictedPosition) > versionAtPause + 8);
        Assert.True(h.DisplayOut.Count > displayAtPause + 8);
    }

    // ---- the display --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_display_gets_a_position_for_every_derived_change_and_nothing_else()
    {
        var h = new GatewayHarness();
        StartSession(h);
        var geo = T.Geo(T.Hawk, heading: 90, speed: 100);
        T.PublishNed(h, T.Hawk, "Hawk", 200, geo);
        var hawk = h.EntityOfNed(T.Hawk)!;

        h.Run(2);

        var positions = h.DisplaySent<DisplayEntityPosition>();
        Assert.Equal((int)Version(hawk, h.Schema.PredictedPosition), positions.Count);
        Assert.All(positions, p => Assert.Equal("Hawk", p.Label));
        Assert.Equal(positions.Count, h.DisplayOut.Count);
        var last = positions[^1];
        NedRprConversions.EcefToGeodetic(hawk.Get(h.Schema.PredictedPosition).AsVec3(), out double lat, out double lon, out double alt);
        Assert.Equal(lat, last.LatitudeDeg, 9);
        Assert.Equal(lon, last.LongitudeDeg, 9);
        Assert.Equal(alt, last.Altitude, 5);
        Assert.InRange(last.HeadingDeg, 89.9, 90.1); // the stored geocentric orientation seen from the predicted position
        // Heading 90 degrees means moving east: the longitude grows with the prediction.
        Assert.True(positions[^1].LongitudeDeg > positions[0].LongitudeDeg);
        Assert.Equal(geo.LatitudeDeg, positions[0].LatitudeDeg, 3);
    }

    [Fact]
    public void The_display_label_comes_from_Identity_Name_and_never_from_the_entity_id()
    {
        var h = new GatewayHarness();
        var hawk = T.PublishNed(h, T.Hawk, "Hawk-Lead");
        h.Run(0.5);

        h.NedStep(T.Master(T.Hawk, "Renamed")); // the label is bound on the first publish and does not follow renames
        h.Run(0.5);

        var positions = h.DisplaySent<DisplayEntityPosition>();
        Assert.NotEmpty(positions);
        Assert.All(positions, p => Assert.Equal("Hawk-Lead", p.Label));
        Assert.True(h.DisplayLabels.TryGetKey(hawk.Id, out var label));
        Assert.Equal("Hawk-Lead", label);
        Assert.NotEqual(hawk.Id.Value.ToString(), label);
    }

    [Fact]
    public void A_name_that_is_taken_gets_an_anonymous_label_that_is_not_the_entity_id()
    {
        var h = new GatewayHarness();
        var a = T.PublishNed(h, T.Hawk, "Twin");
        var b = T.PublishNed(h, T.Viper, "Twin", 100, T.Geo(T.Viper, lat: 50.1));
        var c = T.PublishNed(h, T.Gate, "Twin", 300, T.Geo(T.Gate, lat: 50.2));
        h.Run(0.5);

        h.DisplayLabels.TryGetKey(a.Id, out var la);
        h.DisplayLabels.TryGetKey(b.Id, out var lb);
        h.DisplayLabels.TryGetKey(c.Id, out var lc);

        Assert.Equal("Twin", la);
        Assert.Equal("E1", lb);
        Assert.Equal("E2", lc);
        Assert.Equal(3, h.DisplayLabels.Count);
        Assert.All(h.DisplaySent<DisplayEntityPosition>(), p => Assert.Contains(p.Label, new[] { "Twin", "E1", "E2" }));
    }

    [Fact]
    public void The_display_is_told_when_a_labelled_entity_disappears_and_the_label_is_free_again()
    {
        var h = new GatewayHarness();
        T.PublishNed(h, T.Hawk, "Hawk");
        h.Run(0.5);

        h.NedStep(new NedEntityDisposed(T.Hawk));

        Assert.Equal("Hawk", Assert.Single(h.DisplaySent<DisplayEntityRemoved>()).Label);
        Assert.Equal(0, h.DisplayLabels.Count);
        T.PublishNed(h, T.Viper, "Hawk", 100, T.Geo(T.Viper));
        h.Run(0.5);
        Assert.Equal("Hawk", h.DisplaySent<DisplayEntityPosition>()[^1].Label);
    }

    [Fact]
    public void An_unnamed_entity_is_never_shown_and_never_removed_on_the_display()
    {
        var h = new GatewayHarness();
        h.NedStep(T.Geo(0x77, speed: 20));
        h.Run(1);
        h.NedStep(new NedEntityDisposed(0x77));

        Assert.Equal(0, h.DisplayOut.Count);
    }

    [Fact]
    public void A_derived_position_of_an_unnamed_entity_is_not_shown_by_the_display_itself()
    {
        var h = new GatewayHarness();
        h.NedStep(T.Geo(0x77, speed: 20));
        var ghost = h.EntityOfNed(0x77)!;

        // A custom processor (not the gateway's, which only selects named entities) derives a position for it.
        var changes = h.Engine.BeginChange(h.Engine.Sources.Register("proc.dr"));
        changes.Set(ghost.Id, h.Schema.PredictedPosition, AttrValue.From(T.Position(T.Geo(0x77))));
        changes.Commit();
        h.Step();

        Assert.True(ghost.Has(h.Schema.PredictedPosition));
        Assert.Equal(0, h.DisplayOut.Count);
        Assert.Equal(0, h.DisplayLabels.Count);
    }

    [Fact]
    public void Disabling_the_extrapolation_rule_stops_the_display_while_RPR_is_unaffected()
    {
        var h = new GatewayHarness(Disabling("DrExtrapolation/Extrapolate"));
        var entity = T.PublishNed(h, T.Hawk, "Hawk", 200, T.Geo(T.Hawk, speed: 100));

        h.Run(2);
        h.NedStep(T.Geo(T.Hawk, lat: 50.01, speed: 100));
        h.NedStep(new NedEntityDamage(T.Hawk, 1));
        h.Run(2);

        Assert.Equal(0, h.DisplayOut.Count);
        Assert.False(entity.Has(h.Schema.PredictedPosition));
        Assert.Equal(0, h.Rule("DrExtrapolation/Extrapolate").FireCount);
        Assert.Equal(3, h.RprSent<RprObjectUpdate>().Count); // the first full update, the move and the damage
    }

    // ---- the heartbeat --------------------------------------------------------------------------------------------

    private static bool IsFull(RprObjectUpdate u) =>
        u.Marking is not null && u.EntityType is not null && u.EntityIdentifier is not null && u.WorldLocation is not null &&
        u.Orientation is not null && u.VelocityVector is not null && u.DeadReckoningAlgorithm is not null;

    [Fact]
    public void The_heartbeat_resends_a_full_update_after_five_seconds_of_silence_with_the_position_predicted_to_now()
    {
        var h = new GatewayHarness();
        StartSession(h);
        var geo = T.Geo(T.Hawk, speed: 100, heading: 90);
        T.PublishNed(h, T.Hawk, "Hawk", 200, geo);
        var entity = h.EntityOfNed(T.Hawk)!;
        double stateTime = entity.Get(h.Schema.StateTime).AsDouble();
        var first = Assert.Single(h.RprOut.Messages, m => m.Message is RprObjectUpdate);

        h.Run(20);

        var beats = h.RprOut.Messages.Where(m => m.Message is RprObjectUpdate).Skip(1).ToList();
        Assert.InRange(beats.Count, 3, 4);
        double previous = first.WallTime;
        foreach (var beat in beats)
        {
            var update = (RprObjectUpdate)beat.Message;
            Assert.True(IsFull(update), "a heartbeat is a full update");
            double silence = beat.WallTime - previous;
            Assert.InRange(silence, 5.0, 6.05); // not before five seconds, and promptly after (1 s check period)
            previous = beat.WallTime;

            var analytic = T.Fpw(geo, beat.SimTime - stateTime);
            var sent = T.ToVec3(update.WorldLocation!.Value);
            Assert.True(T.Distance(analytic, sent) < 1.0, "the heartbeat position is not the position predicted to now");
            Assert.True(T.Distance(T.Position(geo), sent) > 400, "the heartbeat sent the stored position, not the predicted one");
            Assert.Equal(NedRprConversions.ToRprVector(T.VelocityOf(geo)), update.VelocityVector); // other values as stored
            Assert.Equal(new DisEntityId(1, 3001, 1), update.EntityIdentifier);
        }

        // The stored state was not touched by the heartbeat.
        Assert.Equal(T.Position(geo), entity.Get(h.Schema.Position).AsVec3());
        Assert.Equal(1u, Version(entity, h.Schema.Position));
    }

    [Fact]
    public void The_heartbeat_of_a_static_entity_repeats_its_stored_position()
    {
        var h = new GatewayHarness();
        var geo = T.Geo(T.Gate, speed: 0);
        T.PublishNed(h, T.Gate, "Gate", 300, geo);

        h.Run(12);

        var updates = h.RprSent<RprObjectUpdate>();
        Assert.InRange(updates.Count, 3, 4);
        Assert.All(updates, u => Assert.Equal(NedRprConversions.ToWorldLocation(T.Position(geo)), u.WorldLocation));
        Assert.All(updates, u => Assert.True(IsFull(u)));
    }

    [Fact]
    public void The_heartbeat_stays_quiet_while_updates_flow()
    {
        var h = new GatewayHarness();
        T.PublishNed(h, T.Hawk, "Hawk", 200, T.Geo(T.Hawk, speed: 100));
        h.ClearOutput();

        // Partial updates every 4 s (shorter than the heartbeat period) for 40 s.
        for (int i = 1; i <= 10; i++)
        {
            h.Run(4);
            h.NedStep(T.Geo(T.Hawk, speed: 100 + i));
        }

        var updates = h.RprSent<RprObjectUpdate>();
        Assert.Equal(10, updates.Count);
        Assert.All(updates, u => Assert.False(IsFull(u)));
        Assert.All(updates, u => Assert.NotNull(u.VelocityVector));
    }

    [Fact]
    public void A_partial_update_counts_as_a_send_for_the_heartbeat()
    {
        var h = new GatewayHarness();
        T.PublishNed(h, T.Hawk, "Hawk", 200, T.Geo(T.Hawk, speed: 100));
        h.ClearOutput();

        h.Run(4.5);
        h.NedStep(new NedEntityDamage(T.Hawk, 1));
        double partialAt = h.WallTime;
        h.Run(10);

        var heartbeats = h.RprOut.Messages.Where(m => m.Message is RprObjectUpdate u && IsFull(u)).ToList();
        Assert.NotEmpty(heartbeats);
        Assert.InRange(heartbeats[0].WallTime - partialAt, 5.0, 6.05); // counted from the partial update, not from the first full one
    }

    [Fact]
    public void The_heartbeat_does_not_touch_an_entity_whose_registration_is_pending()
    {
        var h = new GatewayHarness { DeliverRti = false };
        h.NedStep(T.Master(T.Hawk, "Hawk"));
        h.NedStep(T.Geo(T.Hawk));

        h.Run(13);

        Assert.Empty(h.RprSent<RprObjectUpdate>());
        Assert.Single(h.RprSent<RprRegisterObject>());
    }

    [Fact]
    public void The_heartbeat_period_is_a_setting_and_its_rule_can_be_disabled()
    {
        var fast = new GatewayHarness(WithSettings(new NedRprSettings { HeartbeatPeriod = TimeSpan.FromSeconds(2) }));
        T.PublishNed(fast, T.Hawk, "Hawk");
        fast.Run(10);
        var off = new GatewayHarness(Disabling("RprHeartbeatEgress/Heartbeat"));
        T.PublishNed(off, T.Hawk, "Hawk");
        off.Run(20);

        Assert.InRange(fast.RprSent<RprObjectUpdate>().Count, 4, 5);
        Assert.Single(off.RprSent<RprObjectUpdate>());
    }

    [Fact]
    public void The_heartbeat_during_a_pause_predicts_to_the_frozen_simulation_time()
    {
        var h = new GatewayHarness();
        StartSession(h);
        var geo = T.Geo(T.Hawk, speed: 100);
        T.PublishNed(h, T.Hawk, "Hawk", 200, geo);
        double stateTime = h.EntityOfNed(T.Hawk)!.Get(h.Schema.StateTime).AsDouble();
        h.Run(1);
        h.NedStep(new NedSimControl(NedControlKind.Pause, null));
        double frozen = h.SimTime;
        h.ClearOutput();

        h.Run(14);

        var beats = h.RprSent<RprObjectUpdate>();
        Assert.True(beats.Count >= 2);
        var expected = T.Fpw(geo, frozen - stateTime);
        Assert.All(beats, u => Assert.True(T.Distance(expected, T.ToVec3(u.WorldLocation!.Value)) < 1.0));
    }
}
