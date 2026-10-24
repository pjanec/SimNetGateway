using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>DD-001 section 12, "End to end NED &lt;-&gt; RPR": the NED to RPR direction.</summary>
public class NedToRprTests
{
    [Fact]
    public void Geo_spatial_before_the_master_publishes_nothing_then_the_master_requests_a_handle_and_one_full_update_follows()
    {
        var h = new GatewayHarness();
        var geo = T.Geo(T.Hawk, heading: 90, speed: 100);

        h.NedStep(geo);

        Assert.Equal(0, h.RprOut.Count); // type unknown: nothing, not even a registration
        Assert.Equal(0, h.DisplayOut.Count);
        Assert.Equal(1, h.Engine.State.Count);

        h.NedStep(T.Master(T.Hawk, "Hawk", 200, NedForce.Friendly));

        var requests = h.RprSent<RprRegisterObject>();
        Assert.Single(requests);
        Assert.Equal(1, h.RprOut.Count); // the registration request, no update yet
        long requestTick = h.RprOut.Messages[0].Tick;

        h.Step(); // the RTI answers: the handle arrives one tick later

        var updates = h.RprSent<RprObjectUpdate>();
        var update = Assert.Single(updates);
        Assert.Equal(2, h.RprOut.Count);
        Assert.Equal(requestTick + 1, h.RprOut.Messages[1].Tick);
        Assert.Equal(new RprObjectHandle(SimulatedRti.FirstHandle), update.Handle);
        Assert.Equal("Hawk", update.Marking);
        Assert.Equal(T.Dis(200), update.EntityType);
        Assert.Equal(RprForceId.Friendly, update.ForceId);
        Assert.Equal(new DisEntityId(1, 3001, 1), update.EntityIdentifier);
        Assert.Equal(NedRprConversions.ToWorldLocation(T.Position(geo)), update.WorldLocation);
        Assert.Equal(NedRprConversions.ToRprOrientation(T.OrientationOf(geo)), update.Orientation);
        Assert.Equal(NedRprConversions.ToRprVector(T.VelocityOf(geo)), update.VelocityVector);
        Assert.Equal((byte)2, update.DeadReckoningAlgorithm); // FPW
        Assert.Null(update.DamageState);
        Assert.Null(update.AccelerationVector);
        Assert.Null(update.AngularVelocity);

        h.Run(2);
        Assert.Equal(2, h.RprOut.Count); // quiet afterwards (the heartbeat is 5 s away)
        Assert.Equal(update.Handle, h.HandleOf(h.EntityOfNed(T.Hawk)!));
        Assert.Equal(update.EntityIdentifier, h.RprEntityIds.TryGetKey(h.EntityOfNed(T.Hawk)!.Id, out var key) ? key : default);
    }

    [Fact]
    public void A_master_without_a_position_publishes_nothing_until_the_position_arrives()
    {
        var h = new GatewayHarness();

        h.NedStep(T.Master(T.Hawk, "Hawk"));
        h.Run(1);
        Assert.Equal(0, h.RprOut.Count);

        h.NedStep(T.Geo(T.Hawk));
        Assert.Single(h.RprSent<RprRegisterObject>());
    }

    [Theory]
    [InlineData("MGW")]
    [InlineData("MWG")]
    [InlineData("GMW")]
    [InlineData("GWM")]
    [InlineData("WMG")]
    [InlineData("WGM")]
    public void Descriptors_arriving_in_any_order_give_the_same_single_registration_and_the_same_full_update(string order)
    {
        var h = new GatewayHarness();
        var geo = T.Geo(T.Hawk);
        foreach (char c in order)
        {
            switch (c)
            {
                case 'M': h.NedStep(T.Master(T.Hawk, "Hawk")); break;
                case 'G': h.NedStep(geo); break;
                case 'W': h.NedStep(new NedWeaponStatus(T.Hawk, 501, 30)); break;
            }
        }

        h.Steps(2);

        Assert.Single(h.RprSent<RprRegisterObject>());
        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal("Hawk", update.Marking);
        Assert.Equal(T.Dis(200), update.EntityType);
        Assert.Equal(NedRprConversions.ToWorldLocation(T.Position(geo)), update.WorldLocation);
        Assert.Equal(1, h.Engine.State.Count);
        var entity = h.EntityOfNed(T.Hawk)!;
        Assert.Equal(30, entity.Get(h.Schema.Ammo).AsInt());
    }

