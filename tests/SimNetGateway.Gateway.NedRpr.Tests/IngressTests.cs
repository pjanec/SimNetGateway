using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>What the ingress modules store: attributes, units, frames and provenance (R-013, R-020).</summary>
public class IngressTests
{
    private static readonly RprObjectHandle Wolf = new(0x9001);

    // ---- NED ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_geo_spatial_is_stored_in_pivot_units_and_frames_with_the_dr_parameters_and_ned_provenance()
    {
        var h = new GatewayHarness();
        var geo = T.Geo(T.Hawk, lat: -33.5, lon: 151.2, alt: 1200, heading: 215, pitch: 4, roll: -7, speed: 85);

        h.NedStep(geo);

        var e = h.EntityOfNed(T.Hawk)!;
        Assert.Equal(h.NedSource, e.Origin);
        Assert.Equal(T.Position(geo), e.Get(h.Schema.Position).AsVec3());
        Assert.Equal(T.OrientationOf(geo), e.Get(h.Schema.Orientation).AsVec3());
        Assert.Equal(T.VelocityOf(geo), e.Get(h.Schema.Velocity).AsVec3());
        Assert.Equal(85.0, T.VelocityOf(geo).Length, 9);
        Assert.Equal(2, e.Get(h.Schema.DrAlgorithm).AsInt()); // FPW
        Assert.True(e.TryGetSlot(h.Schema.StateTime, out var stateTime));
        Assert.Equal(h.SimTime, stateTime.Value.AsDouble(), 9);
        Assert.False(e.Has(h.Schema.Acceleration));
        Assert.False(e.Has(h.Schema.AngularVelocity));
        foreach (var attr in new[] { h.Schema.Position, h.Schema.Orientation, h.Schema.Velocity, h.Schema.DrAlgorithm, h.Schema.StateTime })
        {
            Assert.True(e.TryGetSlot(attr, out var slot));
            Assert.Equal(h.NedSource, slot.Meta.Source);
        }

        Assert.Equal(5, e.Count); // nothing but the spatial attributes: the entity needs no master first
    }

    [Fact]
    public void A_stationary_entity_is_static_and_a_moving_one_is_FPW()
    {
        var h = new GatewayHarness();

        h.NedStep(T.Geo(T.Hawk, speed: 0.0001));
        h.NedStep(T.Geo(T.Viper, speed: 0));

        Assert.Equal(2, h.EntityOfNed(T.Hawk)!.Get(h.Schema.DrAlgorithm).AsInt());
        Assert.Equal(1, h.EntityOfNed(T.Viper)!.Get(h.Schema.DrAlgorithm).AsInt());
        Assert.Equal(Vec3.Zero, h.EntityOfNed(T.Viper)!.Get(h.Schema.Velocity).AsVec3());
    }

    [Fact]
    public void A_master_stores_name_type_and_force_and_a_repeated_one_is_written_but_not_changed()
    {
        var h = new GatewayHarness();

        h.NedStep(T.Master(T.Hawk, "Hawk", 100, NedForce.Neutral));
        h.NedStep(T.Master(T.Hawk, "Hawk", 100, NedForce.Neutral));

        var e = h.EntityOfNed(T.Hawk)!;
        Assert.Equal("Hawk", e.Get(h.Schema.Name).AsString());
        Assert.Equal(100, e.Get(h.Schema.TypeTkb).AsInt());
        Assert.Equal(3, e.Get(h.Schema.Force).AsInt());
        Assert.Equal(3, e.Count);
        Assert.True(e.TryGetSlot(h.Schema.Name, out var slot));
        Assert.Equal(1u, slot.Meta.Version);
        Assert.Equal(h.NedSource, slot.Meta.Source);
    }

    [Fact]
    public void A_damage_level_is_stored_for_a_known_entity_only()
    {
        var h = new GatewayHarness();
        h.NedStep(T.Master(T.Hawk, "Hawk"));

        h.NedStep(new NedEntityDamage(T.Hawk, 2));
        h.NedStep(new NedEntityDamage(T.Viper, 2));

        Assert.Equal(2, h.EntityOfNed(T.Hawk)!.Get(h.Schema.Damage).AsInt());
        Assert.Null(h.EntityOfNed(T.Viper));
        Assert.Equal(1, h.Engine.State.Count);
    }

