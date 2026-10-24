using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// DD-002 C6, C9, C15, C16 and R-047 (R-044 .. R-047): damage notification, member events, the pending-handle hold, the customer
/// damage in percent, the replay policy and the showcase configuration.
/// </summary>
public class ShowcaseModuleTests
{
    private const uint Wolf = 0x700;

    private static GatewayHarness Showcase() => new(NedRprConfigurations.Showcase);

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

    private static List<object> NedMessages(GatewayHarness h) => h.NedOut.Messages.Select(m => (object)m.Message).ToList();

    // ---- damage notification (C6) -----------------------------------------------------------------------------------

    [Fact]
    public void A_damage_increase_sends_the_descriptor_and_one_notification_and_a_decrease_only_the_descriptor()
    {
        var h = Showcase();
        h.Rpr(S.Platform(1, "T-1"));
        h.Steps(2);
        var entity = S.RprEntity(h, 0x601);
        Assert.True(h.NedIds.TryGetKey(entity.Id, out ulong id));
        h.ClearOutput();

        h.RprStep(new RprObjectUpdate(S.PlatformHandle(1)) { DamageState = RprDamageState.ModerateDamage }); // absent -> 2
        h.Steps(1);
        var up = NedMessages(h);
        Assert.Equal(2, up.Count);
        Assert.Equal(new NedEntityDamage(id, 2), up[0]); // the state first
        Assert.Equal(new NedDamageNotification(id, -1, 0, 2), up[1]); // then the occurrence

        h.ClearOutput();
        h.RprStep(new RprObjectUpdate(S.PlatformHandle(1)) { DamageState = RprDamageState.SlightDamage }); // 2 -> 1
        Assert.Equal(new object[] { new NedEntityDamage(id, 1) }, NedMessages(h));

        h.ClearOutput();
        h.RprStep(new RprObjectUpdate(S.PlatformHandle(1)) { DamageState = RprDamageState.SlightDamage }); // 1 -> 1
        Assert.Empty(NedMessages(h));

        h.RprStep(new RprObjectUpdate(S.PlatformHandle(1)) { DamageState = RprDamageState.Destroyed }); // 1 -> 3
        Assert.Equal(new NedDamageNotification(id, -1, 1, 3), Assert.Single(h.NedSent<NedDamageNotification>()));
    }

    [Fact]
    public void Damage_to_a_member_of_an_aggregate_is_notified_with_unit_id_and_member_index()
    {
        var h = Showcase();
        h.Rpr(S.Platform(1));
        h.Rpr(S.Platform(2));
        h.Step();
        h.RprStep(S.Aggregate(Wolf, "Wolf", 1, 2));
        h.Steps(2);
        Assert.True(h.NedIds.TryGetKey(S.RprEntity(h, Wolf).Id, out ulong unitId));
        h.ClearOutput();

        h.RprStep(new RprObjectUpdate(S.PlatformHandle(2)) { DamageState = RprDamageState.Destroyed });
        h.Steps(1);

        Assert.Equal(new NedDamageNotification(unitId, 1, 0, 3), Assert.Single(h.NedSent<NedDamageNotification>()));
        Assert.Empty(h.NedSent<NedEntityDamage>());
        Assert.Equal(3, Assert.Single(h.NedSent<NedFormation>()).Members[1].Damage);
    }

    [Fact]
    public void The_introduction_of_a_damaged_entity_sends_the_damage_with_it_and_no_notification()
    {
        var h = Showcase();
        h.Rpr(S.Platform(1) with { DamageState = RprDamageState.ModerateDamage });
        h.Steps(2);

        Assert.Single(h.NedSent<NedEntityMaster>());
        Assert.Single(h.NedSent<NedEntityDamage>());
        Assert.Empty(h.NedSent<NedDamageNotification>());
    }

    [Fact]
    public void A_NED_origin_damage_is_never_notified_back()
    {
        var h = Showcase();
        T.PublishNed(h, T.Hawk, "Hawk");
        h.ClearOutput();

        h.NedStep(new NedEntityDamage(T.Hawk, 3));
        h.Steps(2);

        Assert.Equal(0, h.NedOut.Count);
    }