    [Fact]
    public void Updates_while_the_handle_is_pending_are_coalesced_into_the_first_update()
    {
        var h = new GatewayHarness { DeliverRti = false };
        h.NedStep(T.Master(T.Hawk, "Hawk"));
        h.NedStep(T.Geo(T.Hawk));
        Assert.Single(h.RprSent<RprRegisterObject>());

        // While the registration is pending: movement, damage, a rename and a force change.
        var moved = T.Geo(T.Hawk, lat: 50.01, lon: 14.02, heading: 45, speed: 120);
        h.NedStep(moved);
        h.NedStep(new NedEntityDamage(T.Hawk, 2));
        h.NedStep(T.Master(T.Hawk, "Hawk-2", 200, NedForce.Opposing));
        h.Run(1);
        Assert.Equal(1, h.RprOut.Count); // still only the registration request: nothing was sent with a null handle

        h.DeliverRti = true;
        h.Step();

        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(2, h.RprOut.Count);
        Assert.Equal("Hawk-2", update.Marking);
        Assert.Equal(RprForceId.Opposing, update.ForceId);
        Assert.Equal(RprDamageState.ModerateDamage, update.DamageState);
        Assert.Equal(NedRprConversions.ToWorldLocation(T.Position(moved)), update.WorldLocation);
        Assert.Equal(NedRprConversions.ToRprVector(T.VelocityOf(moved)), update.VelocityVector);
        Assert.Equal(new DisEntityId(1, 3001, 1), update.EntityIdentifier);
        Assert.Single(h.RprSent<RprRegisterObject>()); // never registered twice
    }

    [Fact]
    public void A_change_while_pending_is_never_sent_with_a_null_handle()
    {
        var h = new GatewayHarness { DeliverRti = false };
        h.NedStep(T.Master(T.Hawk, "Hawk"));
        h.NedStep(T.Geo(T.Hawk));

        h.NedStep(new NedEntityDamage(T.Hawk, 1));
        h.NedStep(T.Geo(T.Hawk, speed: 50));

        Assert.Empty(h.RprSent<RprObjectUpdate>());
        Assert.DoesNotContain(h.RprOut.Messages, r => r.Message is RprObjectUpdate u && u.Handle == default);
    }

    [Fact]
    public void A_later_change_sends_one_update_per_input_with_only_the_changed_fields()
    {
        var h = new GatewayHarness();
        var geo = T.Geo(T.Hawk, speed: 100);
        T.PublishNed(h, T.Hawk, "Hawk", 200, geo);
        h.ClearOutput();

        // Speed only: the velocity changes; position, orientation, algorithm and identity do not.
        var faster = geo with { Speed = 130 };
        h.NedStep(faster);
        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(1, h.RprOut.Count);
        Assert.Equal(NedRprConversions.ToRprVector(T.VelocityOf(faster)), update.VelocityVector);
        Assert.Null(update.WorldLocation);
        Assert.Null(update.Orientation);
        Assert.Null(update.DeadReckoningAlgorithm);
        Assert.Null(update.Marking);
        Assert.Null(update.EntityType);
        Assert.Null(update.ForceId);
        Assert.Null(update.EntityIdentifier);
        Assert.Null(update.DamageState);
        h.ClearOutput();

        // A move: position, orientation and velocity change in one input - still one message.
        var moved = T.Geo(T.Hawk, lat: 50.02, lon: 14.03, heading: 10, speed: 130);
        h.NedStep(moved);
        update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(1, h.RprOut.Count);
        Assert.Equal(NedRprConversions.ToWorldLocation(T.Position(moved)), update.WorldLocation);
        Assert.Equal(NedRprConversions.ToRprOrientation(T.OrientationOf(moved)), update.Orientation);
        Assert.Equal(NedRprConversions.ToRprVector(T.VelocityOf(moved)), update.VelocityVector);
        Assert.Null(update.DeadReckoningAlgorithm);
        h.ClearOutput();

        // Stopping changes the velocity and the algorithm.
        var stopped = moved with { Speed = 0 };
        h.NedStep(stopped);
        update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(new RprVector(0, 0, 0), update.VelocityVector);
        Assert.Equal((byte)1, update.DeadReckoningAlgorithm); // Static
        Assert.Null(update.WorldLocation);
        h.ClearOutput();

        // Identity: a rename sends the marking only.
        h.NedStep(T.Master(T.Hawk, "Hawk-9"));
        update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal("Hawk-9", update.Marking);
        Assert.Null(update.EntityType);
        Assert.Null(update.ForceId);
        Assert.Null(update.VelocityVector);
        h.ClearOutput();

        h.NedStep(T.Master(T.Hawk, "Hawk-9", 102, NedForce.Neutral));
        update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Null(update.Marking);
        Assert.Equal(T.Dis(102), update.EntityType);
        Assert.Equal(RprForceId.Neutral, update.ForceId);
    }