    // ---- RPR ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_RPR_update_writes_only_the_attributes_it_carries_and_stamps_the_state_time_only_for_spatial_ones()
    {
        var h = new GatewayHarness();

        h.RprStep(new RprObjectUpdate(Wolf) { Marking = "Wolf" });

        var e = h.EntityOfHandle(Wolf)!;
        Assert.Equal(h.RprSource, e.Origin);
        Assert.Equal(h.RprSource, e.Owner);
        Assert.Equal(1, e.Count);
        Assert.Equal("Wolf", e.Get(h.Schema.Name).AsString());
        Assert.False(e.Has(h.Schema.StateTime));

        h.RprStep(new RprObjectUpdate(Wolf) { DamageState = RprDamageState.SlightDamage });
        Assert.False(e.Has(h.Schema.StateTime)); // damage is not spatial
        Assert.Equal(1, e.Get(h.Schema.Damage).AsInt());

        h.Run(1);
        h.RprStep(new RprObjectUpdate(Wolf) { DeadReckoningAlgorithm = 4 });
        Assert.True(e.TryGetSlot(h.Schema.StateTime, out var slot));
        Assert.Equal(h.SimTime, slot.Value.AsDouble(), 9);
        Assert.Equal(h.RprSource, slot.Meta.Source);
        Assert.Equal(4, e.Get(h.Schema.DrAlgorithm).AsInt());
        Assert.False(e.Has(h.Schema.Position));
        Assert.Equal("Wolf", e.Get(h.Schema.Name).AsString()); // earlier values are kept
    }

    [Fact]
    public void An_RPR_update_stores_every_attribute_in_pivot_units()
    {
        var h = new GatewayHarness();
        var update = new RprObjectUpdate(Wolf)
        {
            Marking = "Wolf",
            EntityType = T.Dis(102),
            ForceId = RprForceId.Neutral,
            WorldLocation = new WorldLocation(1, 2, 3),
            Orientation = new RprOrientation(0.1, 0.2, 0.3),
            VelocityVector = new RprVector(4, 5, 6),
            AccelerationVector = new RprVector(7, 8, 9),
            AngularVelocity = new RprVector(0.01, 0.02, 0.03),
            DeadReckoningAlgorithm = 5,
            DamageState = RprDamageState.Destroyed,
        };

        h.RprStep(update);

        var e = h.EntityOfHandle(Wolf)!;
        Assert.Equal("Wolf", e.Get(h.Schema.Name).AsString());
        Assert.Equal(102, e.Get(h.Schema.TypeTkb).AsInt());
        Assert.Equal(3, e.Get(h.Schema.Force).AsInt());
        Assert.Equal(new Vec3(1, 2, 3), e.Get(h.Schema.Position).AsVec3());
        Assert.Equal(new Vec3(0.1, 0.2, 0.3), e.Get(h.Schema.Orientation).AsVec3());
        Assert.Equal(new Vec3(4, 5, 6), e.Get(h.Schema.Velocity).AsVec3());
        Assert.Equal(new Vec3(7, 8, 9), e.Get(h.Schema.Acceleration).AsVec3());
        Assert.Equal(new Vec3(0.01, 0.02, 0.03), e.Get(h.Schema.AngularVelocity).AsVec3());
        Assert.Equal(5, e.Get(h.Schema.DrAlgorithm).AsInt());
        Assert.Equal(3, e.Get(h.Schema.Damage).AsInt());
        Assert.True(e.Has(h.Schema.StateTime));
    }

    [Fact]
    public void The_entity_identifier_of_an_RPR_object_is_bound_the_first_time_only()
    {
        var h = new GatewayHarness();
        h.RprStep(new RprObjectUpdate(Wolf) { EntityIdentifier = new DisEntityId(7, 7, 1) });
        var entity = h.EntityOfHandle(Wolf)!;

        h.RprStep(new RprObjectUpdate(Wolf) { EntityIdentifier = new DisEntityId(7, 7, 1) }); // the same again
        h.RprStep(new RprObjectUpdate(Wolf) { EntityIdentifier = new DisEntityId(7, 7, 2) }); // a different one: ignored

        Assert.True(h.RprEntityIds.TryGetKey(entity.Id, out var key));
        Assert.Equal(new DisEntityId(7, 7, 1), key);
        Assert.False(h.RprEntityIds.TryResolve(new DisEntityId(7, 7, 2), out _));
        Assert.Equal(1, h.RprEntityIds.Count);
    }

