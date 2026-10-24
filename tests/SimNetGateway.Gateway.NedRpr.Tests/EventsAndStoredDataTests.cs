using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>DD-001 section 12, "Events and stored data" (R-013, R-016, R-027).</summary>
public class EventsAndStoredDataTests
{
    private const double FireLat = 50.01;
    private const double FireLon = 14.01;
    private const double FireAlt = 310.0;

    private sealed class Rig
    {
        public Rig(GatewayHarness harness, DispatchProbe probe)
        {
            H = harness;
            Probe = probe;
        }

        public GatewayHarness H { get; }

        public DispatchProbe Probe { get; }

        public EntityRecord Shooter => H.EntityOfNed(T.Viper)!;

        public EntityRecord Target => H.EntityOfNed(T.Hawk)!;
    }

    /// <summary>Viper (shooter) and Hawk (target) published to RPR, with a probe module watching the dispatch.</summary>
    private static Rig Setup(bool weaponStatus = true)
    {
        DispatchProbe? probe = null;
        var h = new GatewayHarness(beforeBuild: b =>
        {
            probe = new DispatchProbe(new PivotSchema(b.Attributes, b.EventTypes));
            b.AddModule(probe);
        });
        T.PublishNed(h, T.Hawk, "Hawk");
        T.PublishNed(h, T.Viper, "Viper", 100, T.Geo(T.Viper, lat: 50.001));
        if (weaponStatus)
        {
            h.NedStep(new NedWeaponStatus(T.Viper, 501, 30));
        }

        h.ClearOutput();
        return new Rig(h, probe!);
    }

    private static NedWeaponFire Fire(Guid id, ulong shooter, ulong target, int munition, int quantity) =>
        new(id, shooter, target, munition, FireLat, FireLon, FireAlt, quantity);

    private static int EventCount(GatewayHarness h, EventTypeId type)
    {
        int count = 0;
        foreach (var ev in h.Engine.State.Journal.RecentEvents)
        {
            if (ev.Type == type)
            {
                count++;
            }
        }

        return count;
    }

    // ---- weapon status --------------------------------------------------------------------------------------------

    [Fact]
    public void A_weapon_status_stores_the_selected_munition_and_the_ammunition_without_telling_RPR()
    {
        var r = Setup(weaponStatus: false);

        r.H.NedStep(new NedWeaponStatus(T.Viper, 502, 12));

        Assert.Equal(502, r.Shooter.Get(r.H.Schema.SelectedMunition).AsInt());
        Assert.Equal(12, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
        Assert.Equal(0, r.H.RprOut.Count); // no equivalent on the RPR side
        Assert.Equal(0, r.H.NedOut.Count);
    }

    [Fact]
    public void A_weapon_status_of_an_unknown_entity_creates_it_so_descriptors_may_arrive_in_any_order()
    {
        var h = new GatewayHarness();

        h.NedStep(new NedWeaponStatus(T.Viper, 501, 30));

        var entity = h.EntityOfNed(T.Viper)!;
        Assert.Equal(30, entity.Get(h.Schema.Ammo).AsInt());
        Assert.Equal(h.NedSource, entity.Origin);
        Assert.Equal(0, h.RprOut.Count); // no type, no position
    }

    // ---- fire -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_fire_decrements_the_shooters_ammo_before_the_event_is_dispatched_and_RPR_gets_the_fire()
    {
        var r = Setup();
        var fireId = Guid.NewGuid();
        var shooterHandle = r.H.HandleOf(r.Shooter);
        var targetHandle = r.H.HandleOf(r.Target);

        r.H.NedStep(Fire(fireId, T.Viper, T.Hawk, munition: 0, quantity: 2));

        // The probe rule, dispatched with the event, saw the state effect already applied.
        var seen = Assert.Single(r.Probe.FireSeen);
        Assert.Equal(28L, seen.Ammo);
        Assert.Equal(28, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
        var fire = Assert.Single(r.H.RprSent<RprWeaponFire>());
        Assert.Equal(fireId, fire.EventId);
        Assert.Equal(shooterHandle, fire.FiringObject);
        Assert.Equal(targetHandle, fire.TargetObject);
        Assert.Equal(NedRprConversions.ToWorldLocation(NedRprConversions.GeodeticToEcef(FireLat, FireLon, FireAlt)), fire.Location);
        Assert.Equal(T.Dis(501), fire.Munition); // the shooter's stored selection
        Assert.Equal(2, fire.Quantity);
        Assert.Equal(1, r.H.RprOut.Count);
        Assert.Equal(0, r.H.NedOut.Count); // NED is the source: nothing goes back
    }

    [Fact]
    public void The_ammo_decrement_and_the_event_belong_to_one_correlation_and_the_decrement_is_a_journaled_write()
    {
        var r = Setup();

        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, T.Hawk, 0, 3));

        var seen = Assert.Single(r.Probe.FireSeen);
        bool found = false;
        foreach (var write in r.H.Engine.State.Journal.GetWrites(seen.Correlation))
        {
            if (write.Entity == r.Shooter.Id && write.Change.Attr == r.H.Schema.Ammo)
            {
                found = true;
                Assert.Equal(30, write.Change.Old.AsInt());
                Assert.Equal(27, write.Change.New.AsInt());
            }
        }

        Assert.True(found, "the ammunition write is not part of the fire's correlation");
    }

