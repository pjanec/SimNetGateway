using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using SimNetGateway.Protocol.Rpr;
using static SimNetGateway.Gateway.HrotDis.Tests.RprMessages;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// The HLA side of the HROT gateway (T-024, DD-009 section 3 item 3): every row of the message table in both directions, the origin
/// rule (R-116), registration (R-115), the send policy with the hla thresholds (R-117), the stale reaper. Every step also runs the
/// invariants of <see cref="RprInvariantChecker"/> and the H2a ones of <see cref="InvariantChecker"/>.
/// </summary>
public class RprSideTests
{
    private static List<RprObjectUpdate> Updates(HrotDisHarness h) => h.RprChecker!.Updates.ToList();

    // ---- inbound: HLA -> pivot -> HROT ----------------------------------------------------------------------------------

    [Fact]
    public void An_object_update_creates_an_rpr_entity_in_the_pivot_and_a_replica_in_hrot()
    {
        var h = Live();
        var enu = new Vec3(120, -40, 5);
        h.RprStep(Update(h, Peer(1), enu, marking: "FED-1", force: RprForceId.Opposing, enuVelocity: new Vec3(3, 4, 0), damage: RprDamageState.SlightDamage));

        Assert.True(h.RprHandles!.TryResolve(Peer(1), out var id));
        var e = h.Engine.State.Find(id)!;
        Assert.Equal(h.RprSource, e.Origin);
        var s = h.Schema;
        Assert.Equal(Tank.Kind, DisTypeCode.Unpack(e.Get(s.EntityType).AsInt()).Kind);
        Assert.Equal(RprConversions.PackType(Tank), e.Get(s.EntityType).AsInt());
        Assert.Equal("FED-1", e.Get(s.Marking).AsString());
        Assert.Equal(2, e.Get(s.Force).AsInt());
        Assert.Equal(1, e.Get(s.Damage).AsInt());
        Support.Near(h.Frame!.PointToEcef(enu), e.Get(s.Position).AsVec3(), 1e-6, "position");
        Support.Near(h.Frame.VectorToEcef(new Vec3(3, 4, 0)), e.Get(s.Velocity).AsVec3(), 1e-9, "velocity");
        Assert.Equal(2, e.Get(s.DrAlgorithm).AsInt());
        Assert.Equal(h.Engine.WallTime, e.Get(s.StateTime).AsDouble(), 1e-9); // the engine clock at receipt

        var replica = Assert.Single(h.Sent<ReplicaState>());
        Support.Near(enu, replica.State.Position, 1e-6, "replica position");
        Assert.Equal("FED-1", replica.Marking);
        Assert.Equal(2, replica.Force);
        Assert.Equal(RprConversions.PackType(Tank), replica.DisType);
        Assert.True(h.HrotIds.TryResolve(replica.Id, out var viaHrot) && viaHrot == id);
    }

    [Fact]
    public void A_partial_update_changes_only_what_it_carries_and_the_replica_is_sent_again()
    {
        var h = Live();
        h.RprStep(Update(h, Peer(1), new Vec3(10, 10, 0), marking: "A"));
        h.RprStep(new RprObjectUpdate(Peer(1)) { Marking = "B" });
        h.RprStep(new RprObjectUpdate(Peer(1)) { DamageState = RprDamageState.Destroyed });
        h.RprStep(new RprObjectUpdate(Peer(1)) { WorldLocation = RprConversions.ToLocation(h.Frame!.PointToEcef(new Vec3(50, 0, 0))) });

        var e = h.Engine.State.Find(h.RprHandles!.TryResolve(Peer(1), out var id) ? id : default)!;
        Assert.Equal("B", e.Get(h.Schema.Marking).AsString());
        Assert.Equal(3, e.Get(h.Schema.Damage).AsInt());
        Support.Near(h.Frame.PointToEcef(new Vec3(50, 0, 0)), e.Get(h.Schema.Position).AsVec3(), 1e-6);
        Assert.Equal(2, e.Get(h.Schema.Force).AsInt()); // untouched
        var replicas = h.Sent<ReplicaState>();
        Assert.Equal(4, replicas.Count);
        Assert.Single(replicas.Select(r => r.Id).Distinct());
        Assert.Equal("B", replicas[2].Marking);
        Assert.Equal(0.0, replicas[2].HealthCurrent); // destroyed
    }

