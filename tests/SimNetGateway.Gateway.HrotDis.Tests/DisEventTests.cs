using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Dis7;
using SimNetGateway.Protocol.Hrot;
using static SimNetGateway.Gateway.HrotDis.Tests.DisPdus;
using static SimNetGateway.Gateway.HrotDis.Tests.Support;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>Fire and Detonation both ways (DD-006 6.3): HROT events as PDUs with consistent event ids, DIS PDUs as pivot events and on to HROT as Inject*, session gates and the echo rules.</summary>
public class DisEventTests
{
    private static readonly long Munition501 = new DisTypeCode(2, 9, 225, 2, 1, 1, 0).Pack();

    /// <summary>A live gateway with two published HROT entities, 1 at the origin and 2 at 300 m east.</summary>
    private static HrotDisHarness Setup(GatewayConfiguration? config = null)
    {
        var h = new HrotDisHarness(config);
        h.GoLive();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.HrotStep(HrotDisHarness.State(2, new HrotVec3(300, 0, 0)));
        return h;
    }

    private static DisEntityKey KeyOfHrot(HrotDisHarness h, long hrotId) => h.DisKeyOf(h.EntityOfHrot(hrotId)!.Id)!.Value;

    private static HrotFire Shot(long shooter, long target, long tkb = 501, double x = 5) =>
        new(shooter, target, tkb, 0, new HrotVec3(x, 0, 2), new HrotVec3(800, 0, 0), 1);

    private static HrotDetonation Blast(long shooter, long target, long tkb = 501, double x = 300, double y = 2) =>
        new(shooter, target, tkb, 0, new HrotVec3(x, y, 0), new HrotVec3(0, 0, -50));

    [Fact]
    public void A_hrot_fire_becomes_a_fire_pdu_with_the_dis_ids_the_munition_and_ecef_values()
    {
        var h = Setup();
        h.HrotStep(Shot(1, 2));
        var fire = Assert.Single(h.DisChecker!.Sent<FirePdu>());
        Assert.Equal(PduTypes.Fire, fire.PduType);
        Assert.Equal(KeyOfHrot(h, 1), DisConversions.KeyOf(fire.FiringEntityID));
        Assert.Equal(KeyOfHrot(h, 2), DisConversions.KeyOf(fire.TargetEntityID));
        Assert.True(DisConversions.IsNoEntity(fire.MunitionExpendibleID));
        Assert.Equal(new DisEventId(1, 3001, 1), DisConversions.EventIdOf(fire.EventID));
        Assert.Equal(Munition501, DisConversions.PackType(fire.Descriptor.MunitionType));
        Assert.Equal(1000, fire.Descriptor.Warhead);
        Assert.Equal(8000, fire.Descriptor.Fuse);
        Assert.Equal(1, fire.Descriptor.Quantity);
        Assert.Equal(0, fire.Descriptor.Rate);
        var location = h.Frame!.PointToEcef(new Vec3(5, 0, 2));
        Support.Near(location, DisConversions.ToVec(fire.LocationInWorldCoordinates), 1e-6, "location");
        Support.Near(h.Frame.VectorToEcef(new Vec3(800, 0, 0)), DisConversions.ToVec(fire.Velocity), 1e-3, "velocity");
        double range = (h.EntityOfHrot(2)!.Get(h.Schema.Position).AsVec3() - location).Length;
        Assert.Equal(range, fire.Range, 2);
        Assert.False(DisTimestamp.IsAbsolute(fire.Timestamp));
    }

    [Fact]
    public void A_fire_without_a_target_or_with_an_unpublished_target_names_no_target()
    {
        var h = Setup();
        h.HrotStep(HrotDisHarness.State(3, new HrotVec3(10, 0, 0), tkb: 9999)); // no DIS type: never published
        h.HrotStep(Shot(1, 0));
        h.HrotStep(Shot(1, 3));
        h.HrotStep(Shot(1, 99)); // unknown to the gateway
        var fires = h.DisChecker!.Sent<FirePdu>().ToList();
        Assert.Equal(3, fires.Count);
        Assert.All(fires, f => Assert.True(DisConversions.IsNoEntity(f.TargetEntityID)));
        Assert.Equal([1, 2, 3], fires.Select(f => (int)f.EventID.EventNumber).ToArray());
    }

