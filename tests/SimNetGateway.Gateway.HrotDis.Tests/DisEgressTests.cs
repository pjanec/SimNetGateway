using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Dis7;
using SimNetGateway.Protocol.Hrot;
using static SimNetGateway.Gateway.HrotDis.Tests.Support;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// HROT entities on DIS (H2b egress, DD-006 6.1 and 7): one full Entity State PDU per send, thresholds, heartbeat, identity and damage
/// changes, deactivation, number allocation with quarantine, the session and origin gates. Every step also runs the
/// <see cref="DisInvariantChecker"/> (full PDU against an independent oracle, threshold and heartbeat invariants, no redundant send).
/// </summary>
public class DisEgressTests
{
    private static HrotDisHarness Live(GatewayConfiguration? config = null, DisNumbering? numbering = null, bool checker = true)
    {
        var h = new HrotDisHarness(config, numbering: numbering, installChecker: checker);
        h.GoLive();
        return h;
    }

    private static List<EntityStatePdu> Espdus(HrotDisHarness h) => h.DisChecker!.States.Select(s => s.Pdu).ToList();

    /// <summary>The number of Entity State PDUs sent so far (Start/Resume and the like are not counted).</summary>
    private static void SentCount(HrotDisHarness h, int expected)
    {
        int actual = h.DisChecker!.States.Count;
        Assert.True(actual == expected, $"expected {expected} Entity State PDU(s) so far but {actual} were sent");
    }

    private static HrotQuat Yaw(double degrees)
    {
        double half = degrees * Math.PI / 360.0;
        return new HrotQuat(0, 0, Math.Sin(half), Math.Cos(half));
    }

    private static GatewayConfiguration With(Func<DisConfiguration, DisConfiguration> dis) =>
        GatewayConfiguration.CreateDefault() with { Dis = dis(new DisConfiguration { ExerciseId = 1 }) };

    [Fact]
    public void A_hrot_entity_is_sent_as_one_full_entity_state_pdu_in_the_dis_frames()
    {
        var h = Live();
        var enu = new HrotVec3(100, 200, 10);
        h.HrotStep(HrotDisHarness.State(7, enu, tkb: 100, marking: "TANK-01", force: 2, velocity: new HrotVec3(3, 4, 0),
            rotation: Yaw(30)));

        var pdu = Assert.Single(Espdus(h));
        SentCount(h, 1);
        Assert.Equal(PduTypes.EntityState, pdu.PduType);
        Assert.Equal(new DisEntityKey(1, 3001, 1), DisConversions.KeyOf(pdu.EntityID));
        Assert.Equal(1, pdu.ExerciseID);
        Assert.Equal(new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack(), DisConversions.PackType(pdu.EntityType));
        Assert.Equal("TANK-01", EntityMarkingText.Get(pdu.Marking));
        Assert.Equal(2, pdu.ForceId);
        Assert.Equal(DrAlgorithms.Fpw, pdu.DeadReckoningParameters.DeadReckoningAlgorithm);
        Assert.Equal(0u, pdu.EntityAppearance);
        Assert.False(DisTimestamp.IsAbsolute(pdu.Timestamp));
        Support.Near(h.Frame!.PointToEcef(V(enu)), DisConversions.ToVec(pdu.EntityLocation), 1e-6, "location");
        Support.Near(h.Frame.VectorToEcef(new Vec3(3, 4, 0)), DisConversions.ToVec(pdu.EntityLinearVelocity), 1e-4, "velocity");
        Assert.True(h.HrotIds.TryResolve(7, out var id));
        Assert.Equal(new DisEntityKey(1, 3001, 1), h.DisKeyOf(id));
    }

