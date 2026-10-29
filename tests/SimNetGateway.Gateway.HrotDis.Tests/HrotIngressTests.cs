using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>The HROT -> pivot modules: origin, entity state, removal, turret pose (DD-006 sections 3 and 5, DD-007 section 3).</summary>
public class HrotIngressTests
{
    private static readonly DisTypeCode Tank = new(1, 1, 225, 1, 1, 3, 0);

    private static HrotDisHarness LiveAtEquator(double alt = 100.0)
    {
        var h = new HrotDisHarness();
        h.Origin(0.0, 0.0, alt); // ECEF at this place: x up, y east, z north
        h.Session(ClusterPhase.Live);
        return h;
    }

    // ---- before the origin --------------------------------------------------------------------------------------------

    [Fact]
    public void An_entity_before_the_origin_is_created_and_typed_but_has_no_position()
    {
        var h = new HrotDisHarness();
        h.HrotStep(HrotDisHarness.State(1001, new HrotVec3(100, 200, 3), tkb: 100, marking: "Alpha", force: 2, health: 40, healthMax: 100));

        var e = h.EntityOfHrot(1001)!;
        Assert.NotNull(e);
        Assert.Equal(h.HrotSource, e.Origin);
        Assert.Equal(Tank.Pack(), e.Get(h.Schema.EntityType).AsInt());
        Assert.Equal("Alpha", e.Get(h.Schema.Marking).AsString());
        Assert.Equal(2, e.Get(h.Schema.Force).AsInt());
        Assert.Equal(100, e.Get(h.Schema.TkbType).AsInt());
        Assert.Equal(new Vec3(40, 100, 0), e.Get(h.Schema.Health).AsVec3());
        Assert.Equal(DamageMapping.Moderate, e.Get(h.Schema.Damage).AsInt());
        foreach (var attr in new[] { h.Schema.Position, h.Schema.Velocity, h.Schema.Acceleration, h.Schema.Orientation, h.Schema.AngularVelocity, h.Schema.DrAlgorithm, h.Schema.StateTime })
        {
            Assert.False(e.Has(attr), "nothing positional before a valid origin: " + h.Engine.Attributes[attr].Name);
        }

        Assert.Empty(h.Sent<ReplicaState>());

        // the origin alone does not give it a position; its next state does
        h.Origin(50, 14, 300);
        Assert.False(h.EntityOfHrot(1001)!.Has(h.Schema.Position));
        h.HrotStep(HrotDisHarness.State(1001, new HrotVec3(100, 200, 3)));
        Assert.True(h.EntityOfHrot(1001)!.Has(h.Schema.Position));
    }

