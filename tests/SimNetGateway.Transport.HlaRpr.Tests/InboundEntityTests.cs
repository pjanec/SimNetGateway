using SimNetGateway.Showcase;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Transport.HlaRpr.Tests;

/// <summary>The HLA -> gateway direction for objects (DD-008 sections 3.4, 5.2 - 5.4): discovery, reflections, removal, isolation.</summary>
public class InboundEntityTests
{
    private static readonly string[] PhysicalClasses =
    [
        RprFom2.ObjectClasses.GroundVehicle, RprFom2.ObjectClasses.Aircraft, RprFom2.ObjectClasses.SurfaceVessel,
        RprFom2.ObjectClasses.Platform, RprFom2.ObjectClasses.Human, RprFom2.ObjectClasses.Munition, RprFom2.ObjectClasses.PhysicalEntity,
    ];

    // ---- the reference decoder: "exactly the decoded attributes" ------------------------------------------------------

    /// <summary>
    /// What the gateway must receive for a set of raw attribute values: each attribute decoded with RprFom2 (this file's own use of
    /// the codecs, not the transport's), mapped by the rules of DD-008 3.4; undecodable ones counted and left out.
    /// </summary>
    internal static (RprObjectUpdate? Update, int Errors) Expected(RprObjectHandle handle, IReadOnlyDictionary<string, byte[]> raw)
    {
        int errors = 0;
        string? marking = null;
        DisEntityType? type = null;
        RprForceId? force = null;
        DisEntityId? id = null;
        RprDamageState? damage = null;
        WorldLocation? loc = null;
        RprOrientation? ori = null;
        RprVector? vel = null, acc = null, ang = null;
        byte? alg = null;
        bool any = false;
        foreach (var (name, bytes) in raw)
        {
            switch (name)
            {
                case "EntityType":
                    if (RprFom2.TryDecodeEntityType(bytes, out var t)) { type = Gen.ToDis(t); any = true; } else { errors++; }
                    break;
                case "EntityIdentifier":
                    if (RprFom2.TryDecodeEntityIdentifier(bytes, out var i))
                    {
                        id = new DisEntityId(i.FederateIdentifier.SiteId, i.FederateIdentifier.ApplicationId, i.EntityNumber);
                        any = true;
                    }
                    else { errors++; }
                    break;
                case "Marking":
                    if (RprFom2.TryDecodeMarking(bytes, out var m)) { marking = m.AsciiText; any = true; } else { errors++; }
                    break;
                case "ForceIdentifier":
                    if (RprFom2.TryDecodeForceIdentifier(bytes, out var f)) { force = BaseForce((byte)f); any = true; } else { errors++; }
                    break;
                case "DamageState":
                    if (RprFom2.TryDecodeDamageStatus(bytes, out var d)) { damage = (RprDamageState)(byte)d; any = true; } else { errors++; }
                    break;
                case "Spatial":
                    if (RprFom2.TryDecodeSpatial(bytes, out var s))
                    {
                        loc = new WorldLocation(s.WorldLocation.X, s.WorldLocation.Y, s.WorldLocation.Z);
                        ori = new RprOrientation(s.Orientation.Psi, s.Orientation.Theta, s.Orientation.Phi);
                        alg = (byte)s.Algorithm;
                        if (s.HasVelocity) { vel = new RprVector(s.Velocity.X, s.Velocity.Y, s.Velocity.Z); }
                        if (s.HasAcceleration) { acc = new RprVector(s.Acceleration.X, s.Acceleration.Y, s.Acceleration.Z); }
                        if (s.HasAngularVelocity) { ang = new RprVector(s.AngularVelocity.X, s.AngularVelocity.Y, s.AngularVelocity.Z); }
                        any = true;
                    }
                    else { errors++; }
                    break;
            }
        }

        if (!any)
        {
            return (null, errors);
        }

        return (new RprObjectUpdate(handle)
        {
            Marking = marking,
            EntityType = type,
            ForceId = force,
            EntityIdentifier = id,
            DamageState = damage,
            WorldLocation = loc,
            Orientation = ori,
            VelocityVector = vel,
            AccelerationVector = acc,
            AngularVelocity = ang,
            DeadReckoningAlgorithm = alg,
        }, errors);
    }