    [Fact]
    public void Two_identical_fires_are_two_shots()
    {
        var r = Setup();

        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, T.Hawk, 0, 1));
        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, T.Hawk, 0, 1));

        Assert.Equal(2, r.H.RprSent<RprWeaponFire>().Count);
        Assert.Equal(28, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
        Assert.Equal([29L, 28L], r.Probe.FireSeen.Select(f => f.Ammo!.Value));
    }

    [Fact]
    public void A_fire_uses_the_selected_munition_only_when_it_gives_none()
    {
        var r = Setup();

        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, 0, munition: 502, quantity: 1));
        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, 0, munition: 0, quantity: 1));

        var fires = r.H.RprSent<RprWeaponFire>();
        Assert.Equal(T.Dis(502), fires[0].Munition); // the one the fire gives
        Assert.Equal(T.Dis(501), fires[1].Munition); // the stored selection
        Assert.Null(fires[0].TargetObject); // no target
    }

    [Fact]
    public void A_fire_without_munition_and_without_a_stored_selection_uses_the_default_type_and_warns()
    {
        var r = Setup(weaponStatus: false);
        r.H.Checker.Expect("No munition given");

        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, T.Hawk, 0, 1));

        var fire = Assert.Single(r.H.RprSent<RprWeaponFire>());
        Assert.Equal(TypeCatalog.DefaultDis, fire.Munition);
        Assert.Equal(1, r.H.Checker.WarningsContaining("No munition given"));
        Assert.False(r.Shooter.Has(r.H.Schema.Ammo));
    }

    [Fact]
    public void An_unknown_munition_uses_the_default_dis_type_and_warns_once()
    {
        var r = Setup();
        r.H.Checker.Expect("Unknown munition TKB type 999");

        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, 0, 999, 1));
        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, 0, 999, 1));

        var fires = r.H.RprSent<RprWeaponFire>();
        Assert.Equal(2, fires.Count);
        Assert.All(fires, f => Assert.Equal(TypeCatalog.DefaultDis, f.Munition));
        Assert.Equal(1, r.H.Checker.WarningsContaining("Unknown munition TKB type 999"));
        Assert.Equal(28, r.Shooter.Get(r.H.Schema.Ammo).AsInt()); // the stock still goes down
    }

    [Fact]
    public void The_stock_never_goes_below_zero()
    {
        var r = Setup();

        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, 0, 0, 100));

        Assert.Equal(0, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
        Assert.Equal(100, Assert.Single(r.H.RprSent<RprWeaponFire>()).Quantity);
    }

    [Fact]
    public void An_unknown_shooter_aborts_the_transaction_sends_nothing_and_leaves_a_diagnostic()
    {
        var r = Setup();
        r.H.Checker.Expect("unknown shooter");
        string before = r.H.Snapshot();
        long failures = r.H.Engine.Diagnostics.TransactionFailures;
        int events = EventCount(r.H, r.H.Schema.WeaponFire);

        r.H.NedStep(Fire(Guid.NewGuid(), 9999, T.Hawk, 501, 5));

        Assert.Equal(0, r.H.RprOut.Count);
        Assert.Equal(0, r.H.NedOut.Count);
        Assert.Equal(before, r.H.Snapshot());
        Assert.Equal(failures + 1, r.H.Engine.Diagnostics.TransactionFailures);
        Assert.Equal(events, EventCount(r.H, r.H.Schema.WeaponFire));
        Assert.Empty(r.Probe.FireSeen);
        Assert.Equal(1, r.H.Checker.WarningsContaining("unknown shooter"));
        Assert.Equal(30, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
    }

    [Fact]
    public void An_unknown_or_unpublished_target_is_sent_as_no_object()
    {
        var r = Setup();
        r.H.NedStep(T.Geo(0x77)); // an entity without type or name: never published, so without a handle

        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, 9999, 501, 1));
        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, 0x77, 501, 1));

        var fires = r.H.RprSent<RprWeaponFire>();
        Assert.Equal(2, fires.Count);
        Assert.All(fires, f => Assert.NotNull(f.FiringObject));
        Assert.All(fires, f => Assert.Null(f.TargetObject));
    }

    // ---- detonation -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_detonation_sets_the_target_damage_and_RPR_gets_the_detonation_and_a_damage_update_in_one_correlation()
    {
        var r = Setup();
        var detonationId = Guid.NewGuid();
        var targetHandle = r.H.HandleOf(r.Target);

        r.H.NedStep(new NedDetonation(detonationId, T.Hawk, 502, FireLat, FireLon, FireAlt, NedDetonationResult.EntityImpact, 3));

        Assert.Equal(3, r.Target.Get(r.H.Schema.Damage).AsInt());
        var update = Assert.Single(r.H.RprSent<RprObjectUpdate>());
        Assert.Equal(targetHandle, update.Handle);
        Assert.Equal(RprDamageState.Destroyed, update.DamageState);
        Assert.Null(update.WorldLocation);
        var detonation = Assert.Single(r.H.RprSent<RprMunitionDetonation>());
        Assert.Equal(detonationId, detonation.EventId);
        Assert.Equal(targetHandle, detonation.TargetObject);
        Assert.Null(detonation.FiringObject);
        Assert.Equal(T.Dis(502), detonation.Munition);
        Assert.Equal(RprDetonationResult.EntityImpact, detonation.Result);
        Assert.Equal(NedRprConversions.ToWorldLocation(NedRprConversions.GeodeticToEcef(FireLat, FireLon, FireAlt)), detonation.Location);
        Assert.Equal(2, r.H.RprOut.Count);
        Assert.Equal(0, r.H.NedOut.Count);

        // The damage was dispatched before the event, and both belong to the input's correlation.
        Assert.Equal(2, r.Probe.Order.Count);
        var damage = r.Probe.Order[0].Split(':');
        var detonationOrder = r.Probe.Order[1].Split(':');
        Assert.Equal("damage", damage[0]);
        Assert.Equal("detonation", detonationOrder[0]);
        Assert.Equal(damage[1], detonationOrder[1]);
        bool written = false;
        foreach (var write in r.H.Engine.State.Journal.GetWrites(new CorrelationId(long.Parse(damage[1]))))
        {
            written |= write.Entity == r.Target.Id && write.Change.Attr == r.H.Schema.Damage && write.Change.New.AsInt() == 3;
        }

        Assert.True(written);

        // RPR sees the state update first, then the interaction.
        var updateRecord = r.H.RprOut.Messages.Single(m => m.Message is RprObjectUpdate);
        var detonationRecord = r.H.RprOut.Messages.Single(m => m.Message is RprMunitionDetonation);
        Assert.True(updateRecord.Sequence < detonationRecord.Sequence);
        Assert.Equal(updateRecord.Tick, detonationRecord.Tick);
    }

    [Theory]
    [InlineData(NedDetonationResult.Other, RprDetonationResult.Other)]
    [InlineData(NedDetonationResult.EntityImpact, RprDetonationResult.EntityImpact)]
    [InlineData(NedDetonationResult.GroundImpact, RprDetonationResult.GroundImpact)]
    [InlineData(NedDetonationResult.Detonation, RprDetonationResult.Detonation)]
    public void The_detonation_result_keeps_its_meaning(NedDetonationResult ned, RprDetonationResult rpr)
    {
        var r = Setup();

        r.H.NedStep(new NedDetonation(Guid.NewGuid(), 0, 501, FireLat, FireLon, FireAlt, ned, -1));

        Assert.Equal(rpr, Assert.Single(r.H.RprSent<RprMunitionDetonation>()).Result);
    }

    [Fact]
    public void A_detonation_that_reports_no_damage_changes_no_state_and_sends_no_damage_update()
    {
        var r = Setup();
        string before = r.H.Snapshot();

        r.H.NedStep(new NedDetonation(Guid.NewGuid(), T.Hawk, 501, FireLat, FireLon, FireAlt, NedDetonationResult.GroundImpact, -1));

        Assert.Equal(before, r.H.Snapshot());
        Assert.Single(r.H.RprSent<RprMunitionDetonation>());
        Assert.Empty(r.H.RprSent<RprObjectUpdate>());
        Assert.Equal(1, r.H.RprOut.Count);
    }

    [Fact]
    public void A_detonation_on_an_unknown_target_is_forwarded_without_a_target_and_changes_no_state()
    {
        var r = Setup();
        string before = r.H.Snapshot();

        r.H.NedStep(new NedDetonation(Guid.NewGuid(), 9999, 501, FireLat, FireLon, FireAlt, NedDetonationResult.EntityImpact, 2));

        var detonation = Assert.Single(r.H.RprSent<RprMunitionDetonation>());
        Assert.Null(detonation.TargetObject);
        Assert.Equal(before, r.H.Snapshot());
        Assert.Empty(r.H.RprSent<RprObjectUpdate>());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(-2)]
    public void A_detonation_with_a_damage_out_of_range_is_aborted_completely(int damage)
    {
        var r = Setup();
        r.H.Checker.Expect($"target damage {damage} out of range");
        string before = r.H.Snapshot();

        r.H.NedStep(new NedDetonation(Guid.NewGuid(), T.Hawk, 501, FireLat, FireLon, FireAlt, NedDetonationResult.EntityImpact, damage));

        Assert.Equal(0, r.H.RprOut.Count);
        Assert.Equal(before, r.H.Snapshot());
        Assert.Empty(r.Probe.Order);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    public void A_damage_level_out_of_range_is_aborted_and_changes_nothing(int level)
    {
        var r = Setup();
        r.H.Checker.Expect($"damage level {level} out of range");
        string before = r.H.Snapshot();

        r.H.NedStep(new NedEntityDamage(T.Hawk, level));

        Assert.Equal(before, r.H.Snapshot());
        Assert.Equal(0, r.H.RprOut.Count);
        Assert.Equal(1, r.H.Checker.WarningsContaining($"damage level {level} out of range"));
    }

    [Fact]
    public void A_damage_of_an_unknown_entity_is_ignored_without_a_diagnostic()
    {
        var r = Setup();
        string before = r.H.Snapshot();

        r.H.NedStep(new NedEntityDamage(9999, 2));

        Assert.Equal(before, r.H.Snapshot());
        Assert.Equal(0, r.H.Engine.Diagnostics.TransactionFailures);
        Assert.Equal(0, r.H.RprOut.Count);
    }

    // ---- fire from RPR ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_fire_from_RPR_reaches_NED_with_wire_ids_and_is_not_echoed_back_to_RPR()
    {
        var r = Setup();
        r.H.RprStep(T.RprFull(0x9001, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5));
        ulong wolfNed = r.H.NedSent<NedEntityMaster>().Single().Id;
        r.H.ClearOutput();
        var fireId = Guid.NewGuid();
        var location = NedRprConversions.ToWorldLocation(NedRprConversions.GeodeticToEcef(FireLat, FireLon, FireAlt));

        r.H.RprStep(new RprWeaponFire(fireId, new RprObjectHandle(0x9001), r.H.HandleOf(r.Target), location, T.Dis(502), 4));

        var fire = Assert.Single(r.H.NedSent<NedWeaponFire>());
        Assert.Equal(fireId, fire.FireId);
        Assert.Equal(wolfNed, fire.Shooter); // the gateway-range id of the RPR entity
        Assert.Equal(T.Hawk, fire.Target); // the NED id the target arrived with
        Assert.Equal(502, fire.MunitionTkb);
        Assert.Equal(4, fire.Quantity);
        Assert.Equal(FireLat, fire.LatitudeDeg, 9);
        Assert.Equal(FireLon, fire.LongitudeDeg, 9);
        Assert.Equal(FireAlt, fire.Altitude, 5);
        Assert.Equal(1, r.H.NedOut.Count);
        Assert.Equal(0, r.H.RprOut.Count); // never echoed to RPR
    }

    [Fact]
    public void A_fire_from_RPR_with_unknown_shooter_and_target_handles_reaches_NED_with_zero_ids()
    {
        var r = Setup();

        r.H.RprStep(new RprWeaponFire(Guid.NewGuid(), new RprObjectHandle(0xAAAA), null, default, T.Dis(501), 1));

        var fire = Assert.Single(r.H.NedSent<NedWeaponFire>());
        Assert.Equal(0UL, fire.Shooter);
        Assert.Equal(0UL, fire.Target);
        Assert.Equal(0, r.H.RprOut.Count);
    }

    [Fact]
    public void An_unknown_RPR_munition_reaches_NED_as_munition_zero_with_one_warning()
    {
        var r = Setup();
        r.H.Checker.Expect("Unknown DIS munition type");
        var strange = new DisEntityType(2, 9, 1, 1, 1, 1, 1);

        r.H.RprStep(new RprWeaponFire(Guid.NewGuid(), null, null, default, strange, 1));
        r.H.RprStep(new RprWeaponFire(Guid.NewGuid(), null, null, default, strange, 1));

        Assert.All(r.H.NedSent<NedWeaponFire>(), f => Assert.Equal(0, f.MunitionTkb));
        Assert.Equal(2, r.H.NedSent<NedWeaponFire>().Count);
        Assert.Equal(1, r.H.Checker.WarningsContaining("Unknown DIS munition type"));
    }

    [Fact]
    public void A_looped_back_NED_fire_of_an_RPR_shooter_is_ignored()
    {
        var r = Setup();
        r.H.RprStep(T.RprFull(0x9001, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5));
        r.H.LoopbackNed = true;
        r.H.ClearOutput();
        int events = EventCount(r.H, r.H.Schema.WeaponFire);
        int ammo = (int)r.Shooter.Get(r.H.Schema.Ammo).AsInt();

        r.H.RprStep(new RprWeaponFire(Guid.NewGuid(), new RprObjectHandle(0x9001), null, default, T.Dis(501), 1));
        r.H.Run(0.2);

        Assert.Equal(events + 1, EventCount(r.H, r.H.Schema.WeaponFire)); // the original only, not the loopback
        Assert.Equal(1, r.H.NedOut.Count);
        Assert.Equal(0, r.H.RprOut.Count);
        Assert.Equal(ammo, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
        Assert.Equal(0, r.H.Engine.Diagnostics.TransactionFailures);
    }

    [Fact]
    public void A_looped_back_RPR_fire_of_a_NED_shooter_is_ignored()
    {
        var r = Setup();
        r.H.LoopbackRpr = true;
        int events = EventCount(r.H, r.H.Schema.WeaponFire);

        r.H.NedStep(Fire(Guid.NewGuid(), T.Viper, T.Hawk, 0, 1));
        r.H.Run(0.2);

        Assert.Equal(events + 1, EventCount(r.H, r.H.Schema.WeaponFire));
        Assert.Equal(1, r.H.RprOut.Count);
        Assert.Equal(0, r.H.NedOut.Count);
        Assert.Equal(29, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
    }

    // ---- events of other origins (published by custom modules or the host) ------------------------------------------

    private static void Publish(GatewayHarness h, SourceId source, EventTypeId type, EntityId? shooter = null)
    {
        var bag = h.Engine.RentPayload();
        bag.Set(h.Schema.EventId, AttrValue.FromObject(Guid.NewGuid()));
        bag.Set(h.Schema.EventMunitionTkb, AttrValue.From(501));
        bag.Set(h.Schema.EventQuantity, AttrValue.From(1));
        if (shooter is { } s)
        {
            bag.Set(h.Schema.EventShooter, AttrValue.From(s));
        }

        h.Engine.Publish(source, type, bag);
        h.Step();
    }

    [Fact]
    public void An_event_goes_to_the_other_side_only()
    {
        var r = Setup();
        r.H.ClearOutput();

        Publish(r.H, r.H.NedSource, r.H.Schema.WeaponFire, r.Shooter.Id);
        Assert.Single(r.H.RprSent<RprWeaponFire>());
        Assert.Equal(0, r.H.NedOut.Count);
        r.H.ClearOutput();

        Publish(r.H, r.H.RprSource, r.H.Schema.WeaponFire); // (an RPR fire naming a NED shooter would be an echo, which no ingress produces)
        Assert.Single(r.H.NedSent<NedWeaponFire>());
        Assert.Equal(0, r.H.RprOut.Count);
        r.H.ClearOutput();

        Publish(r.H, r.H.NedSource, r.H.Schema.Detonation);
        Assert.Single(r.H.RprSent<RprMunitionDetonation>());
        r.H.ClearOutput();

        Publish(r.H, r.H.RprSource, r.H.Schema.Detonation); // RPR's own detonation never goes back to RPR
        Assert.Equal(0, r.H.RprOut.Count);
        Assert.Equal(0, r.H.NedOut.Count); // and NED has no detonation message in this gateway
    }

    [Fact]
    public void A_session_change_made_by_RPR_is_not_sent_back_to_RPR()
    {
        var h = new GatewayHarness();

        Assert.True(h.Engine.Execute(SessionCommand.Load("x"), h.RprSource));
        Assert.True(h.Engine.Execute(SessionCommand.Start, h.RprSource));
        Assert.True(h.Engine.Execute(SessionCommand.Pause, h.RprSource));
        h.Step();

        Assert.Equal(SessionState.Paused, h.Engine.Session.State);
        Assert.Equal(0, h.RprOut.Count);
        Assert.Equal(0, h.NedOut.Count);
    }
}