    [Fact]
    public void An_unacceptable_origin_aborts_and_leaves_the_frame_invalid()
    {
        var h = new HrotDisHarness();
        h.HrotStep(new HrotOrigin(0, 0, 0, 1)); // 'not set'
        h.HrotStep(new HrotOrigin(95, 0, 0, 2));
        h.HrotStep(new HrotOrigin(double.NaN, 0, 0, 3));
        Assert.Equal(3, h.Engine.Diagnostics.TransactionFailures);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 2, 3)));
        Assert.False(h.EntityOfHrot(1)!.Has(h.Schema.Position));
        Assert.Equal(0, Support.CountEvents(h, h.Schema.FrameRebased));
    }

    [Fact]
    public void The_zero_origin_is_valid_when_the_configuration_allows_it()
    {
        var config = GatewayConfiguration.CreateDefault() with { Hrot = new HrotConfiguration { AllowZeroOrigin = true } };
        var h = new HrotDisHarness(config);
        h.HrotStep(new HrotOrigin(0, 0, 0, 1));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        Support.Near(new Vec3(Wgs84.A, 0, 0), h.EntityOfHrot(1)!.Get(h.Schema.Position).AsVec3(), 1e-6, "origin");
    }

    // ---- conversions --------------------------------------------------------------------------------------------------

    [Fact]
    public void East_north_up_become_the_ecef_axes_at_the_equator()
    {
        var h = LiveAtEquator(100.0);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 200, 5), velocity: new HrotVec3(3, 4, 5), simTime: 12.5));
        var e = h.EntityOfHrot(1)!;
        // at latitude 0, longitude 0: ECEF x is up, y is east, z is north
        Support.Near(new Vec3(Wgs84.A + 100 + 5, 100, 200), e.Get(h.Schema.Position).AsVec3(), 1e-6, "position");
        Support.Near(new Vec3(5, 3, 4), e.Get(h.Schema.Velocity).AsVec3(), 1e-9, "velocity");
    }

    [Fact]
    public void A_north_pointing_identity_body_has_the_known_geocentric_euler_angles()
    {
        // Identity quaternion: HROT body x (forward) = east, y (left) = north, z (up) = up. The DIS body is x forward, y right, z down:
        // x = east = ECEF (0,1,0), y = -north = (0,0,-1), z = -up = (-1,0,0) at (0, 0). Euler ZYX of that matrix: psi = pi/2, theta = 0, phi = -pi/2.
        var h = LiveAtEquator();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 2, 3)));
        Support.Near(new Vec3(Math.PI / 2, 0, -Math.PI / 2), h.EntityOfHrot(1)!.Get(h.Schema.Orientation).AsVec3(), 1e-9, "euler");
    }

    [Fact]
    public void Kinematics_follow_the_conversions_of_the_design()
    {
        var h = new HrotDisHarness();
        h.Origin(48.2, 16.4, 180.0);
        var frame = h.Frame!;
        var q = new HrotQuat(0.1, -0.2, 0.3, 0.9).Normalize();
        var enu = new HrotVec3(-250, 975, 12);
        var velocity = new HrotVec3(7, -3, 0.5);
        var omega = new HrotVec3(0.1, -0.2, 0.3);
        h.HrotStep(HrotDisHarness.State(5, enu, rotation: q, velocity: velocity, angularVelocity: omega, simTime: 33.25));

        var e = h.EntityOfHrot(5)!;
        var quaternion = new Quaternion(q.W, q.X, q.Y, q.Z);
        Support.Near(frame.PointToEcef(Support.V(enu)), e.Get(h.Schema.Position).AsVec3(), 1e-6, "position");
        Support.Near(frame.VectorToEcef(Support.V(velocity)), e.Get(h.Schema.Velocity).AsVec3(), 1e-9, "velocity");
        Support.Near(LocalToGeocentricOrientation.ToDisEuler(quaternion, frame), e.Get(h.Schema.Orientation).AsVec3(), 1e-12, "orientation");
        Support.Near(LocalToGeocentricOrientation.AngularVelocityEnuToBodyFrd(quaternion, Support.V(omega)), e.Get(h.Schema.AngularVelocity).AsVec3(), 1e-12, "angular velocity");
        Assert.Equal(Vec3.Zero, e.Get(h.Schema.Acceleration).AsVec3());
        Assert.Equal((long)DrAlgorithms.Fpw, e.Get(h.Schema.DrAlgorithm).AsInt());
        Assert.Equal(33.25, e.Get(h.Schema.StateTime).AsDouble());
        // the yaw rate of an upright body is about the down axis of the DIS body, opposite to HROT's up
        h.HrotStep(HrotDisHarness.State(6, enu, angularVelocity: new HrotVec3(0, 0, 1)));
        Support.Near(new Vec3(0, 0, -1), h.EntityOfHrot(6)!.Get(h.Schema.AngularVelocity).AsVec3(), 1e-12, "yaw rate");
    }

    [Fact]
    public void A_zero_quaternion_is_the_identity_and_a_long_one_is_normalised()
    {
        var h = LiveAtEquator();
        h.HrotStep(HrotDisHarness.State(1, default, rotation: new HrotQuat(0, 0, 0, 0)));
        h.HrotStep(HrotDisHarness.State(2, default, rotation: new HrotQuat(0, 0, 0, 5)));
        h.HrotStep(HrotDisHarness.State(3, default));
        var a = h.EntityOfHrot(1)!.Get(h.Schema.Orientation).AsVec3();
        Support.Near(h.EntityOfHrot(3)!.Get(h.Schema.Orientation).AsVec3(), a, 1e-12);
        Support.Near(h.EntityOfHrot(3)!.Get(h.Schema.Orientation).AsVec3(), h.EntityOfHrot(2)!.Get(h.Schema.Orientation).AsVec3(), 1e-12);
    }

    [Fact]
    public void A_new_origin_re_bases_the_enu_outputs_and_leaves_the_ecef_pivot_unchanged()
    {
        var h = new HrotDisHarness();
        h.GoLive(50, 14, 300);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 200, 0)));
        var before = h.EntityOfHrot(1)!.Get(h.Schema.Position).AsVec3();
        var pivotOrientation = h.EntityOfHrot(1)!.Get(h.Schema.Orientation).AsVec3();

        // a DIS entity so that the outputs can be seen change
        var dis = h.DisCreate(h.DisStateAt(new HrotVec3(500, 600, 0)));
        h.Step();
        var sentBefore = h.Sent<ReplicaState>().Last().State.Position;
        var disEcef = h.Engine.State.Find(dis)!.Get(h.Schema.Position).AsVec3();

        h.Origin(50.01, 14.02, 300, generation: 2);
        // the HROT entity keeps its ECEF position until HROT reports it again (in the new frame)
        Assert.Equal(before, h.EntityOfHrot(1)!.Get(h.Schema.Position).AsVec3());
        Assert.Equal(pivotOrientation, h.EntityOfHrot(1)!.Get(h.Schema.Orientation).AsVec3());
        Assert.Equal(2, Support.CountEvents(h, h.Schema.FrameRebased)); // the first origin and this one

        // the replica is re-sent: same ECEF, other ENU
        var resent = h.Sent<ReplicaState>().Last();
        Assert.Equal(disEcef, h.Engine.State.Find(dis)!.Get(h.Schema.Position).AsVec3());
        Support.Near(h.Frame!.PointFromEcef(disEcef), resent.State.Position, 1e-6, "new ENU");
        Assert.True((Support.V(resent.State.Position) - Support.V(sentBefore)).Length > 100.0, "the ENU position must change with the origin");
        Assert.Equal(2, h.Sent<ReplicaState>().Count);

        // HROT then reports the same ENU again in the new frame: the ECEF value moves
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 200, 0)));
        Assert.NotEqual(before, h.EntityOfHrot(1)!.Get(h.Schema.Position).AsVec3());
        Support.Near(h.Frame.PointToEcef(new Vec3(100, 200, 0)), h.EntityOfHrot(1)!.Get(h.Schema.Position).AsVec3(), 1e-6, "new frame");
    }

    [Fact]
    public void The_same_origin_again_does_not_re_base()
    {
        var h = new HrotDisHarness();
        h.GoLive();
        h.DisCreate(h.DisStateAt(new HrotVec3(1, 2, 3)));
        h.Step();
        int sent = h.Sent<ReplicaState>().Count;
        h.HrotStep(new HrotOrigin(50, 14, 300, 1));
        Assert.Equal(sent, h.Sent<ReplicaState>().Count);
        Assert.Equal(1, Support.CountEvents(h, h.Schema.FrameRebased));
    }

    // ---- identity, health, commander ----------------------------------------------------------------------------------

    [Fact]
    public void The_dis_type_of_hrot_is_used_when_complete_else_the_catalogue_and_an_unknown_type_warns_once()
    {
        var h = LiveAtEquator();
        long provided = new DisTypeCode(1, 2, 3, 4, 5, 6, 7).Pack();
        h.HrotStep(HrotDisHarness.State(1, tkb: 100, disType: provided));
        Assert.Equal(provided, h.EntityOfHrot(1)!.Get(h.Schema.EntityType).AsInt());
        h.HrotStep(HrotDisHarness.State(2, tkb: 100, disType: new DisTypeCode(1, 2, 0, 4, 0, 0, 0).Pack()));
        Assert.Equal(Tank.Pack(), h.EntityOfHrot(2)!.Get(h.Schema.EntityType).AsInt());

        for (int i = 0; i < 3; i++)
        {
            h.HrotStep(HrotDisHarness.State(3, tkb: 99999));
        }

        h.HrotStep(HrotDisHarness.State(4, tkb: 99999));
        Assert.False(h.EntityOfHrot(3)!.Has(h.Schema.EntityType));
        Assert.Equal(99999, h.EntityOfHrot(3)!.Get(h.Schema.TkbType).AsInt());
        Assert.Equal(1, h.WarningCount(nameof(HrotEntityStateIngress)));
    }

    [Theory]
    [InlineData(100, 100, DamageMapping.None)]
    [InlineData(99, 100, DamageMapping.Slight)]
    [InlineData(50, 100, DamageMapping.Slight)]
    [InlineData(49, 100, DamageMapping.Moderate)]
    [InlineData(1, 100, DamageMapping.Moderate)]
    [InlineData(0, 100, DamageMapping.Destroyed)]
    [InlineData(5, 0, DamageMapping.None)]
    public void Health_becomes_the_damage_state_by_the_shared_thresholds(double current, double max, int damage)
    {
        var h = LiveAtEquator();
        h.HrotStep(HrotDisHarness.State(1, health: current, healthMax: max));
        Assert.Equal(damage, h.EntityOfHrot(1)!.Get(h.Schema.Damage).AsInt());
        Assert.Equal(new Vec3(current, max, 0), h.EntityOfHrot(1)!.Get(h.Schema.Health).AsVec3());
    }

    [Fact]
    public void The_thresholds_come_from_the_configuration()
    {
        var config = GatewayConfiguration.CreateDefault() with
        {
            Shared = new SharedConfiguration { DamageThresholds = new DamageThresholds { Slight = 0.9, Moderate = 0.2 } },
        };
        var h = new HrotDisHarness(config);
        h.GoLive();
        h.HrotStep(HrotDisHarness.State(1, health: 80));
        h.HrotStep(HrotDisHarness.State(2, health: 20));
        Assert.Equal(DamageMapping.Moderate, h.EntityOfHrot(1)!.Get(h.Schema.Damage).AsInt());
        Assert.Equal(DamageMapping.Destroyed, h.EntityOfHrot(2)!.Get(h.Schema.Damage).AsInt());
    }

    [Fact]
    public void The_commander_becomes_the_parent_when_known_else_the_link_is_absent_and_set_later()
    {
        var h = LiveAtEquator();
        h.HrotStep(HrotDisHarness.State(2, commander: 1)); // 1 is not known yet
        Assert.False(h.EntityOfHrot(2)!.Has(h.Schema.Parent));
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(HrotDisHarness.State(2, commander: 1));
        Assert.Equal(h.EntityOfHrot(1)!.Id, h.EntityOfHrot(2)!.Get(h.Schema.Parent).AsEntity());
        h.HrotStep(HrotDisHarness.State(2, commander: 2)); // itself: no link
        Assert.False(h.EntityOfHrot(2)!.Has(h.Schema.Parent));
        h.HrotStep(HrotDisHarness.State(2, commander: 1));
        h.HrotStep(HrotDisHarness.State(2, commander: 0));
        Assert.False(h.EntityOfHrot(2)!.Has(h.Schema.Parent));
        h.HrotStep(HrotDisHarness.State(2, commander: 1));
        h.HrotStep(new HrotEntityRemoved(1));
        Assert.Null(h.EntityOfHrot(1));
    }

    // ---- malformed input ----------------------------------------------------------------------------------------------

    [Fact]
    public void Malformed_states_abort_without_a_trace()
    {
        var h = LiveAtEquator();
        long failures = h.Engine.Diagnostics.TransactionFailures;
        h.HrotStep(HrotDisHarness.State(0));
        h.HrotStep(HrotDisHarness.State(1, force: 300));
        h.HrotStep(HrotDisHarness.State(1, force: -1));
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(double.NaN, 0, 0)));
        h.HrotStep(HrotDisHarness.State(1, health: double.PositiveInfinity));
        h.HrotStep(HrotDisHarness.State(1, simTime: double.NaN));
        h.HrotStep(HrotDisHarness.State(1, rotation: new HrotQuat(0, double.NaN, 0, 1)));
        Assert.Equal(failures + 7, h.Engine.Diagnostics.TransactionFailures);
        Assert.Null(h.EntityOfHrot(1));
        Assert.Null(h.EntityOfHrot(0));
        Assert.Equal(0, h.Engine.State.Count);
    }

    // ---- removal ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_removal_destroys_the_entity_and_an_unknown_id_is_ignored()
    {
        var h = LiveAtEquator();
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(HrotDisHarness.State(2));
        h.HrotStep(new HrotEntityRemoved(1));
        Assert.Null(h.EntityOfHrot(1));
        Assert.NotNull(h.EntityOfHrot(2));
        Assert.False(h.HrotIds.TryResolve(1, out _));
        long failures = h.Engine.Diagnostics.TransactionFailures;
        h.HrotStep(new HrotEntityRemoved(77));
        Assert.Equal(failures, h.Engine.Diagnostics.TransactionFailures);
        Assert.Equal(1, h.Engine.State.Count);
        // the id can come back as a new entity
        h.HrotStep(HrotDisHarness.State(1));
        Assert.NotNull(h.EntityOfHrot(1));
    }

    // ---- echo ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_replica_id_is_never_taken_as_a_hrot_entity()
    {
        var h = new HrotDisHarness();
        h.GoLive();
        var dis = h.DisCreate(h.DisStateAt(new HrotVec3(10, 20, 0)));
        h.Step();
        long replicaId = h.Sent<ReplicaState>().Single().Id;
        var before = h.Engine.State.Find(dis)!.Get(h.Schema.Position).AsVec3();

        // HROT (wrongly or by loopback) reports the replica back as its own entity, removes it, poses a turret on it
        h.HrotStep(HrotDisHarness.State(replicaId, new HrotVec3(9999, 9999, 0), marking: "ECHO"));
        h.HrotStep(new HrotTurretPose(replicaId, 1, 0.5, 0.1));
        h.HrotStep(new HrotEntityRemoved(replicaId));

        var e = h.Engine.State.Find(dis)!;
        Assert.Equal(h.DisSource, e.Origin);
        Assert.Equal(before, e.Get(h.Schema.Position).AsVec3());
        Assert.Equal("DIS-1", e.Get(h.Schema.Marking).AsString());
        Assert.False(e.Has(h.Schema.ArticulationParts));
        Assert.Equal(1, h.Engine.State.Count);
        Assert.Single(h.Sent<ReplicaState>());
        Assert.Empty(h.Sent<ReplicaRemoved>());
    }

    // ---- turrets ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Turret_poses_are_merged_by_part_into_the_articulation_of_the_entity()
    {
        var h = LiveAtEquator();
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(new HrotTurretPose(1, 2, 0.5, 0.1));
        h.HrotStep(new HrotTurretPose(1, 1, -0.5, 0.0));
        h.HrotStep(new HrotTurretPose(1, 2, 0.6, 0.2));
        var parts = h.EntityOfHrot(1)!.Get(h.Schema.ArticulationParts).AsObject<ArticulationParts>();
        Assert.Equal([new ArticulationPart(1, -0.5, 0.0), new ArticulationPart(2, 0.6, 0.2)], parts.Parts.ToArray());

        // a pose for an unknown entity is ignored; a pose that changes nothing writes no change
        h.HrotStep(new HrotTurretPose(99, 1, 0, 0));
        Assert.Null(h.EntityOfHrot(99));
        h.HrotStep(new HrotTurretPose(1, 1, double.NaN, 0.0));
        Assert.Equal(parts, h.EntityOfHrot(1)!.Get(h.Schema.ArticulationParts).AsObject<ArticulationParts>());
        Assert.Empty(h.Sent<ReplicaState>());
    }

    [Fact]
    public void Two_poses_of_one_message_batch_merge_in_arrival_order()
    {
        var h = LiveAtEquator();
        h.HrotStep(HrotDisHarness.State(1));
        h.Hrot(new HrotTurretPose(1, 3, 1.0, 0.0));
        h.Hrot(new HrotTurretPose(1, 4, 2.0, 0.0));
        h.Step();
        Assert.Equal([3, 4], h.EntityOfHrot(1)!.Get(h.Schema.ArticulationParts).AsObject<ArticulationParts>().Parts.Select(p => p.PartId).ToArray());
    }
}

internal static class HrotQuatExtensions
{
    public static HrotQuat Normalize(this HrotQuat q)
    {
        double n = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return new HrotQuat(q.X / n, q.Y / n, q.Z / n, q.W / n);
    }
}