    /// <summary>ForceIdentifierEnum8 -> base force: 4 Friendly_2, 5 Opposing_2, 6 Neutral_2, 7 Friendly_3 ... (the SISO-REF-010 pattern).</summary>
    internal static RprForceId BaseForce(int v)
    {
        if (v <= 3)
        {
            return (RprForceId)v;
        }

        return ((v - 4) % 3) switch { 0 => RprForceId.Friendly, 1 => RprForceId.Opposing, _ => RprForceId.Neutral };
    }

    private static SpatialVariantStruct RandomSpatial(Random r)
    {
        var algorithm = (DeadReckoningAlgorithmEnum8)r.Next(1, 10);
        var probe = new SpatialVariantStruct(algorithm, default, false, default, default, default, default);
        var o = Gen.Ori(r);
        var v = Gen.Vec(r);
        var a = Gen.Vec(r);
        var w = Gen.Vec(r);
        var l = Gen.Loc(r);
        return new SpatialVariantStruct(
            algorithm, new WorldLocationStruct(l.X, l.Y, l.Z), r.Next(2) == 0,
            new OrientationStruct((float)o.Psi, (float)o.Theta, (float)o.Phi),
            probe.HasVelocity ? new VelocityVectorStruct((float)v.X, (float)v.Y, (float)v.Z) : default,
            probe.HasAcceleration ? new AccelerationVectorStruct((float)a.X, (float)a.Y, (float)a.Z) : default,
            probe.HasAngularVelocity ? new AngularVelocityVectorStruct((float)w.X, (float)w.Y, (float)w.Z) : default);
    }

    /// <summary>A random subset (at least <paramref name="atLeast"/>) of valid raw attribute values, by simple attribute name.</summary>
    private static Dictionary<string, byte[]> RandomRaw(Random r, int atLeast = 1)
    {
        Dictionary<string, byte[]> raw;
        do
        {
            raw = new Dictionary<string, byte[]>();
            if (r.Next(2) == 0) { raw["EntityType"] = RprFom2.EncodeEntityType(Gen.ToStruct(Gen.Type(r))); }
            if (r.Next(2) == 0) { var id = Gen.Id(r); raw["EntityIdentifier"] = RprFom2.EncodeEntityIdentifier(new EntityIdentifierStruct(new FederateIdentifierStruct(id.Site, id.Application), id.Entity)); }
            if (r.Next(2) == 0) { raw["Marking"] = RprFom2.EncodeMarking(MarkingStruct.FromAscii(Gen.Marking(r))); }
            if (r.Next(2) == 0) { raw["ForceIdentifier"] = RprFom2.EncodeForceIdentifier(Enum.GetValues<ForceIdentifierEnum8>()[r.Next(31)]); }
            if (r.Next(2) == 0) { raw["DamageState"] = RprFom2.EncodeDamageStatus((DamageStatusEnum32)r.Next(4)); }
            if (r.Next(2) == 0) { raw["Spatial"] = RprFom2.EncodeSpatial(RandomSpatial(r)); }
        }
        while (raw.Count < atLeast);
        return raw;
    }

    private static ScriptedRprFederate.Attributes ToAttributes(Dictionary<string, byte[]> raw)
    {
        var a = new ScriptedRprFederate.Attributes();
        foreach (var (name, bytes) in raw)
        {
            a.Raw(name, bytes);
        }
        return a;
    }

    private static RprObjectHandle HandleOf(Rig rig, string peerObjectName)
    {
        Assert.True(rig.Gateway.TryGetObjectHandle(peerObjectName, out var handle), "the gateway has not discovered " + peerObjectName);
        return handle;
    }

    // ---- discovery ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_discovered_object_gets_a_value_request_and_the_answer_arrives_as_one_update_with_exactly_the_decoded_attributes()
    {
        var rig = new Rig(startGateway: false);
        rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
        var obj = rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
        var r = new Random(1);
        var raw = RandomRaw(r, 6);
        rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, ToAttributes(raw));
        rig.Gateway.Start(); // late subscription: the object is discovered now, no reflection is pending

