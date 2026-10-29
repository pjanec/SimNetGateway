using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Showcase;

namespace SimNetGateway.Transport.HlaRpr.Tests;

/// <summary>
/// The message level API of the scripted federate (DD-008 section 12b, stage HLA-3): <see cref="ScriptedRprFederate.Inject"/> turns the
/// RPR messages of the showcase scripts into the RTI calls of a federate, and the transport under test delivers what a gateway
/// receives. The expectations are re-stated here from the FOM rules, not taken from the code that is tested.
/// </summary>
public class PeerInjectionTests
{
    private const uint Script = 0x601;

    private static Rig NewRig()
    {
        var rig = new Rig();
        rig.Peer.DeclareForGateway();
        return rig;
    }

    private static RprObjectUpdate Full(uint handle, string marking = "T-1", ushort number = 7) => new(new RprObjectHandle(handle))
    {
        Marking = marking,
        EntityType = Gen.Tank,
        ForceId = RprForceId.Opposing,
        EntityIdentifier = new DisEntityId(5, 7, number),
        WorldLocation = new WorldLocation(3_973_000, 999_000, 4_871_000),
        Orientation = new RprOrientation(0.5, 0.25, 0.125),
        VelocityVector = new RprVector(1, 2, 3),
        DeadReckoningAlgorithm = 2,
        DamageState = RprDamageState.SlightDamage,
    };

    [Fact]
    public void The_first_message_registers_the_object_under_an_rti_handle_and_the_gateway_receives_every_attribute_once()
    {
        var rig = NewRig();

        rig.Peer.Inject(Full(Script));
        rig.Settle();

        var actual = rig.Peer.ActualHandle(Script);
        Assert.NotNull(actual);
        Assert.NotEqual(Script, actual!.Value.Value); // the RTI assigns the handle, the script's number is only a key
        var update = Assert.Single(rig.Inbox.Of<RprObjectUpdate>());
        Assert.Equal(actual, update.Handle);
        Assert.Equal("T-1", update.Marking);
        Assert.Equal(Gen.Tank, update.EntityType);
        Assert.Equal(RprForceId.Opposing, update.ForceId);
        Assert.Equal(new DisEntityId(5, 7, 7), update.EntityIdentifier);
        Assert.Equal(RprDamageState.SlightDamage, update.DamageState);
        Assert.Equal(new WorldLocation(3_973_000, 999_000, 4_871_000), update.WorldLocation);
        Assert.Equal(new RprVector(1, 2, 3), update.VelocityVector);
        Assert.Equal((byte)2, update.DeadReckoningAlgorithm);
        Assert.Equal(1, rig.Peer.InjectedObjectCount);
        Assert.True(rig.Gateway.TryGetObjectName(actual.Value, out string name));
        Assert.Equal(rig.Peer.NameOfHandle(actual.Value.Value), name);
    }

    [Fact]
    public void A_second_message_for_the_same_script_handle_updates_the_same_object()
    {
        var rig = NewRig();
        rig.Peer.Inject(Full(Script));
        rig.Settle();

        rig.Peer.Inject(new RprObjectUpdate(new RprObjectHandle(Script)) { Marking = "T-1b" });
        rig.Settle();

        Assert.Equal(1, rig.Peer.InjectedObjectCount);
        Assert.Equal(2, rig.Inbox.Of<RprObjectUpdate>().Count());
        var second = rig.Inbox.Of<RprObjectUpdate>().Last();
        Assert.Equal("T-1b", second.Marking);
        Assert.Equal(rig.Peer.ActualHandle(Script), second.Handle);
        Assert.Null(second.WorldLocation); // a marking-only message reflects the marking only
    }

