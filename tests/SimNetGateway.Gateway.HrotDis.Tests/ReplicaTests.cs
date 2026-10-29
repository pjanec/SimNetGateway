using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>Pivot entities of origin dis as HROT replicas (DD-006 section 0, DD-007 sections 3, 5.2 and 6).</summary>
public class ReplicaTests
{
    private static readonly DisTypeCode Tank = new(1, 1, 225, 1, 1, 3, 0);

    private static HrotDisHarness Live(GatewayConfiguration? config = null, long idLast = 5_000_999)
    {
        var h = new HrotDisHarness(config, idLast: idLast);
        h.GoLive();
        return h;
    }

    [Fact]
    public void A_new_dis_entity_becomes_one_replica_state_with_the_converted_values()
    {
        var h = Live();
        var enu = new HrotVec3(1000, 2000, 10);
        var state = h.DisStateAt(enu, new HrotVec3(3, 4, 5), damage: DamageMapping.Slight, stateTime: 42.5) with
        {
            Marking = "Bravo", Force = 3, Acceleration = new Vec3(0, 0, 0), DrAlgorithm = DrAlgorithms.Fpw,
        };
        var entity = h.DisCreate(state);

        var s = h.Sent<ReplicaState>().Single();
        Assert.InRange(s.Id, 5_000_000, 5_000_999);
        Assert.True(h.HrotIds.TryResolve(s.Id, out var bound) && bound == entity);
        Assert.Equal(Tank.Pack(), s.DisType);
        Assert.Equal(100, s.TkbType);
        Assert.Equal("Bravo", s.Marking);
        Assert.Equal(3, s.Force);
        Assert.False(s.Deactivated);
        Support.Near(enu, s.State.Position, 1e-6, "position");
        Support.Near(new HrotVec3(3, 4, 5), s.State.Velocity, 1e-9, "velocity");
        Support.Near(HrotVec3.Zero, s.State.Acceleration, 1e-12, "acceleration");
        Support.SameRotation(HrotQuat.Identity, s.State.Rotation, 1e-9, "rotation");
        Support.Near(HrotVec3.Zero, s.State.AngularVelocity, 1e-12, "angular velocity");
        Assert.Equal(2, s.State.DrAlgorithm);
        Assert.Equal(42.5, s.State.StateTime);
        Assert.Equal(75.0, s.HealthCurrent);
        Assert.Equal(100.0, s.HealthMax);
        Assert.True(s.State.Rotation.W >= 0);
    }