    [Fact]
    public void The_notification_rule_is_off_in_the_default_configuration_and_an_operator_can_switch_it_on()
    {
        var h = new GatewayHarness();
        Assert.False(h.Rule("NedDamageNotificationEgress/Notify").Enabled);
        h.Rpr(S.Platform(1));
        h.Steps(2);
        h.ClearOutput();

        h.RprStep(new RprObjectUpdate(S.PlatformHandle(1)) { DamageState = RprDamageState.Destroyed });
        Assert.Empty(h.NedSent<NedDamageNotification>());
        Assert.Single(h.NedSent<NedEntityDamage>());

        h.Rule("NedDamageNotificationEgress/Notify").Enabled = true;
        h.RprStep(new RprObjectUpdate(S.PlatformHandle(1)) { DamageState = RprDamageState.NoDamage });
        h.RprStep(new RprObjectUpdate(S.PlatformHandle(1)) { DamageState = RprDamageState.SlightDamage });
        Assert.Single(h.NedSent<NedDamageNotification>());
    }

    // ---- events naming a member (C9) ---------------------------------------------------------------------------------

    [Fact]
    public void An_RPR_fire_at_a_NED_formation_member_reaches_NED_with_the_unit_id_and_the_member_index()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1, 2));
        h.Rpr(S.Platform(1, "T-1"));
        h.Steps(4);
        var member = S.NedMember(h, S.ViperId, 1);
        var tank = S.RprEntity(h, 0x601);
        Assert.True(h.NedIds.TryGetKey(tank.Id, out ulong tankNed));
        h.ClearOutput();

        var fireId = Guid.NewGuid();
        h.RprStep(new RprWeaponFire(fireId, S.PlatformHandle(1), h.HandleOf(member), default, T.Dis(502), 1));
        h.Steps(2);

        var fire = Assert.Single(h.NedSent<NedWeaponFire>());
        Assert.Equal(fireId, fire.FireId);
        Assert.Equal(S.ViperId, fire.Target); // the coarse identity of the member's unit
        Assert.Equal(1, fire.TargetMember); // and the member's index
        Assert.Equal(tankNed, fire.Shooter);
        Assert.Equal(502, fire.MunitionTkb);
    }

    [Fact]
    public void An_RPR_fire_at_an_RPR_aggregate_member_names_the_formation_and_the_member_too()
    {
        var h = Showcase();
        h.Rpr(S.Platform(1));
        h.Rpr(S.Platform(2));
        h.Rpr(S.Platform(3, "T-9"));
        h.Step();
        h.RprStep(S.Aggregate(Wolf, "Wolf", 1, 2));
        h.Steps(2);
        Assert.True(h.NedIds.TryGetKey(S.RprEntity(h, Wolf).Id, out ulong unitId));
        h.ClearOutput();

        // the NED side sends a fire at (formation, member 1); RPR answers with a fire of the member platform at nobody
        h.RprStep(new RprWeaponFire(Guid.NewGuid(), S.PlatformHandle(2), null, default, T.Dis(502), 1));
        h.Steps(1);

        var fire = Assert.Single(h.NedSent<NedWeaponFire>());
        Assert.Equal(unitId, fire.Shooter); // a member shooter is named by its unit ...
        Assert.Equal(1, fire.ShooterMember); // ... and its index
        Assert.Equal(0UL, fire.Target);
        Assert.Equal(-1, fire.TargetMember);
    }

    [Fact]
    public void A_NED_detonation_at_an_RPR_aggregate_member_resolves_it_through_the_member_map()
    {
        var h = Showcase();
        h.Rpr(S.Platform(1));
        h.Rpr(S.Platform(2));
        h.Step();
        h.RprStep(S.Aggregate(Wolf, "Wolf", 1, 2));
        h.Steps(2);
        Assert.True(h.NedIds.TryGetKey(S.RprEntity(h, Wolf).Id, out ulong unitId));
        h.ClearOutput();

        h.NedStep(new NedDetonation(Guid.NewGuid(), unitId, 501, 50, 14, 300, NedDetonationResult.EntityImpact, 2, TargetMember: 1));
        h.Steps(2);

        Assert.Equal(2, S.RprEntity(h, 0x602).Get(h.Schema.Damage).AsInt());
        Assert.Equal(0, S.RprEntity(h, 0x601).Has(h.Schema.Damage) ? S.RprEntity(h, 0x601).Get(h.Schema.Damage).AsInt() : 0);
        Assert.Equal(2, Assert.Single(h.NedSent<NedFormation>()).Members[1].Damage);
        Assert.Equal(S.PlatformHandle(2), Assert.Single(h.RprSent<RprMunitionDetonation>()).TargetObject); // the member's own handle
    }

    // ---- hold for a pending handle (R-047) -----------------------------------------------------------------------------

    private static GatewayHarness HoldRig(GatewayConfiguration? configuration = null)
    {
        var h = new GatewayHarness(configuration ?? NedRprConfigurations.Default);
        T.PublishNed(h, T.Hawk, "Hawk", 200);
        h.NedStep(new NedWeaponStatus(T.Hawk, 501, 30));
        h.DeliverRti = false;
        h.NedStep(T.Master(T.Gate, "Gate", 300));
        h.NedStep(T.Geo(T.Gate, speed: 0)); // registered, the handle is pending
        h.Steps(2);
        h.ClearOutput();
        return h;
    }

    private static NedWeaponFire FireAt(ulong target, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), T.Hawk, target, 501, 50, 14, 300, 1);

    [Fact]
    public void A_fire_at_an_entity_whose_handle_is_pending_waits_and_is_sent_with_the_handle_after_the_full_update()
    {
        var h = HoldRig();
        var gate = h.EntityOfNed(T.Gate)!;
        Assert.False(h.RprHandles.Contains(gate.Id));
        var id = Guid.NewGuid();

        h.NedStep(FireAt(T.Gate, id));
        h.Run(0.5);
        Assert.Empty(h.RprSent<RprWeaponFire>()); // held

        h.DeliverRti = true;
        h.Steps(2);

        var fire = Assert.Single(h.RprSent<RprWeaponFire>());
        Assert.Equal(id, fire.EventId);
        Assert.Equal(h.HandleOf(gate), fire.TargetObject); // with the handle, not "no object"
        Assert.Equal(h.HandleOf(h.EntityOfNed(T.Hawk)!), fire.FiringObject);
        var messages = h.RprOut.Messages.Select(m => m.Message).ToList();
        Assert.True(messages.FindIndex(m => m is RprObjectUpdate) < messages.FindIndex(m => m is RprWeaponFire),
            "the entity's first update must precede the fire that names it");
        Assert.Empty(h.Checker.Warnings);
    }

    [Fact]
    public void A_held_fire_is_sent_after_one_second_with_no_object_and_a_warning_when_the_handle_never_comes()
    {
        var h = HoldRig();
        h.Checker.Expect("no object");
        var id = Guid.NewGuid();

        h.NedStep(FireAt(T.Gate, id));
        double heldAt = h.WallTime;
        h.Run(0.9);
        Assert.Empty(h.RprSent<RprWeaponFire>());
        h.RunUntil(() => h.RprSent<RprWeaponFire>().Count > 0, maxSeconds: 0.5, what: "the held fire was never released");

        var fire = Assert.Single(h.RprSent<RprWeaponFire>());
        Assert.Equal(id, fire.EventId);
        Assert.Null(fire.TargetObject);
        Assert.NotNull(fire.FiringObject);
        Assert.InRange(h.RprOut.Messages.Last(m => m.Message is RprWeaponFire).WallTime - heldAt, 1.0, 1.25);
        Assert.Equal(1, h.Checker.WarningsContaining("no object"));

        h.Run(2.0);
        Assert.Single(h.RprSent<RprWeaponFire>()); // sent once
    }

    [Fact]
    public void A_late_handle_after_the_release_does_not_send_the_fire_again()
    {
        var h = HoldRig();
        h.Checker.Expect("no object");
        h.NedStep(FireAt(T.Gate));
        h.Run(1.3);
        Assert.Single(h.RprSent<RprWeaponFire>());

        h.DeliverRti = true;
        h.Steps(3);

        Assert.Single(h.RprSent<RprWeaponFire>());
    }

    [Fact]
    public void Held_events_keep_their_order_and_an_event_behind_a_held_one_does_not_overtake_it()
    {
        var h = HoldRig();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var det = Guid.NewGuid();
        var free = Guid.NewGuid();

        h.NedStep(FireAt(T.Gate, first)); // held (pending target)
        h.NedStep(new NedDetonation(det, T.Gate, 501, 50, 14, 300, NedDetonationResult.EntityImpact, 1)); // held, too
        h.NedStep(FireAt(0, free)); // names only bound entities, but must not overtake what is held
        h.NedStep(FireAt(T.Gate, second));
        h.Run(0.3);
        Assert.Equal(0, h.RprOut.Count);

        h.DeliverRti = true;
        h.Steps(2);

        var events = h.RprOut.Messages.Select(m => m.Message)
            .Select(m => m switch
            {
                RprWeaponFire f => f.EventId,
                RprMunitionDetonation d => d.EventId,
                _ => Guid.Empty,
            })
            .Where(g => g != Guid.Empty)
            .ToList();
        Assert.Equal(new[] { first, det, free, second }, events);
        Assert.Equal(h.HandleOf(h.EntityOfNed(T.Gate)!), h.RprSent<RprMunitionDetonation>().Single().TargetObject);
        Assert.All(h.RprSent<RprWeaponFire>().Where(f => f.EventId != free), f => Assert.NotNull(f.TargetObject));
    }

    [Fact]
    public void An_event_that_names_only_bound_entities_is_sent_at_once()
    {
        var h = HoldRig();

        h.NedStep(FireAt(0));

        Assert.Single(h.RprSent<RprWeaponFire>());
    }

    [Fact]
    public void A_detonation_at_a_pending_entity_waits_for_the_handle_and_then_carries_it()
    {
        var h = HoldRig();
        var gate = h.EntityOfNed(T.Gate)!;
        var id = Guid.NewGuid();

        h.NedStep(new NedDetonation(id, T.Gate, 501, 50, 14, 300, NedDetonationResult.EntityImpact, 3));
        h.Run(0.4);
        Assert.Empty(h.RprSent<RprMunitionDetonation>());
        Assert.Empty(h.RprSent<RprObjectUpdate>());

        h.DeliverRti = true;
        h.Steps(2);

        var detonation = Assert.Single(h.RprSent<RprMunitionDetonation>());
        Assert.Equal(h.HandleOf(gate), detonation.TargetObject);
        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(RprDamageState.Destroyed, update.DamageState); // the damage went into the first full update
        var messages = h.RprOut.Messages.Select(m => m.Message).ToList();
        Assert.True(messages.IndexOf(update) < messages.IndexOf(detonation));
    }

    [Fact]
    public void A_pending_shooter_holds_its_fire_too()
    {
        var h = HoldRig();
        var id = Guid.NewGuid();

        h.NedStep(new NedWeaponFire(id, T.Gate, 0, 501, 50, 14, 300, 1)); // Gate shoots; its handle is pending
        h.Run(0.3);
        Assert.Empty(h.RprSent<RprWeaponFire>());

        h.DeliverRti = true;
        h.Steps(2);

        Assert.Equal(h.HandleOf(h.EntityOfNed(T.Gate)!), Assert.Single(h.RprSent<RprWeaponFire>()).FiringObject);
    }

    [Fact]
    public void An_entity_removed_while_its_fire_is_held_does_not_stop_the_release()
    {
        var h = HoldRig();
        h.Checker.Expect("no object");
        h.NedStep(FireAt(T.Gate));
        h.NedStep(new NedEntityDisposed(T.Gate));

        h.Run(1.5);

        var fire = Assert.Single(h.RprSent<RprWeaponFire>());
        Assert.Null(fire.TargetObject);
    }

    // ---- customer damage in percent (C15) ----------------------------------------------------------------------------

    private static GatewayHarness PercentRig()
    {
        var h = new GatewayHarness();
        T.PublishNed(h, T.Hawk, "Hawk", 200);
        T.PublishNed(h, T.Gate, "Gate", 300, T.Geo(T.Gate, speed: 0));
        h.ClearOutput();
        return h;
    }

    private static void Swap(GatewayHarness h)
    {
        h.Rule("RprEntityEgress/Damage").Enabled = false;
        h.Rule("CustomerDamagePercent/Damage").Enabled = true;
    }

    [Fact]
    public void The_customer_contributor_is_disabled_by_default_and_the_default_damage_goes_out_as_a_state()
    {
        var h = PercentRig();

        Assert.False(h.Rule("CustomerDamagePercent/Damage").Enabled);
        Assert.True(h.Rule("RprEntityEgress/Damage").Enabled);
        h.NedStep(new NedEntityDamage(T.Gate, 1));

        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(RprDamageState.SlightDamage, update.DamageState);
        Assert.Null(update.DamagePercent);
        Assert.Equal(0, h.Rule("CustomerDamagePercent/Damage").FireCount);
    }

    [Fact]
    public void After_the_operator_swap_a_damage_change_sends_the_percent_and_no_state_and_nothing_else()
    {
        var h = PercentRig();
        Swap(h);

        h.NedStep(new NedEntityDamage(T.Gate, 1));

        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal((byte)33, update.DamagePercent);
        Assert.Null(update.DamageState);
        Assert.Null(update.Marking);
        Assert.Null(update.EntityType);
        Assert.Null(update.ForceId);
        Assert.Null(update.WorldLocation);
        Assert.Null(update.Orientation);
        Assert.Null(update.VelocityVector);
        Assert.Null(update.EntityIdentifier);
        Assert.Equal(h.HandleOf(h.EntityOfNed(T.Gate)!), update.Handle);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 33)]
    [InlineData(2, 66)]
    [InlineData(3, 100)]
    public void The_percent_table_is_0_33_66_100(int level, int percent)
    {
        var h = PercentRig();
        h.NedStep(new NedEntityDamage(T.Gate, level == 0 ? 2 : 0)); // make the next write a change
        h.ClearOutput();
        Swap(h);

        h.NedStep(new NedEntityDamage(T.Gate, level));

        Assert.Equal((byte)percent, Assert.Single(h.RprSent<RprObjectUpdate>()).DamagePercent);
    }

    [Fact]
    public void The_other_contributors_stay_the_default_ones_after_the_swap()
    {
        var h = PercentRig();
        Swap(h);
        var moved = T.Geo(T.Gate, lat: 50.01, speed: 0);

        h.NedStep(moved);

        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(NedRprConversions.ToWorldLocation(T.Position(moved)), update.WorldLocation);
        Assert.Null(update.DamagePercent);
        Assert.Null(update.DamageState);
    }

    [Fact]
    public void The_counters_of_the_disabled_default_rule_freeze_and_the_custom_rule_counts()
    {
        var h = PercentRig();
        h.NedStep(new NedEntityDamage(T.Gate, 1));
        long defaultBefore = h.Rule("RprEntityEgress/Damage").FireCount;
        Assert.True(defaultBefore >= 1);
        Swap(h);

        h.NedStep(new NedEntityDamage(T.Gate, 2));
        h.NedStep(new NedEntityDamage(T.Gate, 3));

        Assert.Equal(defaultBefore, h.Rule("RprEntityEgress/Damage").FireCount);
        Assert.Equal(2, h.Rule("CustomerDamagePercent/Damage").FireCount);
        Assert.False(h.Rule("RprEntityEgress/Damage").Enabled);
        Assert.True(h.Rule("CustomerDamagePercent/Damage").Enabled);
    }

    [Fact]
    public void A_damage_the_RPR_side_reported_is_not_echoed_as_a_percent()
    {
        var h = PercentRig();
        Swap(h);
        h.Rpr(S.Platform(1));
        h.Steps(2);
        h.ClearOutput();

        h.RprStep(new RprObjectUpdate(S.PlatformHandle(1)) { DamageState = RprDamageState.Destroyed });

        Assert.Empty(h.RprSent<RprObjectUpdate>());
        Assert.Empty(h.RprSent<RprRegisterObject>()); // and the RPR-origin entity is not registered as a side effect
    }

    [Fact]
    public void A_percent_is_sent_once_and_not_repeated_with_the_next_unrelated_update()
    {
        var h = PercentRig();
        Swap(h);
        h.NedStep(new NedEntityDamage(T.Gate, 2));

        h.NedStep(T.Geo(T.Gate, lat: 50.01, speed: 0));

        Assert.Equal(2, h.RprSent<RprObjectUpdate>().Count);
        Assert.Null(h.RprSent<RprObjectUpdate>()[1].DamagePercent);
    }

    // ---- the override governs full updates too (R-045) ---------------------------------------------------------------------

    private static RprObjectUpdate LastUpdate(GatewayHarness h) => h.RprSent<RprObjectUpdate>().Last();

    /// <summary>Runs until the next heartbeat full update of the Gate has been sent and returns it.</summary>
    private static RprObjectUpdate NextHeartbeat(GatewayHarness h)
    {
        int before = h.RprSent<RprObjectUpdate>().Count;
        h.RunUntil(() => h.RprSent<RprObjectUpdate>().Count > before, maxSeconds: 8, what: "no heartbeat");
        return LastUpdate(h);
    }

    [Fact]
    public void After_the_swap_the_heartbeat_carries_the_percent_and_no_state_and_swapping_back_restores_the_state()
    {
        var h = PercentRig();
        h.NedStep(new NedEntityDamage(T.Gate, 2));
        var before = NextHeartbeat(h);
        Assert.Equal(RprDamageState.ModerateDamage, before.DamageState); // default: the state
        Assert.Null(before.DamagePercent);

        Swap(h);
        var swapped = NextHeartbeat(h);
        Assert.Equal((byte)66, swapped.DamagePercent);
        Assert.Null(swapped.DamageState);
        Assert.NotNull(swapped.WorldLocation); // the other groups are the default ones
        Assert.Equal("Gate", swapped.Marking);
        Assert.NotNull(swapped.EntityIdentifier);

        h.Rule("RprEntityEgress/Damage").Enabled = true;
        h.Rule("CustomerDamagePercent/Damage").Enabled = false;
        var back = NextHeartbeat(h);
        Assert.Equal(RprDamageState.ModerateDamage, back.DamageState);
        Assert.Null(back.DamagePercent);
    }

    [Fact]
    public void The_first_full_update_of_an_entity_registered_after_the_swap_carries_the_percent_and_no_state()
    {
        var h = PercentRig();
        Swap(h);
        h.ClearOutput();

        h.NedStep(T.Master(50, "Late", 200));
        h.NedStep(T.Geo(50));
        h.NedStep(new NedEntityDamage(50, 3));
        h.Steps(3);

        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal((byte)100, update.DamagePercent);
        Assert.Null(update.DamageState);
        Assert.Equal("Late", update.Marking);
        Assert.NotNull(update.WorldLocation);
    }

    [Fact]
    public void A_full_update_without_any_damage_has_neither_group()
    {
        var h = PercentRig();
        Swap(h);

        var heartbeat = NextHeartbeat(h);

        Assert.Null(heartbeat.DamagePercent);
        Assert.Null(heartbeat.DamageState);
    }

    [Fact]
    public void With_both_damage_contributors_disabled_a_full_update_carries_no_damage_at_all()
    {
        var h = PercentRig();
        h.NedStep(new NedEntityDamage(T.Gate, 1));
        h.Rule("RprEntityEgress/Damage").Enabled = false;

        var heartbeat = NextHeartbeat(h);

        Assert.Null(heartbeat.DamageState);
        Assert.Null(heartbeat.DamagePercent);
        Assert.NotNull(heartbeat.WorldLocation);
    }

    [Fact]
    public void With_the_spatial_contributor_disabled_after_publication_a_heartbeat_carries_no_spatial_field()
    {
        var h = PercentRig();
        h.Rule("RprEntityEgress/Spatial").Enabled = false;

        var heartbeat = NextHeartbeat(h);

        Assert.Null(heartbeat.WorldLocation);
        Assert.Null(heartbeat.Orientation);
        Assert.Null(heartbeat.VelocityVector);
        Assert.Null(heartbeat.AccelerationVector);
        Assert.Null(heartbeat.AngularVelocity);
        Assert.Null(heartbeat.DeadReckoningAlgorithm);
        Assert.Equal("Gate", heartbeat.Marking); // the other groups stay
        Assert.NotNull(heartbeat.EntityType);
        Assert.NotNull(heartbeat.EntityIdentifier);
    }

    [Fact]
    public void With_the_identity_contributor_disabled_after_publication_a_heartbeat_carries_no_identity_field()
    {
        var h = PercentRig();
        h.Rule("RprEntityEgress/Identity").Enabled = false;

        var heartbeat = NextHeartbeat(h);

        Assert.Null(heartbeat.Marking);
        Assert.Null(heartbeat.EntityType);
        Assert.Null(heartbeat.ForceId);
        Assert.NotNull(heartbeat.WorldLocation);
    }

    [Fact]
    public void Disabling_the_spatial_contributor_before_the_position_arrives_still_prevents_registration()
    {
        var h = new GatewayHarness();
        h.Rule("RprEntityEgress/Spatial").Enabled = false;

        h.NedStep(T.Master(T.Hawk, "Hawk", 200)); // type first: queued by the identity contributor, no position yet
        h.NedStep(T.Geo(T.Hawk)); // the position is only a spatial trigger
        h.Steps(3);

        Assert.Empty(h.RprSent<RprRegisterObject>());
    }

    // ---- replay policy (C16) -------------------------------------------------------------------------------------------

    private static GatewayHarness FireRig(string load)
    {
        var h = new GatewayHarness();
        T.PublishNed(h, T.Hawk, "Hawk", 200);
        T.PublishNed(h, T.Gate, "Gate", 300, T.Geo(T.Gate, speed: 0));
        h.NedStep(new NedWeaponStatus(T.Hawk, 501, 30));
        h.NedStep(new NedSimControl(load == "live" ? NedControlKind.Load : NedControlKind.LoadDebrief, "Ambush"));
        h.NedStep(new NedSimControl(NedControlKind.Start, null));
        h.ClearOutput();
        return h;
    }

    [Fact]
    public void In_a_live_session_fire_and_detonation_reach_RPR()
    {
        var h = FireRig("live");
        Assert.Equal(SessionMode.Live, h.Engine.Session.Mode);

        h.NedStep(new NedWeaponFire(Guid.NewGuid(), T.Hawk, T.Gate, 501, 50, 14, 300, 1));
        h.NedStep(new NedDetonation(Guid.NewGuid(), T.Gate, 501, 50, 14, 300, NedDetonationResult.EntityImpact, 2));

        Assert.Single(h.RprSent<RprWeaponFire>());
        Assert.Single(h.RprSent<RprMunitionDetonation>());
    }

    [Fact]
    public void After_a_debrief_load_no_weapon_effect_reaches_RPR_but_the_events_are_dispatched_and_the_states_still_go_out()
    {
        var h = FireRig("replay");
        Assert.Equal(SessionMode.Replay, h.Engine.Session.Mode);
        var fire = Guid.NewGuid();
        var det = Guid.NewGuid();

        h.NedStep(new NedWeaponFire(fire, T.Hawk, T.Gate, 501, 50, 14, 300, 2));
        h.NedStep(new NedDetonation(det, T.Gate, 501, 50, 14, 300, NedDetonationResult.EntityImpact, 2));
        h.Steps(2);

        Assert.Empty(h.RprSent<RprWeaponFire>());
        Assert.Empty(h.RprSent<RprMunitionDetonation>());
        Assert.Equal(1, EventCount(h, h.Schema.WeaponFire)); // dispatched: visible in the event log
        Assert.Equal(1, EventCount(h, h.Schema.Detonation));
        // the state effects are not weapon effects: the ammunition, and the damage as a state, reach their consumers
        Assert.Equal(28, h.EntityOfNed(T.Hawk)!.Get(h.Schema.Ammo).AsInt());
        Assert.Equal(RprDamageState.ModerateDamage, Assert.Single(h.RprSent<RprObjectUpdate>()).DamageState);
    }

    [Fact]
    public void A_held_event_is_dropped_when_the_session_is_a_replay_by_then()
    {
        var h = HoldRig();
        h.NedStep(FireAt(T.Gate));
        h.NedStep(new NedSimControl(NedControlKind.LoadDebrief, "r"));
        h.DeliverRti = true;
        h.Steps(3);

        Assert.Empty(h.RprSent<RprWeaponFire>());
    }

    [Fact]
    public void A_replay_session_does_not_hold_events_either()
    {
        var h = HoldRig();
        h.NedStep(new NedSimControl(NedControlKind.LoadDebrief, "r"));

        h.NedStep(FireAt(T.Gate));
        h.DeliverRti = true;
        h.Run(1.5);

        Assert.Empty(h.RprSent<RprWeaponFire>());
        Assert.Equal(0, h.Checker.WarningsContaining("no object"));
    }

    // ---- configurations ----------------------------------------------------------------------------------------------

    [Fact]
    public void Every_configuration_is_registered_named_and_validates_without_errors_or_warnings()
    {
        Assert.Contains(NedRprConfigurations.Default, NedRprConfigurations.All);
        Assert.Contains(NedRprConfigurations.Showcase, NedRprConfigurations.All);
        Assert.Equal(["ned-rpr/default", "ned-rpr/showcase"], NedRprConfigurations.All.Select(c => c.Name));
        foreach (var configuration in NedRprConfigurations.All)
        {
            var h = new GatewayHarness(configuration);

            var report = h.Engine.Validate();

            Assert.True(report.Errors.Count == 0, configuration.Name + ": " + string.Join("; ", report.Errors));
            Assert.True(report.Warnings.Count == 0, configuration.Name + ": " + string.Join("; ", report.Warnings));
            Assert.Empty(h.Binding.Validation.Warnings);
        }
    }

    [Fact]
    public void The_showcase_settings_are_the_default_ones_with_the_split_fire_the_small_id_range_and_the_short_quarantine()
    {
        var defaults = (NedRprSettings)NedRprConfigurations.Default.Settings;
        var showcase = (NedRprSettings)NedRprConfigurations.Showcase.Settings;

        Assert.False(defaults.SplitFireAssembly);
        Assert.True(showcase.SplitFireAssembly);
        Assert.Equal(60, defaults.DisQuarantineSeconds);
        Assert.Equal(10, showcase.DisQuarantineSeconds);
        Assert.Equal(TimeSpan.FromSeconds(1), showcase.FireAssemblyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), showcase.EventHoldTimeout);
        Assert.Equal(new NedIdRange(1_000_000, 1_999_999), defaults.NedIdRange);
        Assert.Equal(NedRprConfigurations.ShowcaseNedIdRange, showcase.NedIdRange);
        Assert.Equal(showcase with { }, showcase);
        Assert.Equal(defaults.HeartbeatPeriod, showcase.HeartbeatPeriod);
        Assert.Equal(defaults.StaleTimeout, showcase.StaleTimeout);
    }

    [Fact]
    public void The_two_showcase_identity_maps_exist_with_their_key_types()
    {
        foreach (var configuration in NedRprConfigurations.All)
        {
            var h = new GatewayHarness(configuration);

            Assert.Equal(typeof(NedMemberKey), h.Engine.Identities.Find("ned.member")!.KeyType);
            Assert.Equal(typeof(Guid), h.Engine.Identities.Find("ned.fire")!.KeyType);
            Assert.Equal(h.NedSource, h.Host.NedMembers.Side);
            Assert.Equal(h.NedSource, h.Host.NedFires.Side);
        }
    }

    [Fact]
    public void The_showcase_id_range_leaves_exactly_four_ids_when_the_script_has_published_its_RPR_entities_by_act_7()
    {
        var h = Showcase();
        h.Rpr(S.Platform(1, "T-1")); // act 1
        for (int n = 2; n <= 4; n++)
        {
            h.Rpr(S.Platform(n, "W" + n)); // act 3: Wolf's members first ...
        }

        h.Steps(2);
        h.RprStep(S.Aggregate(Wolf, "Wolf", 2, 3, 4, 5)); // ... then the aggregate, with a 4th member to come
        h.Steps(2);
        h.Rpr(S.Platform(5, "W5"));
        h.Steps(2);

        long range = (long)(NedRprConfigurations.ShowcaseNedIdRange.Last - NedRprConfigurations.ShowcaseNedIdRange.First + 1);
        Assert.Equal(2, h.NedIds.Count); // T-1 and the Wolf unit: the withdrawn members gave their ids back
        Assert.Equal(4, range - h.NedIds.Count);

        // act 7: six reinforcements, four ids
        h.Checker.Expect("NED id range exhausted");
        for (int n = 10; n < 16; n++)
        {
            h.Rpr(S.Platform(n, "Red-" + n, lonOffset: 0.01));
        }

        h.Steps(2);
        Assert.Equal(6, h.NedIds.Count);
        Assert.Equal(2, h.Checker.WarningsContaining("NED id range exhausted"));
    }
}