    [Fact]
    public void A_fire_at_a_dis_entity_names_the_entitys_own_dis_id()
    {
        var h = Setup();
        h.DisStep(EntityState(3, h.Frame!.PointToEcef(new Vec3(100, 100, 0))));
        long replica = h.Sent<ReplicaState>().Last().Id;
        h.HrotStep(Shot(1, replica));
        var fire = h.DisChecker!.Sent<FirePdu>().Single();
        Assert.Equal(new DisEntityKey(PeerSite, PeerApplication, 3), DisConversions.KeyOf(fire.TargetEntityID));
    }

    [Fact]
    public void A_detonation_carries_the_event_id_of_its_fire_within_thirty_seconds()
    {
        var h = Setup();
        h.HrotStep(Shot(1, 2));
        h.Run(10);
        h.HrotStep(Blast(1, 2));
        var fire = h.DisChecker!.Sent<FirePdu>().Single();
        var detonation = h.DisChecker.Sent<DetonationPdu>().Single();
        Assert.Equal(PduTypes.Detonation, detonation.PduType);
        Assert.Equal(DisConversions.EventIdOf(fire.EventID), DisConversions.EventIdOf(detonation.EventID));
        Assert.Equal(DisConversions.KeyOf(fire.FiringEntityID), DisConversions.KeyOf(detonation.FiringEntityID));
        Assert.Equal(KeyOfHrot(h, 2), DisConversions.KeyOf(detonation.TargetEntityID));
        Assert.Equal(DetonationResults.EntityImpact, detonation.DetonationResult);
        Assert.Equal(Munition501, DisConversions.PackType(detonation.Descriptor.MunitionType));
        Assert.Equal(1000, detonation.Descriptor.Warhead);
        Support.Near(h.Frame!.PointToEcef(new Vec3(300, 2, 0)), DisConversions.ToVec(detonation.LocationInWorldCoordinates), 1e-6, "location");
        Support.Near(h.Frame.VectorToEcef(new Vec3(0, 0, -50)), DisConversions.ToVec(detonation.Velocity), 1e-3, "velocity");
        double relative = DisConversions.ToVec(detonation.LocationOfEntityCoordinates).Length;
        Assert.Equal(2.0, relative, 3); // two metres from the target's reference point, in its body axes
    }

    [Fact]
    public void A_detonation_after_thirty_seconds_or_without_a_fire_gets_a_new_event_id()
    {
        var h = Setup();
        h.HrotStep(Shot(1, 2));
        h.Run(31);
        h.HrotStep(Blast(1, 2));
        h.HrotStep(Blast(1, 2, tkb: 502)); // another munition: no fire to link to
        var ids = h.DisChecker!.Sent<DetonationPdu>().Select(d => (int)d.EventID.EventNumber).ToArray();
        Assert.Equal([2, 3], ids);
    }

    [Fact]
    public void Several_fires_are_matched_first_in_first_out_per_shooter_and_munition()
    {
        var h = Setup();
        h.HrotStep(Shot(1, 2));
        h.HrotStep(Shot(1, 2));
        h.HrotStep(Shot(2, 1));
        h.HrotStep(Blast(1, 2));
        h.HrotStep(Blast(2, 1, x: 0));
        h.HrotStep(Blast(1, 2));
        var ids = h.DisChecker!.Sent<DetonationPdu>().Select(d => (int)d.EventID.EventNumber).ToArray();
        Assert.Equal([1, 3, 2], ids);
    }

    [Fact]
    public void A_ground_detonation_names_no_target_and_a_gone_shooter_is_optional()
    {
        var h = Setup();
        h.HrotStep(new HrotEntityRemoved(1));
        h.HrotStep(Blast(1, 0));
        var detonation = h.DisChecker!.Sent<DetonationPdu>().Single();
        Assert.Equal(DetonationResults.GroundImpact, detonation.DetonationResult);
        Assert.True(DisConversions.IsNoEntity(detonation.TargetEntityID));
        Assert.True(DisConversions.IsNoEntity(detonation.FiringEntityID));
    }