    [Fact]
    public void A_location_only_message_re_encodes_the_whole_spatial_variant_from_the_peers_state()
    {
        var rig = NewRig();
        rig.Peer.Inject(Full(Script));
        rig.Settle();

        rig.Peer.Inject(new RprObjectUpdate(new RprObjectHandle(Script)) { WorldLocation = new WorldLocation(3_973_100, 999_100, 4_871_100) });
        rig.Settle();

        var moved = rig.Inbox.Of<RprObjectUpdate>().Last();
        Assert.Equal(new WorldLocation(3_973_100, 999_100, 4_871_100), moved.WorldLocation);
        // the orientation, velocity and algorithm of the first message are still part of the one variant record
        Assert.Equal(new RprOrientation(0.5, 0.25, 0.125), moved.Orientation);
        Assert.Equal(new RprVector(1, 2, 3), moved.VelocityVector);
        Assert.Equal((byte)2, moved.DeadReckoningAlgorithm);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Random_message_sequences_arrive_as_the_fom_rules_say(int seed)
    {
        var random = new Random(seed);
        var rig = NewRig();
        const int objects = 3;
        var models = Enumerable.Range(0, objects).Select(_ => new SpatialModel()).ToArray();
        for (int step = 0; step < 40; step++)
        {
            int index = random.Next(objects);
            uint script = Script + (uint)index;
            var message = Gen.PartialUpdate(random, new RprObjectHandle(script));
            int before = rig.Inbox.Messages.Count;

            rig.Peer.Inject(message);
            rig.Settle();

            models[index].Merge(message);
            var delivered = rig.Inbox.Messages.Skip(before).OfType<RprObjectUpdate>().Where(u => u.Handle == rig.Peer.ActualHandle(script)).ToList();
            bool spatial = Gen.HasSpatial(message) && models[index].Expected() is not null;
            bool anything = message.Marking is not null || message.EntityType is not null || message.ForceId is not null ||
                message.EntityIdentifier is not null || message.DamageState is not null || spatial;
            if (!anything)
            {
                Assert.Empty(delivered); // nothing the FOM can carry: nothing is sent
                continue;
            }

            var update = Assert.Single(delivered);
            Assert.Equal(message.Marking, update.Marking);
            Assert.Equal(message.EntityType, update.EntityType);
            Assert.Equal(message.EntityIdentifier, update.EntityIdentifier);
            Assert.Equal(message.DamageState, update.DamageState);
            Assert.Equal(message.ForceId, update.ForceId);
            if (!spatial)
            {
                Assert.Null(update.WorldLocation);
                continue;
            }

            var expected = models[index].Expected()!.Value;
            Assert.Equal(new WorldLocation(expected.WorldLocation.X, expected.WorldLocation.Y, expected.WorldLocation.Z), update.WorldLocation);
            Assert.Equal((byte)expected.Algorithm, update.DeadReckoningAlgorithm);
            Assert.Equal(new RprOrientation(expected.Orientation.Psi, expected.Orientation.Theta, expected.Orientation.Phi), update.Orientation);
            Assert.Equal(expected.HasVelocity, update.VelocityVector is not null);
            if (expected.HasVelocity)
            {
                Assert.Equal(new RprVector(expected.Velocity.X, expected.Velocity.Y, expected.Velocity.Z), update.VelocityVector);
            }
        }
    }

    [Fact]
    public void A_damage_percentage_is_never_sent_by_the_peer()
    {
        var rig = NewRig();
        rig.Peer.Inject(Full(Script));
        rig.Settle();
        int before = rig.Inbox.Messages.Count;

        rig.Peer.Inject(new RprObjectUpdate(new RprObjectHandle(Script)) { DamagePercent = 40 });
        rig.Settle();

        Assert.Equal(before, rig.Inbox.Messages.Count);
    }

    [Fact]
    public void A_removal_deletes_the_object_and_a_removal_of_an_unknown_handle_sends_nothing()
    {
        var rig = NewRig();
        rig.Peer.Inject(Full(Script));
        rig.Settle();
        var actual = rig.Peer.ActualHandle(Script)!.Value;

        rig.Peer.Inject(new RprObjectRemoved(new RprObjectHandle(Script)));
        rig.Settle();
        var removed = Assert.Single(rig.Inbox.Of<RprObjectRemoved>());
        Assert.Equal(actual, removed.Handle);
        Assert.Equal(0, rig.Peer.InjectedObjectCount);
        Assert.Null(rig.Peer.ActualHandle(Script));

        int before = rig.Inbox.Messages.Count;
        rig.Peer.Inject(new RprObjectRemoved(new RprObjectHandle(0x999))); // never registered
        rig.Settle();
        Assert.Equal(before, rig.Inbox.Messages.Count);
    }

    [Fact]
    public void An_aggregate_names_its_members_by_rti_name_and_a_member_that_does_not_exist_yet_takes_the_reserved_name_later()
    {
        var rig = NewRig();
        rig.Peer.Inject(Full(0x701, "Wolf-1", 1));
        rig.Peer.Inject(Full(0x702, "Wolf-2", 2));
        rig.Settle();
        var unit = new RprAggregateEntity(new RprObjectHandle(0x700), "Wolf", Gen.Tank, RprForceId.Opposing,
            [new DisEntityId(5, 7, 1), new DisEntityId(5, 7, 2), new DisEntityId(5, 7, 3)]);

        rig.Peer.Inject(unit);
        rig.Settle();

        var first = Assert.Single(rig.Inbox.Of<RprAggregateEntity>());
        Assert.Equal("Wolf", first.Marking);
        Assert.Equal(RprForceId.Opposing, first.ForceId);
        // member 3 has no object yet: the transport can only name the two it knows
        Assert.Equal([new DisEntityId(5, 7, 1), new DisEntityId(5, 7, 2)], first.MemberIds);

        rig.Peer.Inject(Full(0x703, "Wolf-3", 3)); // registered under the name the aggregate already used
        rig.Settle();

        var all = rig.Inbox.Of<RprAggregateEntity>().ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal([new DisEntityId(5, 7, 1), new DisEntityId(5, 7, 2), new DisEntityId(5, 7, 3)], all[1].MemberIds);
    }

    [Fact]
    public void An_aggregate_update_of_its_marking_is_a_marking_attribute_and_does_not_need_the_members()
    {
        var rig = NewRig();
        rig.Peer.Inject(new RprAggregateEntity(new RprObjectHandle(0x700), "Wolf", Gen.Tank, RprForceId.Opposing, []));
        rig.Settle();

        rig.Peer.Inject(new RprObjectUpdate(new RprObjectHandle(0x700)) { Marking = "Wolf2" });
        rig.Settle();

        var all = rig.Inbox.Of<RprAggregateEntity>().ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal("Wolf2", all[1].Marking);
    }

    [Fact]
    public void A_fire_and_its_detonation_share_one_event_identifier_and_the_gateway_sees_one_guid_for_both()
    {
        var rig = NewRig();
        rig.Peer.Inject(Full(Script));
        rig.Settle();
        var shooter = new RprObjectHandle(Script);
        var munition = new DisEntityType(2, 1, 225, 1, 1, 0, 0);
        var location = new WorldLocation(3_973_000, 999_000, 4_871_000);
        var e1 = Guid.Parse("e1000000-0000-4000-8000-000000000001");
        var e2 = Guid.Parse("e2000000-0000-4000-8000-000000000002");

        rig.Peer.Inject(new RprWeaponFire(e1, shooter, null, location, munition, 2));
        rig.Peer.Inject(new RprMunitionDetonation(e1, shooter, null, location, munition, RprDetonationResult.GroundImpact));
        rig.Peer.Inject(new RprWeaponFire(e2, shooter, null, location, munition, 1));
        rig.Settle();

        var fires = rig.Inbox.Of<RprWeaponFire>().ToList();
        var detonations = rig.Inbox.Of<RprMunitionDetonation>().ToList();
        Assert.Equal(2, fires.Count);
        Assert.Single(detonations);
        Assert.Equal(fires[0].EventId, detonations[0].EventId); // the same event
        Assert.NotEqual(fires[0].EventId, fires[1].EventId); // another event
        Assert.NotEqual(rig.Peer.EventIdentifierOf(e1), rig.Peer.EventIdentifierOf(e2));
        Assert.Equal(rig.Peer.NameOfHandle(rig.Peer.ActualHandle(Script)!.Value.Value), rig.Peer.EventIdentifierOf(e1)!.Value.IssuingObjectIdentifier);
        Assert.Equal(2, fires[0].Quantity);
        Assert.Equal(munition, fires[0].Munition);
        Assert.Equal(RprDetonationResult.GroundImpact, detonations[0].Result);
        Assert.Equal(rig.Peer.ActualHandle(Script), fires[0].FiringObject);
        Assert.Null(fires[0].TargetObject);
    }

    [Fact]
    public void An_event_without_a_firing_object_is_issued_by_the_federate_itself()
    {
        var rig = NewRig();

        var id = Guid.NewGuid();

        rig.Peer.Inject(new RprWeaponFire(id, null, null, default, Gen.Tank, 1));
        rig.Settle();

        // not the empty name of "no object" (the gateway's own events without a firing object use that): two federates never share an identifier
        Assert.Equal("Peer", rig.Peer.EventIdentifierOf(id)!.Value.IssuingObjectIdentifier);
        var fire = Assert.Single(rig.Inbox.Of<RprWeaponFire>());
        Assert.Null(fire.FiringObject);
    }

    [Fact]
    public void Messages_the_rpr_side_does_not_send_over_hla_are_refused()
    {
        var rig = NewRig();

        Assert.Throws<NotSupportedException>(() => rig.Peer.Inject(new RprSimulationControl(RprControlKind.StartResume)));
        Assert.Throws<NotSupportedException>(() => rig.Peer.Inject(new RprRegisterObject(1, Gen.Tank)));
    }

    [Fact]
    public void What_the_gateway_sends_is_decoded_with_the_tick_it_arrived_at_and_kept_only_while_decoding_is_on()
    {
        var rig = NewRig();
        long tick = 41;
        rig.Peer.TickSource = () => tick;
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        rig.Peer.SubscribeInteractions();
        var handle = rig.Register(Gen.Tank);

        rig.Send(Full(handle.Value));
        tick = 42;
        rig.Round();
        var seen = rig.Peer.DiscoveredObject(handle.Value);
        Assert.NotNull(seen);
        Assert.Equal(42, seen!.LastReflectTick);

        rig.Peer.Decode = false;
        rig.Send(new RprObjectUpdate(handle) { Marking = "SILENT" });
        tick = 43;
        rig.Round();
        Assert.Equal(42, seen.LastReflectTick); // drained, not decoded
        Assert.Equal("T-1", seen.Marking!.Value.AsciiText);
    }
}