    [Fact]
    public void An_unknown_dr_algorithm_is_read_as_static_and_a_bad_update_leaves_no_trace()
    {
        var h = Live();
        h.RprStep(Update(h, Peer(1), new Vec3(1, 2, 3), algorithm: 77));
        var e = h.Engine.State.Find(h.RprHandles!.TryResolve(Peer(1), out var id) ? id : default)!;
        Assert.Equal(DrAlgorithms.Static, e.Get(h.Schema.DrAlgorithm).AsInt());

        int before = h.Engine.State.Count;
        h.RprStep(Update(h, Peer(2), new Vec3(1, 2, 3)) with { DamageState = (RprDamageState)9 });
        h.RprStep(Update(h, Peer(3), new Vec3(1, 2, 3)) with { WorldLocation = new WorldLocation(double.NaN, 0, 0) });
        h.RprStep(Update(h, Peer(4), new Vec3(1, 2, 3)) with { VelocityVector = new RprVector(0, double.PositiveInfinity, 0) });
        Assert.Equal(before, h.Engine.State.Count);
        Assert.False(h.RprHandles.TryResolve(Peer(2), out _));
        Assert.False(h.RprHandles.TryResolve(Peer(3), out _));
        Assert.False(h.RprHandles.TryResolve(Peer(4), out _));
    }

    [Fact]
    public void An_object_removal_destroys_the_entity_and_hrot_forgets_the_replica_an_unknown_handle_is_ignored()
    {
        var h = Live();
        h.RprStep(Update(h, Peer(1), new Vec3(1, 1, 0)));
        long replica = h.Sent<ReplicaState>().Single().Id;
        h.RprStep(new RprObjectRemoved(Peer(99)));
        Assert.Empty(h.Sent<ReplicaRemoved>());
        h.RprStep(new RprObjectRemoved(Peer(1)));
        Assert.False(h.RprHandles!.TryResolve(Peer(1), out _));
        Assert.Equal(replica, h.Sent<ReplicaRemoved>().Single().Id);
        Assert.Equal(0, h.HrotIds.Count);
        Assert.Equal(0UL, h.Ids.InUse);
    }