        rig.Gateway.Pump(); // discovery -> the transport requests the values
        Assert.Contains(rig.Hub.Trace, line => line.Contains("RequestAttributeValueUpdate"));
        rig.Peer.Evoke();
        Assert.Equal([obj], rig.Peer.ProvideRequests);
        rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, ToAttributes(raw)); // the owner answers the request
        rig.Round();

        var handle = HandleOf(rig, rig.Peer.NameOf(obj));
        var message = Assert.Single(rig.Inbox.Messages);
        var (expected, errors) = Expected(handle, raw);
        Assert.Equal(expected, message);
        Assert.Equal(0, errors);
        Assert.Equal(0, rig.Gateway.DecodeErrors);
    }

    [Fact]
    public void Our_own_objects_are_never_discovered_back()
    {
        var rig = new Rig();
        rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
        rig.Register(Gen.Tank);
        rig.Hub.ClearTrace();

        rig.Settle();

        Assert.DoesNotContain(rig.Hub.Trace, line => line.Contains("callback DiscoverObjectInstance") && line.StartsWith("A1 "));
        Assert.Empty(rig.Inbox.Of<RprObjectUpdate>());
    }

    // ---- reflections (success condition a, the other direction) ------------------------------------------------------

    [Fact]
    public void Every_reflection_becomes_one_update_with_exactly_the_decoded_attributes()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var r = new Random(seed);
            var rig = new Rig();
            var cls = PhysicalClasses[r.Next(PhysicalClasses.Length)];
            rig.Peer.PublishPhysical(cls);
            var obj = rig.Peer.Register(cls);
            rig.Settle();
            var handle = HandleOf(rig, rig.Peer.NameOf(obj));

            int steps = r.Next(1, 8);
            for (int step = 0; step < steps; step++)
            {
                rig.Inbox.Messages.Clear();
                var raw = RandomRaw(r);
                rig.Peer.Update(obj, cls, ToAttributes(raw));
                rig.Round();

                var (expected, errors) = Expected(handle, raw);
                var message = Assert.Single(rig.Inbox.Messages);
                Assert.True(expected!.Equals(message), $"seed {seed} step {step}:\n expected {expected}\n actual   {message}");
                Assert.Equal(0, errors);
            }

            Assert.Equal(0, rig.Gateway.DecodeErrors);
        }
    }

    [Fact]
    public void A_spatial_value_delivers_the_fields_its_algorithm_carries_and_no_others()
    {
        var rig = new Rig();
        rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
        var obj = rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
        rig.Settle();

        foreach (var algorithm in Enum.GetValues<DeadReckoningAlgorithmEnum8>().Where(a => a != DeadReckoningAlgorithmEnum8.Other))
        {
            rig.Inbox.Messages.Clear();
            var probe = new SpatialVariantStruct(algorithm, default, false, default, default, default, default);
            var spatial = new SpatialVariantStruct(
                algorithm, new WorldLocationStruct(1, 2, 3), false, new OrientationStruct(0.5f, 0.25f, 0.125f),
                probe.HasVelocity ? new VelocityVectorStruct(4, 5, 6) : default,
                probe.HasAcceleration ? new AccelerationVectorStruct(7, 8, 9) : default,
                probe.HasAngularVelocity ? new AngularVelocityVectorStruct(10, 11, 12) : default);
            rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, new ScriptedRprFederate.Attributes().Spatial(spatial));
            rig.Round();

            var u = Assert.IsType<RprObjectUpdate>(Assert.Single(rig.Inbox.Messages));
            Assert.Equal((byte)algorithm, u.DeadReckoningAlgorithm);
            Assert.Equal(new WorldLocation(1, 2, 3), u.WorldLocation);
            Assert.Equal(new RprOrientation(0.5, 0.25, 0.125), u.Orientation);
            Assert.Equal(probe.HasVelocity ? new RprVector(4, 5, 6) : null, u.VelocityVector);
            Assert.Equal(probe.HasAcceleration ? new RprVector(7, 8, 9) : null, u.AccelerationVector);
            Assert.Equal(probe.HasAngularVelocity ? new RprVector(10, 11, 12) : null, u.AngularVelocity);
        }
    }

    [Fact]
    public void ForceIdentifier_values_above_3_map_to_their_base_force()
    {
        var rig = new Rig();
        rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
        var obj = rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
        rig.Settle();

        foreach (var force in Enum.GetValues<ForceIdentifierEnum8>())
        {
            rig.Inbox.Messages.Clear();
            rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, new ScriptedRprFederate.Attributes().Force(force));
            rig.Round();

            var u = Assert.IsType<RprObjectUpdate>(Assert.Single(rig.Inbox.Messages));
            Assert.Equal(BaseForce((byte)force), u.ForceId);
        }

        // spot checks of the table itself, written out: Friendly_2 = 4 -> Friendly, Opposing_3 = 8 -> Opposing, Neutral_10 = 30 -> Neutral
        Assert.Equal(RprForceId.Friendly, BaseForce((byte)ForceIdentifierEnum8.Friendly_2));
        Assert.Equal(RprForceId.Opposing, BaseForce((byte)ForceIdentifierEnum8.Opposing_3));
        Assert.Equal(RprForceId.Neutral, BaseForce((byte)ForceIdentifierEnum8.Neutral_10));
    }

    [Fact]
    public void Friendly_2_is_delivered_as_Friendly_and_not_as_Other()
    {
        var rig = new Rig();
        rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
        var obj = rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
        rig.Settle();

        rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, new ScriptedRprFederate.Attributes().Force(ForceIdentifierEnum8.Friendly_2));
        rig.Round();

        Assert.Equal(RprForceId.Friendly, Assert.IsType<RprObjectUpdate>(Assert.Single(rig.Inbox.Messages)).ForceId);
    }

    // ---- removal --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_removal_by_the_peer_becomes_RprObjectRemoved_and_forgets_the_name()
    {
        var rig = new Rig();
        rig.Peer.PublishPhysical(RprFom2.ObjectClasses.Aircraft);
        var obj = rig.Peer.Register(RprFom2.ObjectClasses.Aircraft);
        rig.Settle();
        var name = rig.Peer.NameOf(obj);
        var handle = HandleOf(rig, name);
        rig.Inbox.Messages.Clear();

        rig.Peer.Rti.DeleteObjectInstance(obj, Array.Empty<byte>());
        rig.Round();

        Assert.Equal([new RprObjectRemoved(handle)], rig.Inbox.Of<RprObjectRemoved>());
        Assert.False(rig.Gateway.TryGetObjectHandle(name, out _));
        Assert.False(rig.Gateway.TryGetObjectName(handle, out _));
    }

    [Fact]
    public void A_peer_that_resigns_removes_all_its_objects()
    {
        var rig = new Rig();
        rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
        var handles = Enumerable.Range(0, 5).Select(_ => rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle)).ToList();
        rig.Settle();
        var gatewayHandles = handles.Select(h => HandleOf(rig, rig.Peer.NameOf(h))).ToList();
        rig.Inbox.Messages.Clear();

        rig.Peer.Rti.ResignFederationExecution(SimNetGateway.Protocol.Rpr.Hla.ResignAction.DeleteObjects);
        rig.Settle();

        Assert.Equal(gatewayHandles.Select(h => h.Value).Order(), rig.Inbox.Of<RprObjectRemoved>().Select(m => m.Handle.Value).Order());
    }

    // ---- isolation (success condition e) ------------------------------------------------------------------------------

    [Fact]
    public void A_corrupt_attribute_drops_only_that_attribute_and_is_counted()
    {
        var rig = new Rig();
        rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
        var obj = rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
        rig.Settle();
        var handle = HandleOf(rig, rig.Peer.NameOf(obj));
        var truncatedSpatial = RprFom2.EncodeSpatial(new SpatialVariantStruct(
            DeadReckoningAlgorithmEnum8.DRM_FPW, new WorldLocationStruct(1, 2, 3), false, default, new VelocityVectorStruct(1, 1, 1), default, default))[..30];
        rig.Inbox.Messages.Clear();

        rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, new ScriptedRprFederate.Attributes()
            .Raw("Spatial", truncatedSpatial)
            .Marking("Wolf")
            .Force(ForceIdentifierEnum8.Opposing));
        rig.Round(); // no exception out of Pump

        var u = Assert.IsType<RprObjectUpdate>(Assert.Single(rig.Inbox.Messages));
        Assert.Equal(new RprObjectUpdate(handle) { Marking = "Wolf", ForceId = RprForceId.Opposing }, u);
        Assert.Null(u.WorldLocation);
        Assert.Equal(1, rig.Gateway.DecodeErrors);
        Assert.Equal(0, rig.Gateway.CallbackErrors);
    }

    [Fact]
    public void A_reflection_whose_every_attribute_is_corrupt_delivers_nothing()
    {
        var rig = new Rig();
        rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
        var obj = rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
        rig.Settle();
        rig.Inbox.Messages.Clear();

        rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, new ScriptedRprFederate.Attributes()
            .Raw("Marking", new byte[3]).Raw("ForceIdentifier", [31]).Raw("DamageState", [0, 0, 0, 9]));
        rig.Round();

        Assert.Empty(rig.Inbox.Messages);
        Assert.Equal(3, rig.Gateway.DecodeErrors); // an enumerator outside the FOM is as undecodable as a short value
    }

    [Fact]
    public void Random_corruption_of_any_subset_drops_exactly_those_attributes_and_never_throws()
    {
        int totalErrors = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            var r = new Random(seed);
            var rig = new Rig();
            rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
            var obj = rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
            rig.Settle();
            var handle = HandleOf(rig, rig.Peer.NameOf(obj));
            rig.Inbox.Messages.Clear();

            var raw = RandomRaw(r, 2);
            var good = new Dictionary<string, byte[]>();
            int corrupted = 0;
            foreach (var (name, bytes) in raw.ToList())
            {
                if (r.Next(2) == 0)
                {
                    // truncation by 1..n octets or one trailing octet: both are always undecodable for these fixed-size values
                    raw[name] = r.Next(2) == 0 ? bytes[..r.Next(0, bytes.Length)] : [.. bytes, (byte)r.Next(256)];
                    corrupted++;
                }
                else
                {
                    good[name] = bytes;
                }
            }

            int errorsBefore = rig.Gateway.DecodeErrors;
            rig.Peer.Update(obj, RprFom2.ObjectClasses.GroundVehicle, ToAttributes(raw));
            rig.Round();

            Assert.True(rig.Gateway.DecodeErrors - errorsBefore == corrupted, $"seed {seed}: counted {rig.Gateway.DecodeErrors - errorsBefore}, corrupted {corrupted}");
            totalErrors += corrupted;
            var (expected, _) = Expected(handle, good);
            if (expected is null)
            {
                Assert.True(rig.Inbox.Messages.Count == 0, $"seed {seed}: nothing valid was sent but {rig.Inbox.Messages.Count} messages came");
            }
            else
            {
                var message = Assert.Single(rig.Inbox.Messages);
                Assert.True(expected.Equals(message), $"seed {seed}:\n expected {expected}\n actual   {message}");
            }
        }

        Assert.True(totalErrors > 100); // the generator really corrupts
    }

    [Fact]
    public void Garbage_bytes_never_break_the_pump_and_what_decodes_is_delivered()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var r = new Random(seed);
            var rig = new Rig();
            rig.Peer.PublishPhysical(RprFom2.ObjectClasses.Aircraft);
            var obj = rig.Peer.Register(RprFom2.ObjectClasses.Aircraft);
            rig.Settle();
            var handle = HandleOf(rig, rig.Peer.NameOf(obj));
            rig.Inbox.Messages.Clear();
            var raw = new Dictionary<string, byte[]>();
            foreach (var name in new[] { "EntityType", "EntityIdentifier", "Marking", "ForceIdentifier", "DamageState", "Spatial" })
            {
                if (r.Next(3) != 0)
                {
                    var bytes = new byte[r.Next(0, 90)];
                    r.NextBytes(bytes);
                    raw[name] = bytes;
                }
            }

            if (raw.Count == 0)
            {
                continue;
            }

            int errorsBefore = rig.Gateway.DecodeErrors;
            rig.Peer.Update(obj, RprFom2.ObjectClasses.Aircraft, ToAttributes(raw));
            rig.Round();

            var (expected, errors) = Expected(handle, raw);
            Assert.True(rig.Gateway.DecodeErrors - errorsBefore == errors, $"seed {seed}: error count");
            if (expected is null)
            {
                Assert.Empty(rig.Inbox.Messages);
            }
            else
            {
                Assert.True(expected.Equals(Assert.Single(rig.Inbox.Messages)), $"seed {seed}");
            }

            Assert.Equal(0, rig.Gateway.CallbackErrors);
        }
    }
}