    [Fact]
    public void A_replica_state_is_sent_only_on_a_new_dis_state_and_never_per_tick()
    {
        var h = Live();
        var a = h.DisCreate(h.DisStateAt(new HrotVec3(10, 0, 0)));
        var b = h.DisCreate(h.DisStateAt(new HrotVec3(20, 0, 0)));
        int written = 2;
        h.Run(2.0); // 120 quiet ticks: each asserts that nothing was sent
        Assert.Equal(written, h.Sent<ReplicaState>().Count);

        for (int i = 1; i <= 5; i++)
        {
            h.DisUpdate(a, h.DisStateAt(new HrotVec3(10 + i, 0, 0)));
            written++;
            h.Steps(3);
            if (i % 2 == 0)
            {
                h.DisUpdate(b, h.DisStateAt(new HrotVec3(20 + i, 0, 0)));
                written++;
            }
        }

        h.Run(1.0);
        Assert.Equal(written, h.Sent<ReplicaState>().Count);
        Assert.Equal(2, h.Sent<ReplicaState>().Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Writing_the_same_state_again_sends_nothing_but_a_new_damage_or_identity_does()
    {
        var h = Live();
        var state = h.DisStateAt(new HrotVec3(10, 0, 0));
        var e = h.DisCreate(state);
        h.DisUpdate(e, state); // identical: nothing changed
        Assert.Single(h.Sent<ReplicaState>());
        h.DisUpdate(e, state with { Damage = DamageMapping.Moderate }); // same state time, new damage
        Assert.Equal(2, h.Sent<ReplicaState>().Count);
        Assert.Equal(40.0, h.Sent<ReplicaState>()[1].HealthCurrent);
        h.DisUpdate(e, state with { Damage = DamageMapping.Moderate, Marking = "Renamed" });
        Assert.Equal("Renamed", h.Sent<ReplicaState>()[2].Marking);
        h.DisUpdate(e, state with { Damage = DamageMapping.Moderate, Marking = "Renamed", Force = 1 });
        Assert.Equal(1, h.Sent<ReplicaState>()[3].Force);
        h.DisUpdate(e, state with { Damage = DamageMapping.Destroyed, Marking = "Renamed", Force = 1, Deactivated = true });
        var last = h.Sent<ReplicaState>()[4];
        Assert.Equal(0.0, last.HealthCurrent);
        Assert.True(last.Deactivated);
        Assert.Equal(5, h.Sent<ReplicaState>().Count);
        Assert.Single(h.Sent<ReplicaState>().Select(s => s.Id).Distinct());
    }

    [Fact]
    public void A_changed_position_without_a_new_state_time_is_not_a_new_state()
    {
        var h = Live();
        var state = h.DisStateAt(new HrotVec3(10, 0, 0));
        var e = h.DisCreate(state);
        h.DisUpdate(e, h.DisStateAt(new HrotVec3(99, 0, 0), stateTime: state.StateTime)); // moved, same state time
        Assert.Single(h.Sent<ReplicaState>());
    }

    [Fact]
    public void An_entity_that_is_still_incomplete_is_not_shown_until_it_has_type_position_and_time()
    {
        var h = Live();
        var changes = h.Engine.BeginChange(h.DisSource);
        var id = changes.CreateEntity();
        changes.Set(id, h.Schema.Marking, AttrValue.From("partial"));
        changes.Set(id, h.Schema.EntityType, AttrValue.From(Tank.Pack()));
        changes.Commit();
        Assert.Empty(h.Sent<ReplicaState>());
        Assert.False(h.HrotIds.Contains(id));

        changes = h.Engine.BeginChange(h.DisSource);
        changes.Set(id, h.Schema.Position, AttrValue.From(h.DisStateAt(new HrotVec3(1, 2, 3)).Position));
        changes.Commit();
        Assert.Empty(h.Sent<ReplicaState>()); // no state time yet: nothing is known about when the position was valid
        Assert.False(h.HrotIds.Contains(id));

        changes = h.Engine.BeginChange(h.DisSource);
        changes.Set(id, h.Schema.Marking, AttrValue.From("renamed")); // a trigger for an entity that is still incomplete
        changes.Commit();
        Assert.Empty(h.Sent<ReplicaState>());
        Assert.False(h.HrotIds.Contains(id));
        Assert.Equal(0, h.Engine.Diagnostics.RuleFailures);

        changes = h.Engine.BeginChange(h.DisSource);
        changes.Set(id, h.Schema.StateTime, AttrValue.From(5.0));
        changes.Commit();
        var s = h.Sent<ReplicaState>().Single();
        Support.Near(new HrotVec3(1, 2, 3), s.State.Position, 1e-6);
        Assert.Equal(DrAlgorithms.Static, s.State.DrAlgorithm); // defaults for what DIS did not give
        Support.Near(HrotVec3.Zero, s.State.Velocity, 1e-12);
    }

    // ---- before the origin and the session ----------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_sent_before_a_valid_origin_and_everything_waiting_is_introduced_when_it_arrives()
    {
        var h = new HrotDisHarness();
        h.Session(ClusterPhase.Live);
        // DIS entities exist before HROT told the origin; their ECEF state is known, the ENU one cannot be built
        var changes = h.Engine.BeginChange(h.DisSource);
        var ids = new List<EntityId>();
        for (int i = 0; i < 3; i++)
        {
            var id = changes.CreateEntity();
            ids.Add(id);
            changes.Set(id, h.Schema.EntityType, AttrValue.From(Tank.Pack()));
            changes.Set(id, h.Schema.Position, AttrValue.From(new Vec3(Wgs84.A + 100 + i, 0, 0)));
            changes.Set(id, h.Schema.StateTime, AttrValue.From(1.0 + i));
        }

        changes.Commit();
        h.Run(0.5);
        Assert.Empty(h.Sent<ReplicaState>());
        Assert.All(ids, id => Assert.False(h.HrotIds.Contains(id)));
        Assert.Equal(0UL, h.Ids.InUse);

        h.Origin(0.0, 0.0, 100.0);
        Assert.Equal(3, h.Sent<ReplicaState>().Count);
        Assert.Equal(3, h.Sent<ReplicaState>().Select(s => s.Id).Distinct().Count());
        Support.Near(new HrotVec3(0, 0, 0), h.Sent<ReplicaState>().Single(s => s.State.StateTime == 1.0).State.Position, 1e-6);
        Support.Near(new HrotVec3(0, 0, 2), h.Sent<ReplicaState>().Single(s => s.State.StateTime == 3.0).State.Position, 1e-6);
    }

    [Fact]
    public void An_idle_session_shows_no_replicas_and_edit_shows_them_without_effects()
    {
        var h = new HrotDisHarness();
        h.Origin(50, 14, 300); // origin but the session is still Idle
        var e = h.DisCreate(h.DisStateAt(new HrotVec3(5, 5, 0)));
        h.Run(0.2);
        Assert.Empty(h.Sent<ReplicaState>());

        h.Session(ClusterPhase.Edit, advancing: false);
        Assert.Single(h.Sent<ReplicaState>());
        h.DisUpdate(e, h.DisStateAt(new HrotVec3(6, 5, 0)));
        Assert.Equal(2, h.Sent<ReplicaState>().Count);

        h.Session(ClusterPhase.Live); // Loaded -> Running keeps the replicas: nothing new is sent
        h.Session(ClusterPhase.Live, advancing: false);
        Assert.Equal(2, h.Sent<ReplicaState>().Count);
        Assert.Empty(h.Sent<ReplicaRemoved>());
    }

    [Fact]
    public void Idle_removes_the_replicas_and_the_ids_go_back()
    {
        var h = Live();
        h.DisCreate(h.DisStateAt(new HrotVec3(1, 0, 0)));
        h.DisCreate(h.DisStateAt(new HrotVec3(2, 0, 0)));
        Assert.Equal(2UL, h.Ids.InUse);
        var ids = h.Sent<ReplicaState>().Select(s => s.Id).ToHashSet();

        h.Session(ClusterPhase.Idle);
        Assert.Equal(ids, h.Sent<ReplicaRemoved>().Select(r => r.Id).ToHashSet());
        Assert.Equal(2, h.Sent<ReplicaRemoved>().Count);
        Assert.Equal(0UL, h.Ids.InUse);
        Assert.Equal(0, h.Engine.State.Count); // the unload removed the pivot entities
    }

    [Fact]
    public void Replay_shows_no_replicas_and_leaving_it_introduces_fresh_ones()
    {
        var h = Live();
        var e = h.DisCreate(h.DisStateAt(new HrotVec3(1, 0, 0)));
        h.HrotStep(HrotDisHarness.State(7, new HrotVec3(100, 0, 0)));
        Assert.Single(h.Sent<ReplicaState>());

        h.Session(ClusterPhase.Replay);
        Assert.Single(h.Sent<ReplicaRemoved>());
        Assert.Equal(0UL, h.Ids.InUse);
        Assert.Equal(SessionMode.Replay, h.Engine.Session.Mode);

        // DIS keeps talking while the replay runs: the pivot may even hold DIS entities, HROT sees none
        var during = h.DisCreate(h.DisStateAt(new HrotVec3(2, 0, 0)));
        h.DisUpdate(during, h.DisStateAt(new HrotVec3(3, 0, 0)));
        h.Run(0.3);
        Assert.Single(h.Sent<ReplicaState>());
        Assert.Equal(0UL, h.Ids.InUse);

        h.Session(ClusterPhase.Live);
        Assert.Equal(SessionMode.Live, h.Engine.Session.Mode);
        // the mode change unloaded everything; DIS entities come back with their next PDU
        Assert.Single(h.Sent<ReplicaState>());
        var again = h.DisCreate(h.DisStateAt(new HrotVec3(4, 0, 0)));
        Assert.Equal(2, h.Sent<ReplicaState>().Count);
        Assert.True(h.HrotIds.Contains(again));
        Assert.False(h.Engine.State.Find(e) is { IsAlive: true });
    }

    // ---- identity and removal -----------------------------------------------------------------------------------------

    [Fact]
    public void Every_dis_entity_gets_exactly_one_hrot_id_which_is_released_on_removal_and_a_removed_entity_is_removed_in_hrot()
    {
        var h = Live();
        var a = h.DisCreate(h.DisStateAt(new HrotVec3(1, 0, 0)));
        var b = h.DisCreate(h.DisStateAt(new HrotVec3(2, 0, 0)));
        h.DisUpdate(a, h.DisStateAt(new HrotVec3(1.5, 0, 0)));
        h.DisUpdate(a, h.DisStateAt(new HrotVec3(1.6, 0, 0)));
        Assert.Equal(2UL, h.Ids.InUse); // one id per entity however many states
        h.HrotIds.TryGetKey(a, out long idA);
        h.HrotIds.TryGetKey(b, out long idB);
        Assert.NotEqual(idA, idB);
        Assert.Equal(3, h.Sent<ReplicaState>().Count(s => s.Id == idA));

        h.DisRemove(a);
        Assert.Equal(new[] { idA }, h.Sent<ReplicaRemoved>().Select(r => r.Id).ToArray());
        Assert.Equal(1UL, h.Ids.InUse);
        Assert.False(h.HrotIds.TryResolve(idA, out _));
        h.DisRemove(b);
        Assert.Equal(0UL, h.Ids.InUse);
        Assert.Equal(2, h.Sent<ReplicaRemoved>().Count);
    }

    [Fact]
    public void A_dis_entity_that_was_never_shown_is_removed_without_a_message()
    {
        var h = new HrotDisHarness();
        h.Session(ClusterPhase.Live); // no origin: nothing is shown
        var e = h.DisCreate(new DisState { Position = new Vec3(Wgs84.A, 0, 0) });
        h.DisRemove(e);
        Assert.Empty(h.Sent<ReplicaState>());
        Assert.Empty(h.Sent<ReplicaRemoved>());
        Assert.Equal(0UL, h.Ids.InUse);
    }

    [Fact]
    public void An_exhausted_id_allocator_warns_once_and_the_entity_is_shown_on_its_next_state_after_an_id_came_back()
    {
        var h = Live(idLast: 5_000_001); // two ids
        var a = h.DisCreate(h.DisStateAt(new HrotVec3(1, 0, 0)));
        var b = h.DisCreate(h.DisStateAt(new HrotVec3(2, 0, 0)));
        var c = h.DisCreate(h.DisStateAt(new HrotVec3(3, 0, 0)));
        Assert.Equal(2, h.Sent<ReplicaState>().Count);
        Assert.False(h.HrotIds.Contains(c));
        h.DisUpdate(c, h.DisStateAt(new HrotVec3(3.5, 0, 0)));
        h.DisUpdate(c, h.DisStateAt(new HrotVec3(3.6, 0, 0)));
        Assert.Equal(2, h.Sent<ReplicaState>().Count);
        Assert.Equal(1, h.WarningCount(nameof(ReplicaPublisher)));

        h.DisRemove(a);
        Assert.DoesNotContain(h.Sent<ReplicaState>(), s => s.State.Position.X > 3);
        h.DisUpdate(c, h.DisStateAt(new HrotVec3(3.7, 0, 0)));
        Assert.True(h.HrotIds.Contains(c));
        Assert.Equal(3, h.Sent<ReplicaState>().Count);
        Assert.Equal(2UL, h.Ids.InUse);
        Assert.True(h.HrotIds.Contains(b));
    }

    // ---- echo ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Hrot_entities_never_come_back_as_replicas()
    {
        var h = Live();
        for (int i = 0; i < 20; i++)
        {
            h.HrotStep(HrotDisHarness.State(100 + i, new HrotVec3(i * 10, 0, 0), simTime: i));
        }

        h.Session(ClusterPhase.Preview, advancing: false);
        h.Session(ClusterPhase.Live);
        h.HrotStep(new HrotOrigin(51, 15, 100, 2));
        h.HrotStep(new HrotEntityRemoved(105));
        h.Run(1.0);
        Assert.Empty(h.Sent<ReplicaState>());
        Assert.Empty(h.Sent<ReplicaRemoved>());
        Assert.Equal(0UL, h.Ids.InUse);
    }

    [Fact]
    public void Dis_and_hrot_entities_live_side_by_side_with_disjoint_ids()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1001, new HrotVec3(5, 5, 0)));
        h.DisCreate(h.DisStateAt(new HrotVec3(6, 6, 0)));
        Assert.Single(h.Sent<ReplicaState>());
        Assert.InRange(h.Sent<ReplicaState>()[0].Id, 5_000_000, 5_000_999);
        Assert.NotNull(h.EntityOfHrot(1001));
        Assert.Equal(h.HrotSource, h.EntityOfHrot(1001)!.Origin);
    }

    // ---- kinematics of the snapshot -----------------------------------------------------------------------------------

    [Fact]
    public void Body_axis_algorithms_keep_velocity_and_acceleration_in_body_axes()
    {
        var h = Live();
        var state = h.DisStateAt(new HrotVec3(0, 0, 0)) with
        {
            DrAlgorithm = 8, Velocity = new Vec3(11, 22, 33), Acceleration = new Vec3(1, 2, 3),
        };
        h.DisCreate(state);
        var s = h.Sent<ReplicaState>().Single();
        Assert.Equal(8, s.State.DrAlgorithm);
        Support.Near(new HrotVec3(11, 22, 33), s.State.Velocity, 1e-12, "body velocity is not rotated");
        Support.Near(new HrotVec3(1, 2, 3), s.State.Acceleration, 1e-12, "body acceleration is not rotated");

        var world = h.DisStateAt(new HrotVec3(0, 0, 0)) with { DrAlgorithm = 4, Velocity = new Vec3(11, 22, 33), Acceleration = new Vec3(1, 2, 3) };
        h.DisCreate(world);
        var w = h.Sent<ReplicaState>().Last();
        Support.Near(h.Frame!.VectorFromEcef(world.Velocity), w.State.Velocity, 1e-9, "world velocity is rotated to ENU");
        Support.Near(h.Frame.VectorFromEcef(world.Acceleration), w.State.Acceleration, 1e-9, "world acceleration is rotated to ENU");
    }

    [Fact]
    public void Orientation_and_angular_velocity_are_converted_to_the_hrot_quaternion_and_enu_axes()
    {
        var h = Live();
        var q = new HrotQuat(0.3, 0.1, -0.5, 0.8).Normalize();
        var quaternion = new Quaternion(q.W, q.X, q.Y, q.Z);
        var omegaEnu = new Vec3(0.2, -0.1, 0.4);
        var state = h.DisStateAt(new HrotVec3(0, 0, 0)) with
        {
            Orientation = LocalToGeocentricOrientation.ToDisEuler(quaternion, h.Frame!),
            AngularVelocity = LocalToGeocentricOrientation.AngularVelocityEnuToBodyFrd(quaternion, omegaEnu),
        };
        h.DisCreate(state);
        var s = h.Sent<ReplicaState>().Single();
        Support.SameRotation(q, s.State.Rotation, 1e-9, "rotation");
        Support.Near(omegaEnu, s.State.AngularVelocity, 1e-9, "angular velocity");
    }

    [Fact]
    public void An_unknown_dis_type_gets_the_default_tkb_and_one_warning()
    {
        var config = GatewayConfiguration.CreateDefault() with { Shared = new SharedConfiguration { DefaultTkbForUnknown = 4242 } };
        var h = Live(config);
        var odd = new DisTypeCode(3, 3, 9, 9, 9, 9, 9).Pack();
        var e = h.DisCreate(h.DisStateAt(new HrotVec3(1, 0, 0)) with { EntityType = odd });
        h.DisUpdate(e, h.DisStateAt(new HrotVec3(2, 0, 0)) with { EntityType = odd });
        h.DisCreate(h.DisStateAt(new HrotVec3(3, 0, 0)) with { EntityType = odd });
        Assert.All(h.Sent<ReplicaState>(), s => Assert.Equal(4242, s.TkbType));
        Assert.All(h.Sent<ReplicaState>(), s => Assert.Equal(odd, s.DisType));
        Assert.Equal(1, h.WarningCount(nameof(ReplicaPublisher)));
    }

    [Fact]
    public void The_damage_to_health_mapping_follows_the_shared_section()
    {
        var config = GatewayConfiguration.CreateDefault() with
        {
            Shared = new SharedConfiguration { EntityMaxHealth = 500, DamageThresholds = new DamageThresholds { Slight = 0.6, Moderate = 0.1 } },
        };
        var h = Live(config);
        var e = h.DisCreate(h.DisStateAt(new HrotVec3(1, 0, 0), damage: 0));
        for (int damage = 1; damage <= 3; damage++)
        {
            h.DisUpdate(e, h.DisStateAt(new HrotVec3(1, 0, 0), damage: damage));
        }

        var sent = h.Sent<ReplicaState>();
        Assert.Equal(4, sent.Count);
        Assert.All(sent, s => Assert.Equal(500.0, s.HealthMax));
        Assert.Equal(500.0, sent[0].HealthCurrent);
        Assert.True(sent[1].HealthCurrent < sent[0].HealthCurrent && sent[2].HealthCurrent < sent[1].HealthCurrent && sent[3].HealthCurrent == 0.0);
        var mapping = new DamageMapping(config.Shared);
        for (int damage = 0; damage <= 3; damage++)
        {
            Assert.Equal(damage, mapping.DamageOf(sent[damage].HealthCurrent, sent[damage].HealthMax));
        }
    }
}
