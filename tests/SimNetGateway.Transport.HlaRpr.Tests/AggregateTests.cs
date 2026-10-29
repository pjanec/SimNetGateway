using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Transport.HlaRpr.Tests;

/// <summary>AggregateEntity reflections become RprAggregateEntity; member object names map to the DIS ids learnt from the members (DD-008 3.4).</summary>
public class AggregateTests
{
    private static ScriptedRprFederate.Attributes Identity(ushort site, ushort app, ushort entity) =>
        new ScriptedRprFederate.Attributes().EntityIdentifier(new EntityIdentifierStruct(new FederateIdentifierStruct(site, app), entity));

    private sealed class World
    {
        public World()
        {
            Rig = new Rig();
            Rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
            Rig.Peer.PublishAggregate();
        }

        public Rig Rig { get; }

        public (Protocol.Rpr.Hla.ObjectInstanceHandle Object, string Name) Member(ushort entity, bool announce = true)
        {
            var obj = Rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
            if (announce)
            {
                Rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, Identity(9, 9, entity));
            }

            return (obj, Rig.Peer.NameOf(obj));
        }
    }

    [Fact]
    public void An_aggregate_is_delivered_with_marking_type_force_and_the_ids_of_its_known_members_in_order()
    {
        var w = new World();
        var a = w.Member(11);
        var b = w.Member(12);
        var c = w.Member(13);
        w.Rig.Settle();
        var aggregate = w.Rig.Peer.Register(RprFom2.ObjectClasses.AggregateEntity);
        w.Rig.Settle();
        w.Rig.Inbox.Messages.Clear();
        var type = new EntityTypeStruct(1, 1, 225, 7, 1, 0, 0);

        w.Rig.Peer.Update(aggregate, RprFom2.ObjectClasses.AggregateEntity, new ScriptedRprFederate.Attributes()
            .AggregateMarking("Alpha Coy")
            .EntityType(type)
            .Force(ForceIdentifierEnum8.Friendly_3)
            .EntityIdentifiers(c.Name, "NoSuchObject", a.Name, b.Name));
        w.Rig.Round();

        Assert.True(w.Rig.Gateway.TryGetObjectHandle(w.Rig.Peer.NameOf(aggregate), out var handle));
        var message = Assert.IsType<RprAggregateEntity>(Assert.Single(w.Rig.Inbox.Messages));
        Assert.Equal(handle, message.Handle);
        Assert.Equal("Alpha Coy", message.Marking);
        Assert.Equal(Gen.ToDis(type), message.EntityType);
        Assert.Equal(RprForceId.Friendly, message.ForceId);
        Assert.Equal([new DisEntityId(9, 9, 13), new DisEntityId(9, 9, 11), new DisEntityId(9, 9, 12)], message.MemberIds); // list order, unknown skipped
    }

    [Fact]
    public void A_member_that_becomes_known_later_re_emits_the_aggregate_with_it()
    {
        var w = new World();
        var a = w.Member(21);
        var late = w.Member(22, announce: false);
        var aggregate = w.Rig.Peer.Register(RprFom2.ObjectClasses.AggregateEntity);
        w.Rig.Settle();
        w.Rig.Peer.Update(aggregate, RprFom2.ObjectClasses.AggregateEntity, new ScriptedRprFederate.Attributes()
            .AggregateMarking("Bravo").EntityIdentifiers(a.Name, late.Name));
        w.Rig.Round();
        Assert.Equal([new DisEntityId(9, 9, 21)], Assert.IsType<RprAggregateEntity>(w.Rig.Inbox.Messages.Last()).MemberIds);
        w.Rig.Inbox.Messages.Clear();

        w.Rig.Peer.Update(late.Object, RprFom2.ObjectClasses.GroundVehicle, Identity(9, 9, 22)); // the member says who it is
        w.Rig.Round();

        var again = Assert.IsType<RprAggregateEntity>(w.Rig.Inbox.Messages.OfType<RprAggregateEntity>().Single());
        Assert.Equal("Bravo", again.Marking);
        Assert.Equal([new DisEntityId(9, 9, 21), new DisEntityId(9, 9, 22)], again.MemberIds);
    }

    [Fact]
    public void A_partial_aggregate_update_keeps_what_an_earlier_one_set()
    {
        var w = new World();
        var a = w.Member(31);
        var aggregate = w.Rig.Peer.Register(RprFom2.ObjectClasses.AggregateEntity);
        w.Rig.Settle();
        w.Rig.Peer.Update(aggregate, RprFom2.ObjectClasses.AggregateEntity, new ScriptedRprFederate.Attributes()
            .AggregateMarking("Charlie").EntityIdentifiers(a.Name));
        w.Rig.Round();
        w.Rig.Inbox.Messages.Clear();

        w.Rig.Peer.Update(aggregate, RprFom2.ObjectClasses.AggregateEntity, new ScriptedRprFederate.Attributes().Force(ForceIdentifierEnum8.Opposing));
        w.Rig.Round();

        var message = Assert.IsType<RprAggregateEntity>(Assert.Single(w.Rig.Inbox.Messages));
        Assert.Equal("Charlie", message.Marking);
        Assert.Equal(RprForceId.Opposing, message.ForceId);
        Assert.Equal([new DisEntityId(9, 9, 31)], message.MemberIds);
    }

    [Fact]
    public void A_corrupt_aggregate_attribute_is_counted_and_the_others_are_delivered()
    {
        var w = new World();
        var a = w.Member(41);
        var aggregate = w.Rig.Peer.Register(RprFom2.ObjectClasses.AggregateEntity);
        w.Rig.Settle();
        w.Rig.Inbox.Messages.Clear();

        w.Rig.Peer.Update(aggregate, RprFom2.ObjectClasses.AggregateEntity, new ScriptedRprFederate.Attributes()
            .Raw("AggregateMarking", new byte[10])
            .Raw("EntityIdentifiers", [0, 0, 0, 5, 65])
            .Force(ForceIdentifierEnum8.Neutral));
        w.Rig.Round();

        var message = Assert.IsType<RprAggregateEntity>(Assert.Single(w.Rig.Inbox.Messages));
        Assert.Equal(RprForceId.Neutral, message.ForceId);
        Assert.Equal(string.Empty, message.Marking);
        Assert.Empty(message.MemberIds);
        Assert.Equal(2, w.Rig.Gateway.DecodeErrors);
        Assert.NotNull(a.Name);
    }

    [Fact]
    public void A_removed_aggregate_is_reported_removed()
    {
        var w = new World();
        var aggregate = w.Rig.Peer.Register(RprFom2.ObjectClasses.AggregateEntity);
        w.Rig.Settle();
        Assert.True(w.Rig.Gateway.TryGetObjectHandle(w.Rig.Peer.NameOf(aggregate), out var handle));
        w.Rig.Inbox.Messages.Clear();

        w.Rig.Peer.Rti.DeleteObjectInstance(aggregate, Array.Empty<byte>());
        w.Rig.Round();

        Assert.Equal([new RprObjectRemoved(handle)], w.Rig.Inbox.Of<RprObjectRemoved>());
    }

    [Fact]
    public void A_discovered_aggregate_is_asked_for_its_values()
    {
        var w = new World();
        var aggregate = w.Rig.Peer.Register(RprFom2.ObjectClasses.AggregateEntity);

        w.Rig.Settle();

        Assert.Equal([aggregate], w.Rig.Peer.ProvideRequests);
    }
}
