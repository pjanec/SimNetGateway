using SimNetGateway.Showcase;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;
using SimNetGateway.Transport.FakeRti;

namespace SimNetGateway.Transport.HlaRpr.Tests;

/// <summary>Join, publish and subscribe (DD-008 section 5.1) and the pump discipline of section 6.</summary>
public class LifecycleTests
{
    private static RprHlaConnection Connection(FakeRtiHub hub, RprHlaSettings settings, string name = "rpr") =>
        new(hub.CreateAmbassador(), settings, new ManualClock(), new SourceId(1), name);

    [Fact]
    public void Start_creates_the_federation_joins_it_and_publishes_and_subscribes_what_section_3_5_names()
    {
        var hub = new FakeRtiHub();
        var connection = Connection(hub, new RprHlaSettings { FederationName = "F", FederateName = "G" });

        connection.Start();

        Assert.Equal(["F"], hub.FederationNames);
        Assert.Equal(1, hub.JoinedFederateCount("F"));
        var trace = string.Join("\n", hub.Trace);
        Assert.Equal(RprHlaClasses.Published.Count, hub.Trace.Count(l => l.Contains("call PublishObjectClassAttributes")));
        Assert.Equal(2, hub.Trace.Count(l => l.Contains("call SubscribeObjectClassAttributes"))); // PhysicalEntity and AggregateEntity
        Assert.Equal(2, hub.Trace.Count(l => l.Contains("call PublishInteractionClass")));
        Assert.Equal(2, hub.Trace.Count(l => l.Contains("call SubscribeInteractionClass")));
        Assert.DoesNotContain("ReserveObjectInstanceName", trace); // RTI-generated names (R-104)
        Assert.True(connection.IsStarted);
    }

    [Fact]
    public void An_existing_federation_is_joined_not_an_error_and_without_CreateIfMissing_a_missing_one_is()
    {
        var hub = new FakeRtiHub();
        Connection(hub, new RprHlaSettings { FederationName = "F", FederateName = "A" }).Start();

        Connection(hub, new RprHlaSettings { FederationName = "F", FederateName = "B", CreateIfMissing = false }).Start(); // joins
        Assert.Equal(2, hub.JoinedFederateCount("F"));
        Assert.Throws<FederationExecutionDoesNotExist>(
            () => Connection(hub, new RprHlaSettings { FederationName = "Missing", FederateName = "C", CreateIfMissing = false }).Start());
        Assert.Throws<FederateNameAlreadyInUse>(
            () => Connection(hub, new RprHlaSettings { FederationName = "F", FederateName = "A" }).Start());
    }

    [Fact]
    public void Starting_twice_is_refused_and_a_connection_that_never_started_does_nothing()
    {
        var hub = new FakeRtiHub();
        var connection = Connection(hub, new RprHlaSettings());
        connection.Pump(); // before Start: nothing happens
        connection.Send(new RprRegisterObject(1, default) as IRprMessage);
        Assert.Empty(hub.Trace);
        Assert.Equal(1, connection.RejectedOutbound);

        connection.Start();
        Assert.Throws<InvalidOperationException>(connection.Start);
    }

    [Fact]
    public void Callbacks_only_enqueue_and_a_pump_is_the_only_place_the_transport_calls_the_RTI_in_reaction()
    {
        var rig = new Rig();
        rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
        rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
        rig.Hub.ClearTrace();

        rig.Gateway.Pump();

        // The fake RTI throws CallNotAllowedFromWithinCallback for any service used inside a callback; the discovery was handled
        // after the evoke, so nothing threw and the value request is in the trace after the callback line.
        Assert.Equal(0, rig.Gateway.CallbackErrors);
        Assert.Equal(0, rig.Gateway.SendErrors);
        var lines = rig.Hub.Trace.ToList();
        int callback = lines.FindIndex(l => l.Contains("callback DiscoverObjectInstance"));
        int request = lines.FindIndex(l => l.Contains("call RequestAttributeValueUpdate"));
        Assert.True(callback >= 0 && request > callback, string.Join("\n", lines));
        Assert.DoesNotContain(lines, l => l.Contains("throws"));
    }

    [Fact]
    public void The_inbound_channel_hands_each_message_to_the_engine_with_its_own_type_within_the_budget()
    {
        var rig = new Rig();
        rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
        var obj = rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
        rig.Settle();
        rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, new ScriptedRprFederate.Attributes().Marking("A"));
        rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, new ScriptedRprFederate.Attributes().Marking("B"));
        rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, new ScriptedRprFederate.Attributes().Marking("C"));
        rig.Gateway.Pump();
        Assert.Equal(3, rig.Gateway.PendingInbound);

        Assert.Equal(2, rig.Gateway.Drain(rig.Inbox, 2));
        Assert.Equal(1, rig.Gateway.PendingInbound);
        Assert.Equal(1, rig.Gateway.Drain(rig.Inbox, 10));
        Assert.Equal(["A", "B", "C"], rig.Inbox.Of<RprObjectUpdate>().Select(u => u.Marking)); // arrival order
        Assert.Equal(rig.Gateway.Source, new SourceId(7));
        Assert.Equal("rpr", rig.Gateway.Name);
    }

    [Fact]
    public void Two_gateways_in_one_federation_see_each_others_objects_and_not_their_own()
    {
        var hub = new FakeRtiHub();
        var clock = new ManualClock();
        var a = new RprHlaConnection(hub.CreateAmbassador(), new RprHlaSettings { FederationName = "F", FederateName = "A" }, clock, new SourceId(1));
        var b = new RprHlaConnection(hub.CreateAmbassador(), new RprHlaSettings { FederationName = "F", FederateName = "B" }, clock, new SourceId(2));
        a.Start();
        b.Start();
        var inboxA = new CollectingSink();
        var inboxB = new CollectingSink();
        IRprMessage register = new RprRegisterObject(1, Gen.Tank);
        a.Send(in register);
        a.Pump();
        a.Drain(inboxA, 10);
        var handleA = inboxA.Of<RprObjectRegistered>().Single().Handle;
        a.Send(new RprObjectUpdate(handleA) { Marking = "From-A", WorldLocation = new WorldLocation(1, 2, 3) } as IRprMessage);

        for (int i = 0; i < 4; i++)
        {
            b.Pump();
            b.Drain(inboxB, 10);
            a.Pump();
            a.Drain(inboxA, 10);
        }

        // B discovered A's object, asked for its values; A's gateway gets the RprUpdateRequested (a real gateway answers with a full update)
        Assert.Equal([new RprUpdateRequested(handleA)], inboxA.Of<RprUpdateRequested>());
        Assert.Empty(inboxA.Of<RprObjectUpdate>()); // never its own object back
        Assert.True(b.TryGetObjectName(handleA, out var name)); // B knows A's object by its RTI name
        Assert.Equal("HLAobject_1", name);
        a.Send(new RprObjectUpdate(handleA) { Marking = "From-A" } as IRprMessage);
        b.Pump();
        b.Drain(inboxB, 10);
        Assert.Equal("From-A", inboxB.Of<RprObjectUpdate>().Last().Marking);
    }
}