    [Fact]
    public void Dis_and_rpr_entities_are_both_replicas_each_with_its_own_hrot_id_and_neither_is_forwarded_to_the_other_side()
    {
        var h = Live();
        h.DisStep(DisPdus.EntityState(5, h.Frame!.PointToEcef(new Vec3(10, 10, 0))));
        h.RprStep(Update(h, Peer(1), new Vec3(20, 20, 0)));
        h.Run(2);
        Assert.Equal(2, h.Sent<ReplicaState>().Select(r => r.Id).Distinct().Count());

        // the origin rule (R-116): nothing of either went to the other side
        Assert.Empty(h.DisChecker!.States); // no hrot entity yet, and the dis entity is not echoed
        Assert.Empty(h.RprChecker!.Updates);
        Assert.Empty(h.RprChecker.Sent<RprRegisterObject>());

        // a hrot entity appears on both sides, and neither replica shows up on either side
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 5, 0)));
        h.Run(2);
        Assert.Single(h.DisChecker.States.Select(s => DisConversions.KeyOf(s.Pdu.EntityID)).Distinct());
        Assert.Single(h.RprChecker.Sent<RprRegisterObject>());
        Assert.Single(h.RprChecker.Updates.Select(u => u.Handle).Distinct());
    }

    [Fact]
    public void An_update_for_a_handle_that_belongs_to_a_hrot_entity_creates_nothing_and_changes_nothing()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 5, 0), marking: "OURS"));
        h.Steps(2);
        var ours = HandleOf(h, 1)!.Value;
        int count = h.Engine.State.Count;
        h.RprStep(Update(h, ours, new Vec3(900, 900, 0), marking: "HIJACK"));
        h.RprStep(new RprObjectRemoved(ours)); // an echo of a removal does not remove our entity either
        Assert.Equal(count, h.Engine.State.Count);
        Assert.Equal("OURS", h.EntityOfHrot(1)!.Get(h.Schema.Marking).AsString());
        Assert.Empty(h.Entities(h.RprSource));
        Assert.Empty(h.Sent<ReplicaState>());
    }

    [Fact]
    public void A_weapon_fire_of_the_federation_becomes_an_inject_fire_with_shooter_and_a_hrot_target()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 0, 0), tkb: 100));
        h.Steps(2);
        var ours = HandleOf(h, 1)!.Value;
        h.RprStep(Update(h, Peer(1), new Vec3(-300, 0, 0)));
        long shooter = h.Sent<ReplicaState>().Single().Id;
        var origin = new Vec3(-300, 0, 2);
        h.RprStep(new RprWeaponFire(Guid.NewGuid(), Peer(1), ours, RprConversions.ToLocation(h.Frame!.PointToEcef(origin)), MunitionKind, 3)
        {
            InitialVelocity = RprConversions.ToVector(h.Frame.VectorToEcef(new Vec3(400, 0, 0))),
            WarheadType = 1000,
            FuseType = 8000,
        });

        var fire = Assert.Single(h.Sent<InjectFire>());
        Assert.Equal(shooter, fire.ShooterId);
        Assert.Equal(1, fire.TargetId);
        Assert.True(fire.TargetIsHrot);
        Assert.Equal(3, fire.Quantity);
        Assert.Equal(RprConversions.PackType(MunitionKind), fire.MunitionDisType);
        Support.Near(origin, fire.Origin, 1e-6, "origin");
        Support.Near(new Vec3(400, 0, 0), fire.Velocity, 1e-4, "velocity");
    }

    [Fact]
    public void A_fire_at_an_unknown_target_or_from_an_unknown_shooter_is_still_an_inject_fire_without_ids()
    {
        var h = Live();
        h.RprStep(new RprWeaponFire(Guid.NewGuid(), Peer(7), Peer(8), RprConversions.ToLocation(h.Frame!.PointToEcef(default)), MunitionKind, 1));
        var fire = Assert.Single(h.Sent<InjectFire>());
        Assert.Equal(0, fire.ShooterId);
        Assert.Equal(0, fire.TargetId);
        Assert.False(fire.TargetIsHrot);
    }

    [Fact]
    public void A_fire_of_a_hrot_entity_that_comes_back_through_the_federation_is_an_echo()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 0, 0)));
        h.Steps(2);
        var ours = HandleOf(h, 1)!.Value;
        h.RprStep(new RprWeaponFire(Guid.NewGuid(), ours, null, RprConversions.ToLocation(h.Frame!.PointToEcef(default)), MunitionKind, 1));
        h.RprStep(new RprMunitionDetonation(Guid.NewGuid(), ours, null, RprConversions.ToLocation(h.Frame.PointToEcef(default)), MunitionKind, RprDetonationResult.GroundImpact));
        Assert.Empty(h.Sent<InjectFire>());
        Assert.Empty(h.Sent<InjectDetonation>());
    }

    [Fact]
    public void A_munition_detonation_of_the_federation_becomes_an_inject_detonation_and_only_while_the_exercise_runs()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 0, 0), tkb: 100));
        h.Steps(2);
        var ours = HandleOf(h, 1)!.Value;
        h.RprStep(Update(h, Peer(1), new Vec3(-300, 0, 0)));
        long shooter = h.Sent<ReplicaState>().Single().Id;
        var at = new Vec3(98, 1, 0);
        h.RprStep(new RprMunitionDetonation(Guid.NewGuid(), Peer(1), ours, RprConversions.ToLocation(h.Frame!.PointToEcef(at)), MunitionKind, RprDetonationResult.EntityImpact)
        {
            FinalVelocity = RprConversions.ToVector(h.Frame.VectorToEcef(new Vec3(0, 0, -10))),
            RelativeDetonationLocation = new RprVector(1, 0, 0),
        });

        var d = Assert.Single(h.Sent<InjectDetonation>());
        Assert.Equal(shooter, d.ShooterId);
        Assert.Equal(1, d.TargetId);
        Assert.True(d.TargetIsHrot);
        Support.Near(at, d.Location, 1e-6, "location");
        Support.Near(new Vec3(0, 0, -10), d.Velocity, 1e-4, "velocity");

        h.Session(ClusterPhase.Live, advancing: false); // paused: effects are not injected
        h.RprStep(new RprMunitionDetonation(Guid.NewGuid(), Peer(1), ours, RprConversions.ToLocation(h.Frame.PointToEcef(at)), MunitionKind, RprDetonationResult.EntityImpact));
        Assert.Single(h.Sent<InjectDetonation>());
    }

    [Fact]
    public void An_update_request_for_a_hrot_object_is_answered_with_a_full_update_and_for_anything_else_ignored()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 5, 0), marking: "REQ"));
        h.Steps(3);
        var ours = HandleOf(h, 1)!.Value;
        h.RprStep(Update(h, Peer(1), new Vec3(1, 1, 0)));
        int before = Updates(h).Count;

        h.RprChecker!.AllowRedundant = true;
        h.RprStep(new RprUpdateRequested(Peer(1)));   // an object of the federation: not ours
        h.RprStep(new RprUpdateRequested(Peer(55)));  // unknown
        Assert.Equal(before, Updates(h).Count);
        h.RprStep(new RprUpdateRequested(ours));
        h.RprChecker.AllowRedundant = false;
        var answer = Updates(h).Last();
        Assert.Equal(before + 1, Updates(h).Count);
        Assert.Equal(ours, answer.Handle);
        Assert.Equal("REQ", answer.Marking);
        Assert.NotNull(answer.WorldLocation);
        Assert.NotNull(answer.EntityIdentifier);
    }

    // ---- outbound: HROT -> pivot -> HLA ------------------------------------------------------------------------------------

    [Fact]
    public void A_hrot_entity_registers_with_its_type_and_gets_one_full_update_when_the_answer_is_bound()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(7, new HrotVec3(100, 200, 10), tkb: 100, marking: "TANK-01", force: 2, velocity: new HrotVec3(3, 4, 0),
            rotation: new HrotQuat(0, 0, Math.Sin(0.26), Math.Cos(0.26))));
        // the registration was sent, the answer is still on its way: nothing else yet
        var register = Assert.Single(h.RprChecker!.Sent<RprRegisterObject>());
        Assert.Equal(Tank, register.EntityType);
        Assert.Empty(Updates(h));
        Assert.Null(HandleOf(h, 7));

        h.Step();
        var handle = HandleOf(h, 7)!.Value;
        var update = Assert.Single(Updates(h));
        Assert.Equal(handle, update.Handle);
        Assert.Equal(Tank, update.EntityType);
        Assert.Equal("TANK-01", update.Marking);
        Assert.Equal(RprForceId.Opposing, update.ForceId);
        Assert.Equal((byte)2, update.DeadReckoningAlgorithm);
        Assert.Equal(RprDamageState.NoDamage, update.DamageState);
        Assert.Equal(new DisEntityId(1, 3001, 1), update.EntityIdentifier);
        Support.Near(h.Frame!.PointToEcef(new Vec3(100, 200, 10)), RprConversions.ToVec(update.WorldLocation!.Value), 0.2, "location (predicted a tick ahead)");
        Support.Near(h.Frame.VectorToEcef(new Vec3(3, 4, 0)), RprConversions.ToVec(update.VelocityVector!.Value), 1e-4, "velocity");
        h.Run(0.5);
        Assert.Single(Updates(h)); // steady: nothing more while it follows its model
    }

    [Fact]
    public void What_changes_while_the_registration_is_pending_is_coalesced_into_the_first_update()
    {
        var h = Live();
        h.DeliverRti = false;
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 5, 0), marking: "ONE"));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(6, 5, 0), marking: "TWO"));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(7, 5, 0), marking: "THREE", health: 0));
        h.Run(2);
        Assert.Single(h.RprChecker!.Sent<RprRegisterObject>()); // one request, however often it changed
        Assert.Empty(Updates(h));

        h.DeliverRti = true;
        h.Steps(2);
        var update = Assert.Single(Updates(h));
        Assert.Equal("THREE", update.Marking);
        Assert.Equal(RprDamageState.Destroyed, update.DamageState);
    }

    [Fact]
    public void An_identity_or_damage_change_sends_a_full_update_and_a_small_move_does_not()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 5, 0), marking: "A", force: 1));
        h.Steps(3);
        Assert.Single(Updates(h));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5.2, 5, 0), marking: "A", force: 1)); // 0.2 m: within the 1 m threshold
        h.Steps(2);
        Assert.Single(Updates(h));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5.2, 5, 0), marking: "B", force: 1));
        Assert.Equal(2, Updates(h).Count);
        Assert.Equal("B", Updates(h)[1].Marking);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5.2, 5, 0), marking: "B", force: 2));
        Assert.Equal(RprForceId.Opposing, Updates(h)[2].ForceId);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5.2, 5, 0), marking: "B", force: 2, health: 40));
        Assert.Equal(RprDamageState.ModerateDamage, Updates(h)[3].DamageState);
        Assert.Equal(4, Updates(h).Count);
    }

    [Fact]
    public void A_move_beyond_the_hla_position_threshold_sends_and_one_within_it_does_not()
    {
        var h = Live(Both(hla: x => x with { PositionThresholdM = 2.0 }));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.Steps(3);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1.9, 0, 0)));
        h.Steps(2);
        Assert.Single(Updates(h));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(2.3, 0, 0)));
        Assert.Equal(2, Updates(h).Count);
    }

    [Fact]
    public void An_orientation_change_beyond_the_hla_orientation_threshold_sends_alone()
    {
        var h = Live(Both(hla: x => x with { OrientationThresholdDeg = 10.0 }));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.Steps(3);
        Assert.Single(Updates(h));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0), rotation: new HrotQuat(0, 0, Math.Sin(4 * Math.PI / 180), Math.Cos(4 * Math.PI / 180)))); // 8 deg
        Assert.Single(Updates(h));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0), rotation: new HrotQuat(0, 0, Math.Sin(7 * Math.PI / 180), Math.Cos(7 * Math.PI / 180)))); // 14 deg
        Assert.Equal(2, Updates(h).Count);
    }

    [Fact]
    public void Each_side_holds_its_own_threshold_when_both_run()
    {
        // dis: 5 m, hla: 1 m. The invariant checkers of both sides run after every step with their own thresholds.
        var h = Live(Both(hla: x => x with { PositionThresholdM = 1.0 }, dis: x => x with { PositionThresholdM = 5.0 }));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.Steps(3);
        int dis = h.DisChecker!.States.Count;
        int rpr = Updates(h).Count;
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(3, 0, 0))); // 3 m: beyond hla's 1 m, within dis's 5 m
        h.Steps(2);
        Assert.Equal(dis, h.DisChecker.States.Count);
        Assert.Equal(rpr + 1, Updates(h).Count);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(9, 0, 0))); // 6 m from the last dis PDU, 6 m from the last update
        Assert.Equal(dis + 1, h.DisChecker.States.Count);
        Assert.Equal(rpr + 2, Updates(h).Count);
    }

    [Fact]
    public void The_hla_side_reads_its_thresholds_and_heartbeat_from_the_hla_section_only()
    {
        // dis very lax, hla strict and the other way round: each side follows its own section
        var h = Live(Both(hla: x => x with { PositionThresholdM = 0.5, HeartbeatS = 2.0 }, dis: x => x with { PositionThresholdM = 50.0, HeartbeatS = 20.0 }));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.Steps(3);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0.8, 0, 0)));
        Assert.Equal(2, Updates(h).Count);
        Assert.Single(h.DisChecker!.States);
        h.Run(2.5); // the hla heartbeat (2 s) passed, the dis one (20 s) did not
        Assert.True(Updates(h).Count >= 3);
        Assert.Single(h.DisChecker.States);
    }

    [Fact]
    public void The_heartbeat_comes_from_the_configuration_and_is_quiet_while_updates_flow()
    {
        var h = Live(HlaOnly(x => x with { HeartbeatS = 3.0 }));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.Steps(3);
        h.Run(2.5);
        Assert.Single(Updates(h));
        h.Run(1.0);
        Assert.Equal(2, Updates(h).Count);
        Assert.True(h.RprOut!.Messages.Last(m => m.Message is RprObjectUpdate).WallTime - h.RprOut.Messages.Where(m => m.Message is RprObjectUpdate).First().WallTime >= 3.0 - 1e-6);
        h.Run(60);
        Assert.InRange(Updates(h).Count, 20, 23); // a heartbeat every 3 s
    }

    [Fact]
    public void A_hrot_removal_deletes_the_object_once_and_an_entity_removed_while_pending_is_deleted_when_the_answer_comes()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.Steps(3);
        var first = HandleOf(h, 1)!.Value;
        h.HrotStep(new HrotEntityRemoved(1));
        h.Steps(2);
        Assert.Equal(first, Assert.Single(h.RprChecker!.Sent<RprObjectRemoved>()).Handle);

        h.DeliverRti = false;
        h.HrotStep(HrotDisHarness.State(2, new HrotVec3(0, 0, 0)));
        h.HrotStep(new HrotEntityRemoved(2));
        h.Run(1);
        Assert.Single(h.RprChecker.Sent<RprObjectRemoved>()); // nothing to delete yet: the RTI has not answered
        h.DeliverRti = true;
        h.Steps(2);
        var removed = h.RprChecker.Sent<RprObjectRemoved>().ToList();
        Assert.Equal(2, removed.Count);
        Assert.NotEqual(first, removed[1].Handle);
        Assert.Equal(0, h.DisIds!.Count);
        Assert.Equal(0, h.RprHandles!.Count);
    }

    [Fact]
    public void An_answer_for_an_unknown_token_is_a_warning_and_changes_nothing()
    {
        var h = Live();
        h.RprStep(new RprObjectRegistered(424242, Peer(1)));
        Assert.Equal(1, h.WarningCount(nameof(RprObjectRegisteredIngress)));
        Assert.Empty(h.Entities(h.RprSource));
        Assert.Equal(0, h.RprHandles!.Count);
    }

    [Fact]
    public void Nothing_is_registered_before_the_exercise_runs_and_the_waiting_entities_register_when_it_opens()
    {
        var h = new HrotDisHarness(Both());
        h.Origin(50, 14, 300);
        h.Session(ClusterPhase.Edit);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 5, 0)));
        h.Run(2);
        Assert.Empty(h.RprChecker!.Sent<RprRegisterObject>());
        h.Session(ClusterPhase.Live);
        Assert.Single(h.RprChecker.Sent<RprRegisterObject>());
        h.Steps(2);
        Assert.Single(Updates(h));
    }

    [Fact]
    public void Units_are_not_published_unless_the_configuration_forwards_them()
    {
        long unit = new DisTypeCode(11, 1, 225, 1, 0, 0, 0).Pack();
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 5, 0), disType: unit));
        h.Run(1);
        Assert.Empty(h.RprChecker!.Sent<RprRegisterObject>());

        var forwarding = Live(Both(shared: new SharedConfiguration { ForwardUnitEntities = true }));
        forwarding.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 5, 0), disType: unit));
        forwarding.Run(1);
        Assert.Single(forwarding.RprChecker!.Sent<RprRegisterObject>());
    }

    [Fact]
    public void The_entity_identifiers_come_from_a_pool_under_the_gateways_site_and_application_and_an_exhausted_pool_warns_once_per_entity()
    {
        var h = new HrotDisHarness(Both(), numbering: new DisNumbering(1, 2, 5.0));
        h.GoLive();
        h.RprChecker!.ExpectAllSent = false;
        h.DisChecker!.ExpectAllSent = false;
        for (int i = 1; i <= 4; i++)
        {
            h.HrotStep(HrotDisHarness.State(i, new HrotVec3(i, 0, 0)));
        }

        h.Run(1);
        Assert.Equal(2, h.RprChecker.Sent<RprRegisterObject>().Count());
        for (int i = 3; i <= 4; i++)
        {
            h.HrotStep(HrotDisHarness.State(i, new HrotVec3(i + 10, 0, 0))); // retried on the next change: still no number, no second warning
        }

        Assert.Equal(2, h.WarningCount(nameof(RprPublication))); // one warning per entity without a number, not one per retry
        var numbers = new List<int>();
        foreach (var e in h.Entities(h.HrotSource))
        {
            if (h.DisIds!.TryGetKey(e.Id, out var k))
            {
                numbers.Add(k.Number);
            }
        }

        numbers.Sort();
        Assert.Equal(new List<int> { 1, 2 }, numbers);
    }

    // ---- fire and detonation, HROT -> HLA ----------------------------------------------------------------------------------

    [Fact]
    public void A_hrot_fire_and_its_detonation_go_to_hla_with_the_object_handles_and_one_guid()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0), tkb: 100));
        h.HrotStep(HrotDisHarness.State(2, new HrotVec3(300, 0, 0), tkb: 400, force: 2));
        h.Steps(3);
        var shooter = HandleOf(h, 1)!.Value;
        var target = HandleOf(h, 2)!.Value;
        h.HrotStep(new HrotFire(1, 2, 501, 0, new HrotVec3(0, 0, 2), new HrotVec3(400, 0, 0), 2));
        h.HrotStep(new HrotDetonation(1, 2, 501, 0, new HrotVec3(299, 0, 0), new HrotVec3(0, 0, -5)));

        var fire = Assert.Single(h.RprChecker!.Sent<RprWeaponFire>());
        Assert.Equal(shooter, fire.FiringObject);
        Assert.Equal(target, fire.TargetObject);
        Assert.Equal(2, fire.Quantity);
        Support.Near(h.Frame!.PointToEcef(new Vec3(0, 0, 2)), RprConversions.ToVec(fire.Location), 1e-6, "fire location");
        Support.Near(h.Frame.VectorToEcef(new Vec3(400, 0, 0)), RprConversions.ToVec(fire.InitialVelocity!.Value), 1e-3, "initial velocity");
        var detonation = Assert.Single(h.RprChecker.Sent<RprMunitionDetonation>());
        Assert.Equal(fire.EventId, detonation.EventId); // the correlator linked them (one DIS event id -> one guid)
        Assert.Equal(shooter, detonation.FiringObject);
        Assert.Equal(target, detonation.TargetObject);
        Assert.Equal(RprDetonationResult.EntityImpact, detonation.Result);
        Assert.Equal(fire.Munition, detonation.Munition);
        // and DIS saw the same events with its own ids
        Assert.Single(h.DisChecker!.Sent<FirePdu>());
        Assert.Single(h.DisChecker.Sent<DetonationPdu>());
    }

    [Fact]
    public void A_detonation_on_the_ground_has_no_target_object()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.Steps(3);
        h.HrotStep(new HrotDetonation(1, 0, 501, 0, new HrotVec3(50, 50, 0), default));
        var d = Assert.Single(h.RprChecker!.Sent<RprMunitionDetonation>());
        Assert.Null(d.TargetObject);
        Assert.Equal(RprDetonationResult.GroundImpact, d.Result);
    }

    [Fact]
    public void Events_of_the_replay_session_go_to_hla_only_with_the_replay_flag_and_not_before_the_exercise_runs()
    {
        foreach (bool flag in new[] { false, true })
        {
            var h = Live(Both(shared: new SharedConfiguration { SendEventsInReplay = flag }));
            h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
            h.Steps(3);
            h.Session(ClusterPhase.Replay); // the reload into Replay unloads: the entities come again from the replayed HROT
            h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
            h.HrotStep(new HrotFire(1, 0, 501, 0, default, default, 1));
            Assert.Equal(flag ? 1 : 0, h.RprChecker!.Sent<RprWeaponFire>().Count());
        }

        var edit = new HrotDisHarness(Both());
        edit.Origin(50, 14, 300);
        edit.Session(ClusterPhase.Edit);
        edit.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        edit.HrotStep(new HrotFire(1, 0, 501, 0, default, default, 1));
        Assert.Empty(edit.RprChecker!.Sent<RprWeaponFire>());
    }

    [Fact]
    public void A_dis_event_never_goes_to_hla_and_an_hla_event_never_goes_to_dis()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.Steps(3);
        h.DisStep(DisPdus.EntityState(5, h.Frame!.PointToEcef(new Vec3(10, 10, 0))));
        h.RprStep(Update(h, Peer(1), new Vec3(20, 20, 0)));
        var disShooter = new DisEntityKey(DisPdus.PeerSite, DisPdus.PeerApplication, 5);
        h.DisStep(DisPdus.Fire(disShooter, null, h.Frame.PointToEcef(new Vec3(10, 10, 0)), default, 0));
        h.RprStep(new RprWeaponFire(Guid.NewGuid(), Peer(1), null, RprConversions.ToLocation(h.Frame.PointToEcef(new Vec3(20, 20, 0))), MunitionKind, 1));
        h.Steps(2);
        Assert.Equal(2, h.Sent<InjectFire>().Count);     // both reached HROT
        Assert.Empty(h.RprChecker!.Sent<RprWeaponFire>()); // the DIS fire did not reach HLA
        Assert.Empty(h.DisChecker!.Sent<FirePdu>());       // the HLA fire did not reach DIS
    }

    // ---- the stale reaper ---------------------------------------------------------------------------------------------------

    [Fact]
    public void An_rpr_entity_silent_longer_than_hla_timeout_is_reaped_and_hrot_is_told()
    {
        var h = Live(Both(hla: x => x with { TimeoutS = 6.0 }, dis: x => x with { TimeoutS = 60.0 }));
        h.RprStep(Update(h, Peer(1), new Vec3(1, 1, 0)));
        long replica = h.Sent<ReplicaState>().Single().Id;
        h.Run(5.5);
        Assert.True(h.RprHandles!.TryResolve(Peer(1), out _));
        h.Run(2.0);
        Assert.False(h.RprHandles.TryResolve(Peer(1), out _)); // after hla.timeoutS, not dis.timeoutS
        Assert.Equal(replica, h.Sent<ReplicaRemoved>().Single().Id);
        Assert.Equal(0, h.HrotIds.Count);
    }

    [Fact]
    public void Updates_within_the_timeout_keep_an_rpr_entity_but_writes_of_other_sources_do_not()
    {
        var h = Live(Both(hla: x => x with { TimeoutS = 6.0 }));
        h.RprStep(Update(h, Peer(1), new Vec3(1, 1, 0)));
        h.RprStep(Update(h, Peer(2), new Vec3(2, 2, 0)));
        var derived = h.Engine.Sources.Register("test.derived");
        for (int second = 0; second < 10; second++)
        {
            h.Run(1);
            h.RprStep(new RprObjectUpdate(Peer(1)) { Marking = "K" + second }); // Peer 1 keeps talking
            if (h.RprHandles!.TryResolve(Peer(2), out var id))
            {
                var changes = h.Engine.BeginChange(derived);
                changes.Set(id, h.Schema.Marking, AttrValue.From("D" + second)); // someone else keeps writing Peer 2
                changes.Commit();
            }
        }

        Assert.True(h.RprHandles!.TryResolve(Peer(1), out _));
        Assert.False(h.RprHandles.TryResolve(Peer(2), out _));
    }

    [Fact]
    public void Hrot_entities_are_never_reaped_by_the_rpr_reaper()
    {
        var h = Live(Both(hla: x => x with { TimeoutS = 3.0 }));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.Run(30);
        Assert.NotNull(h.EntityOfHrot(1));
        Assert.True(Updates(h).Count >= 5);
    }

    // ---- composition ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Only_the_hla_side_runs_when_the_dis_section_is_absent()
    {
        var h = Live(HlaOnly());
        Assert.Null(h.DisOut);
        Assert.DoesNotContain(h.Engine.Rules, r => r.Module.StartsWith("Dis", StringComparison.Ordinal));
        Assert.Contains(h.Engine.Rules, r => r.Module == nameof(RprEntityEgress));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.Steps(3);
        Assert.Single(Updates(h));
        Assert.NotNull(h.Engine.Identities.Find("dis.entity")); // the one entity number space serves the HLA side alone, too
        Assert.Null(h.Engine.Identities.Find("rpr.entityId"));
    }

    [Fact]
    public void Without_the_hla_side_there_are_no_rpr_modules_ports_or_maps()
    {
        var h = new HrotDisHarness(GatewayConfiguration.CreateDefault());
        Assert.Null(h.RprOut);
        Assert.DoesNotContain(h.Engine.Rules, r => r.Module.StartsWith("Rpr", StringComparison.Ordinal));
        Assert.Null(h.Engine.Identities.Find("rpr.handle"));
        Assert.Null(h.Engine.Identities.Find("rpr.entityId"));
        Assert.False(h.Engine.Sources.TryGet("proc.reaper.rpr", out _));
    }

    [Fact]
    public void A_disabled_hla_side_declares_nothing()
    {
        var config = Both(hla: x => x with { Enabled = false });
        var h = new HrotDisHarness(config);
        Assert.Null(h.RprOut);
        Assert.Null(h.Engine.Identities.Find("rpr.handle"));
    }

    [Fact]
    public void Every_rpr_message_of_the_contract_has_a_handler_and_a_stray_one_changes_nothing()
    {
        var h = Live();
        h.RprStep(Update(h, Peer(1), new Vec3(1, 1, 0)));
        h.RprStep(new RprAggregateEntity(Peer(2), "AGG", Tank, RprForceId.Friendly, []));          // an aggregate is out of scope for this gateway
        h.RprStep(new RprSimulationControl(RprControlKind.StopFreeze));                              // not carried over HLA
        Assert.Equal(0, h.Engine.Diagnostics.RuleFailures);
        Assert.Single(h.Entities(h.RprSource));
    }
}