    [Fact]
    public void A_fire_from_a_shooter_that_was_not_published_to_dis_is_dropped_with_one_warning()
    {
        var h = Setup();
        h.HrotStep(HrotDisHarness.State(3, new HrotVec3(10, 0, 0), tkb: 9999));
        h.HrotStep(Shot(3, 2));
        h.HrotStep(Shot(3, 2));
        Assert.Empty(h.DisChecker!.Sent<FirePdu>());
        Assert.Equal(1, h.WarningCount(nameof(DisFireEgress)));
    }

    [Fact]
    public void No_fire_or_detonation_goes_to_dis_in_edit_or_idle_sessions()
    {
        var h = new HrotDisHarness();
        h.Origin(50, 14, 300);
        h.Session(ClusterPhase.Edit);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.HrotStep(HrotDisHarness.State(2, new HrotVec3(300, 0, 0)));
        h.HrotStep(Shot(1, 2));
        h.HrotStep(Blast(1, 2));
        Assert.Empty(h.DisChecker!.Others);
    }

    [Fact]
    public void Events_are_suppressed_in_replay_unless_the_configuration_sends_them()
    {
        foreach (bool send in new[] { false, true })
        {
            var config = GatewayConfiguration.CreateDefault() with { Shared = new SharedConfiguration { SendEventsInReplay = send } };
            var h = new HrotDisHarness(config);
            h.Origin(50, 14, 300);
            h.Session(ClusterPhase.Replay);
            h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
            h.HrotStep(HrotDisHarness.State(2, new HrotVec3(300, 0, 0)));
            h.HrotStep(Shot(1, 2));
            h.HrotStep(Blast(1, 2));
            Assert.Equal(send ? 1 : 0, h.DisChecker!.Sent<FirePdu>().Count());
            Assert.Equal(send ? 1 : 0, h.DisChecker.Sent<DetonationPdu>().Count());
            Assert.Equal(2, h.DisChecker.States.Count); // the entities themselves still go out in a replay
        }
    }

    [Fact]
    public void A_paused_session_still_passes_events_in_live_mode()
    {
        var h = Setup();
        h.Session(ClusterPhase.Live, advancing: false);
        h.HrotStep(Shot(1, 2));
        Assert.Single(h.DisChecker!.Sent<FirePdu>());
    }

    // ---- DIS to HROT -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_dis_fire_pdu_becomes_a_pivot_event_and_an_inject_fire_for_hrot()
    {
        var h = Setup();
        h.DisStep(EntityState(3, h.Frame!.PointToEcef(new Vec3(100, 100, 0))));
        var shooter = new DisEntityKey(PeerSite, PeerApplication, 3);
        long replica = h.Sent<ReplicaState>().Last().Id;
        var target = KeyOfHrot(h, 1);
        var location = h.Frame.PointToEcef(new Vec3(110, 100, 1));
        var velocity = h.Frame.VectorToEcef(new Vec3(0, -700, 0));
        h.DisStep(Fire(shooter, target, location, velocity, Munition501, eventNumber: 42, rate: 3, quantity: 2, range: 123.5f));

        var payload = LastEvent(h, h.Schema.WeaponFire);
        Assert.Equal(new DisEventId(PeerSite, PeerApplication, 42), payload.Get(h.Schema.EventId).AsObject<DisEventId>());
        Assert.Equal(h.DisIds!.TryResolve(shooter, out var shooterEntity) ? shooterEntity : default, payload.Get(h.Schema.EventFiring).AsEntity());
        Assert.Equal(h.EntityOfHrot(1)!.Id, payload.Get(h.Schema.EventTarget).AsEntity());
        Assert.False(payload.TryGet(h.Schema.EventMunitionEntity, out _));
        Assert.Equal(Munition501, payload.Get(h.Schema.EventMunitionType).AsInt());
        Assert.Equal(1000, payload.Get(h.Schema.EventWarhead).AsInt());
        Assert.Equal(8000, payload.Get(h.Schema.EventFuse).AsInt());
        Assert.Equal(2, payload.Get(h.Schema.EventQuantity).AsInt());
        Assert.Equal(3, payload.Get(h.Schema.EventRate).AsInt());
        Assert.Equal(location, payload.Get(h.Schema.EventLocation).AsVec3());
        Assert.Equal(123.5, payload.Get(h.Schema.EventRange).AsDouble());

        var inject = Assert.Single(h.Sent<InjectFire>());
        Assert.Equal(replica, inject.ShooterId);
        Assert.Equal(1, inject.TargetId);
        Assert.True(inject.TargetIsHrot);
        Assert.Equal(501, inject.MunitionTkb);
        Assert.Equal(Munition501, inject.MunitionDisType);
        Assert.Equal(2, inject.Quantity);
        Support.Near(new Vec3(110, 100, 1), inject.Origin, 1e-6, "origin");
        Support.Near(new Vec3(0, -700, 0), inject.Velocity, 1e-3, "velocity");
    }

