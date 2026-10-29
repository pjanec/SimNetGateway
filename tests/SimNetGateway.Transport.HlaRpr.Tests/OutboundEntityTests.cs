using SimNetGateway.Showcase;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Transport.HlaRpr.Tests;

/// <summary>
/// The gateway -> HLA direction for objects (DD-008 sections 3.4, 3.5, 5.2). The peer is a ScriptedRprFederate that decodes with
/// RprFom2 itself; expectations come from small reference tables / models in this file, not from the transport's code.
/// </summary>
public class OutboundEntityTests
{
    private static readonly string[] AllPublished =
    [
        RprFom2.ObjectClasses.GroundVehicle, RprFom2.ObjectClasses.Aircraft, RprFom2.ObjectClasses.SurfaceVessel,
        RprFom2.ObjectClasses.SubmersibleVessel, RprFom2.ObjectClasses.Spacecraft, RprFom2.ObjectClasses.Platform,
        RprFom2.ObjectClasses.Human, RprFom2.ObjectClasses.Munition, RprFom2.ObjectClasses.PhysicalEntity,
    ];

    /// <summary>The 3.5 table, restated.</summary>
    private static string ExpectedClass(DisEntityType t)
    {
        if (t.Kind == 1)
        {
            return t.Domain switch
            {
                1 => RprFom2.ObjectClasses.GroundVehicle,
                2 => RprFom2.ObjectClasses.Aircraft,
                3 => RprFom2.ObjectClasses.SurfaceVessel,
                4 => RprFom2.ObjectClasses.SubmersibleVessel,
                5 => RprFom2.ObjectClasses.Spacecraft,
                _ => RprFom2.ObjectClasses.Platform,
            };
        }

        return t.Kind switch
        {
            3 => RprFom2.ObjectClasses.Human,
            2 => RprFom2.ObjectClasses.Munition,
            _ => RprFom2.ObjectClasses.PhysicalEntity,
        };
    }