    [Fact]
    public void An_entity_identifier_that_belongs_to_another_entity_is_ignored_with_a_warning()
    {
        var h = new GatewayHarness();
        h.Checker.Expect("already bound to another entity");
        h.RprStep(new RprObjectUpdate(Wolf) { EntityIdentifier = new DisEntityId(7, 7, 1) });

        h.RprStep(new RprObjectUpdate(new RprObjectHandle(0x9002)) { Marking = "Fox", EntityIdentifier = new DisEntityId(7, 7, 1) });

        Assert.Equal(2, h.Engine.State.Count);
        Assert.Equal(1, h.RprEntityIds.Count);
        Assert.Equal("Fox", h.EntityOfHandle(new RprObjectHandle(0x9002))!.Get(h.Schema.Name).AsString()); // the rest was applied
        Assert.Equal(1, h.Checker.WarningsContaining("already bound to another entity"));
    }

    [Fact]
    public void An_unknown_DIS_type_is_stored_as_the_default_tkb_with_one_warning_per_unknown_type()
    {
        var h = new GatewayHarness();
        h.Checker.Expect("Unknown DIS entity type");
        var strange = new DisEntityType(1, 1, 1, 1, 1, 1, 1);

        h.RprStep(new RprObjectUpdate(Wolf) { EntityType = strange });
        h.RprStep(new RprObjectUpdate(new RprObjectHandle(0x9002)) { EntityType = strange });

        Assert.Equal(0, h.EntityOfHandle(Wolf)!.Get(h.Schema.TypeTkb).AsInt());
        Assert.Equal(0, h.EntityOfHandle(new RprObjectHandle(0x9002))!.Get(h.Schema.TypeTkb).AsInt());
        Assert.Equal(1, h.Checker.WarningsContaining("Unknown DIS entity type"));
    }

    [Fact]
    public void An_RPR_damage_state_out_of_range_aborts_the_update_without_creating_the_entity()
    {
        var h = new GatewayHarness();
        h.Checker.Expect("damage state 9 out of range");

        h.RprStep(new RprObjectUpdate(Wolf) { Marking = "Wolf", DamageState = (RprDamageState)9 });

        Assert.Equal(0, h.Engine.State.Count);
        Assert.Equal(1, h.Checker.WarningsContaining("damage state 9 out of range"));
    }

    [Fact]
    public void An_RPR_force_out_of_range_is_not_stored()
    {
        var h = new GatewayHarness();

        h.RprStep(new RprObjectUpdate(Wolf) { Marking = "Wolf", ForceId = (RprForceId)9 });

        var e = h.EntityOfHandle(Wolf)!;
        Assert.False(e.Has(h.Schema.Force));
        Assert.Equal("Wolf", e.Get(h.Schema.Name).AsString());
    }

    [Fact]
    public void A_registration_answer_for_an_unknown_token_is_ignored_with_a_warning()
    {
        var h = new GatewayHarness();
        h.Checker.Expect("unknown token");

        h.RprStep(new RprObjectRegistered(12345, new RprObjectHandle(0x1111)));

        Assert.Equal(0, h.RprHandles.Count);
        Assert.Equal(0, h.RprOut.Count);
        Assert.Equal(1, h.Checker.WarningsContaining("unknown token"));
    }

    [Fact]
    public void A_registration_answer_binds_the_handle_to_the_entity_that_asked()
    {
        var h = new GatewayHarness();
        var a = T.PublishNed(h, T.Hawk, "Hawk");
        var b = T.PublishNed(h, T.Viper, "Viper", 100, T.Geo(T.Viper, lat: 50.1));

        // the handles are the RTI's: assigned by the fake RTI at the registration, answered in request order, distinct
        Assert.Equal(2, h.RegisteredHandles.Count);
        Assert.NotEqual(h.RegisteredHandles[0], h.RegisteredHandles[1]);
        Assert.Equal(h.RegisteredHandles[0], h.HandleOf(a));
        Assert.Equal(h.RegisteredHandles[1], h.HandleOf(b));
        Assert.Equal(2, h.RprHandles.Count);
    }
}