    [Fact]
    public void One_change_set_that_changes_several_attribute_groups_sends_exactly_one_update()
    {
        var h = new GatewayHarness();
        var entity = T.PublishNed(h, T.Hawk, "Hawk");
        h.ClearOutput();
        var moved = T.Position(T.Geo(T.Hawk, lat: 50.02));

        // A custom module (or processor) writing identity, spatial and damage state in one transaction.
        var changes = h.Engine.BeginChange(h.NedSource);
        changes.Set(entity.Id, h.Schema.Name, AttrValue.From("Hawk-X"));
        changes.Set(entity.Id, h.Schema.Position, AttrValue.From(moved));
        changes.Set(entity.Id, h.Schema.Damage, AttrValue.From(1));
        changes.Commit();
        h.Step();

        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(1, h.RprOut.Count);
        Assert.Equal("Hawk-X", update.Marking);
        Assert.Equal(NedRprConversions.ToWorldLocation(moved), update.WorldLocation);
        Assert.Equal(RprDamageState.SlightDamage, update.DamageState);
        Assert.Null(update.VelocityVector);
    }

    [Theory]
    [InlineData("RprEntityEgress/Identity")]
    [InlineData("RprEntityEgress/Spatial")]
    [InlineData("RprEntityEgress/Damage")]
    public void A_contributor_rule_can_be_disabled_without_touching_the_others(string disabled)
    {
        var h = new GatewayHarness();
        T.PublishNed(h, T.Hawk, "Hawk");
        h.Rule(disabled).Enabled = false; // the operator override of R-046, at runtime
        h.ClearOutput();

        h.NedStep(T.Master(T.Hawk, "Hawk-2"));
        var identity = h.RprSent<RprObjectUpdate>().Count;
        h.NedStep(T.Geo(T.Hawk, lat: 50.01));
        var spatial = h.RprSent<RprObjectUpdate>().Count - identity;
        h.NedStep(new NedEntityDamage(T.Hawk, 2));
        var damage = h.RprSent<RprObjectUpdate>().Count - identity - spatial;

        Assert.Equal(disabled.EndsWith("Identity", StringComparison.Ordinal) ? 0 : 1, identity);
        Assert.Equal(disabled.EndsWith("Spatial", StringComparison.Ordinal) ? 0 : 1, spatial);
        Assert.Equal(disabled.EndsWith("Damage", StringComparison.Ordinal) ? 0 : 1, damage);
    }

    [Fact]
    public void A_damage_only_update_carries_only_the_damage_state()
    {
        var h = new GatewayHarness();
        T.PublishNed(h, T.Hawk, "Hawk");
        h.ClearOutput();

        h.NedStep(new NedEntityDamage(T.Hawk, 3));

        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(RprDamageState.Destroyed, update.DamageState);
        Assert.Null(update.WorldLocation);
        Assert.Null(update.Orientation);
        Assert.Null(update.VelocityVector);
        Assert.Null(update.Marking);
        Assert.Null(update.EntityType);
        Assert.Equal(1, h.RprOut.Count);
    }