    [Fact]
    public void The_marking_is_truncated_to_eleven_ascii_characters()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, marking: "A-VERY-LONG-NAME-é"));
        Assert.Equal("A-VERY-LONG", EntityMarkingText.Get(Espdus(h).Single().Marking));
    }

    [Fact]
    public void Nothing_is_sent_before_the_origin_is_valid()
    {
        var h = new HrotDisHarness();
        h.Session(ClusterPhase.Live);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 2, 3)));
        h.HrotStep(HrotDisHarness.State(2, new HrotVec3(1, 2, 3)));
        SentCount(h, 0);
        Assert.NotNull(h.EntityOfHrot(1));

        h.Origin(50, 14, 300);
        h.Run(1);
        SentCount(h, 0); // the entities have no position yet (their next state brings one)
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 2, 3)));
        SentCount(h, 1);
        Assert.Equal(1, Espdus(h).Count(p => DisConversions.KeyOf(p.EntityID).Number == 1));
    }

    [Fact]
    public void Nothing_is_sent_in_edit_and_the_first_pdus_follow_the_start()
    {
        var h = new HrotDisHarness();
        h.Origin(50, 14, 300);
        h.Session(ClusterPhase.Edit);
        for (int i = 1; i <= 3; i++)
        {
            h.HrotStep(HrotDisHarness.State(i, new HrotVec3(i * 10, 0, 0)));
        }

        h.Run(2);
        SentCount(h, 0);

        h.Session(ClusterPhase.Live);
        SentCount(h, 3);
        Assert.Equal(3, Espdus(h).Select(p => p.EntityID.EntityNumber).Distinct().Count());
        h.Run(1);
        SentCount(h, 3);
    }

    [Fact]
    public void A_replay_session_still_sends_hrot_entities_to_dis()
    {
        var h = new HrotDisHarness();
        h.Origin(50, 14, 300);
        h.Session(ClusterPhase.Replay);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 2, 3)));
        SentCount(h, 1);
    }

    [Fact]
    public void Position_drift_sends_only_beyond_the_threshold()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        SentCount(h, 1);

        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0.9, 0, 0)));
        SentCount(h, 1);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(-0.9, 0.3, 0)));
        SentCount(h, 1);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1.1, 0, 0)));
        SentCount(h, 2);
        Support.Near(h.Frame!.PointToEcef(new Vec3(1.1, 0, 0)), DisConversions.ToVec(Espdus(h)[1].EntityLocation), 1e-6, "second location");

        // the new PDU is the new reference: 0.9 m from it is quiet again, 1.1 m is not
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(2.0, 0, 0)));
        SentCount(h, 2);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(2.3, 0, 0)));
        SentCount(h, 3);
    }

    [Fact]
    public void Orientation_drift_alone_sends_beyond_the_threshold()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(HrotDisHarness.State(1, rotation: Yaw(2)));
        SentCount(h, 1);
        h.HrotStep(HrotDisHarness.State(1, rotation: Yaw(-2.5)));
        SentCount(h, 1);
        h.HrotStep(HrotDisHarness.State(1, rotation: Yaw(4)));
        SentCount(h, 2);
    }

    [Fact]
    public void A_change_of_velocity_is_caught_by_the_per_tick_drift_check_when_no_further_hrot_state_arrives()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0.5, 0, 0), velocity: new HrotVec3(1, 0, 0))); // within the threshold: quiet for now
        SentCount(h, 1);
        double t0 = h.Clock.Now;
        h.Run(1.0); // HROT sends nothing more; the DIS model (standing still) drifts away from the pivot's FPW model
        SentCount(h, 2);
        double sentAt = h.DisChecker!.States[1].Wall;
        Assert.InRange(sentAt - t0, 0.5 - 1e-9, 0.5 + HrotDisHarness.Dt + 1e-9); // 1 m after 0.5 m + 1 m/s * 0.5 s
        Support.Near(h.Frame!.PointToEcef(new Vec3(0.5 + (sentAt - t0), 0, 0)), DisConversions.ToVec(Espdus(h)[1].EntityLocation), 1e-6, "location");
    }

    [Fact]
    public void The_thresholds_come_from_the_configuration()
    {
        var h = Live(With(d => d with { PositionThresholdM = 5.0, OrientationThresholdDeg = 20.0 }));
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(4, 0, 0), rotation: Yaw(15)));
        SentCount(h, 1);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(6, 0, 0)));
        SentCount(h, 2);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(6, 0, 0), rotation: Yaw(25)));
        SentCount(h, 3);
    }

    [Fact]
    public void A_moving_entity_that_follows_its_dr_model_sends_only_the_heartbeat()
    {
        var h = Live();
        var v = new HrotVec3(10, 0, 0);
        double t0 = h.Clock.Now + HrotDisHarness.Dt;
        for (int i = 0; i < 12 * 60; i++)
        {
            double t = h.Clock.Now + HrotDisHarness.Dt - t0;
            h.HrotStep(HrotDisHarness.State(1, new HrotVec3(10 * t, 0, 0), velocity: v));
        }

        var times = h.DisChecker!.States.Select(s => s.Wall).ToList();
        Assert.Equal(3, times.Count); // the first PDU, then one per heartbeat
        Assert.InRange(times[1] - times[0], 5.0, 5.0 + HrotDisHarness.Dt + 1e-9);
        Assert.InRange(times[2] - times[1], 5.0, 5.0 + HrotDisHarness.Dt + 1e-9);
    }

    [Fact]
    public void A_silent_entity_is_refreshed_at_the_heartbeat_with_its_position_predicted_to_now()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0), velocity: new HrotVec3(10, 0, 0)));
        h.Run(11);
        var states = h.DisChecker!.States;
        Assert.Equal(3, states.Count);
        var first = DisConversions.ToVec(states[0].Pdu.EntityLocation);
        var second = DisConversions.ToVec(states[1].Pdu.EntityLocation);
        double elapsed = states[1].Wall - states[0].Wall;
        Assert.InRange((second - first).Length, 10 * elapsed - 1e-4, 10 * elapsed + 1e-4);
        Assert.True((second - first).Length > 49.0);
    }

    [Fact]
    public void The_heartbeat_comes_from_the_configuration()
    {
        var h = Live(With(d => d with { HeartbeatS = 2.0 }));
        h.HrotStep(HrotDisHarness.State(1));
        h.Run(7);
        SentCount(h, 4); // t = 0, 2, 4, 6
    }

    [Fact]
    public void A_change_of_damage_marking_force_or_type_is_sent_at_once_and_an_unchanged_state_is_not()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, marking: "T1", force: 1, tkb: 100));
        h.HrotStep(HrotDisHarness.State(1, marking: "T1", force: 1, tkb: 100));
        SentCount(h, 1);

        h.HrotStep(HrotDisHarness.State(1, marking: "T1", force: 1, tkb: 100, health: 40)); // 40 % = moderate
        SentCount(h, 2);
        Assert.Equal(2, DisConversions.DamageOf(Espdus(h)[1].EntityAppearance));
        h.HrotStep(HrotDisHarness.State(1, marking: "T1", force: 1, tkb: 100, health: 0));
        Assert.Equal(3, DisConversions.DamageOf(Espdus(h)[2].EntityAppearance));
        h.HrotStep(HrotDisHarness.State(1, marking: "T2", force: 1, tkb: 100, health: 0));
        Assert.Equal("T2", EntityMarkingText.Get(Espdus(h)[3].Marking));
        h.HrotStep(HrotDisHarness.State(1, marking: "T2", force: 3, tkb: 100, health: 0));
        Assert.Equal(3, Espdus(h)[4].ForceId);
        h.HrotStep(HrotDisHarness.State(1, marking: "T2", force: 3, tkb: 101, health: 0));
        Assert.Equal(new DisTypeCode(1, 1, 225, 6, 1, 0, 0).Pack(), DisConversions.PackType(Espdus(h)[5].EntityType));
        SentCount(h, 6);
    }

    [Fact]
    public void Every_pdu_is_complete_even_when_only_the_damage_changed()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 6, 7), marking: "FULL", force: 2, velocity: new HrotVec3(1, 2, 0), rotation: Yaw(40)));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 6, 7), marking: "FULL", force: 2, velocity: new HrotVec3(1, 2, 0), rotation: Yaw(40), health: 10));
        var damaged = Espdus(h)[1];
        Assert.Equal("FULL", EntityMarkingText.Get(damaged.Marking));
        Assert.Equal(2, damaged.ForceId);
        Assert.NotEqual(0.0, damaged.EntityLocation.X);
        Assert.NotEqual(0f, damaged.EntityOrientation.Psi);
        Assert.NotEqual(0f, damaged.EntityLinearVelocity.X + damaged.EntityLinearVelocity.Y + damaged.EntityLinearVelocity.Z);
        Assert.NotEqual(0, DisConversions.PackType(damaged.EntityType));
        Assert.Equal(2, damaged.DeadReckoningParameters.DeadReckoningAlgorithm);
    }

    [Fact]
    public void A_removed_entity_gets_exactly_one_deactivated_pdu_and_then_silence()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(HrotDisHarness.State(2));
        h.HrotStep(new HrotEntityRemoved(1));
        var states = Espdus(h);
        Assert.Equal(3, states.Count);
        var last = states[2];
        Assert.True(EntityAppearance.GetDeactivated(last.EntityAppearance));
        Assert.Equal(states[0].EntityID.EntityNumber, last.EntityID.EntityNumber);
        Assert.Equal(1, states.Count(p => EntityAppearance.GetDeactivated(p.EntityAppearance)));
        h.Run(8);
        Assert.Equal(1, Espdus(h).Count(p => EntityAppearance.GetDeactivated(p.EntityAppearance)));
    }

    [Fact]
    public void An_entity_that_was_never_sent_is_removed_without_a_pdu()
    {
        var h = new HrotDisHarness();
        h.Origin(50, 14, 300);
        h.Session(ClusterPhase.Edit);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 1, 1)));
        h.HrotStep(new HrotEntityRemoved(1));
        SentCount(h, 0);
    }

    [Fact]
    public void Unloading_the_session_deactivates_every_sent_entity_once()
    {
        var h = Live();
        for (int i = 1; i <= 4; i++)
        {
            h.HrotStep(HrotDisHarness.State(i, new HrotVec3(i, 0, 0)));
        }

        SentCount(h, 4);
        h.Session(ClusterPhase.Idle);
        var finals = Espdus(h).Where(p => EntityAppearance.GetDeactivated(p.EntityAppearance)).ToList();
        Assert.Equal(4, finals.Count);
        Assert.Equal(4, finals.Select(p => p.EntityID.EntityNumber).Distinct().Count());
        h.Run(8);
        SentCount(h, 8);
    }

    [Fact]
    public void Entity_numbers_are_allocated_ascending_under_the_gateways_site_and_application()
    {
        var h = Live();
        var config = GatewayConfiguration.CreateDefault() with { Shared = new SharedConfiguration { Site = 9, Application = 77 } };
        var other = Live(config);
        for (int i = 1; i <= 3; i++)
        {
            h.HrotStep(HrotDisHarness.State(i));
            other.HrotStep(HrotDisHarness.State(i));
        }

        Assert.Equal([1, 2, 3], Espdus(h).Select(p => (int)p.EntityID.EntityNumber).ToArray());
        Assert.All(Espdus(h), p => Assert.Equal(new DisEntityKey(1, 3001, p.EntityID.EntityNumber), DisConversions.KeyOf(p.EntityID)));
        Assert.All(Espdus(other), p => Assert.Equal(new DisEntityKey(9, 77, p.EntityID.EntityNumber), DisConversions.KeyOf(p.EntityID)));
    }

    [Fact]
    public void A_released_number_stays_in_quarantine_and_comes_back_afterwards()
    {
        var h = Live(numbering: new DisNumbering(1, 3, 10.0));
        h.DisChecker!.ExpectAllSent = false;
        for (int i = 1; i <= 3; i++)
        {
            h.HrotStep(HrotDisHarness.State(i, new HrotVec3(i * 100, 0, 0)));
        }

        h.HrotStep(new HrotEntityRemoved(2)); // number 2 is released
        var pool = (NumberPool)((MappedAllocator<DisEntityKey>)h.DisIds!.Allocator!).Numbers;
        Assert.Equal(1, pool.InQuarantine);

        h.HrotStep(HrotDisHarness.State(4, new HrotVec3(400, 0, 0)));
        SentCount(h, 4); // 3 first PDUs and the final one of 2: entity 4 got no number
        Assert.Equal(1, h.WarningCount(nameof(DisPublication)));
        h.HrotStep(HrotDisHarness.State(4, new HrotVec3(400, 0, 0)));
        Assert.Equal(1, h.WarningCount(nameof(DisPublication))); // once per entity

        h.Run(10.5);
        h.HrotStep(HrotDisHarness.State(4, new HrotVec3(400, 1, 0)));
        var last = Espdus(h).Last();
        Assert.Equal(2, last.EntityID.EntityNumber); // the quarantined number is back
        Assert.False(EntityAppearance.GetDeactivated(last.EntityAppearance));
    }

    [Fact]
    public void An_entity_without_a_number_is_sent_at_the_next_change_after_one_became_free()
    {
        var h = Live(numbering: new DisNumbering(1, 1, 0.0));
        h.DisChecker!.ExpectAllSent = false;
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(HrotDisHarness.State(2, new HrotVec3(500, 0, 0)));
        SentCount(h, 1);
        Assert.Null(h.DisKeyOf(h.EntityOfHrot(2)!.Id));
        h.HrotStep(new HrotEntityRemoved(1));
        h.HrotStep(HrotDisHarness.State(2, new HrotVec3(500, 0, 0), marking: "NOW"));
        SentCount(h, 3);
        Assert.Equal("NOW", EntityMarkingText.Get(Espdus(h).Last().Marking));
    }

    [Fact]
    public void Entities_of_origin_dis_are_never_sent_and_the_gateways_own_site_is_never_a_dis_entity()
    {
        var h = Live();
        var id = h.DisCreate(h.DisStateAt(new HrotVec3(10, 10, 0)));
        h.Run(6);
        h.DisUpdate(id, h.DisStateAt(new HrotVec3(20, 10, 0)));
        h.Run(1);
        SentCount(h, 0);
        Assert.Null(h.DisKeyOf(id));
    }

    [Fact]
    public void Unit_entities_are_not_forwarded_unless_configured()
    {
        var unit = new DisTypeCode(11, 1, 225, 1, 0, 0, 0).Pack();
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, disType: unit));
        h.HrotStep(HrotDisHarness.State(2));
        SentCount(h, 1);

        var forwarding = GatewayConfiguration.CreateDefault() with { Shared = new SharedConfiguration { ForwardUnitEntities = true } };
        var f = Live(forwarding);
        f.HrotStep(HrotDisHarness.State(1, disType: unit));
        SentCount(f, 1);
        Assert.Equal(11, Espdus(f).Single().EntityType.EntityKind);
    }

    [Fact]
    public void A_hrot_provided_dis_type_is_used_as_it_is()
    {
        var h = Live();
        long custom = new DisTypeCode(1, 2, 71, 5, 3, 2, 1).Pack();
        h.HrotStep(HrotDisHarness.State(1, tkb: 9999, disType: custom));
        Assert.Equal(custom, DisConversions.PackType(Espdus(h).Single().EntityType));
    }

    [Fact]
    public void A_tkb_type_without_a_dis_counterpart_is_not_published()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, tkb: 9999));
        h.Run(6);
        SentCount(h, 0);
    }

    [Fact]
    public void A_paused_session_keeps_the_heartbeats_going()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1));
        h.Session(ClusterPhase.Live, advancing: false);
        h.Run(11);
        SentCount(h, 3);
    }

    [Fact]
    public void A_disabled_contributor_leaves_its_group_out_of_every_pdu()
    {
        var h = new HrotDisHarness(installChecker: false) { CheckDis = false };
        h.GoLive();
        h.Engine.Rules.First(r => r.FullName == "DisEntityStateEgress/Damage").Enabled = false;
        h.HrotStep(HrotDisHarness.State(1, health: 0, marking: "KEPT"));
        var pdu = h.DisOut!.OfType<EntityStatePdu>().Single();
        Assert.Equal(0, DisConversions.DamageOf(pdu.EntityAppearance)); // destroyed in the pivot, but the damage contributor is off
        Assert.Equal("KEPT", EntityMarkingText.Get(pdu.Marking));

        h.Engine.Rules.First(r => r.FullName == "DisEntityStateEgress/Spatial").Enabled = false;
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(500, 0, 0), marking: "CHANGED"));
        var second = h.DisOut.OfType<EntityStatePdu>().Last();
        Assert.Equal("CHANGED", EntityMarkingText.Get(second.Marking));
        Assert.Equal(0.0, second.EntityLocation.X); // the spatial group is off: its fields stay zero
    }

    [Fact]
    public void A_body_axis_algorithm_sends_the_body_axis_velocity()
    {
        var h = new HrotDisHarness(installChecker: false) { CheckDis = false };
        h.GoLive();
        // an entity written straight into the pivot as the HROT side, with a body-axis algorithm
        var s = h.Schema;
        var changes = h.Engine.BeginChange(h.HrotSource);
        var id = changes.CreateEntity();
        changes.Set(id, s.EntityType, AttrValue.From(new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack()));
        changes.Set(id, s.Force, AttrValue.From(1L));
        changes.Set(id, s.Position, AttrValue.From(h.Frame!.PointToEcef(new Vec3(0, 0, 0))));
        changes.Set(id, s.Velocity, AttrValue.From(new Vec3(10, 0, 0)));
        changes.Set(id, s.Acceleration, AttrValue.From(new Vec3(1, 0, 0)));
        changes.Set(id, s.DrAlgorithm, AttrValue.From(9L)); // FVB
        changes.Set(id, s.StateTime, AttrValue.From(h.Engine.WallTime - 2.0));
        changes.Commit();
        var pdu = h.DisOut!.OfType<EntityStatePdu>().Single();
        Assert.Equal(9, pdu.DeadReckoningParameters.DeadReckoningAlgorithm);
        Assert.Equal(12.0, pdu.EntityLinearVelocity.X, 3); // v + a * dt in body axes (dt = 2 s)
        Assert.Equal(1.0, pdu.DeadReckoningParameters.EntityLinearAcceleration.X, 3);
    }

    [Fact]
    public void Entity_state_pdus_carry_the_time_of_the_send_as_a_relative_timestamp()
    {
        var h = Live();
        h.Run(3);
        h.HrotStep(HrotDisHarness.State(1));
        var (pdu, wall) = h.DisChecker!.States.Single();
        Assert.False(DisTimestamp.IsAbsolute(pdu.Timestamp));
        Assert.Equal(wall % 3600.0, DisTimestamp.ToSecondsPastHour(pdu.Timestamp), 6);
    }

    [Fact]
    public void An_origin_change_does_not_move_the_hrot_entities_ecef_position()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(10, 20, 0)));
        var before = DisConversions.ToVec(Espdus(h).Single().EntityLocation);
        h.Origin(51, 15, 100, generation: 2);
        h.Run(1);
        SentCount(h, 1); // nothing re-sent, the entity's ECEF is what it was
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(10, 20, 0)));
        // the same local position in the new frame is a different place: the PDU follows the move
        var after = DisConversions.ToVec(Espdus(h).Last().EntityLocation);
        Assert.True((after - before).Length > 1000.0);
    }
}
