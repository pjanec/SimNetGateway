using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.FakeRti.Tests;

/// <summary>DD-008 3.6 rows "Publish / subscribe" and "Discovery".</summary>
public class DeclarationAndDiscoveryTests
{
    // --- publish / subscribe ---

    [Fact]
    public void Publish_and_subscribe_validate_the_class_and_that_every_attribute_belongs_to_it()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var stance = a.Attr(Names.Lifeform, "StanceCode");
        var headLights = a.Attr(Names.Platform, "HeadLightsOn");
        var entityType = a.Attr(Names.Physical, "EntityType");

        Assert.Throws<ObjectClassNotDefined>(() => a.Rti.PublishObjectClassAttributes(new ObjectClassHandle(9999), new AttributeHandleSet()));
        Assert.Throws<ObjectClassNotDefined>(() => a.Rti.SubscribeObjectClassAttributes(new ObjectClassHandle(0), new AttributeHandleSet()));
        Assert.Throws<AttributeNotDefined>(() => a.Rti.PublishObjectClassAttributes(a.Class(Names.Platform), new AttributeHandleSet([stance])));
        Assert.Throws<AttributeNotDefined>(() => a.Rti.SubscribeObjectClassAttributes(a.Class(Names.Physical), new AttributeHandleSet([headLights]))); // Platform's, not Physical's
        Assert.Throws<AttributeNotDefined>(() => a.Rti.SubscribeObjectClassAttributes(a.Class(Names.Physical), new AttributeHandleSet([new AttributeHandle(9999)])));
        Assert.Throws<AttributeNotDefined>(() => a.Rti.PublishObjectClassAttributes(a.Class(Names.Platform), new AttributeHandleSet([entityType, stance])));
        // a failed call has no effect: nothing was published
        Assert.Throws<ObjectClassNotPublished>(() => a.Rti.RegisterObjectInstance(a.Class(Names.Platform)));
        // inherited attributes are fine
        a.Rti.PublishObjectClassAttributes(a.Class(Names.Platform), new AttributeHandleSet([entityType, headLights]));
        a.Rti.RegisterObjectInstance(a.Class(Names.Platform));
    }

    [Fact]
    public void A_later_subscribe_replaces_the_attribute_set_of_that_class()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        var sub = rig.Join("Sub");
        pub.Publish(Names.Ground, "EntityType", "Marking");
        var obj = pub.Register(Names.Ground);
        sub.Subscribe(Names.Physical, "EntityType", "Marking");
        sub.Evoke();
        sub.Clear();

        sub.Subscribe(Names.Physical, "EntityType"); // replaces, does not add
        pub.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)), ("Marking", Names.Bytes(2)));
        sub.Evoke();

        var reflected = Assert.Single(sub.Recorder.Of<Reflected>());
        Assert.Equal(new[] { sub.Attr(Names.Physical, "EntityType") }, reflected.Values.Select(v => v.Attribute).ToArray());

        sub.Clear();
        sub.Subscribe(Names.Physical, "EntityType", "Marking"); // and back
        pub.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)), ("Marking", Names.Bytes(2)));
        sub.Evoke();
        Assert.Equal(2, Assert.Single(sub.Recorder.Of<Reflected>()).Values.Length);
    }

    [Fact]
    public void Subscribing_an_empty_set_or_unsubscribing_stops_reflections_but_keeps_the_instance_known()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        var sub = rig.Join("Sub");
        pub.Publish(Names.Ground, "EntityType");
        var obj = pub.Register(Names.Ground);
        sub.Subscribe(Names.Physical, "EntityType");
        sub.Evoke();
        sub.Clear();

        sub.Rti.SubscribeObjectClassAttributes(sub.Class(Names.Physical), new AttributeHandleSet());
        pub.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)));
        Assert.Equal(0, sub.Evoke());
        Assert.Equal("HLAobject_1", sub.Rti.GetObjectInstanceName(obj));

        sub.Subscribe(Names.Physical, "EntityType");
        sub.Rti.UnsubscribeObjectClass(sub.Class(Names.Physical));
        pub.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)));
        Assert.Equal(0, sub.Evoke());

        sub.Subscribe(Names.Physical, "EntityType");
        pub.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)));
        Assert.Equal(1, sub.Evoke());
        Assert.Empty(sub.Recorder.Of<Discovered>()); // coming back is not a rediscovery
    }

    [Fact]
    public void A_later_publish_replaces_the_set_but_an_existing_instance_keeps_the_ownership_it_was_registered_with()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        var sub = rig.Join("Sub");
        sub.Subscribe(Names.Physical, "EntityType", "Marking");
        pub.Publish(Names.Ground, "EntityType");
        var first = pub.Register(Names.Ground);

        pub.Publish(Names.Ground, "EntityType", "Marking"); // replaces
        var second = pub.Register(Names.Ground);

        Assert.Throws<AttributeNotOwned>(() => pub.Update(first, Names.Ground, ("Marking", Names.Bytes(1))));
        pub.Update(second, Names.Ground, ("Marking", Names.Bytes(1)));
        pub.Update(first, Names.Ground, ("EntityType", Names.Bytes(1)));

        pub.Publish(Names.Ground, "Marking"); // replaces again: EntityType is no longer published
        var third = pub.Register(Names.Ground);
        Assert.Throws<AttributeNotOwned>(() => pub.Update(third, Names.Ground, ("EntityType", Names.Bytes(1))));

        pub.Rti.PublishObjectClassAttributes(pub.Class(Names.Ground), new AttributeHandleSet()); // empty = unpublish
        Assert.Throws<ObjectClassNotPublished>(() => pub.Register(Names.Ground));
        pub.Publish(Names.Ground, "Marking");
        pub.Register(Names.Ground);
        pub.Rti.UnpublishObjectClass(pub.Class(Names.Ground));
        Assert.Throws<ObjectClassNotPublished>(() => pub.Register(Names.Ground));
        pub.Update(second, Names.Ground, ("Marking", Names.Bytes(2))); // ownership survives an unpublish
    }

    // --- discovery ---

    [Fact]
    public void Subscribing_a_superclass_discovers_the_instance_as_that_class_and_reflects_only_its_attributes()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        var sub = rig.Join("Sub");
        sub.Subscribe(Names.Physical, "EntityType", "Marking");
        pub.Publish(Names.Ground, "EntityType", "Marking", "HeadLightsOn");
        var obj = pub.Register(Names.Ground);

        pub.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)), ("Marking", Names.Bytes(2)), ("HeadLightsOn", Names.Bytes(1)));
        sub.Evoke();

        var discovered = Assert.Single(sub.Recorder.Of<Discovered>());
        Assert.Equal(sub.Class(Names.Physical), discovered.Class); // promoted: GroundVehicle seen as PhysicalEntity
        Assert.Equal(obj, discovered.Object);
        Assert.Equal("HLAobject_1", discovered.Name);
        var reflected = Assert.Single(sub.Recorder.Of<Reflected>());
        Assert.Equal(
            new[] { sub.Attr(Names.Physical, "EntityType"), sub.Attr(Names.Physical, "Marking") },
            reflected.Values.Select(v => v.Attribute).ToArray());
        Assert.IsType<Discovered>(sub.Events[0]);
    }

    [Fact]
    public void The_closest_subscribed_class_wins_and_unrelated_or_more_specific_subscriptions_do_not_discover()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        pub.Publish(Names.Ground, "EntityType");
        var farSub = rig.Join("Far");
        farSub.Subscribe(Names.BaseEntity, "Spatial");
        var mixedSub = rig.Join("Mixed");
        mixedSub.Subscribe(Names.BaseEntity, "EntityType");
        mixedSub.Subscribe(Names.Platform, "EntityType");
        mixedSub.Subscribe(Names.Physical, "EntityType");
        var exactSub = rig.Join("Exact");
        exactSub.Subscribe(Names.Ground, "EntityType");
        exactSub.Subscribe(Names.Platform, "EntityType");
        var subclassSub = rig.Join("Subclass"); // a subclass of the registered class: not a superclass, no discovery
        pub.Publish(Names.Platform, "EntityType");
        subclassSub.Subscribe(Names.Ground, "EntityType");
        var unrelatedSub = rig.Join("Unrelated");
        unrelatedSub.Subscribe(Names.Lifeform, "StanceCode");
        unrelatedSub.Subscribe(Names.Aircraft, "EntityType");

        pub.Register(Names.Ground);
        pub.Register(Names.Platform);

        foreach (var f in new[] { farSub, mixedSub, exactSub, subclassSub, unrelatedSub })
        {
            f.Evoke();
        }
        ObjectClassHandle KnownAs(Fed f, int instance) => f.Recorder.Of<Discovered>().Single(d => d.Object.Value == instance).Class;
        // instance 1 is a GroundVehicle, instance 2 a Platform
        Assert.Equal(farSub.Class(Names.BaseEntity), KnownAs(farSub, 1));
        Assert.Equal(farSub.Class(Names.BaseEntity), KnownAs(farSub, 2));
        Assert.Equal(mixedSub.Class(Names.Platform), KnownAs(mixedSub, 1)); // Platform is closer to GroundVehicle than Physical
        Assert.Equal(mixedSub.Class(Names.Platform), KnownAs(mixedSub, 2));
        Assert.Equal(exactSub.Class(Names.Ground), KnownAs(exactSub, 1));
        Assert.Equal(exactSub.Class(Names.Platform), KnownAs(exactSub, 2));
        // a subscription to a SUBclass of the registered class discovers nothing of that instance
        Assert.Equal(new[] { 1 }, subclassSub.Recorder.Of<Discovered>().Select(d => d.Object.Value).ToArray());
        Assert.Equal(2, farSub.Recorder.Of<Discovered>().Count());
        Assert.Empty(unrelatedSub.Events);
    }

    [Fact]
    public void Promotion_goes_to_the_closest_class_not_the_root_the_attribute_set_is_that_classs()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        var sub = rig.Join("Sub");
        pub.Publish(Names.Ground, "EntityType", "Marking", "HeadLightsOn", "Spatial");
        sub.Subscribe(Names.BaseEntity, "EntityType", "Spatial");
        sub.Subscribe(Names.Platform, "Marking", "HeadLightsOn");
        var obj = pub.Register(Names.Ground);

        pub.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)), ("Spatial", Names.Bytes(2)), ("Marking", Names.Bytes(3)), ("HeadLightsOn", Names.Bytes(4)));
        sub.Evoke();

        Assert.Equal(sub.Class(Names.Platform), Assert.Single(sub.Recorder.Of<Discovered>()).Class);
        // reflected through the Platform subscription only: Marking and HeadLightsOn, not the BaseEntity ones
        Assert.Equal(
            new[] { sub.Attr(Names.Platform, "Marking"), sub.Attr(Names.Platform, "HeadLightsOn") }.OrderBy(h => h.Value).ToArray(),
            Assert.Single(sub.Recorder.Of<Reflected>()).Values.Select(v => v.Attribute).ToArray());
    }

    [Fact]
    public void The_known_class_is_fixed_at_discovery_a_later_more_specific_subscription_changes_neither_it_nor_the_filter()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        var sub = rig.Join("Sub");
        pub.Publish(Names.Ground, "EntityType", "Marking", "HeadLightsOn");
        sub.Subscribe(Names.Physical, "EntityType");
        var obj = pub.Register(Names.Ground);
        sub.Evoke();
        sub.Clear();

        sub.Subscribe(Names.Ground, "Marking", "HeadLightsOn");
        pub.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)), ("Marking", Names.Bytes(2)), ("HeadLightsOn", Names.Bytes(3)));
        sub.Evoke();

        Assert.Empty(sub.Recorder.Of<Discovered>()); // once per instance
        var reflected = Assert.Single(sub.Recorder.Of<Reflected>());
        Assert.Equal(new[] { sub.Attr(Names.Physical, "EntityType") }, reflected.Values.Select(v => v.Attribute).ToArray());
        // the federate knows the instance as PhysicalEntity: Platform attributes are not defined for it
        Assert.Throws<AttributeNotDefined>(() =>
            sub.Rti.RequestAttributeValueUpdate(obj, new AttributeHandleSet([sub.Attr(Names.Platform, "HeadLightsOn")]), []));
    }

    [Fact]
    public void An_instance_is_discovered_once_per_federate_however_often_it_is_updated_or_subscribed_again()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        var sub = rig.Join("Sub");
        pub.Publish(Names.Ground, "EntityType");
        var obj = pub.Register(Names.Ground);
        for (var i = 0; i < 3; i++)
        {
            sub.Subscribe(Names.Physical, "EntityType");
            sub.Subscribe(Names.BaseEntity, "EntityType");
            pub.Update(obj, Names.Ground, ("EntityType", Names.Bytes(i)));
        }

        sub.Evoke();

        Assert.Single(sub.Recorder.Of<Discovered>());
        Assert.Equal(3, sub.Recorder.Of<Reflected>().Count());
    }

    [Fact]
    public void A_federate_never_discovers_or_receives_reflections_of_its_own_instances()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        a.Publish(Names.Ground, "EntityType");
        a.Subscribe(Names.Physical, "EntityType");
        b.Publish(Names.Ground, "EntityType");
        b.Subscribe(Names.Physical, "EntityType");

        var own = a.Register(Names.Ground);
        var other = b.Register(Names.Ground);
        a.Update(own, Names.Ground, ("EntityType", Names.Bytes(1)));
        a.Rti.SubscribeObjectClassAttributes(a.Class(Names.Physical), a.Attrs(Names.Physical, "EntityType")); // re-subscribing: still not
        a.Evoke();
        b.Evoke();

        Assert.Equal(new[] { other }, a.Recorder.Of<Discovered>().Select(d => d.Object).ToArray());
        Assert.Empty(a.Recorder.Of<Reflected>()); // no self-reflection
        Assert.Equal(new[] { own }, b.Recorder.Of<Discovered>().Select(d => d.Object).ToArray());
        Assert.Equal(new[] { own }, b.Recorder.Of<Reflected>().Select(r => r.Object).ToArray());
    }

    [Fact]
    public void A_late_subscription_discovers_the_existing_instances_in_handle_order_but_not_deleted_ones()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        pub.Publish(Names.Ground, "EntityType");
        pub.Publish(Names.Human, "EntityType");
        var g1 = pub.Register(Names.Ground);
        var h2 = pub.Register(Names.Human);
        var g3 = pub.Register(Names.Ground);
        var g4 = pub.Register(Names.Ground);
        pub.Rti.DeleteObjectInstance(g3, []);
        var late = rig.Join("Late"); // joins after all of it

        late.Subscribe(Names.Platform, "EntityType");
        late.Evoke();

        Assert.Equal(new[] { g1, g4 }, late.Recorder.Of<Discovered>().Select(d => d.Object).ToArray());
        late.Clear();
        late.Subscribe(Names.Lifeform, "StanceCode");
        late.Evoke();
        Assert.Equal(new[] { h2 }, late.Recorder.Of<Discovered>().Select(d => d.Object).ToArray());
        Assert.Equal(late.Class(Names.Lifeform), Assert.Single(late.Recorder.Of<Discovered>()).Class);
    }

    [Fact]
    public void Discovery_needs_a_subscription_before_or_after_registration_not_just_a_publication()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        var peer = rig.Join("Peer");
        peer.Publish(Names.Ground, "EntityType"); // published but not subscribed
        pub.Publish(Names.Ground, "EntityType");

        pub.Register(Names.Ground);
        peer.Evoke();

        Assert.Empty(peer.Events);
    }

    // --- interaction declarations ---

    [Fact]
    public void Interaction_publish_subscribe_validate_the_class_and_unpublish_unsubscribe_take_effect()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        var fire = a.Interaction(Names.Fire);
        var none = new ParameterHandleValueMap();

        Assert.Throws<InteractionClassNotDefined>(() => a.Rti.PublishInteractionClass(new InteractionClassHandle(9999)));
        Assert.Throws<InteractionClassNotDefined>(() => a.Rti.SubscribeInteractionClass(new InteractionClassHandle(0)));
        Assert.Throws<InteractionClassNotDefined>(() => a.Rti.UnpublishInteractionClass(new InteractionClassHandle(-3)));
        Assert.Throws<InteractionClassNotDefined>(() => a.Rti.UnsubscribeInteractionClass(new InteractionClassHandle(77)));
        Assert.Throws<InteractionClassNotDefined>(() => a.Rti.SendInteraction(new InteractionClassHandle(77), none, []));
        Assert.Throws<InteractionClassNotPublished>(() => a.Rti.SendInteraction(fire, none, []));

        b.Rti.SubscribeInteractionClass(fire);
        a.Rti.PublishInteractionClass(fire);
        a.Rti.PublishInteractionClass(fire); // idempotent
        a.Rti.SendInteraction(fire, none, []);
        Assert.Equal(1, b.Evoke());

        b.Rti.UnsubscribeInteractionClass(fire);
        a.Rti.SendInteraction(fire, none, []);
        Assert.Equal(0, b.Evoke());

        b.Rti.SubscribeInteractionClass(fire);
        a.Rti.UnpublishInteractionClass(fire);
        Assert.Throws<InteractionClassNotPublished>(() => a.Rti.SendInteraction(fire, none, []));
        Assert.Equal(0, b.Evoke());
    }
}
