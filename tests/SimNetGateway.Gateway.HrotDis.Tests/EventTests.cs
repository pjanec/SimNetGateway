using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>Fire and detonation in both directions (DD-006 sections 3.1 and 6.3, DD-007 sections 3 item 7 and 5.2 / 5.3).</summary>
public class EventTests
{
    private static HrotDisHarness Live()
    {
        var h = new HrotDisHarness();
        h.GoLive();
        return h;
    }

    private static DisEventId IdOf(AttributeBag payload, PivotSchema schema) => payload.Get(schema.EventId).AsObject<DisEventId>();

    // ---- HROT -> pivot -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_hrot_fire_becomes_a_weapon_fire_event_with_converted_location_and_the_catalogue_munition()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(10, 20, 0)));
        h.HrotStep(HrotDisHarness.State(2, new HrotVec3(500, 600, 0)));
        h.HrotStep(new HrotFire(1, 2, 501, 0, new HrotVec3(11, 21, 2), new HrotVec3(100, 0, 5), 3));

        var p = Support.LastEvent(h, h.Schema.WeaponFire);
        var s = h.Schema;
        Assert.Equal(h.EntityOfHrot(1)!.Id, p.Get(s.EventFiring).AsEntity());
        Assert.Equal(h.EntityOfHrot(2)!.Id, p.Get(s.EventTarget).AsEntity());
        Support.Near(h.Frame!.PointToEcef(new Vec3(11, 21, 2)), p.Get(s.EventLocation).AsVec3(), 1e-6, "location");
        Support.Near(h.Frame.VectorToEcef(new Vec3(100, 0, 5)), p.Get(s.EventVelocity).AsVec3(), 1e-9, "velocity");
        var munition = new MunitionCatalog().Describe(501, 0, out _);
        Assert.Equal(munition.DisType, p.Get(s.EventMunitionType).AsInt());
        Assert.Equal(501, p.Get(s.EventMunitionTkb).AsInt());
        Assert.Equal(munition.Warhead, p.Get(s.EventWarhead).AsInt());
        Assert.Equal(munition.Fuse, p.Get(s.EventFuse).AsInt());
        Assert.Equal(3, p.Get(s.EventQuantity).AsInt());
        Assert.Equal(new DisEventId(1, 3001, 1), IdOf(p, s));
        var expectedRange = (h.EntityOfHrot(2)!.Get(s.Position).AsVec3() - p.Get(s.EventLocation).AsVec3()).Length;
        Assert.Equal(expectedRange, p.Get(s.EventRange).AsDouble(), 6);
        Assert.False(p.TryGet(s.EventMunitionEntity, out _));
    }

    [Fact]
    public void The_event_ids_use_the_site_and_application_of_the_shared_section()
    {
        var config = GatewayConfiguration.CreateDefault() with { Shared = new SharedConfiguration { Site = 9, Application = 77 } };
        var h = new HrotDisHarness(config);
        h.GoLive();
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(new HrotFire(1, 0, 501, 0, default, default, 1));
        Assert.Equal(new DisEventId(9, 77, 1), IdOf(Support.LastEvent(h, h.Schema.WeaponFire), h.Schema));
    }

    [Fact]
    public void The_munition_type_of_hrot_is_used_when_complete_and_an_unknown_munition_warns_once()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1));
        long provided = new DisTypeCode(2, 9, 225, 3, 3, 3, 0).Pack();
        h.HrotStep(new HrotFire(1, 0, 501, provided, default, default, 1));
        Assert.Equal(provided, Support.LastEvent(h, h.Schema.WeaponFire).Get(h.Schema.EventMunitionType).AsInt());
        h.HrotStep(new HrotFire(1, 0, 9999, 0, default, default, 1));
        h.HrotStep(new HrotFire(1, 0, 9999, 0, default, default, 1));
        var p = Support.LastEvent(h, h.Schema.WeaponFire);
        Assert.Equal(0, p.Get(h.Schema.EventMunitionType).AsInt());
        Assert.Equal(0, p.Get(h.Schema.EventWarhead).AsInt());
        Assert.Equal(1, h.WarningCount(nameof(HrotFireIngress)));
        Assert.False(p.TryGet(h.Schema.EventTarget, out _));
    }

    [Fact]
    public void A_fire_of_an_unknown_shooter_or_before_the_origin_is_refused_and_a_replica_cannot_shoot()
    {
        var h = new HrotDisHarness();
        h.Session(ClusterPhase.Live);
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(new HrotFire(1, 0, 501, 0, default, default, 1)); // no origin yet
        h.Origin(50, 14, 300);
        h.HrotStep(new HrotFire(77, 0, 501, 0, default, default, 1)); // unknown shooter
        h.HrotStep(new HrotFire(1, 0, 501, 0, new HrotVec3(double.NaN, 0, 0), default, 1));
        Assert.Equal(3, h.Engine.Diagnostics.TransactionFailures);
        Assert.Equal(0, Support.CountEvents(h, h.Schema.WeaponFire));

        var dis = h.DisCreate(h.DisStateAt(new HrotVec3(5, 5, 0)));
        h.HrotIds.TryGetKey(dis, out long replicaId);
        h.HrotStep(new HrotFire(replicaId, 0, 501, 0, default, default, 1)); // an echo
        Assert.Equal(0, Support.CountEvents(h, h.Schema.WeaponFire));
        Assert.Equal(3, h.Engine.Diagnostics.TransactionFailures);
    }

    [Fact]
    public void A_target_may_be_a_dis_replica()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        var dis = h.DisCreate(h.DisStateAt(new HrotVec3(100, 0, 0)));
        h.HrotIds.TryGetKey(dis, out long replicaId);
        h.HrotStep(new HrotFire(1, replicaId, 501, 0, default, default, 1));
        Assert.Equal(dis, Support.LastEvent(h, h.Schema.WeaponFire).Get(h.Schema.EventTarget).AsEntity());
        h.HrotStep(new HrotDetonation(1, replicaId, 501, 0, new HrotVec3(100, 0, 0), default));
        var d = Support.LastEvent(h, h.Schema.MunitionDetonation);
        Assert.Equal(dis, d.Get(h.Schema.EventTarget).AsEntity());
        Assert.Equal(DetonationResults.EntityImpact, d.Get(h.Schema.EventResult).AsInt());
    }

    [Fact]
    public void A_detonation_is_an_entity_impact_with_a_target_and_a_ground_impact_without()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.HrotStep(HrotDisHarness.State(2, new HrotVec3(1000, 0, 0)));
        h.HrotStep(new HrotDetonation(1, 2, 501, 0, new HrotVec3(1001, 2, 3), new HrotVec3(0, 0, -10)));
        var s = h.Schema;
        var hit = Support.LastEvent(h, s.MunitionDetonation);
        Assert.Equal(DetonationResults.EntityImpact, hit.Get(s.EventResult).AsInt());
        Assert.Equal(h.EntityOfHrot(2)!.Id, hit.Get(s.EventTarget).AsEntity());
        Assert.Equal(h.EntityOfHrot(1)!.Id, hit.Get(s.EventFiring).AsEntity());
        Support.Near(h.Frame!.PointToEcef(new Vec3(1001, 2, 3)), hit.Get(s.EventLocation).AsVec3(), 1e-6, "location");
        Support.Near(h.Frame.VectorToEcef(new Vec3(0, 0, -10)), hit.Get(s.EventVelocity).AsVec3(), 1e-9, "velocity");

        // the location in the target's body axes: independent computation from Common
        var target = h.EntityOfHrot(2)!;
        var bodyToEcef = Orientation.ToMatrix(target.Get(s.Orientation).AsVec3());
        var expectedRelative = bodyToEcef.Transpose() * (hit.Get(s.EventLocation).AsVec3() - target.Get(s.Position).AsVec3());
        Support.Near(expectedRelative, hit.Get(s.EventRelativeLocation).AsVec3(), 1e-9, "relative location");
        // 1 m east and 2 m north and 3 m up of the target; its body (identity FLU) is x east, so relative x = +1 m forward, y = -2 m (right is south), z = -3 m (down)
        Support.Near(new Vec3(1, -2, -3), hit.Get(s.EventRelativeLocation).AsVec3(), 1e-6, "relative location, by hand");

        h.HrotStep(new HrotDetonation(1, 0, 501, 0, new HrotVec3(5, 5, 0), default));
        var ground = Support.LastEvent(h, s.MunitionDetonation);
        Assert.Equal(DetonationResults.GroundImpact, ground.Get(s.EventResult).AsInt());
        Assert.False(ground.TryGet(s.EventTarget, out _));
        Assert.False(ground.TryGet(s.EventRelativeLocation, out _));
        Assert.Equal(1, ground.Get(s.EventQuantity).AsInt());

        // an unknown target id is a ground impact, a gone shooter is just not named
        h.HrotStep(new HrotDetonation(55, 66, 501, 0, new HrotVec3(5, 5, 0), default));
        var unknown = Support.LastEvent(h, s.MunitionDetonation);
        Assert.Equal(DetonationResults.GroundImpact, unknown.Get(s.EventResult).AsInt());
        Assert.False(unknown.TryGet(s.EventFiring, out _));
    }

    [Fact]
    public void A_detonation_before_the_origin_is_refused()
    {
        var h = new HrotDisHarness();
        h.HrotStep(new HrotDetonation(1, 0, 501, 0, default, default));
        Assert.Equal(1, h.Engine.Diagnostics.TransactionFailures);
        Assert.Equal(0, Support.CountEvents(h, h.Schema.MunitionDetonation));
    }

    // ---- the correlator through the modules ---------------------------------------------------------------------------

    [Fact]
    public void A_detonation_reuses_the_event_id_of_the_fire_of_the_same_shooter_and_munition_within_30_seconds_only()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(HrotDisHarness.State(2));
        var s = h.Schema;

        h.HrotStep(new HrotFire(1, 0, 501, 0, default, default, 1));
        var fire = IdOf(Support.LastEvent(h, s.WeaponFire), s);
        h.Run(10.0);
        h.HrotStep(new HrotDetonation(1, 0, 501, 0, default, default));
        Assert.Equal(fire, IdOf(Support.LastEvent(h, s.MunitionDetonation), s));

        // a second detonation has no fire left: a new id
        h.HrotStep(new HrotDetonation(1, 0, 501, 0, default, default));
        var fresh = IdOf(Support.LastEvent(h, s.MunitionDetonation), s);
        Assert.NotEqual(fire, fresh);

        // other shooter / other munition do not match
        h.HrotStep(new HrotFire(1, 0, 501, 0, default, default, 1));
        var second = IdOf(Support.LastEvent(h, s.WeaponFire), s);
        h.HrotStep(new HrotDetonation(2, 0, 501, 0, default, default));
        Assert.NotEqual(second, IdOf(Support.LastEvent(h, s.MunitionDetonation), s));
        h.HrotStep(new HrotDetonation(1, 0, 502, 0, default, default));
        Assert.NotEqual(second, IdOf(Support.LastEvent(h, s.MunitionDetonation), s));
        h.HrotStep(new HrotDetonation(1, 0, 501, 0, default, default));
        Assert.Equal(second, IdOf(Support.LastEvent(h, s.MunitionDetonation), s));

        // 30.5 s later: too late
        h.HrotStep(new HrotFire(1, 0, 501, 0, default, default, 1));
        var late = IdOf(Support.LastEvent(h, s.WeaponFire), s);
        h.Run(30.5);
        h.HrotStep(new HrotDetonation(1, 0, 501, 0, default, default));
        Assert.NotEqual(late, IdOf(Support.LastEvent(h, s.MunitionDetonation), s));
    }

    [Fact]
    public void Two_fires_are_matched_in_order()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(new HrotFire(1, 0, 501, 0, default, default, 1));
        var first = IdOf(Support.LastEvent(h, h.Schema.WeaponFire), h.Schema);
        h.Run(1.0);
        h.HrotStep(new HrotFire(1, 0, 501, 0, default, default, 1));
        var second = IdOf(Support.LastEvent(h, h.Schema.WeaponFire), h.Schema);
        Assert.NotEqual(first, second);
        h.HrotStep(new HrotDetonation(1, 0, 501, 0, default, default));
        Assert.Equal(first, IdOf(Support.LastEvent(h, h.Schema.MunitionDetonation), h.Schema));
        h.HrotStep(new HrotDetonation(1, 0, 501, 0, default, default));
        Assert.Equal(second, IdOf(Support.LastEvent(h, h.Schema.MunitionDetonation), h.Schema));
    }

    [Fact]
    public void Events_of_the_hrot_side_are_never_injected_back()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1));
        h.HrotStep(HrotDisHarness.State(2));
        h.HrotStep(new HrotFire(1, 2, 501, 0, default, default, 1));
        h.HrotStep(new HrotDetonation(1, 2, 501, 0, default, default));
        h.Run(1.0);
        Assert.Equal(1, Support.CountEvents(h, h.Schema.WeaponFire));
        Assert.Empty(h.Sent<InjectFire>());
        Assert.Empty(h.Sent<InjectDetonation>());
    }

    // ---- DIS -> HROT ---------------------------------------------------------------------------------------------------

    private static (EntityId Shooter, long ShooterId) DisShooter(HrotDisHarness h, double east)
    {
        var e = h.DisCreate(h.DisStateAt(new HrotVec3(east, 0, 0)));
        h.HrotIds.TryGetKey(e, out long id);
        return (e, id);
    }

    private static void DisFire(HrotDisHarness h, EntityId shooter, EntityId? target, Vec3 location, Vec3 velocity, long munitionType, long? tkb, int quantity = 2)
    {
        h.DisEvent(h.Schema.WeaponFire, bag =>
        {
            bag.Set(h.Schema.EventId, AttrValue.FromObject(new DisEventId(1, 3002, 5)));
            bag.Set(h.Schema.EventFiring, AttrValue.From(shooter));
            if (target is { } t)
            {
                bag.Set(h.Schema.EventTarget, AttrValue.From(t));
            }

            bag.Set(h.Schema.EventMunitionType, AttrValue.From(munitionType));
            if (tkb is { } k)
            {
                bag.Set(h.Schema.EventMunitionTkb, AttrValue.From(k));
            }

            bag.Set(h.Schema.EventQuantity, AttrValue.From((long)quantity));
            bag.Set(h.Schema.EventLocation, AttrValue.From(location));
            bag.Set(h.Schema.EventVelocity, AttrValue.From(velocity));
        });
    }

    [Fact]
    public void A_dis_fire_becomes_an_inject_fire_with_the_target_marked_hrot_or_not()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(7, new HrotVec3(300, 0, 0)));
        var (shooter, shooterId) = DisShooter(h, 100);
        var (other, otherId) = DisShooter(h, 200);
        var location = h.Frame!.PointToEcef(new Vec3(101, 2, 3));
        var velocity = h.Frame.VectorToEcef(new Vec3(400, 0, 1));
        long munition = new MunitionCatalog().Describe(501, 0, out _).DisType;

        DisFire(h, shooter, h.EntityOfHrot(7)!.Id, location, velocity, munition, tkb: null);
        var f = h.Sent<InjectFire>().Single();
        Assert.Equal(shooterId, f.ShooterId);
        Assert.Equal(7, f.TargetId);
        Assert.True(f.TargetIsHrot);
        Assert.Equal(501, f.MunitionTkb); // from the munition catalogue, since the event carries no TKB mirror
        Assert.Equal(munition, f.MunitionDisType);
        Assert.Equal(2, f.Quantity);
        Support.Near(new Vec3(101, 2, 3), f.Origin, 1e-6, "origin");
        Support.Near(new Vec3(400, 0, 1), f.Velocity, 1e-9, "velocity");

        DisFire(h, shooter, other, location, velocity, munition, tkb: 502); // the target is another DIS entity
        var g = h.Sent<InjectFire>()[1];
        Assert.Equal(otherId, g.TargetId);
        Assert.False(g.TargetIsHrot);
        Assert.Equal(502, g.MunitionTkb); // the mirror, when given, wins

        DisFire(h, shooter, null, location, velocity, 0, tkb: null);
        var n = h.Sent<InjectFire>()[2];
        Assert.Equal(0, n.TargetId);
        Assert.False(n.TargetIsHrot);
        Assert.Equal(0, n.MunitionTkb); // nothing known: the default for unknown (0)
    }

    [Fact]
    public void A_dis_detonation_becomes_an_inject_detonation()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(7, new HrotVec3(300, 0, 0)));
        var (shooter, shooterId) = DisShooter(h, 100);
        var location = h.Frame!.PointToEcef(new Vec3(299, 1, 0));
        var velocity = h.Frame.VectorToEcef(new Vec3(0, 0, -30));
        long munition = new MunitionCatalog().Describe(501, 0, out _).DisType;

        void Detonate(EntityId? target)
        {
            h.DisEvent(h.Schema.MunitionDetonation, bag =>
            {
                bag.Set(h.Schema.EventId, AttrValue.FromObject(new DisEventId(1, 3002, 6)));
                bag.Set(h.Schema.EventFiring, AttrValue.From(shooter));
                if (target is { } t)
                {
                    bag.Set(h.Schema.EventTarget, AttrValue.From(t));
                }

                bag.Set(h.Schema.EventMunitionType, AttrValue.From(munition));
                bag.Set(h.Schema.EventLocation, AttrValue.From(location));
                bag.Set(h.Schema.EventVelocity, AttrValue.From(velocity));
                bag.Set(h.Schema.EventResult, AttrValue.From((long)DetonationResults.EntityImpact));
            });
        }

        Detonate(h.EntityOfHrot(7)!.Id);
        var d = h.Sent<InjectDetonation>().Single();
        Assert.Equal(shooterId, d.ShooterId);
        Assert.Equal(7, d.TargetId);
        Assert.True(d.TargetIsHrot);
        Assert.Equal(501, d.MunitionTkb);
        Assert.Equal(munition, d.MunitionDisType);
        Support.Near(new Vec3(299, 1, 0), d.Location, 1e-6, "location");
        Support.Near(new Vec3(0, 0, -30), d.Velocity, 1e-9, "velocity");

        Detonate(null);
        var ground = h.Sent<InjectDetonation>()[1];
        Assert.Equal(0, ground.TargetId);
        Assert.False(ground.TargetIsHrot);
    }

    [Fact]
    public void A_dis_event_that_names_a_dead_target_or_an_unshown_shooter_is_injected_with_zero_ids()
    {
        var h = Live();
        var (shooter, _) = DisShooter(h, 100);
        var victim = h.DisCreate(h.DisStateAt(new HrotVec3(200, 0, 0)));
        h.DisRemove(victim);
        var location = h.Frame!.PointToEcef(new Vec3(1, 1, 1));
        DisFire(h, shooter, victim, location, default, 0, null);
        Assert.Equal(0, h.Sent<InjectFire>().Single().TargetId);

        // a shooter that never became a replica (incomplete: no state time)
        var changes = h.Engine.BeginChange(h.DisSource);
        var ghost = changes.CreateEntity();
        changes.Set(ghost, h.Schema.Marking, AttrValue.From("ghost"));
        changes.Commit();
        DisFire(h, ghost, null, location, default, 0, null);
        Assert.Equal(0, h.Sent<InjectFire>()[1].ShooterId);
    }

    public static TheoryData<ClusterPhase, bool> ClosedForEffects() => new()
    {
        { ClusterPhase.Idle, true },
        { ClusterPhase.Edit, true },
        { ClusterPhase.Preview, false },
        { ClusterPhase.Live, false },
        { ClusterPhase.Replay, true },
        { ClusterPhase.Replay, false },
        { ClusterPhase.Review, true },
        { ClusterPhase.Review, false },
        { ClusterPhase.Degraded, true },
    };

    [Theory]
    [MemberData(nameof(ClosedForEffects))]
    public void Dis_effects_reach_hrot_only_while_the_exercise_runs(ClusterPhase phase, bool advancing)
    {
        var h = new HrotDisHarness();
        h.GoLive();
        var (shooter, _) = DisShooter(h, 100);
        h.Session(phase, advancing);
        if (h.Engine.State.Find(shooter) is null)
        {
            // a mode change or an unload removed the pivot entities: make one for the event
            shooter = h.DisCreate(h.DisStateAt(new HrotVec3(100, 0, 0)));
        }

        var location = h.Frame!.PointToEcef(new Vec3(1, 1, 1));
        DisFire(h, shooter, null, location, default, 0, null);
        h.DisEvent(h.Schema.MunitionDetonation, bag =>
        {
            bag.Set(h.Schema.EventFiring, AttrValue.From(shooter));
            bag.Set(h.Schema.EventLocation, AttrValue.From(location));
        });
        h.Run(0.2);
        Assert.Empty(h.Sent<InjectFire>());
        Assert.Empty(h.Sent<InjectDetonation>());
        Assert.Equal(2, Support.Events(h).Count(e => e.Source == h.DisSource)); // the events were dispatched, just not injected
    }

    [Fact]
    public void Dis_effects_reach_hrot_in_every_running_phase_and_before_the_origin_not_at_all()
    {
        foreach (var phase in new[] { ClusterPhase.Preview, ClusterPhase.Live })
        {
            var h = new HrotDisHarness();
            h.Origin(50, 14, 300);
            h.Session(phase);
            var shooter = h.DisCreate(h.DisStateAt(new HrotVec3(1, 0, 0)));
            DisFire(h, shooter, null, h.Frame!.PointToEcef(new Vec3(1, 1, 1)), default, 0, null);
            Assert.Single(h.Sent<InjectFire>());
        }

        var none = new HrotDisHarness();
        none.Session(ClusterPhase.Live);
        var changes = none.Engine.BeginChange(none.DisSource);
        var e = changes.CreateEntity();
        changes.Set(e, none.Schema.Marking, AttrValue.From("x"));
        changes.Commit();
        DisFire(none, e, null, new Vec3(Wgs84.A, 0, 0), default, 0, null);
        Assert.Empty(none.Sent<InjectFire>());
    }
}