    [Fact]
    public void A_dis_detonation_pdu_becomes_a_pivot_event_and_an_inject_detonation_for_hrot()
    {
        var h = Setup();
        h.DisStep(EntityState(3, h.Frame!.PointToEcef(new Vec3(100, 100, 0))));
        long replica = h.Sent<ReplicaState>().Last().Id;
        var shooter = new DisEntityKey(PeerSite, PeerApplication, 3);
        var location = h.Frame.PointToEcef(new Vec3(299, 1, 0));
        h.DisStep(Detonation(shooter, KeyOfHrot(h, 2), location, new Vec3(1, 2, 3), new Vec3(0, 0, -30), Munition501, result: 1, eventNumber: 7));

        var payload = LastEvent(h, h.Schema.MunitionDetonation);
        Assert.Equal(new DisEventId(PeerSite, PeerApplication, 7), payload.Get(h.Schema.EventId).AsObject<DisEventId>());
        Assert.Equal(1, payload.Get(h.Schema.EventResult).AsInt());
        Assert.Equal(new Vec3(1, 2, 3), payload.Get(h.Schema.EventRelativeLocation).AsVec3());
        Assert.Equal(location, payload.Get(h.Schema.EventLocation).AsVec3());
        Assert.Equal(h.EntityOfHrot(2)!.Id, payload.Get(h.Schema.EventTarget).AsEntity());

        var inject = Assert.Single(h.Sent<InjectDetonation>());
        Assert.Equal(replica, inject.ShooterId);
        Assert.Equal(2, inject.TargetId);
        Assert.True(inject.TargetIsHrot);
        Assert.Equal(501, inject.MunitionTkb);
        Support.Near(new Vec3(299, 1, 0), inject.Location, 1e-6, "location");
    }

    [Fact]
    public void A_dis_fire_at_a_dis_entity_is_effects_only_for_hrot()
    {
        var h = Setup();
        h.DisStep(EntityState(3));
        h.DisStep(EntityState(4));
        h.DisStep(Fire(new DisEntityKey(PeerSite, PeerApplication, 3), new DisEntityKey(PeerSite, PeerApplication, 4),
            h.Frame!.PointToEcef(new Vec3(1, 1, 1)), default, Munition501));
        var inject = Assert.Single(h.Sent<InjectFire>());
        Assert.False(inject.TargetIsHrot);
        Assert.NotEqual(0, inject.TargetId);
    }

    [Fact]
    public void A_dis_fire_from_an_unknown_shooter_is_still_published_without_a_shooter()
    {
        var h = Setup();
        h.DisStep(Fire(new DisEntityKey(PeerSite, PeerApplication, 99), null, h.Frame!.PointToEcef(new Vec3(1, 1, 1)), default, Munition501));
        Assert.False(LastEvent(h, h.Schema.WeaponFire).TryGet(h.Schema.EventFiring, out _));
        var inject = Assert.Single(h.Sent<InjectFire>());
        Assert.Equal(0, inject.ShooterId);
        Assert.Equal(0, inject.TargetId);
    }

