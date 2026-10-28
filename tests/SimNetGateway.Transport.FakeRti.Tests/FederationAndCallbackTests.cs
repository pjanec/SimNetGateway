using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.FakeRti.Tests;

/// <summary>DD-008 3.6 rows "Federations", "Callbacks" and the handle / trace test hooks, one behaviour per test.</summary>
public class FederationAndCallbackTests
{
    // --- federations ---

    [Fact]
    public void Creating_an_existing_federation_is_refused_and_changes_nothing()
    {
        var rig = new Rig();
        var fed = rig.Join("A");

        Assert.Throws<FederationExecutionAlreadyExists>(() => rig.Admin.CreateFederationExecution(Rig.FederationName, [RprFom2.ModuleName]));

        Assert.Equal(1, rig.Hub.JoinedFederateCount(Rig.FederationName));
        Assert.Equal(new[] { Rig.FederationName }, rig.Hub.FederationNames);
        Assert.True(fed.Class(Names.Physical).Value > 0); // the federation still works
    }

    [Fact]
    public void Unknown_module_is_NameNotFound_and_conflicting_modules_are_InconsistentFdd_and_nothing_is_created()
    {
        var hub = new FakeRtiHub();
        var admin = hub.CreateAmbassador();
        admin.Connect(new Recorder());
        hub.RegisterModule("A", new FomModelBuilder().ObjectClass("HLAobjectRoot.X", ("P", "T1")).Build());
        hub.RegisterModule("B", new FomModelBuilder().ObjectClass("HLAobjectRoot.X", ("P", "T2")).Build());

        Assert.Throws<NameNotFound>(() => admin.CreateFederationExecution("F", ["A", "Nope"]));
        Assert.Throws<InconsistentFdd>(() => admin.CreateFederationExecution("F", ["A", "B"]));
        Assert.Throws<NameNotFound>(() => admin.CreateFederationExecution("F", ["B", "Nope"])); // unknown wins over conflicts

        Assert.Empty(hub.FederationNames);
        admin.CreateFederationExecution("F", ["A", RprFom2.ModuleName, "A"]); // repeated and mixed modules merge
        Assert.Equal(new[] { "F" }, hub.FederationNames);
    }

    [Fact]
    public void Registered_modules_override_the_default_registry_for_that_hub_only()
    {
        var hub = new FakeRtiHub();
        hub.RegisterModule(RprFom2.ModuleName, new FomModelBuilder().ObjectClass("HLAobjectRoot.OnlyHere").Build());
        var admin = hub.CreateAmbassador();
        admin.Connect(new Recorder());
        admin.CreateFederationExecution("F", [RprFom2.ModuleName]);
        var fed = hub.CreateAmbassador();
        fed.Connect(new Recorder());
        fed.JoinFederationExecution("x", "t", "F");

        Assert.Equal(2, fed.GetObjectClassHandle("HLAobjectRoot.OnlyHere").Value);
        Assert.Throws<NameNotFound>(() => fed.GetObjectClassHandle(Names.Physical));
        Assert.NotNull(RprFom2.Model().FindObjectClass(Names.Physical)); // the shared registry is untouched
    }

    [Fact]
    public void Join_gives_sequential_handles_and_refuses_duplicate_names_double_joins_and_missing_federations()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");