    [Theory]
    [InlineData(0, RprDamageState.NoDamage)]
    [InlineData(1, RprDamageState.SlightDamage)]
    [InlineData(2, RprDamageState.ModerateDamage)]
    [InlineData(3, RprDamageState.Destroyed)]
    public void Each_damage_level_maps_to_its_rpr_state(int level, RprDamageState expected)
    {
        var h = new GatewayHarness();
        T.PublishNed(h, T.Hawk, "Hawk");
        h.NedStep(new NedEntityDamage(T.Hawk, level == 0 ? 3 : 0)); // make sure the level is a change
        h.ClearOutput();

        h.NedStep(new NedEntityDamage(T.Hawk, level));

        Assert.Equal(expected, Assert.Single(h.RprSent<RprObjectUpdate>()).DamageState);
    }

    [Fact]
    public void An_unchanged_resend_of_the_geo_spatial_sends_nothing_to_rpr()
    {
        var h = new GatewayHarness();
        var geo = T.Geo(T.Hawk, speed: 0);
        T.PublishNed(h, T.Hawk, "Hawk", 200, geo);
        h.ClearOutput();
        string before = h.Snapshot();

        h.Run(1);
        h.NedStep(geo);
        h.Run(1);
        h.NedStep(T.Master(T.Hawk, "Hawk")); // unchanged identity as well

        Assert.Equal(0, h.RprOut.Count);
        Assert.Equal(0, h.NedOut.Count);
        var entity = h.EntityOfNed(T.Hawk)!;
        Assert.Equal(1u, entity.TryGetSlot(h.Schema.Position, out var slot) ? slot.Meta.Version : 0u); // written, not changed
    }

    [Fact]
    public void Dispose_sends_the_removal_of_the_object_and_releases_the_identities()
    {
        var h = new GatewayHarness();
        var entity = T.PublishNed(h, T.Hawk, "Hawk");
        var handle = h.HandleOf(entity);
        h.ClearOutput();

        h.NedStep(new NedEntityDisposed(T.Hawk));

        var removed = Assert.Single(h.RprSent<RprObjectRemoved>());
        Assert.Equal(handle, removed.Handle);
        Assert.Equal(1, h.RprOut.Count);
        Assert.Equal(0, h.Engine.State.Count);
        Assert.False(h.NedIds.TryResolve(T.Hawk, out _));
        Assert.False(h.RprHandles.TryResolve(handle, out _));
        Assert.Equal(0, h.RprEntityIds.Count);
    }

    [Fact]
    public void A_dispose_of_an_unknown_entity_is_ignored()
    {
        var h = new GatewayHarness();
        T.PublishNed(h, T.Hawk, "Hawk");
        h.ClearOutput();
        string before = h.Snapshot();

        h.NedStep(new NedEntityDisposed(9999));

        Assert.Equal(0, h.RprOut.Count);
        Assert.Equal(0, h.NedOut.Count);
        Assert.Equal(before, h.Snapshot());
        Assert.Equal(1, h.Engine.State.Count);
    }

    [Fact]
    public void An_entity_that_was_never_published_is_removed_without_a_message()
    {
        var h = new GatewayHarness();
        h.NedStep(T.Geo(T.Hawk)); // no type: never published

        h.NedStep(new NedEntityDisposed(T.Hawk));

        Assert.Equal(0, h.RprOut.Count);
        Assert.Equal(0, h.Engine.State.Count);
    }

