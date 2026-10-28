using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.FakeRti.Tests;

/// <summary>DD-008 3.6 rows "Names", "Registration / ownership", "Updates", "Interactions", "Attribute value requests", "Delete", "Opaque values".</summary>
public class ObjectAndInteractionTests
{
    // --- names ---

    [Fact]
    public void Name_reservation_answers_asynchronously_and_names_are_unique()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");

        a.Rti.ReserveObjectInstanceName("tank-1");
        Assert.Empty(a.Events); // not before an evoke
        b.Rti.ReserveObjectInstanceName("tank-1"); // already reserved by a
        b.Rti.ReserveObjectInstanceName("tank-2");
        a.Rti.ReserveObjectInstanceName("tank-1"); // and again by the holder: also a failure

        a.Evoke();
        b.Evoke();
        Assert.Equal(new RtiEvent[] { new NameReserved("tank-1"), new NameReservationFailed("tank-1") }, a.Events);
        Assert.Equal(new RtiEvent[] { new NameReservationFailed("tank-1"), new NameReserved("tank-2") }, b.Events);
    }

    [Fact]
    public void A_named_registration_needs_the_callers_own_reservation()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        a.Publish(Names.Ground, "EntityType");
        b.Publish(Names.Ground, "EntityType");
        var ground = a.Class(Names.Ground);

        Assert.Throws<ObjectInstanceNameNotReserved>(() => a.Rti.RegisterObjectInstance(ground, "nobody-reserved"));
        a.Rti.ReserveObjectInstanceName("mine");
        Assert.Throws<ObjectInstanceNameNotReserved>(() => b.Rti.RegisterObjectInstance(ground, "mine")); // reserved by someone else

        var obj = a.Rti.RegisterObjectInstance(ground, "mine");

        Assert.Equal("mine", a.Rti.GetObjectInstanceName(obj));
        Assert.Throws<ObjectInstanceNameInUse>(() => a.Rti.RegisterObjectInstance(ground, "mine"));
        Assert.Throws<ObjectInstanceNameInUse>(() => b.Rti.RegisterObjectInstance(ground, "mine"));
        b.Rti.ReserveObjectInstanceName("mine");
        b.Evoke();
        Assert.Equal(new NameReservationFailed("mine"), Assert.Single(b.Events)); // a name in use cannot be reserved
    }

    [Fact]
    public void RTI_generated_names_are_HLAobject_n_with_the_instance_handle_and_cannot_collide_with_reservations()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        a.Publish(Names.Ground, "EntityType");
        a.Rti.ReserveObjectInstanceName("named");
        var named = a.Rti.RegisterObjectInstance(a.Class(Names.Ground), "named");
        var first = a.Register(Names.Ground);
        var second = a.Register(Names.Ground);

        Assert.Equal(1, named.Value);
        Assert.Equal("HLAobject_2", a.Rti.GetObjectInstanceName(first));
        Assert.Equal("HLAobject_3", a.Rti.GetObjectInstanceName(second));
        Assert.Throws<IllegalName>(() => a.Rti.ReserveObjectInstanceName("HLAobject_4"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("HLAanything")]
    [InlineData("HLA")]
    [InlineData("café")]
    [InlineData("tab\there")]
    [InlineData("new\nline")]
    public void Illegal_names_are_refused_for_reservation_and_named_registration(string name)
    {
        var rig = new Rig();
        var a = rig.Join("A");
        a.Publish(Names.Ground, "EntityType");

        Assert.Throws<IllegalName>(() => a.Rti.ReserveObjectInstanceName(name));
        Assert.Throws<IllegalName>(() => a.Rti.RegisterObjectInstance(a.Class(Names.Ground), name));
        Assert.Equal(0, a.Rti.PendingCallbackCount);
    }

    [Fact]
    public void Name_length_is_limited_and_printable_ascii_is_accepted()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var longest = new string('x', FakeRtiHub.MaxObjectNameLength);

        a.Rti.ReserveObjectInstanceName(longest);
        a.Rti.ReserveObjectInstanceName("a b.c-d_e~!{}[]");
        Assert.Throws<IllegalName>(() => a.Rti.ReserveObjectInstanceName(longest + "x"));

        Assert.Equal(2, a.Evoke());
        Assert.All(a.Events, e => Assert.IsType<NameReserved>(e));
    }

    [Fact]
    public void A_deleted_objects_name_is_free_again()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        a.Publish(Names.Ground, "EntityType");
        a.Rti.ReserveObjectInstanceName("reuse");
        var first = a.Rti.RegisterObjectInstance(a.Class(Names.Ground), "reuse");
        a.Rti.DeleteObjectInstance(first, []);

        a.Rti.ReserveObjectInstanceName("reuse");
        var second = a.Rti.RegisterObjectInstance(a.Class(Names.Ground), "reuse");

        Assert.NotEqual(first, second);
        a.Evoke();
        Assert.Equal(new RtiEvent[] { new NameReserved("reuse"), new NameReserved("reuse") }, a.Events);
    }

    [Fact]
    public void An_instance_name_is_visible_only_to_federates_that_know_the_instance()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var known = rig.Join("Known");
        var unknown = rig.Join("Unknown");
        a.Publish(Names.Ground, "EntityType");
        known.Subscribe(Names.Physical, "EntityType");
        var obj = a.Register(Names.Ground);

        Assert.Equal("HLAobject_1", known.Rti.GetObjectInstanceName(obj));
        Assert.Throws<ObjectInstanceNotKnown>(() => unknown.Rti.GetObjectInstanceName(obj));
        Assert.Throws<ObjectInstanceNotKnown>(() => unknown.Rti.GetObjectInstanceName(new ObjectInstanceHandle(77)));
    }

    // --- registration and ownership ---

    [Fact]
    public void Registration_needs_the_exact_class_published()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        a.Publish(Names.Platform, "EntityType");

        Assert.Throws<ObjectClassNotPublished>(() => a.Register(Names.Ground)); // a published superclass does not count
        Assert.Throws<ObjectClassNotPublished>(() => a.Register(Names.Physical));
        Assert.Throws<ObjectClassNotDefined>(() => a.Rti.RegisterObjectInstance(new ObjectClassHandle(9999)));
        Assert.Throws<ObjectClassNotDefined>(() => a.Rti.RegisterObjectInstance(new ObjectClassHandle(0), "x"));
        a.Register(Names.Platform);
    }

    [Fact]
    public void The_registrant_owns_the_attributes_it_published_at_that_class_and_nothing_else()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        a.Publish(Names.Ground, "EntityType", "Marking");
        a.Publish(Names.Platform, "Spatial"); // published at another class: not owned by a Ground instance
        var obj = a.Register(Names.Ground);

        a.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)), ("Marking", Names.Bytes(2)));
        Assert.Throws<AttributeNotOwned>(() => a.Update(obj, Names.Ground, ("Spatial", Names.Bytes(1))));
        Assert.Throws<AttributeNotOwned>(() => a.Update(obj, Names.Ground, ("HeadLightsOn", Names.Bytes(1))));
        // the delete privilege comes with registering, whatever was published
        a.Rti.DeleteObjectInstance(obj, []);
    }

    // --- updates ---

    [Fact]
    public void Updates_must_be_of_attributes_defined_for_the_class_and_owned_and_a_failed_update_delivers_nothing()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        a.Publish(Names.Ground, "EntityType", "Marking");
        b.Subscribe(Names.Physical, "EntityType", "Marking");
        var obj = a.Register(Names.Ground);
        b.Evoke();
        b.Clear();
        var stance = a.Attr(Names.Lifeform, "StanceCode");
        var spatial = a.Attr(Names.Ground, "Spatial");
        var entityType = a.Attr(Names.Ground, "EntityType");

        var mixed = new AttributeHandleValueMap();
        mixed.Set(entityType, Names.Bytes(1));
        mixed.Set(stance, Names.Bytes(2)); // not an attribute of GroundVehicle at all
        Assert.Throws<AttributeNotDefined>(() => a.Rti.UpdateAttributeValues(obj, mixed, []));
        var notOwned = new AttributeHandleValueMap();
        notOwned.Set(entityType, Names.Bytes(1));
        notOwned.Set(spatial, Names.Bytes(2)); // defined but not owned
        Assert.Throws<AttributeNotOwned>(() => a.Rti.UpdateAttributeValues(obj, notOwned, []));
        var bogus = new AttributeHandleValueMap();
        bogus.Set(new AttributeHandle(9999), Names.Bytes(2));
        Assert.Throws<AttributeNotDefined>(() => a.Rti.UpdateAttributeValues(obj, bogus, []));
        Assert.Throws<ObjectInstanceNotKnown>(() => a.Rti.UpdateAttributeValues(new ObjectInstanceHandle(99), notOwned, []));
        Assert.Throws<ObjectInstanceNotKnown>(() => a.Rti.UpdateAttributeValues(new ObjectInstanceHandle(0), notOwned, []));

        Assert.Equal(0, b.Evoke()); // no partial delivery of the valid part
    }

    [Fact]
    public void A_discoverer_cannot_update_and_an_unknown_instance_cannot_be_updated()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        var stranger = rig.Join("Stranger");
        a.Publish(Names.Ground, "EntityType");
        b.Subscribe(Names.Physical, "EntityType");
        var obj = a.Register(Names.Ground);

        Assert.Throws<AttributeNotOwned>(() => b.Update(obj, Names.Physical, ("EntityType", Names.Bytes(1))));
        Assert.Throws<ObjectInstanceNotKnown>(() => stranger.Update(obj, Names.Physical, ("EntityType", Names.Bytes(1))));
    }

    [Fact]
    public void Each_discoverer_gets_the_attributes_of_its_own_subscription_at_its_own_known_class_and_an_empty_result_is_not_delivered()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        var wide = rig.Join("Wide");
        var narrow = rig.Join("Narrow");
        var none = rig.Join("None");
        var other = rig.Join("Other");
        pub.Publish(Names.Ground, "EntityType", "Marking", "HeadLightsOn", "Spatial");
        wide.Subscribe(Names.Ground, "EntityType", "Marking", "HeadLightsOn", "Spatial");
        narrow.Subscribe(Names.Physical, "Marking");
        none.Subscribe(Names.Physical, "ForceIdentifier"); // an attribute nobody updates
        other.Subscribe(Names.Aircraft, "EntityType"); // not a GroundVehicle: never discovers
        var obj = pub.Register(Names.Ground);
        foreach (var f in new[] { wide, narrow, none, other })
        {
            f.Evoke();
            f.Clear();
        }

        pub.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)), ("Marking", Names.Bytes(2)), ("HeadLightsOn", Names.Bytes(3)));

        Assert.Equal(1, wide.Evoke());
        Assert.Equal(3, wide.Recorder.Of<Reflected>().Single().Values.Length);
        Assert.Equal(1, narrow.Evoke());
        var narrowValues = narrow.Recorder.Of<Reflected>().Single().Values;
        Assert.Equal(narrow.Attr(Names.Physical, "Marking"), Assert.Single(narrowValues).Attribute);
        Assert.Equal(Names.Bytes(2), narrowValues[0].Value);
        Assert.Equal(0, none.Evoke());
        Assert.Equal(0, other.Evoke());
    }

    [Fact]
    public void Values_and_tags_are_opaque_octets_delivered_unchanged_in_separate_copies()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        var x = rig.Join("X");
        var y = rig.Join("Y");
        pub.Publish(Names.Ground, "Spatial", "Marking");
        x.Subscribe(Names.Physical, "Spatial", "Marking");
        y.Subscribe(Names.Physical, "Spatial", "Marking");
        var obj = pub.Register(Names.Ground);
        var rnd = new Random(42);
        var garbage = new byte[1000];
        rnd.NextBytes(garbage); // not a valid SpatialVariantStruct, and the RTI must not care
        var map = new AttributeHandleValueMap();
        map.Set(pub.Attr(Names.Ground, "Spatial"), garbage);
        map.Set(pub.Attr(Names.Ground, "Marking"), []);
        var tag = new byte[] { 9, 8, 7 };

        pub.Rti.UpdateAttributeValues(obj, map, tag);
        garbage[0] ^= 0xFF; // the sender mutates its arrays afterwards
        tag[0] = 0;
        x.Evoke();
        y.Evoke();

        var rx = x.Recorder.Of<Reflected>().Single();
        var ry = y.Recorder.Of<Reflected>().Single();
        Assert.Equal(new byte[] { 9, 8, 7 }, rx.Tag);
        Assert.Equal(new byte[] { 9, 8, 7 }, ry.Tag);
        var spatial = pub.Attr(Names.Ground, "Spatial");
        var sent = rx.Values.Single(v => v.Attribute == spatial).Value;
        Assert.Equal(1000, sent.Length);
        Assert.Equal((byte)(garbage[0] ^ 0xFF), sent[0]);
        Assert.Equal(garbage.AsSpan(1).ToArray(), sent.AsSpan(1).ToArray());
        Assert.Empty(rx.Values.Single(v => v.Attribute == pub.Attr(Names.Ground, "Marking")).Value);
        Assert.NotSame(sent, ry.Values.Single(v => v.Attribute == spatial).Value); // each federate has its own copy
        Assert.NotSame(rx.Tag, ry.Tag);
    }

    [Fact]
    public void Real_RPR_encodings_pass_through_byte_for_byte()
    {
        var rig = new Rig();
        var pub = rig.Join("Pub");
        var sub = rig.Join("Sub");
        pub.Publish(Names.Ground, "Spatial", "EntityType");
        sub.Subscribe(Names.BaseEntity, "Spatial", "EntityType");
        var obj = pub.Register(Names.Ground);
        var spatial = RprFom2.EncodeSpatial(new SpatialVariantStruct(
            DeadReckoningAlgorithmEnum8.DRM_FPW, new WorldLocationStruct(1, 2, 3), false, new OrientationStruct(0.1f, 0.2f, 0.3f),
            new VelocityVectorStruct(4, 5, 6), default, default));
        var type = RprFom2.EncodeEntityType(new EntityTypeStruct(1, 1, 225, 1, 1, 3, 0));

        pub.Update(obj, Names.Ground, ("Spatial", spatial), ("EntityType", type));
        sub.Evoke();

        var values = sub.Recorder.Of<Reflected>().Single().Values;
        Assert.Equal(type, values.Single(v => v.Attribute == sub.Attr(Names.BaseEntity, "EntityType")).Value);
        Assert.Equal(spatial, values.Single(v => v.Attribute == sub.Attr(Names.BaseEntity, "Spatial")).Value);
    }

    // --- delete ---

    [Fact]
    public void Delete_needs_the_privilege_and_goes_to_every_discoverer_with_the_tag()
    {
        var rig = new Rig();
        var owner = rig.Join("Owner");
        var d1 = rig.Join("D1");
        var d2 = rig.Join("D2");
        var blind = rig.Join("Blind");
        owner.Publish(Names.Ground, "EntityType");
        d1.Subscribe(Names.Physical, "EntityType");
        d2.Subscribe(Names.BaseEntity, "EntityType");
        var obj = owner.Register(Names.Ground);
        d1.Evoke();
        d2.Evoke();
        d1.Clear();
        d2.Clear();

        Assert.Throws<DeletePrivilegeNotHeld>(() => d1.Rti.DeleteObjectInstance(obj, []));
        Assert.Throws<ObjectInstanceNotKnown>(() => blind.Rti.DeleteObjectInstance(obj, []));
        Assert.Throws<ObjectInstanceNotKnown>(() => owner.Rti.DeleteObjectInstance(new ObjectInstanceHandle(55), []));
        owner.Rti.DeleteObjectInstance(obj, Names.Bytes(4, 5));

        d1.Evoke();
        d2.Evoke();
        blind.Evoke();
        owner.Evoke();
        Assert.Equal(obj, d1.Recorder.Of<Removed>().Single().Object);
        Assert.Equal(Names.Bytes(4, 5), d1.Recorder.Of<Removed>().Single().Tag);
        Assert.Equal(Names.Bytes(4, 5), d2.Recorder.Of<Removed>().Single().Tag);
        Assert.Empty(blind.Events);
        Assert.Empty(owner.Events); // the deleter is not told
        Assert.Throws<ObjectInstanceNotKnown>(() => owner.Rti.DeleteObjectInstance(obj, []));
        Assert.Throws<ObjectInstanceNotKnown>(() => owner.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1))));
        var late = rig.Join("Late");
        late.Subscribe(Names.Physical, "EntityType");
        Assert.Equal(0, late.Evoke()); // a deleted instance is not discovered
    }

    // --- interactions ---

    private static Rig InteractionRig() => Rig.With(
        hub => hub.RegisterModule("IA", new FomModelBuilder()
            .InteractionClass("HLAinteractionRoot.I1", ("a", "T"))
            .InteractionClass("HLAinteractionRoot.I1.I2", ("b", "T"))
            .InteractionClass("HLAinteractionRoot.I1.I2.I3", ("c", "T"))
            .InteractionClass("HLAinteractionRoot.Other", ("z", "T"))
            .Build()),
        RprFom2.ModuleName, "IA");

    private const string I1 = "HLAinteractionRoot.I1";
    private const string I2 = "HLAinteractionRoot.I1.I2";
    private const string I3 = "HLAinteractionRoot.I1.I2.I3";

    private static ParameterHandleValueMap Params(Fed f, string cls, params (string Name, int Value)[] values)
    {
        var map = new ParameterHandleValueMap();
        foreach (var (name, value) in values)
        {
            map.Set(f.Param(cls, name), Names.Bytes(value));
        }
        return map;
    }

    [Fact]
    public void An_interaction_is_received_as_the_closest_subscribed_class_with_only_that_classs_parameters()
    {
        var rig = InteractionRig();
        var sender = rig.Join("Sender");
        var asI1 = rig.Join("AsI1");
        var asI2 = rig.Join("AsI2");
        var asI3 = rig.Join("AsI3");
        var mixed = rig.Join("Mixed");
        var unrelated = rig.Join("Unrelated");
        sender.Rti.PublishInteractionClass(sender.Interaction(I3));
        asI1.Rti.SubscribeInteractionClass(asI1.Interaction(I1));
        asI2.Rti.SubscribeInteractionClass(asI2.Interaction(I2));
        asI3.Rti.SubscribeInteractionClass(asI3.Interaction(I3));
        mixed.Rti.SubscribeInteractionClass(mixed.Interaction(I1));
        mixed.Rti.SubscribeInteractionClass(mixed.Interaction(I2));
        unrelated.Rti.SubscribeInteractionClass(unrelated.Interaction("HLAinteractionRoot.Other"));

        sender.Rti.SendInteraction(sender.Interaction(I3), Params(sender, I3, ("a", 1), ("b", 2), ("c", 3)), Names.Bytes(6));

        foreach (var f in new[] { asI1, asI2, asI3, mixed, unrelated })
        {
            f.Evoke();
        }
        Interaction Got(Fed f) => f.Recorder.Of<Interaction>().Single();
        Assert.Equal(asI1.Interaction(I1), Got(asI1).Class);
        Assert.Equal(new[] { "a" }, ParameterNames(asI1, Got(asI1)));
        Assert.Equal(asI2.Interaction(I2), Got(asI2).Class);
        Assert.Equal(new[] { "a", "b" }, ParameterNames(asI2, Got(asI2)));
        Assert.Equal(asI3.Interaction(I3), Got(asI3).Class);
        Assert.Equal(new[] { "a", "b", "c" }, ParameterNames(asI3, Got(asI3)));
        Assert.Equal(mixed.Interaction(I2), Got(mixed).Class); // I2 is closer to I3 than I1
        Assert.Equal(new[] { "a", "b" }, ParameterNames(mixed, Got(mixed)));
        Assert.Equal(Names.Bytes(2), Got(asI2).Values.Single(v => v.Parameter == asI2.Param(I2, "b")).Value);
        Assert.Equal(Names.Bytes(6), Got(asI2).Tag);
        Assert.Empty(unrelated.Events);
    }

    private static string[] ParameterNames(Fed f, Interaction i)
    {
        var all = new[] { "a", "b", "c" };
        return all.Where(n => i.Values.Any(v => v.Parameter == f.Param(I3, n))).ToArray();
    }

    [Fact]
    public void A_sender_never_receives_its_own_interaction_and_one_published_class_is_not_another()
    {
        var rig = InteractionRig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        a.Rti.PublishInteractionClass(a.Interaction(I2));
        a.Rti.SubscribeInteractionClass(a.Interaction(I1));
        b.Rti.SubscribeInteractionClass(b.Interaction(I1));

        a.Rti.SendInteraction(a.Interaction(I2), Params(a, I2, ("a", 1)), []);

        Assert.Equal(0, a.Evoke());
        Assert.Equal(1, b.Evoke());
        // a published I2 only: sending the superclass or the subclass is not allowed
        Assert.Throws<InteractionClassNotPublished>(() => a.Rti.SendInteraction(a.Interaction(I1), Params(a, I1, ("a", 1)), []));
        Assert.Throws<InteractionClassNotPublished>(() => a.Rti.SendInteraction(a.Interaction(I3), Params(a, I3, ("a", 1)), []));
    }

    [Fact]
    public void Interaction_parameters_must_belong_to_the_sent_class_and_a_failed_send_delivers_nothing()
    {
        var rig = InteractionRig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        a.Rti.PublishInteractionClass(a.Interaction(I1));
        b.Rti.SubscribeInteractionClass(b.Interaction(I1));
        var bad = new ParameterHandleValueMap();
        bad.Set(a.Param(I1, "a"), Names.Bytes(1));
        bad.Set(a.Param(I3, "c"), Names.Bytes(2)); // declared by a subclass of the sent class
        var bogus = new ParameterHandleValueMap();
        bogus.Set(new ParameterHandle(9999), Names.Bytes(1));

        Assert.Throws<InteractionParameterNotDefined>(() => a.Rti.SendInteraction(a.Interaction(I1), bad, []));
        Assert.Throws<InteractionParameterNotDefined>(() => a.Rti.SendInteraction(a.Interaction(I1), bogus, []));

        Assert.Equal(0, b.Evoke());
        a.Rti.SendInteraction(a.Interaction(I1), new ParameterHandleValueMap(), []); // no parameters at all is a valid interaction
        Assert.Equal(1, b.Evoke());
        Assert.Empty(b.Recorder.Of<Interaction>().Single().Values);
    }

    [Fact]
    public void RPR_WeaponFire_and_MunitionDetonation_are_independent_classes_with_their_own_parameters()
    {
        var rig = new Rig();
        var shooter = rig.Join("Shooter");
        var watcher = rig.Join("Watcher");
        shooter.Rti.PublishInteractionClass(shooter.Interaction(Names.Fire));
        shooter.Rti.PublishInteractionClass(shooter.Interaction(Names.Detonation));
        watcher.Rti.SubscribeInteractionClass(watcher.Interaction(Names.Detonation));
        var fireMap = new ParameterHandleValueMap();
        fireMap.Set(shooter.Param(Names.Fire, "EventIdentifier"), RprFom2.EncodeEventIdentifier(new EventIdentifierStruct(1, "HLAobject_1")));
        var wrong = new ParameterHandleValueMap();
        wrong.Set(shooter.Param(Names.Detonation, "DetonationResultCode"), Names.Bytes(1)); // a Detonation parameter on a Fire

        shooter.Rti.SendInteraction(shooter.Interaction(Names.Fire), fireMap, []);
        Assert.Throws<InteractionParameterNotDefined>(() => shooter.Rti.SendInteraction(shooter.Interaction(Names.Fire), wrong, []));

        Assert.Equal(0, watcher.Evoke()); // subscribed to Detonation only
    }

    // --- attribute value requests ---

    [Fact]
    public void A_value_request_goes_to_the_owner_not_the_requester_filtered_to_what_the_owner_owns()
    {
        var rig = new Rig();
        var owner = rig.Join("Owner");
        var req = rig.Join("Req");
        var third = rig.Join("Third");
        owner.Publish(Names.Ground, "EntityType", "Marking");
        req.Subscribe(Names.Physical, "EntityType", "Marking", "ForceIdentifier");
        third.Subscribe(Names.Physical, "EntityType");
        var obj = owner.Register(Names.Ground);
        foreach (var f in new[] { owner, req, third })
        {
            f.Evoke();
            f.Clear();
        }
        var all = req.Attrs(Names.Physical, "EntityType", "Marking", "ForceIdentifier");

        req.Rti.RequestAttributeValueUpdate(obj, all, Names.Bytes(3));

        Assert.Equal(0, req.Evoke());
        Assert.Equal(0, third.Evoke());
        Assert.Equal(1, owner.Evoke());
        var provide = owner.Recorder.Of<Provide>().Single();
        Assert.Equal(obj, provide.Object);
        Assert.Equal(
            new[] { owner.Attr(Names.Physical, "EntityType"), owner.Attr(Names.Physical, "Marking") },
            provide.Attributes); // ForceIdentifier is not owned by anyone
        Assert.Equal(Names.Bytes(3), provide.Tag);

        // nothing owned among the requested attributes: no callback at all
        req.Rti.RequestAttributeValueUpdate(obj, req.Attrs(Names.Physical, "ForceIdentifier"), []);
        req.Rti.RequestAttributeValueUpdate(obj, new AttributeHandleSet(), []);
        Assert.Equal(0, owner.Evoke());
        // the owner asking about its own instance is not told about itself
        owner.Rti.RequestAttributeValueUpdate(obj, owner.Attrs(Names.Ground, "EntityType"), []);
        Assert.Equal(0, owner.Evoke());
    }

    [Fact]
    public void A_class_wide_value_request_covers_the_instances_of_that_class_and_subclasses_the_requester_knows()
    {
        var rig = new Rig();
        var owner = rig.Join("Owner");
        var req = rig.Join("Req");
        owner.Publish(Names.Ground, "EntityType");
        owner.Publish(Names.Human, "EntityType");
        owner.Publish(Names.Aircraft, "EntityType");
        req.Subscribe(Names.Platform, "EntityType"); // knows ground vehicles and aircraft, not humans
        var g = owner.Register(Names.Ground);
        owner.Register(Names.Human); // a human: not known to the requester
        var ac = owner.Register(Names.Aircraft);
        owner.Evoke();
        req.Evoke();
        req.Clear();

        req.Rti.RequestAttributeValueUpdate(req.Class(Names.Physical), req.Attrs(Names.Physical, "EntityType"), []);
        owner.Evoke();
        Assert.Equal(new[] { g, ac }, owner.Recorder.Of<Provide>().Select(p => p.Object).ToArray());

        owner.Clear();
        req.Rti.RequestAttributeValueUpdate(req.Class(Names.Aircraft), req.Attrs(Names.Aircraft, "EntityType"), []);
        owner.Evoke();
        Assert.Equal(new[] { ac }, owner.Recorder.Of<Provide>().Select(p => p.Object).ToArray());
        Assert.Throws<ObjectClassNotDefined>(() => req.Rti.RequestAttributeValueUpdate(new ObjectClassHandle(9999), new AttributeHandleSet(), []));
        Assert.Throws<AttributeNotDefined>(() =>
            req.Rti.RequestAttributeValueUpdate(req.Class(Names.Physical), new AttributeHandleSet([req.Attr(Names.Platform, "HeadLightsOn")]), []));
    }

    [Fact]
    public void A_value_request_for_an_unknown_instance_or_undefined_attribute_is_refused()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        a.Publish(Names.Ground, "EntityType");
        var obj = a.Register(Names.Ground);

        Assert.Throws<ObjectInstanceNotKnown>(() => b.Rti.RequestAttributeValueUpdate(obj, new AttributeHandleSet(), []));
        Assert.Throws<ObjectInstanceNotKnown>(() => a.Rti.RequestAttributeValueUpdate(new ObjectInstanceHandle(42), new AttributeHandleSet(), []));
        Assert.Throws<AttributeNotDefined>(() =>
            a.Rti.RequestAttributeValueUpdate(obj, new AttributeHandleSet([a.Attr(Names.Lifeform, "StanceCode")]), []));
        Assert.Equal(0, b.Rti.PendingCallbackCount);
    }
}