        Assert.Equal(new FederateHandle(1), a.Rti.FederateHandle);
        Assert.Equal(new FederateHandle(2), b.Rti.FederateHandle);
        var other = rig.Hub.CreateAmbassador();
        other.Connect(new Recorder());
        Assert.Throws<FederateNameAlreadyInUse>(() => other.JoinFederationExecution("A", "t", Rig.FederationName));
        Assert.Throws<FederationExecutionDoesNotExist>(() => other.JoinFederationExecution("C", "t", "Nowhere"));
        Assert.Throws<FederateAlreadyExecutionMember>(() => a.Rti.JoinFederationExecution("Z", "t", Rig.FederationName));
        Assert.Equal(2, rig.Hub.JoinedFederateCount(Rig.FederationName));
        Assert.Null(other.FederateHandle);
    }

    [Fact]
    public void A_resigned_federate_can_rejoin_with_a_new_handle_and_its_name_is_free_again()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        a.Rti.ResignFederationExecution(ResignAction.NoAction);

        Assert.Null(a.Rti.FederateHandle);
        var handle = a.Rti.JoinFederationExecution("A", "t", Rig.FederationName);
        Assert.Equal(new FederateHandle(2), handle);
    }

    [Fact]
    public void Destroy_needs_every_federate_to_have_resigned()
    {
        var rig = new Rig();
        var a = rig.Join("A");

        Assert.Throws<FederatesCurrentlyJoined>(() => rig.Admin.DestroyFederationExecution(Rig.FederationName));
        Assert.Throws<FederationExecutionDoesNotExist>(() => rig.Admin.DestroyFederationExecution("Nowhere"));
        a.Rti.ResignFederationExecution(ResignAction.DeleteObjectsThenDivest);
        rig.Admin.DestroyFederationExecution(Rig.FederationName);

        Assert.Empty(rig.Hub.FederationNames);
        Assert.Throws<FederationExecutionDoesNotExist>(() => a.Rti.JoinFederationExecution("A", "t", Rig.FederationName));
        Assert.Throws<FederationExecutionDoesNotExist>(() => rig.Admin.DestroyFederationExecution(Rig.FederationName));
        rig.Admin.CreateFederationExecution(Rig.FederationName, [RprFom2.ModuleName]); // the name is free again
    }

    [Fact]
    public void Resign_DeleteObjectsThenDivest_deletes_the_federates_objects_for_their_discoverers_and_frees_its_names()
    {
        var rig = new Rig();
        var owner = rig.Join("Owner");
        var watcher = rig.Join("Watcher");
        var blind = rig.Join("Blind");
        owner.Publish(Names.Ground, "EntityType");
        watcher.Subscribe(Names.Physical, "EntityType");
        owner.Rti.ReserveObjectInstanceName("mine");
        owner.Evoke();
        var named = owner.Rti.RegisterObjectInstance(owner.Class(Names.Ground), "mine");
        var unnamed = owner.Register(Names.Ground);
        watcher.Evoke();
        watcher.Clear();
        owner.Update(named, Names.Ground, ("EntityType", Names.Bytes(1)));
        owner.Rti.ReserveObjectInstanceName("spare");

        owner.Rti.ResignFederationExecution(ResignAction.DeleteObjectsThenDivest);

        Assert.Equal(0, owner.Rti.PendingCallbackCount); // a resigned federate has no callbacks left
        Assert.Equal(0, blind.Evoke());
        watcher.Evoke();
        Assert.Equal(new[] { named, unnamed }, watcher.Recorder.Of<Removed>().Select(r => r.Object).ToArray());
        Assert.Throws<ObjectInstanceNotKnown>(() => watcher.Rti.GetObjectInstanceName(named));
        // the names are free again for everybody
        watcher.Rti.ReserveObjectInstanceName("mine");
        watcher.Rti.ReserveObjectInstanceName("spare");
        watcher.Evoke();
        Assert.Equal(new[] { "mine", "spare" }, watcher.Recorder.Of<NameReserved>().Select(n => n.Name).ToArray());
    }

    [Fact]
    public void Resign_UnconditionallyDivest_leaves_the_objects_without_an_owner()
    {
        var rig = new Rig();
        var owner = rig.Join("Owner");
        var watcher = rig.Join("Watcher");
        owner.Publish(Names.Ground, "EntityType");
        watcher.Subscribe(Names.Physical, "EntityType");
        var obj = owner.Register(Names.Ground);
        watcher.Evoke();
        watcher.Clear();

        owner.Rti.ResignFederationExecution(ResignAction.UnconditionallyDivestAttributes);

        watcher.Evoke();
        Assert.Empty(watcher.Events); // not deleted
        Assert.Equal("HLAobject_1", watcher.Rti.GetObjectInstanceName(obj));
        Assert.Throws<AttributeNotOwned>(() => watcher.Update(obj, Names.Physical, ("EntityType", Names.Bytes(1))));
        Assert.Throws<DeletePrivilegeNotHeld>(() => watcher.Rti.DeleteObjectInstance(obj, Array.Empty<byte>()));
        // a federate that subscribes later still discovers it
        var late = rig.Join("Late");
        late.Subscribe(Names.Physical, "EntityType");
        late.Evoke();
        Assert.Single(late.Recorder.Of<Discovered>());
    }

    [Theory]
    [InlineData(ResignAction.NoAction)]
    [InlineData(ResignAction.CancelPendingOwnershipAcquisitions)]
    public void Resign_without_divesting_while_owning_is_refused_and_changes_nothing(ResignAction action)
    {
        var rig = new Rig();
        var owner = rig.Join("Owner");
        var watcher = rig.Join("Watcher");
        owner.Publish(Names.Ground, "EntityType");
        watcher.Subscribe(Names.Physical, "EntityType");
        var obj = owner.Register(Names.Ground);
        watcher.Evoke();
        watcher.Clear();

        Assert.Throws<FederateOwnsAttributes>(() => owner.Rti.ResignFederationExecution(action));

        Assert.NotNull(owner.Rti.FederateHandle);
        owner.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)));
        Assert.Equal(1, watcher.Evoke());
        owner.Rti.DeleteObjectInstance(obj, Array.Empty<byte>());
        owner.Rti.ResignFederationExecution(action); // nothing owned any more
    }

    // --- connection state ---

    [Fact]
    public void Services_before_connect_are_NotConnected_and_connecting_twice_is_AlreadyConnected()
    {
        var hub = new FakeRtiHub();
        var rti = hub.CreateAmbassador();
        var cls = new ObjectClassHandle(1);
        var ia = new InteractionClassHandle(1);
        var obj = new ObjectInstanceHandle(1);
        var calls = new Action[]
        {
            () => rti.CreateFederationExecution("F", [RprFom2.ModuleName]),
            () => rti.JoinFederationExecution("x", "t", "F"),
            () => rti.DestroyFederationExecution("F"),
            () => rti.ResignFederationExecution(ResignAction.NoAction),
            () => rti.GetObjectClassHandle("HLAobjectRoot"),
            () => rti.GetAttributeHandle(cls, "x"),
            () => rti.GetInteractionClassHandle("HLAinteractionRoot"),
            () => rti.GetParameterHandle(ia, "x"),
            () => rti.PublishObjectClassAttributes(cls, new AttributeHandleSet()),
            () => rti.SubscribeObjectClassAttributes(cls, new AttributeHandleSet()),
            () => rti.UnpublishObjectClass(cls),
            () => rti.UnsubscribeObjectClass(cls),
            () => rti.PublishInteractionClass(ia),
            () => rti.SubscribeInteractionClass(ia),
            () => rti.UnpublishInteractionClass(ia),
            () => rti.UnsubscribeInteractionClass(ia),
            () => rti.ReserveObjectInstanceName("n"),
            () => rti.RegisterObjectInstance(cls),
            () => rti.RegisterObjectInstance(cls, "n"),
            () => rti.GetObjectInstanceName(obj),
            () => rti.UpdateAttributeValues(obj, new AttributeHandleValueMap(), []),
            () => rti.DeleteObjectInstance(obj, []),
            () => rti.SendInteraction(ia, new ParameterHandleValueMap(), []),
            () => rti.RequestAttributeValueUpdate(obj, new AttributeHandleSet(), []),
            () => rti.RequestAttributeValueUpdate(cls, new AttributeHandleSet(), []),
            () => rti.EvokeCallback(0),
            () => rti.EvokeMultipleCallbacks(0, 0),
        };
        foreach (var call in calls)
        {
            Assert.Throws<NotConnected>(call);
        }

        rti.Connect(new Recorder());
        Assert.True(rti.IsConnected);
        Assert.Throws<AlreadyConnected>(() => rti.Connect(new Recorder()));
    }

    [Fact]
    public void Services_that_need_membership_are_FederateNotExecutionMember_before_join()
    {
        var rig = new Rig();
        var rti = rig.Hub.CreateAmbassador();
        rti.Connect(new Recorder());
        var cls = new ObjectClassHandle(1);
        var ia = new InteractionClassHandle(1);
        var obj = new ObjectInstanceHandle(1);
        var calls = new Action[]
        {
            () => rti.ResignFederationExecution(ResignAction.NoAction),
            () => rti.GetObjectClassHandle("HLAobjectRoot"),
            () => rti.GetAttributeHandle(cls, "x"),
            () => rti.GetInteractionClassHandle("HLAinteractionRoot"),
            () => rti.GetParameterHandle(ia, "x"),
            () => rti.PublishObjectClassAttributes(cls, new AttributeHandleSet()),
            () => rti.SubscribeObjectClassAttributes(cls, new AttributeHandleSet()),
            () => rti.UnpublishObjectClass(cls),
            () => rti.UnsubscribeObjectClass(cls),
            () => rti.PublishInteractionClass(ia),
            () => rti.SubscribeInteractionClass(ia),
            () => rti.UnpublishInteractionClass(ia),
            () => rti.UnsubscribeInteractionClass(ia),
            () => rti.ReserveObjectInstanceName("n"),
            () => rti.RegisterObjectInstance(cls),
            () => rti.RegisterObjectInstance(cls, "n"),
            () => rti.GetObjectInstanceName(obj),
            () => rti.UpdateAttributeValues(obj, new AttributeHandleValueMap(), []),
            () => rti.DeleteObjectInstance(obj, []),
            () => rti.SendInteraction(ia, new ParameterHandleValueMap(), []),
            () => rti.RequestAttributeValueUpdate(obj, new AttributeHandleSet(), []),
            () => rti.RequestAttributeValueUpdate(cls, new AttributeHandleSet(), []),
        };
        foreach (var call in calls)
        {
            Assert.Throws<FederateNotExecutionMember>(call);
        }
        Assert.False(rti.EvokeCallback(0)); // evoking needs only a connection
        Assert.False(rti.EvokeMultipleCallbacks(0, 0));
    }

    // --- callbacks ---

    [Fact]
    public void Nothing_is_delivered_outside_an_evoke()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        b.Subscribe(Names.Physical, "EntityType");
        a.Publish(Names.Ground, "EntityType");
        a.Register(Names.Ground);
        var obj = new ObjectInstanceHandle(1);
        a.Update(obj, Names.Ground, ("EntityType", Names.Bytes(9)));

        Assert.Empty(b.Events);
        Assert.Equal(2, b.Rti.PendingCallbackCount); // discover + reflect
        Assert.Equal(2, b.Evoke());
        Assert.Equal(0, b.Rti.PendingCallbackCount);
    }

    [Fact]
    public void EvokeCallback_delivers_one_in_order_and_reports_whether_more_are_pending()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        b.Subscribe(Names.Physical, "EntityType");
        a.Publish(Names.Ground, "EntityType");
        var obj = a.Register(Names.Ground);
        a.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)));
        a.Update(obj, Names.Ground, ("EntityType", Names.Bytes(2)));

        Assert.True(b.Rti.EvokeCallback(0));
        Assert.IsType<Discovered>(Assert.Single(b.Events));
        Assert.True(b.Rti.EvokeCallback(0));
        Assert.False(b.Rti.EvokeCallback(0)); // the third and last
        Assert.False(b.Rti.EvokeCallback(0)); // nothing left
        Assert.Equal(new[] { 1, 2 }, b.Recorder.Of<Reflected>().Select(r => (int)r.Values[0].Value[0]).ToArray());
    }

    [Fact]
    public void EvokeMultipleCallbacks_delivers_what_was_queued_at_call_time_only()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        b.Subscribe(Names.Physical, "EntityType");
        a.Publish(Names.Ground, "EntityType");
        var obj = a.Register(Names.Ground);
        // while b receives its discovery, a callback of b triggers another update towards b (through a's ambassador)
        b.Recorder.InsideCallback = () =>
        {
            b.Recorder.InsideCallback = null;
            a.Update(obj, Names.Ground, ("EntityType", Names.Bytes(7)));
        };

        var more = b.Rti.EvokeMultipleCallbacks(0, 0);

        Assert.True(more); // the update queued during delivery waits for the next evoke
        Assert.Single(b.Events);
        Assert.False(b.Rti.EvokeMultipleCallbacks(0, 0));
        Assert.Equal(2, b.Events.Count);
        Assert.IsType<Reflected>(b.Events[1]);
    }

    [Fact]
    public void Services_called_from_inside_a_callback_are_refused_and_have_no_effect()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        b.Subscribe(Names.Physical, "EntityType");
        a.Publish(Names.Ground, "EntityType");
        b.Publish(Names.Ground, "EntityType");
        a.Register(Names.Ground);
        var cls = b.Class(Names.Ground);
        var attempts = new List<Action>
        {
            () => b.Rti.RegisterObjectInstance(cls),
            () => b.Rti.GetObjectClassHandle(Names.Ground),
            () => b.Rti.EvokeCallback(0),
            () => b.Rti.EvokeMultipleCallbacks(0, 0),
            () => b.Rti.ReserveObjectInstanceName("inside"),
            () => b.Rti.ResignFederationExecution(ResignAction.DeleteObjects),
            () => b.Rti.Connect(new Recorder()),
            () => b.Rti.CreateFederationExecution("X", [RprFom2.ModuleName]),
            () => b.Rti.DestroyFederationExecution(Rig.FederationName),
        };
        var refused = 0;
        b.Recorder.InsideCallback = () =>
        {
            foreach (var attempt in attempts)
            {
                Assert.Throws<CallNotAllowedFromWithinCallback>(attempt);
                refused++;
            }
        };

        b.Evoke();

        Assert.Equal(attempts.Count, refused);
        Assert.NotNull(b.Rti.FederateHandle); // the resign did not happen
        Assert.Equal(new[] { "TestFederation" }, rig.Hub.FederationNames);
        b.Recorder.InsideCallback = null;
        var own = b.Rti.RegisterObjectInstance(cls); // allowed again once the callback returned
        Assert.Equal(2, own.Value); // the attempt inside the callback registered nothing
        Assert.Equal(0, b.Rti.PendingCallbackCount); // and reserved no name
    }

    [Fact]
    public void A_callback_that_throws_is_consumed_and_the_ambassador_stays_usable()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        b.Subscribe(Names.Physical, "EntityType");
        a.Publish(Names.Ground, "EntityType");
        var obj = a.Register(Names.Ground);
        a.Update(obj, Names.Ground, ("EntityType", Names.Bytes(1)));
        b.Recorder.InsideCallback = () => throw new InvalidOperationException("federate bug");

        Assert.Throws<InvalidOperationException>(() => b.Rti.EvokeCallback(0));

        b.Recorder.InsideCallback = null;
        Assert.Equal(1, b.Rti.PendingCallbackCount); // the failed one is gone, the next is still queued
        Assert.NotNull(b.Rti.GetObjectClassHandle(Names.Ground).Value.ToString());
        Assert.Equal(1, b.Evoke());
        Assert.IsType<Reflected>(b.Events[0]);
    }

    // --- handles and trace ---

    [Fact]
    public void Handles_are_deterministic_inherited_attributes_share_the_declaring_classes_handle()
    {
        var rig = new Rig();
        var a = rig.Join("A");

        Assert.Equal(1, a.Class("HLAobjectRoot").Value);
        Assert.Equal(2, a.Class(Names.BaseEntity).Value);
        Assert.Equal(3, a.Class(Names.Physical).Value);
        Assert.Equal(1, a.Rti.GetAttributeHandle(a.Class("HLAobjectRoot"), FomModel.PrivilegeToDeleteObjectName).Value);
        var viaPhysical = a.Attr(Names.Physical, "Marking");
        Assert.Equal(viaPhysical, a.Attr(Names.Ground, "Marking"));
        Assert.Equal(viaPhysical, a.Attr(Names.Human, "Marking"));
        Assert.Equal(a.Attr(Names.BaseEntity, "Spatial"), a.Attr(Names.Ground, "Spatial"));
        Assert.NotEqual(a.Attr(Names.Physical, "ForceIdentifier"), a.Attr(Names.Aggregate, "ForceIdentifier")); // different declaring classes
        Assert.Equal(1, a.Interaction("HLAinteractionRoot").Value);
        Assert.Equal(2, a.Interaction(Names.Fire).Value);

        // the same call sequence on a second hub gives the same handles and the same trace
        string[] Script(out List<int> handles)
        {
            var r = new Rig();
            var x = r.Join("A");
            var y = r.Join("B");
            y.Subscribe(Names.Physical, "EntityType", "Spatial");
            x.Publish(Names.Ground, "EntityType", "Spatial");
            handles = [x.Register(Names.Ground).Value, x.Register(Names.Ground).Value, x.Class(Names.Ground).Value];
            x.Update(new ObjectInstanceHandle(1), Names.Ground, ("Spatial", Names.Bytes(1, 2, 3)));
            y.Evoke();
            return r.Hub.Trace.ToArray();
        }
        var first = Script(out var h1);
        var second = Script(out var h2);
        Assert.Equal(h1, h2);
        Assert.Equal(first, second);
        Assert.Equal(new[] { 1, 2 }, h1.Take(2));
    }

    [Fact]
    public void Handle_lookups_report_unknown_names_and_invalid_handles()
    {
        var rig = new Rig();
        var a = rig.Join("A");

        Assert.Throws<NameNotFound>(() => a.Rti.GetObjectClassHandle("HLAobjectRoot.Nope"));
        Assert.Throws<NameNotFound>(() => a.Rti.GetObjectClassHandle("PhysicalEntity")); // names are fully qualified
        Assert.Throws<NameNotFound>(() => a.Rti.GetAttributeHandle(a.Class(Names.Platform), "StanceCode"));
        Assert.Throws<NameNotFound>(() => a.Rti.GetInteractionClassHandle("HLAinteractionRoot.Nope"));
        Assert.Throws<NameNotFound>(() => a.Rti.GetParameterHandle(a.Interaction(Names.Fire), "Nope"));
        foreach (var bad in new[] { 0, -1, 9999 })
        {
            Assert.Throws<InvalidObjectClassHandle>(() => a.Rti.GetAttributeHandle(new ObjectClassHandle(bad), "x"));
            Assert.Throws<InvalidInteractionClassHandle>(() => a.Rti.GetParameterHandle(new InteractionClassHandle(bad), "x"));
        }
    }

    [Fact]
    public void Trace_lists_every_call_exception_and_callback_in_order()
    {
        var rig = new Rig();
        rig.Hub.ClearTrace();
        var a = rig.Join("A"); // ambassador 2 (the admin is 1)
        var b = rig.Join("B");
        b.Subscribe(Names.Physical, "EntityType");
        a.Publish(Names.Ground, "EntityType");
        Assert.Throws<ObjectClassNotPublished>(() => a.Register(Names.Aircraft));
        a.Register(Names.Ground);
        b.Evoke();

        var trace = rig.Hub.Trace;
        Assert.Contains("A2 call Connect", trace);
        Assert.Contains(trace, l => l.StartsWith("A2 call JoinFederationExecution(A, test, TestFederation)", StringComparison.Ordinal));
        var failed = trace.ToList().FindIndex(l => l.StartsWith("A2 call RegisterObjectInstance(", StringComparison.Ordinal));
        Assert.Equal("A2 throws ObjectClassNotPublished", trace[failed + 1]);
        var discover = trace.ToList().FindIndex(l => l.StartsWith("A3 callback DiscoverObjectInstance(ObjectInstance#1,", StringComparison.Ordinal));
        Assert.True(discover > failed);
        Assert.Contains(trace, l => l.StartsWith("A3 call EvokeMultipleCallbacks", StringComparison.Ordinal));
        rig.Hub.ClearTrace();
        Assert.Empty(rig.Hub.Trace);
    }
}