    [Fact]
    public void An_entity_removed_while_its_registration_is_pending_sends_nothing_then_the_orphaned_object_is_deleted()
    {
        var h = new GatewayHarness { DeliverRti = false };
        h.NedStep(T.Master(T.Hawk, "Hawk"));
        h.NedStep(T.Geo(T.Hawk));

        h.NedStep(new NedEntityDisposed(T.Hawk));

        Assert.Equal(1, h.RprOut.Count); // only the registration request: the removal is dropped, nothing to delete yet
        Assert.Equal(0, h.Engine.State.Count);

        h.DeliverRti = true;
        h.Step();

        // The RTI answered for an entity that is gone: the object it created is deleted again, no update is sent.
        Assert.Empty(h.RprSent<RprObjectUpdate>());
        var removed = Assert.Single(h.RprSent<RprObjectRemoved>());
        Assert.Equal(new RprObjectHandle(SimulatedRti.FirstHandle), removed.Handle);
        Assert.Equal(0, h.RprHandles.Count);
    }

    [Fact]
    public void Two_entities_get_distinct_handles_and_distinct_dis_entity_identifiers()
    {
        var h = new GatewayHarness();
        var a = T.PublishNed(h, T.Hawk, "Hawk");
        var b = T.PublishNed(h, T.Viper, "Viper", 100, T.Geo(T.Viper, lat: 50.1));

        Assert.NotEqual(h.HandleOf(a), h.HandleOf(b));
        h.RprEntityIds.TryGetKey(a.Id, out var idA);
        h.RprEntityIds.TryGetKey(b.Id, out var idB);
        Assert.NotEqual(idA, idB);
        Assert.Equal((ushort)1, idA.Site);
        Assert.Equal((ushort)3001, idA.Application);
        Assert.Equal(2, h.RprSent<RprObjectUpdate>().Count);
    }

    [Fact]
    public void A_released_dis_entity_number_is_not_reused_during_its_quarantine()
    {
        var h = new GatewayHarness();
        var a = T.PublishNed(h, T.Hawk, "Hawk");
        h.RprEntityIds.TryGetKey(a.Id, out var first);
        h.NedStep(new NedEntityDisposed(T.Hawk));
        h.Run(1);

        var b = T.PublishNed(h, T.Viper, "Viper", 100, T.Geo(T.Viper));

        h.RprEntityIds.TryGetKey(b.Id, out var second);
        Assert.NotEqual(first.Entity, second.Entity); // never-used numbers come first, the released one rests for 60 s
    }

    [Fact]
    public void An_unknown_type_is_published_with_the_default_dis_type_and_one_warning_per_unknown_value()
    {
        var h = new GatewayHarness();
        h.Checker.Expect("Unknown TKB type");

        h.NedStep(T.Master(T.Hawk, "Hawk", 777));
        h.NedStep(T.Geo(T.Hawk));
        h.Steps(2);
        Assert.Equal(TypeCatalog.DefaultDis, Assert.Single(h.RprSent<RprObjectUpdate>()).EntityType);
        Assert.Equal(1, h.Checker.WarningsContaining("Unknown TKB type 777"));

        h.NedStep(T.Master(T.Hawk, "Hawk", 777, NedForce.Neutral)); // the type is unchanged: no conversion, no warning
        Assert.Equal(1, h.Checker.WarningsContaining("Unknown TKB type 777"));

        h.NedStep(T.Master(T.Hawk, "Hawk", 778)); // another unknown value: its own warning
        Assert.Equal(TypeCatalog.DefaultDis, h.RprSent<RprObjectUpdate>()[^1].EntityType);
        Assert.Equal(1, h.Checker.WarningsContaining("Unknown TKB type 778"));
        h.NedStep(T.Master(T.Hawk, "Hawk", 777));
        h.NedStep(T.Master(T.Hawk, "Hawk", 778));
        Assert.Equal(1, h.Checker.WarningsContaining("Unknown TKB type 777"));
        Assert.Equal(1, h.Checker.WarningsContaining("Unknown TKB type 778"));
    }

    [Fact]
    public void An_out_of_range_force_aborts_the_master_without_a_trace()
    {
        var h = new GatewayHarness();
        h.Checker.Expect("force 9 out of range");

        h.NedStep(T.Master(T.Hawk, "Hawk", 200, (NedForce)9));

        Assert.Equal(0, h.Engine.State.Count);
        Assert.Equal(0, h.RprOut.Count);
        Assert.Equal(1, h.Checker.WarningsContaining("force 9 out of range"));
    }
}