    private static Rig RigSubscribedEverywhere()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(AllPublished);
        return rig;
    }

    // ---- registration (success condition c) ---------------------------------------------------------------------------

    [Fact]
    public void A_registration_is_answered_on_the_next_pump_never_inside_send()
    {
        var rig = RigSubscribedEverywhere();

        rig.Send(new RprRegisterObject(5, Gen.Tank));

        Assert.Equal(0, rig.Gateway.PendingInbound); // nothing is deliverable before the pump
        Assert.Equal(0, rig.Gateway.Drain(rig.Inbox, 10));
        rig.Gateway.Pump();
        Assert.Equal(1, rig.Gateway.Drain(rig.Inbox, 10));
        var answer = Assert.IsType<RprObjectRegistered>(Assert.Single(rig.Inbox.Messages));
        Assert.Equal(5, answer.Token);
        rig.Peer.Evoke();
        var seen = Assert.Single(rig.Peer.Discovered);
        Assert.True(rig.Gateway.TryGetObjectName(answer.Handle, out var name));
        Assert.Equal(name, seen.Name); // the handle the gateway got is the object the peer discovered
        Assert.StartsWith("HLAobject_", name); // RTI-generated names (R-104)
    }

    [Fact]
    public void Every_registration_gets_exactly_one_answer_with_its_own_token_under_interleaving()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var r = new Random(seed);
            var rig = RigSubscribedEverywhere();
            var typeOfToken = new Dictionary<long, DisEntityType>();
            int count = r.Next(1, 40);
            for (int i = 0; i < count; i++)
            {
                long token = 1000 + i * 7 + r.Next(5);
                typeOfToken[token] = Gen.Type(r);
                rig.Send(new RprRegisterObject(token, typeOfToken[token]));
                if (r.Next(3) == 0)
                {
                    rig.Round(); // pumps interleaved with the sends
                }
            }

            rig.Settle();

            var answers = rig.Inbox.Of<RprObjectRegistered>().ToList();
            Assert.True(answers.Count == typeOfToken.Count, $"seed {seed}: {answers.Count} answers for {typeOfToken.Count} registrations");
            Assert.Equal(typeOfToken.Keys.Order(), answers.Select(a => a.Token).Order());
            Assert.Equal(answers.Count, answers.Select(a => a.Handle).Distinct().Count());
            Assert.Equal(count, rig.Peer.Discovered.Count);
            foreach (var answer in answers)
            {
                Assert.True(rig.Gateway.TryGetObjectName(answer.Handle, out var name));
                var peerObject = Assert.Single(rig.Peer.Discovered, o => o.Name == name);
                Assert.True(ExpectedClass(typeOfToken[answer.Token]) == peerObject.ClassName,
                    $"seed {seed} token {answer.Token}: peer knows the object as {peerObject.ClassName}");
            }
        }
    }

    // ---- class choice (success condition b) ---------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1, "GroundVehicle")]
    [InlineData(1, 2, "Aircraft")]
    [InlineData(1, 3, "SurfaceVessel")]
    [InlineData(1, 4, "SubmersibleVessel")]
    [InlineData(1, 5, "Spacecraft")]
    [InlineData(1, 0, "Platform")]
    [InlineData(1, 6, "Platform")]
    [InlineData(1, 9, "Platform")]
    [InlineData(3, 1, "Human")]
    [InlineData(3, 2, "Human")]
    [InlineData(3, 0, "Human")]
    [InlineData(2, 2, "Munition")]
    [InlineData(2, 9, "Munition")]
    [InlineData(0, 1, "PhysicalEntity")]
    [InlineData(4, 1, "PhysicalEntity")]
    [InlineData(5, 3, "PhysicalEntity")]
    [InlineData(9, 2, "PhysicalEntity")]
    public void The_registered_class_follows_kind_and_domain(int kind, int domain, string simpleClass)
    {
        var rig = RigSubscribedEverywhere();

        rig.Register(new DisEntityType((byte)kind, (byte)domain, 225, 1, 1, 1, 0));

        var seen = Assert.Single(rig.Peer.Discovered);
        Assert.EndsWith("." + simpleClass, seen.ClassName);
        Assert.Equal(ExpectedClass(new DisEntityType((byte)kind, (byte)domain, 0, 0, 0, 0, 0)), seen.ClassName);
    }

    [Fact]
    public void A_peer_subscribed_only_at_Platform_discovers_vehicles_but_not_lifeforms_munitions_or_other_entities()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.Platform);
        var ground = new DisEntityType(1, 1, 225, 1, 1, 1, 0);
        var air = new DisEntityType(1, 2, 225, 1, 1, 1, 0);
        var other = new DisEntityType(1, 8, 225, 1, 1, 1, 0);

        long token = 0;
        foreach (var type in new[] { ground, air, other, new DisEntityType(3, 1, 0, 0, 0, 0, 0), new DisEntityType(2, 1, 0, 0, 0, 0, 0), new DisEntityType(5, 1, 0, 0, 0, 0, 0) })
        {
            rig.Register(type, ++token);
        }

        Assert.Equal(
            [RprFom2.ObjectClasses.Platform, RprFom2.ObjectClasses.Platform, RprFom2.ObjectClasses.Platform],
            rig.Peer.Discovered.Select(o => o.ClassName)); // promotion: known as the subscribed class
        Assert.Equal(3, rig.Peer.Discovered.Count); // ground, air and the other-domain platform; no lifeform, munition or generic entity
    }

    // ---- updates (success condition a) ---------------------------------------------------------------------------------

    /// <summary>The attributes of one object the peer should hold, kept by this test as a model of what was sent.</summary>
    private sealed class Held
    {
        public DisEntityType? Type;
        public DisEntityId? Id;
        public string? Marking;
        public RprForceId? Force;
        public RprDamageState? Damage;
        public readonly SpatialModel Spatial = new();
        public readonly Dictionary<string, int> Carried = new();

        public void Apply(RprObjectUpdate u)
        {
            Type = u.EntityType ?? Type;
            Id = u.EntityIdentifier ?? Id;
            Marking = u.Marking ?? Marking;
            Force = u.ForceId ?? Force;
            Damage = u.DamageState ?? Damage;
            Bump(u.EntityType is not null, "EntityType");
            Bump(u.EntityIdentifier is not null, "EntityIdentifier");
            Bump(u.Marking is not null, "Marking");
            Bump(u.ForceId is not null, "ForceIdentifier");
            Bump(u.DamageState is not null, "DamageState");
            Spatial.Merge(u);
            Bump(Gen.HasSpatial(u) && Spatial.Expected() is not null, "Spatial"); // a variant needs a position
        }

        private void Bump(bool condition, string attribute)
        {
            if (condition)
            {
                Carried[attribute] = (Carried.TryGetValue(attribute, out var n) ? n : 0) + 1;
            }
        }
    }

    private static void AssertPeerHolds(Rig rig, RprObjectHandle handle, Held held, string where)
    {
        var o = Assert.Single(rig.Peer.Discovered);
        foreach (var attribute in new[] { "EntityType", "EntityIdentifier", "Marking", "ForceIdentifier", "DamageState", "Spatial" })
        {
            Assert.True(
                (held.Carried.TryGetValue(attribute, out var n) ? n : 0) == o.Count(attribute),
                $"{where}: {attribute} was reflected {o.Count(attribute)} times, expected {(held.Carried.TryGetValue(attribute, out var m) ? m : 0)}");
        }

        Assert.True(Equal<EntityTypeStruct>(held.Type is { } t ? Gen.ToStruct(t) : null, o.EntityType), $"{where}: EntityType");
        Assert.True(Equal<EntityIdentifierStruct>(held.Id is { } id ? new EntityIdentifierStruct(new FederateIdentifierStruct(id.Site, id.Application), id.Entity) : null, o.EntityIdentifier), $"{where}: EntityIdentifier");
        Assert.True(held.Marking == o.Marking?.AsciiText, $"{where}: Marking '{held.Marking}' vs '{o.Marking?.AsciiText}'");
        Assert.True(Equal<ForceIdentifierEnum8>(held.Force is { } f ? (ForceIdentifierEnum8)(byte)f : null, o.Force), $"{where}: Force");
        Assert.True(Equal<DamageStatusEnum32>(held.Damage is { } d ? (DamageStatusEnum32)(byte)d : null, o.Damage), $"{where}: Damage");

        // success condition a: the Spatial the peer last decoded is the merged full state - never stale, never zeroed
        var expected = held.Carried.ContainsKey("Spatial") ? held.Spatial.Expected() : null;
        Assert.True(Equal<SpatialVariantStruct>(expected, o.Spatial), $"{where}: Spatial\n expected {expected}\n peer     {o.Spatial}");
        if (o.Spatial is { } seen)
        {
            Assert.True(rig.Gateway.TryGetCachedSpatial(handle, out var cached), $"{where}: no cached spatial");
            Assert.True(Equal<SpatialVariantStruct>(cached, seen), $"{where}: the transport's cache differs from what the peer decoded\n cache {cached}\n peer  {seen}");
        }
    }

    private static bool Equal<T>(T? a, T? b)
        where T : struct => a.HasValue == b.HasValue && (!a.HasValue || a.Value.Equals(b!.Value));

    [Fact]
    public void After_any_sequence_of_partial_updates_the_peer_holds_exactly_what_was_sent_and_the_merged_spatial_state()
    {
        const int Seeds = 300;
        for (int seed = 0; seed < Seeds; seed++)
        {
            var r = new Random(seed);
            var rig = new Rig();
            rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
            var type = Gen.Type(r);
            var handle = rig.Register(type);
            var held = new Held();

            // the gateway's first update is a full one; the random partial updates follow
            var first = Gen.FullUpdate(r, handle, type);
            rig.Send(first);
            held.Apply(first);
            rig.Round();
            AssertPeerHolds(rig, handle, held, $"seed {seed} full");

            int steps = r.Next(1, 14);
            for (int i = 0; i < steps; i++)
            {
                var update = Gen.PartialUpdate(r, handle);
                rig.Send(update);
                held.Apply(update);
                rig.Round();
                AssertPeerHolds(rig, handle, held, $"seed {seed} step {i}");
            }

            Assert.Equal(0, rig.Gateway.EncodeErrors);
            Assert.Equal(0, rig.Gateway.SendErrors);
        }
    }

    [Fact]
    public void A_partial_update_that_changes_one_spatial_field_re_sends_the_others_from_the_cache()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        var handle = rig.Register(Gen.Tank);
        var loc = new WorldLocation(4_000_000, 1_000_000, 4_800_000);
        rig.Send(new RprObjectUpdate(handle)
        {
            WorldLocation = loc,
            Orientation = new RprOrientation(0.5, 0.25, 0.125),
            VelocityVector = new RprVector(10, 20, 30),
            DeadReckoningAlgorithm = 2,
        });
        rig.Round();

        rig.Send(new RprObjectUpdate(handle) { VelocityVector = new RprVector(1, 2, 3) }); // only the velocity changes
        rig.Round();

        var spatial = Assert.Single(rig.Peer.Discovered).Spatial!.Value;
        Assert.Equal(DeadReckoningAlgorithmEnum8.DRM_FPW, spatial.Algorithm);
        Assert.Equal(new WorldLocationStruct(4_000_000, 1_000_000, 4_800_000), spatial.WorldLocation); // not zeroed
        Assert.Equal(new OrientationStruct(0.5f, 0.25f, 0.125f), spatial.Orientation); // not stale, not zeroed
        Assert.Equal(new VelocityVectorStruct(1, 2, 3), spatial.Velocity);
    }

    [Fact]
    public void Dead_reckoning_Other_is_never_sent_Static_without_a_velocity_and_FPW_with_one()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        var handle = rig.Register(Gen.Tank);
        var loc = new WorldLocation(1, 2, 3);
        var spatial = () => Assert.Single(rig.Peer.Discovered).Spatial!.Value.Algorithm;

        rig.Send(new RprObjectUpdate(handle) { WorldLocation = loc, DeadReckoningAlgorithm = 0 }); // Other, no velocity
        rig.Round();
        Assert.Equal(DeadReckoningAlgorithmEnum8.Static, spatial());

        rig.Send(new RprObjectUpdate(handle) { VelocityVector = new RprVector(1, 0, 0) }); // Other still, a velocity now
        rig.Round();
        Assert.Equal(DeadReckoningAlgorithmEnum8.DRM_FPW, spatial());

        rig.Send(new RprObjectUpdate(handle) { DeadReckoningAlgorithm = 200 }); // not a DIS algorithm
        rig.Round();
        Assert.Equal(DeadReckoningAlgorithmEnum8.DRM_FPW, spatial());

        rig.Send(new RprObjectUpdate(handle) { DeadReckoningAlgorithm = 4 }); // RVW: velocity, acceleration and angular velocity carried (zero here)
        rig.Round();
        Assert.Equal(DeadReckoningAlgorithmEnum8.DRM_RVW, spatial());

        rig.Send(new RprObjectUpdate(handle) { DeadReckoningAlgorithm = 1 }); // Static asked for explicitly
        rig.Round();
        Assert.Equal(DeadReckoningAlgorithmEnum8.Static, spatial());
        Assert.Equal(0, rig.Gateway.EncodeErrors);
    }

    [Fact]
    public void A_spatial_field_without_a_position_sends_no_variant_until_a_position_is_known()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        var handle = rig.Register(Gen.Tank);

        rig.Send(new RprObjectUpdate(handle) { VelocityVector = new RprVector(1, 2, 3), Marking = "M" });
        rig.Round();
        var o = Assert.Single(rig.Peer.Discovered);
        Assert.Null(o.Spatial);
        Assert.Equal("M", o.Marking!.Value.AsciiText); // the rest of the update was delivered
        Assert.False(rig.Gateway.TryGetCachedSpatial(handle, out _));

        rig.Send(new RprObjectUpdate(handle) { WorldLocation = new WorldLocation(5, 6, 7) });
        rig.Round();
        Assert.Equal(new VelocityVectorStruct(1, 2, 3), o.Spatial!.Value.Velocity); // the earlier velocity is in the variant now
    }

    [Fact]
    public void Marking_is_cut_to_11_ascii_characters()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        var handle = rig.Register(Gen.Tank);

        rig.Send(new RprObjectUpdate(handle) { Marking = "ABCDEFGHIJKLMNOP" });
        rig.Round();
        Assert.Equal("ABCDEFGHIJK", Assert.Single(rig.Peer.Discovered).Marking!.Value.AsciiText);

        rig.Send(new RprObjectUpdate(handle) { Marking = "T\u00e4nk" });
        rig.Round();
        Assert.Equal("T?nk", rig.Peer.Discovered[0].Marking!.Value.AsciiText);
        Assert.Equal(0, rig.Gateway.EncodeErrors);
    }

    [Fact]
    public void Force_and_damage_values_round_trip_for_every_value()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        var handle = rig.Register(Gen.Tank);
        var o = rig.Peer.Discovered.Count == 1 ? rig.Peer.Discovered[0] : null;

        foreach (var force in Enum.GetValues<RprForceId>())
        {
            foreach (var damage in Enum.GetValues<RprDamageState>())
            {
                rig.Send(new RprObjectUpdate(handle) { ForceId = force, DamageState = damage });
                rig.Round();
                o ??= rig.Peer.Discovered.Single();
                Assert.Equal((byte)force, (byte)o.Force!.Value);
                Assert.Equal((uint)(byte)damage, (uint)o.Damage!.Value);
            }
        }
    }

    // ---- dropped by design (R-105, R-107) ---------------------------------------------------------------------------------

    [Fact]
    public void DamagePercent_is_dropped_with_one_warning_and_the_rest_of_the_update_is_sent()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        var handle = rig.Register(Gen.Tank);

        for (int i = 0; i < 3; i++)
        {
            rig.Send(new RprObjectUpdate(handle) { DamagePercent = (byte)(10 * i), Marking = "M" + i });
            rig.Round();
        }

        Assert.Equal(3, rig.Gateway.DroppedDamagePercent);
        var warning = Assert.Single(rig.Gateway.Warnings);
        Assert.Contains("DamagePercent", warning);
        var o = Assert.Single(rig.Peer.Discovered);
        Assert.Equal("M2", o.Marking!.Value.AsciiText);
        Assert.Equal(3, o.Count("Marking"));
        Assert.Null(o.Damage); // no attribute for it

        rig.Send(new RprObjectUpdate(handle) { DamagePercent = 50 }); // an update with only a percentage sends nothing at all
        rig.Round();
        Assert.Equal(3, o.Count("Marking"));
        Assert.Single(rig.Gateway.Warnings);
    }

    [Fact]
    public void RprSimulationControl_is_counted_and_dropped_with_one_warning()
    {
        var rig = new Rig();
        rig.Hub.ClearTrace();

        rig.Send(new RprSimulationControl(RprControlKind.StartResume));
        rig.Send(new RprSimulationControl(RprControlKind.StopFreeze));

        Assert.Equal(2, rig.Gateway.DroppedSimulationControl);
        var warning = Assert.Single(rig.Gateway.Warnings);
        Assert.Contains("RprSimulationControl", warning);
        Assert.Empty(rig.Hub.Trace); // nothing reached the RTI
    }

    // ---- removal, unknown handles, provide ---------------------------------------------------------------------------

    [Fact]
    public void An_object_removal_deletes_the_instance_and_later_sends_for_it_are_rejected()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        var handle = rig.Register(Gen.Tank);
        Assert.True(rig.Gateway.TryGetObjectName(handle, out var name));

        rig.Send(new RprObjectRemoved(handle));
        rig.Round();

        Assert.Equal([name], rig.Peer.RemovedNames);
        Assert.Empty(rig.Peer.Objects);
        rig.Send(new RprObjectUpdate(handle) { Marking = "X" });
        rig.Send(new RprObjectRemoved(handle));
        Assert.Equal(2, rig.Gateway.RejectedOutbound);
        Assert.False(rig.Gateway.TryGetObjectName(handle, out _));
    }

    [Fact]
    public void An_update_for_a_handle_the_transport_never_registered_is_rejected_not_thrown()
    {
        var rig = new Rig();

        rig.Send(new RprObjectUpdate(new RprObjectHandle(4242)) { Marking = "X" });

        Assert.Equal(1, rig.Gateway.RejectedOutbound);
        Assert.Equal(0, rig.Gateway.SendErrors);
    }

    [Fact]
    public void A_value_request_from_the_peer_becomes_RprUpdateRequested_for_that_object_only()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        var a = rig.Register(Gen.Tank, 1);
        var b = rig.Register(Gen.Uav, 2);
        rig.Inbox.Messages.Clear();
        var seenB = rig.Peer.Discovered.Single(o => rig.Gateway.TryGetObjectName(b, out var n) && o.Name == n);

        rig.Peer.RequestAll(seenB);
        rig.Settle();

        Assert.Equal([new RprUpdateRequested(b)], rig.Inbox.Of<RprUpdateRequested>());
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Several_value_requests_before_the_engine_drains_are_one_RprUpdateRequested()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        var handle = rig.Register(Gen.Tank);
        rig.Inbox.Messages.Clear();
        var seen = rig.Peer.Discovered.Single();

        rig.Peer.RequestAll(seen);
        rig.Peer.RequestAll(seen);
        rig.Gateway.Pump(); // both requests are processed, nothing drained yet
        rig.Peer.RequestAll(seen);
        rig.Gateway.Pump();
        rig.Gateway.Drain(rig.Inbox, 100);
        rig.Peer.RequestAll(seen); // after the drain it is a new request
        rig.Round();

        Assert.Equal([new RprUpdateRequested(handle), new RprUpdateRequested(handle)], rig.Inbox.Of<RprUpdateRequested>());
    }

    [Fact]
    public void Resigning_deletes_the_objects_of_the_gateway()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        rig.Register(Gen.Tank, 1);
        rig.Register(Gen.Uav, 2);

        rig.Gateway.Dispose();
        rig.Peer.Evoke();

        Assert.Equal(2, rig.Peer.RemovedNames.Count);
        Assert.False(rig.Gateway.IsStarted);
        rig.Gateway.Dispose(); // idempotent
        rig.Send(new RprRegisterObject(9, Gen.Tank)); // after resign: rejected, not thrown
        Assert.Equal(1, rig.Gateway.RejectedOutbound);
    }
}