    [Fact]
    public void Dis_events_never_come_back_to_dis_as_pdus()
    {
        var h = Setup();
        h.DisStep(EntityState(3));
        h.DisStep(Fire(new DisEntityKey(PeerSite, PeerApplication, 3), KeyOfHrot(h, 1), h.Frame!.PointToEcef(new Vec3(1, 1, 1)), default, Munition501));
        h.DisStep(Detonation(new DisEntityKey(PeerSite, PeerApplication, 3), KeyOfHrot(h, 1), h.Frame.PointToEcef(new Vec3(1, 1, 1)), default, default, Munition501));
        Assert.Empty(h.DisChecker!.Sent<FirePdu>());
        Assert.Empty(h.DisChecker.Sent<DetonationPdu>());
        Assert.Equal(1, CountEvents(h, h.Schema.WeaponFire));
    }

    [Fact]
    public void The_gateways_own_fire_and_detonation_pdus_coming_back_are_ignored()
    {
        var h = Setup();
        h.HrotStep(Shot(1, 2));
        h.HrotStep(Blast(1, 2));
        int events = CountEvents(h, h.Schema.WeaponFire) + CountEvents(h, h.Schema.MunitionDetonation);
        h.DisStep(ThroughTheWire(h.DisChecker!.Sent<FirePdu>().Single()));
        h.DisStep(ThroughTheWire(h.DisChecker.Sent<DetonationPdu>().Single()));
        Assert.Equal(events, CountEvents(h, h.Schema.WeaponFire) + CountEvents(h, h.Schema.MunitionDetonation));
        Assert.Equal(2, h.Services.DisIngress.Echoes);
        Assert.Empty(h.Sent<InjectFire>());
        Assert.Empty(h.Sent<InjectDetonation>());
    }

    [Fact]
    public void A_fire_pdu_with_the_gateways_event_site_is_ignored_even_from_a_foreign_shooter()
    {
        var h = Setup();
        h.DisStep(EntityState(3));
        h.DisStep(Fire(new DisEntityKey(PeerSite, PeerApplication, 3), null, default, default, Munition501, eventSite: 1, eventApplication: 3001));
        Assert.Equal(0, CountEvents(h, h.Schema.WeaponFire));
    }

    [Fact]
    public void Dis_fire_and_detonation_reach_hrot_only_while_the_exercise_runs_in_live_mode()
    {
        var h = new HrotDisHarness();
        h.Origin(50, 14, 300);
        h.Session(ClusterPhase.Edit);
        h.DisStep(EntityState(3, h.Frame!.PointToEcef(new Vec3(1, 1, 0))));
        var shooter = new DisEntityKey(PeerSite, PeerApplication, 3);
        h.DisStep(Fire(shooter, null, default, default, Munition501));
        Assert.Empty(h.Sent<InjectFire>());
        h.Session(ClusterPhase.Live);
        h.DisStep(Fire(shooter, null, default, default, Munition501));
        Assert.Single(h.Sent<InjectFire>());
        h.Session(ClusterPhase.Live, advancing: false);
        h.DisStep(Fire(shooter, null, default, default, Munition501));
        Assert.Single(h.Sent<InjectFire>());
    }

    [Fact]
    public void A_dis_fire_at_a_hrot_target_end_to_end_a_hrot_shot_back_keeps_the_ids_consistent()
    {
        var h = Setup();
        h.DisStep(EntityState(3, h.Frame!.PointToEcef(new Vec3(100, 100, 0))));
        var dis3 = new DisEntityKey(PeerSite, PeerApplication, 3);
        long replica = h.Sent<ReplicaState>().Last().Id;
        h.DisStep(Fire(dis3, KeyOfHrot(h, 1), h.Frame.PointToEcef(new Vec3(100, 100, 1)), default, Munition501, eventNumber: 9));
        // HROT answers with a shot at the replica
        h.HrotStep(Shot(1, replica));
        var fire = h.DisChecker!.Sent<FirePdu>().Single();
        Assert.Equal(dis3, DisConversions.KeyOf(fire.TargetEntityID));
        Assert.Equal(KeyOfHrot(h, 1), DisConversions.KeyOf(fire.FiringEntityID));
    }
}
