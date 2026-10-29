using SimNetGateway.Showcase;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Transport.HlaRpr.Tests;

/// <summary>WeaponFire and MunitionDetonation both ways, with object references as instance names (DD-008 sections 3.4, 5.3).</summary>
public class InteractionTests
{
    private static readonly FuseTypeEnum16[] Fuses = Enum.GetValues<FuseTypeEnum16>();
    private static readonly WarheadTypeEnum16[] Warheads = Enum.GetValues<WarheadTypeEnum16>();

    /// <summary>A rig with one gateway object (named by the RTI), one peer object, and the peer subscribed to everything.</summary>
    private sealed class World
    {
        public World()
        {
            Rig = new Rig();
            Rig.Peer.SubscribePhysical(RprFom2.ObjectClasses.PhysicalEntity);
            Rig.Peer.SubscribeInteractions();
            Rig.Peer.PublishPhysical(RprFom2.ObjectClasses.GroundVehicle);
            Rig.Peer.PublishInteractions();
            GatewayObject = Rig.Register(Gen.Tank);
            PeerObject = Rig.Peer.Register(RprFom2.ObjectClasses.GroundVehicle);
            Rig.Settle();
            Assert.True(Rig.Gateway.TryGetObjectName(GatewayObject, out var gatewayName));
            GatewayName = gatewayName;
            PeerName = Rig.Peer.NameOf(PeerObject);
            Assert.True(Rig.Gateway.TryGetObjectHandle(PeerName, out var peerHandle));
            PeerHandle = peerHandle;
            Rig.Inbox.Messages.Clear();
        }

        public Rig Rig { get; }
        public RprObjectHandle GatewayObject { get; }
        public Protocol.Rpr.Hla.ObjectInstanceHandle PeerObject { get; }
        public string GatewayName { get; }
        public string PeerName { get; }
        public RprObjectHandle PeerHandle { get; }
    }

    private static RprObjectHandle? Ref(Random r, World w) => r.Next(5) switch
    {
        0 => null,
        1 => w.GatewayObject,
        2 => w.PeerHandle,
        3 => new RprObjectHandle(900_000 + (uint)r.Next(100)), // a handle nobody registered
        _ => null,
    };

    /// <summary>The name an object reference travels as; empty for none and for a handle the transport cannot name.</summary>
    private static string NameFor(World w, RprObjectHandle? h) =>
        h == w.GatewayObject ? w.GatewayName : h == w.PeerHandle ? w.PeerName : string.Empty;

    // ---- gateway -> peer ---------------------------------------------------------------------------------------------

    [Fact]
    public void Every_field_of_an_outbound_fire_reaches_the_peer_decoded()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var r = new Random(seed);
            var w = new World();
            var fire = new RprWeaponFire(
                new Guid(seed, 1, 2, [1, 2, 3, 4, 5, 6, 7, 8]), Ref(r, w), Ref(r, w), Gen.Loc(r), Gen.Type(r), r.Next(0, 70000))
            {
                FuseType = r.Next(2) == 0 ? (ushort?)Fuses[r.Next(Fuses.Length)] : null,
                WarheadType = r.Next(2) == 0 ? (ushort?)Warheads[r.Next(Warheads.Length)] : null,
                RateOfFire = r.Next(2) == 0 ? (ushort)r.Next(65536) : null,
                InitialVelocity = r.Next(2) == 0 ? Gen.Vec(r) : null,
                FireControlSolutionRange = r.Next(2) == 0 ? (float)Gen.F(r) : null,
                MunitionObject = Ref(r, w),
            };

            w.Rig.Send(fire);
            w.Rig.Round();

            var seen = Assert.Single(w.Rig.Peer.Fires);
            Assert.Empty(w.Rig.Peer.InteractionDecodeFailures);
            Assert.True(NameFor(w, fire.FiringObject) == seen.Firing, $"seed {seed}: firing");
            Assert.True(NameFor(w, fire.TargetObject) == seen.Target, $"seed {seed}: target");
            Assert.True(NameFor(w, fire.MunitionObject) == seen.MunitionObject, $"seed {seed}: munition object");
            Assert.True(seen.Event.IssuingObjectIdentifier == NameFor(w, fire.FiringObject), $"seed {seed}: the event is issued by the firing object");
            Assert.Equal(Gen.ToStruct(fire.Munition), seen.Munition);
            Assert.Equal(Gen.ToStruct(fire.Location), seen.Location);
            Assert.Equal((ushort)Math.Clamp(fire.Quantity, 0, 65535), seen.Quantity);
            Assert.Equal(fire.RateOfFire ?? 0, seen.Rate);
            Assert.Equal((FuseTypeEnum16)(fire.FuseType ?? 0), seen.Fuse);
            Assert.Equal((WarheadTypeEnum16)(fire.WarheadType ?? 0), seen.Warhead);
            Assert.Equal(fire.FireControlSolutionRange ?? 0f, seen.Range);
            var v = fire.InitialVelocity ?? default;
            Assert.Equal(new VelocityVectorStruct((float)v.X, (float)v.Y, (float)v.Z), seen.Velocity);
            Assert.Equal(0u, seen.MissionIndex);
            Assert.Equal(0, w.Rig.Gateway.EncodeErrors);
        }
    }

    [Fact]
    public void Every_field_of_an_outbound_detonation_reaches_the_peer_decoded()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var r = new Random(seed);
            var w = new World();
            var detonation = new RprMunitionDetonation(
                new Guid(seed, 3, 4, [1, 2, 3, 4, 5, 6, 7, 8]), Ref(r, w), Ref(r, w), Gen.Loc(r), Gen.Type(r), (RprDetonationResult)r.Next(0, 7))
            {
                FuseType = r.Next(2) == 0 ? (ushort?)Fuses[r.Next(Fuses.Length)] : null,
                WarheadType = r.Next(2) == 0 ? (ushort?)Warheads[r.Next(Warheads.Length)] : null,
                RateOfFire = r.Next(2) == 0 ? (ushort)r.Next(65536) : null,
                FinalVelocity = r.Next(2) == 0 ? Gen.Vec(r) : null,
                RelativeDetonationLocation = r.Next(2) == 0 ? Gen.Vec(r) : null,
                MunitionObject = Ref(r, w),
            };

            w.Rig.Send(detonation);
            w.Rig.Round();

            var seen = Assert.Single(w.Rig.Peer.Detonations);
            Assert.Empty(w.Rig.Peer.InteractionDecodeFailures);
            Assert.True(NameFor(w, detonation.FiringObject) == seen.Firing, $"seed {seed}: firing");
            Assert.True(NameFor(w, detonation.TargetObject) == seen.Target, $"seed {seed}: target");
            Assert.True(NameFor(w, detonation.MunitionObject) == seen.MunitionObject, $"seed {seed}: munition object");
            Assert.Equal(Gen.ToStruct(detonation.Munition), seen.Munition);
            Assert.Equal(Gen.ToStruct(detonation.Location), seen.Location);
            Assert.Equal((byte)detonation.Result, (byte)seen.Result); // the FOM numbering
            Assert.Equal(detonation.RateOfFire ?? 0, seen.Rate);
            Assert.Equal((FuseTypeEnum16)(detonation.FuseType ?? 0), seen.Fuse);
            Assert.Equal((WarheadTypeEnum16)(detonation.WarheadType ?? 0), seen.Warhead);
            var v = detonation.FinalVelocity ?? default;
            Assert.Equal(new VelocityVectorStruct((float)v.X, (float)v.Y, (float)v.Z), seen.Velocity);
            var rel = detonation.RelativeDetonationLocation ?? default;
            Assert.Equal(new RelativePositionStruct((float)rel.X, (float)rel.Y, (float)rel.Z), seen.Relative);
            Assert.Equal(0, seen.ArticulatedPartDataLength);
            Assert.Equal(1, seen.Quantity);
        }
    }

    [Fact]
    public void A_fire_names_its_objects_by_their_RTI_name_and_an_unknown_handle_is_an_empty_reference()
    {
        var w = new World();

        w.Rig.Send(new RprWeaponFire(Guid.NewGuid(), w.GatewayObject, new RprObjectHandle(123456), default, Gen.Tank, 1));
        w.Rig.Round();

        var seen = Assert.Single(w.Rig.Peer.Fires);
        Assert.Equal(w.GatewayName, seen.Firing);
        Assert.StartsWith("HLAobject_", seen.Firing);
        Assert.Equal(string.Empty, seen.Target);
        Assert.Equal(string.Empty, seen.MunitionObject);
    }

    // ---- event correlation (success condition d) -----------------------------------------------------------------------

    [Fact]
    public void A_fire_and_its_detonation_carry_the_same_EventIdentifier_to_the_peer()
    {
        var w = new World();
        var id = Guid.NewGuid();

        w.Rig.Send(new RprWeaponFire(id, w.GatewayObject, w.PeerHandle, default, Gen.Tank, 1));
        w.Rig.Send(new RprMunitionDetonation(id, w.GatewayObject, w.PeerHandle, default, Gen.Tank, RprDetonationResult.EntityImpact));
        w.Rig.Round();

        Assert.Equal(Assert.Single(w.Rig.Peer.Fires).Event, Assert.Single(w.Rig.Peer.Detonations).Event);
        Assert.Equal(w.GatewayName, w.Rig.Peer.Fires[0].Event.IssuingObjectIdentifier);
    }

    [Fact]
    public void Distinct_outbound_events_never_share_an_EventIdentifier()
    {
        var r = new Random(7);
        var w = new World();
        var seen = new HashSet<(ushort, string)>();
        var guids = new HashSet<Guid>();
        for (int i = 0; i < 400; i++)
        {
            var id = new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
            guids.Add(id);
            var firing = r.Next(3) switch { 0 => (RprObjectHandle?)w.GatewayObject, 1 => w.PeerHandle, _ => null };
            w.Rig.Send(new RprWeaponFire(id, firing, null, default, Gen.Tank, 1));
            if (r.Next(2) == 0)
            {
                w.Rig.Send(new RprMunitionDetonation(id, firing, null, default, Gen.Tank, RprDetonationResult.Other)); // the same event again
            }
        }

        w.Rig.Round();

        var fires = w.Rig.Peer.Fires;
        Assert.Equal(400, fires.Count);
        foreach (var fire in fires)
        {
            Assert.True(seen.Add((fire.Event.EventCount, fire.Event.IssuingObjectIdentifier)), $"duplicate event identifier {fire.Event}");
        }

        // every detonation carries the identifier of its fire
        var fireById = new Dictionary<int, EventIdentifierStruct>();
        for (int i = 0; i < fires.Count; i++)
        {
            fireById[i] = fires[i].Event;
        }

        Assert.All(w.Rig.Peer.Detonations, d => Assert.Contains(d.Event, fires.Select(f => f.Event)));
    }

    [Fact]
    public void An_outbound_correlation_expires_after_30_seconds_of_the_clock()
    {
        var w = new World();
        var early = Guid.NewGuid();
        var late = Guid.NewGuid();
        w.Rig.Send(new RprWeaponFire(early, w.GatewayObject, null, default, Gen.Tank, 1));
        w.Rig.Send(new RprWeaponFire(late, w.GatewayObject, null, default, Gen.Tank, 1));

        w.Rig.Clock.Advance(29.9);
        w.Rig.Send(new RprMunitionDetonation(early, w.GatewayObject, null, default, Gen.Tank, RprDetonationResult.Other));
        w.Rig.Clock.Advance(0.2); // 30.1 s after the fires
        w.Rig.Gateway.Pump();
        Assert.Equal(0, w.Rig.Gateway.EventCorrelationCount); // all aged out
        w.Rig.Send(new RprMunitionDetonation(late, w.GatewayObject, null, default, Gen.Tank, RprDetonationResult.Other));
        w.Rig.Round();

        var fires = w.Rig.Peer.Fires;
        Assert.Equal(fires[0].Event, w.Rig.Peer.Detonations[0].Event); // 29.9 s: still correlated
        Assert.NotEqual(fires[1].Event, w.Rig.Peer.Detonations[1].Event); // 30.1 s: gone, the detonation is a new event
    }

    [Fact]
    public void An_outbound_correlation_expires_at_exactly_30_seconds_and_not_before()
    {
        foreach (var (advance, correlated) in new[] { (29.999, true), (30.0, false), (31.0, false) })
        {
            var w = new World();
            var id = Guid.NewGuid();
            w.Rig.Send(new RprWeaponFire(id, w.GatewayObject, null, default, Gen.Tank, 1));
            w.Rig.Clock.Advance(advance);

            w.Rig.Send(new RprMunitionDetonation(id, w.GatewayObject, null, default, Gen.Tank, RprDetonationResult.Other));
            w.Rig.Round();

            Assert.Equal(correlated, w.Rig.Peer.Fires[0].Event == w.Rig.Peer.Detonations[0].Event);
        }
    }

    // ---- peer -> gateway ------------------------------------------------------------------------------------------------

    private static (ushort Count, string Issuer) RandomEvent(Random r, World w) =>
        ((ushort)r.Next(0, 65536), r.Next(3) switch { 0 => w.PeerName, 1 => w.GatewayName, _ => "Other" + r.Next(5) });

    private static string RandomRef(Random r, World w) => r.Next(5) switch
    {
        0 => string.Empty,
        1 => w.GatewayName,
        2 => w.PeerName,
        3 => "NoSuchObject" + r.Next(9), // unknown name: absent in the message, the message still comes
        _ => w.PeerName,
    };

    private static RprObjectHandle? HandleFor(World w, string name) =>
        name == w.GatewayName ? w.GatewayObject : name == w.PeerName ? w.PeerHandle : null;

    [Fact]
    public void Every_field_of_an_inbound_fire_is_delivered_and_unknown_names_become_absent_references()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var r = new Random(seed);
            var w = new World();
            var (count, issuer) = RandomEvent(r, w);
            string firing = RandomRef(r, w), target = RandomRef(r, w), munitionObject = RandomRef(r, w);
            var munition = Gen.ToStruct(Gen.Type(r));
            var location = Gen.ToStruct(Gen.Loc(r));
            ushort quantity = (ushort)r.Next(65536), rate = (ushort)r.Next(65536);
            var fuse = Fuses[r.Next(Fuses.Length)];
            var warhead = Warheads[r.Next(Warheads.Length)];
            float range = (float)Gen.F(r);
            var velocity = Gen.Vec(r);

            w.Rig.Peer.SendFire(ScriptedRprFederate.FireParameters(
                count, issuer, firing, target, munitionObject, munition, location, quantity, rate, fuse, warhead, range, 77,
                new VelocityVectorStruct((float)velocity.X, (float)velocity.Y, (float)velocity.Z)));
            w.Rig.Round();

            var m = Assert.IsType<RprWeaponFire>(Assert.Single(w.Rig.Inbox.Messages));
            Assert.NotEqual(Guid.Empty, m.EventId);
            Assert.True(HandleFor(w, firing) == m.FiringObject, $"seed {seed}: firing '{firing}'");
            Assert.True(HandleFor(w, target) == m.TargetObject, $"seed {seed}: target '{target}'");
            Assert.True(HandleFor(w, munitionObject) == m.MunitionObject, $"seed {seed}: munition object");
            Assert.Equal(Gen.ToDis(munition), m.Munition);
            Assert.Equal(new WorldLocation(location.X, location.Y, location.Z), m.Location);
            Assert.Equal((int)quantity, m.Quantity);
            Assert.Equal(rate, m.RateOfFire);
            Assert.Equal((ushort)fuse, m.FuseType);
            Assert.Equal((ushort)warhead, m.WarheadType);
            Assert.Equal(range, m.FireControlSolutionRange);
            Assert.Equal(velocity, m.InitialVelocity);
            Assert.Equal(0, w.Rig.Gateway.DecodeErrors);
        }
    }

    [Fact]
    public void Every_field_of_an_inbound_detonation_is_delivered_and_FOM_result_codes_beyond_None_become_Other()
    {
        foreach (var code in Enum.GetValues<DetonationResultCodeEnum8>())
        {
            var r = new Random((int)code);
            var w = new World();
            string firing = RandomRef(r, w), target = RandomRef(r, w);
            var munition = Gen.ToStruct(Gen.Type(r));
            var location = Gen.ToStruct(Gen.Loc(r));
            var velocity = Gen.Vec(r);
            var relative = Gen.Vec(r);

            w.Rig.Peer.SendDetonation(ScriptedRprFederate.DetonationParameters(
                9, w.PeerName, firing, target, w.PeerName, munition, location, 1, 120, FuseTypeEnum16.Contact, WarheadTypeEnum16.HighExplosive, code,
                new VelocityVectorStruct((float)velocity.X, (float)velocity.Y, (float)velocity.Z),
                new RelativePositionStruct((float)relative.X, (float)relative.Y, (float)relative.Z)));
            w.Rig.Round();

            var m = Assert.IsType<RprMunitionDetonation>(Assert.Single(w.Rig.Inbox.Messages));
            Assert.Equal((byte)code <= 6 ? (RprDetonationResult)(byte)code : RprDetonationResult.Other, m.Result);
            Assert.Equal(HandleFor(w, firing), m.FiringObject);
            Assert.Equal(HandleFor(w, target), m.TargetObject);
            Assert.Equal(w.PeerHandle, m.MunitionObject);
            Assert.Equal(Gen.ToDis(munition), m.Munition);
            Assert.Equal(new WorldLocation(location.X, location.Y, location.Z), m.Location);
            Assert.Equal((ushort)120, m.RateOfFire);
            Assert.Equal((ushort)FuseTypeEnum16.Contact, m.FuseType);
            Assert.Equal((ushort)WarheadTypeEnum16.HighExplosive, m.WarheadType);
            Assert.Equal(velocity, m.FinalVelocity);
            Assert.Equal(relative, m.RelativeDetonationLocation);
        }
    }

    [Fact]
    public void An_inbound_fire_and_detonation_with_the_same_EventIdentifier_get_the_same_Guid_and_distinct_events_never_collide()
    {
        var w = new World();
        var loc = new WorldLocationStruct(1, 2, 3);
        var type = Gen.ToStruct(Gen.Tank);
        var identifiers = new List<(ushort, string)>();
        for (ushort count = 0; count < 20; count++)
        {
            foreach (var issuer in new[] { w.PeerName, w.GatewayName, "" })
            {
                identifiers.Add((count, issuer));
            }
        }

        foreach (var (count, issuer) in identifiers)
        {
            w.Rig.Peer.SendFire(ScriptedRprFederate.FireParameters(count, issuer, w.PeerName, "", "", type, loc, 1, 0, FuseTypeEnum16.Other, WarheadTypeEnum16.Other, 0, 0, default));
        }

        foreach (var (count, issuer) in identifiers.AsEnumerable().Reverse())
        {
            w.Rig.Peer.SendDetonation(ScriptedRprFederate.DetonationParameters(count, issuer, w.PeerName, "", "", type, loc, 1, 0, FuseTypeEnum16.Other, WarheadTypeEnum16.Other, DetonationResultCodeEnum8.EntityImpact, default, default));
        }

        w.Rig.Round();

        var fires = w.Rig.Inbox.Of<RprWeaponFire>().ToList();
        var detonations = w.Rig.Inbox.Of<RprMunitionDetonation>().ToList();
        Assert.Equal(identifiers.Count, fires.Count);
        Assert.Equal(identifiers.Count, fires.Select(f => f.EventId).Distinct().Count()); // distinct events never collide
        for (int i = 0; i < identifiers.Count; i++)
        {
            Assert.Equal(fires[identifiers.Count - 1 - i].EventId, detonations[i].EventId); // the same event, the same Guid
        }
    }

    [Fact]
    public void An_inbound_correlation_expires_after_30_seconds_of_the_clock()
    {
        foreach (var (advance, correlated) in new[] { (29.999, true), (30.0, false), (45.0, false) })
        {
            var w = new World();
            var loc = new WorldLocationStruct(1, 2, 3);
            var type = Gen.ToStruct(Gen.Tank);
            w.Rig.Peer.SendFire(ScriptedRprFederate.FireParameters(12, w.PeerName, w.PeerName, "", "", type, loc, 1, 0, FuseTypeEnum16.Other, WarheadTypeEnum16.Other, 0, 0, default));
            w.Rig.Round();
            w.Rig.Clock.Advance(advance);
            w.Rig.Peer.SendDetonation(ScriptedRprFederate.DetonationParameters(12, w.PeerName, w.PeerName, "", "", type, loc, 1, 0, FuseTypeEnum16.Other, WarheadTypeEnum16.Other, DetonationResultCodeEnum8.Other, default, default));
            w.Rig.Round();

            var fire = w.Rig.Inbox.Of<RprWeaponFire>().Single();
            var detonation = w.Rig.Inbox.Of<RprMunitionDetonation>().Single();
            Assert.Equal(correlated, fire.EventId == detonation.EventId);
        }
    }

    [Fact]
    public void An_event_the_gateway_sent_is_recognised_when_the_peer_answers_with_its_identifier()
    {
        var w = new World();
        var id = Guid.NewGuid();
        w.Rig.Send(new RprWeaponFire(id, w.GatewayObject, w.PeerHandle, default, Gen.Tank, 1));
        w.Rig.Round();
        var identifier = Assert.Single(w.Rig.Peer.Fires).Event;

        w.Rig.Peer.SendDetonation(ScriptedRprFederate.DetonationParameters(
            identifier.EventCount, identifier.IssuingObjectIdentifier, w.GatewayName, w.PeerName, "", Gen.ToStruct(Gen.Tank),
            new WorldLocationStruct(1, 2, 3), 1, 0, FuseTypeEnum16.Other, WarheadTypeEnum16.Other, DetonationResultCodeEnum8.EntityImpact, default, default));
        w.Rig.Round();

        Assert.Equal(id, Assert.IsType<RprMunitionDetonation>(Assert.Single(w.Rig.Inbox.Messages)).EventId);
    }

    [Fact]
    public void An_undecodable_parameter_is_counted_and_defaulted_and_the_rest_of_the_fire_is_delivered()
    {
        var w = new World();
        var parameters = ScriptedRprFederate.FireParameters(
            1, w.PeerName, w.PeerName, w.GatewayName, "", Gen.ToStruct(Gen.Tank), new WorldLocationStruct(1, 2, 3), 4, 5,
            FuseTypeEnum16.Contact, WarheadTypeEnum16.HighExplosive, 6, 7, default);
        parameters[RprFom2.WeaponFireParameters.FiringLocation] = new byte[10]; // truncated
        parameters[RprFom2.WeaponFireParameters.FuseType] = [0xFF, 0xFF]; // not a FOM enumerator
        parameters.Remove(RprFom2.WeaponFireParameters.WarheadType); // simply absent: no error

        w.Rig.Peer.SendFire(parameters);
        w.Rig.Round();

        var m = Assert.IsType<RprWeaponFire>(Assert.Single(w.Rig.Inbox.Messages));
        Assert.Equal(w.PeerHandle, m.FiringObject);
        Assert.Equal(w.GatewayObject, m.TargetObject);
        Assert.Equal(4, m.Quantity);
        Assert.Equal(default, m.Location);
        Assert.Null(m.FuseType);
        Assert.Null(m.WarheadType);
        Assert.Equal((ushort)5, m.RateOfFire);
        Assert.Equal(2, w.Rig.Gateway.DecodeErrors);
    }

    [Fact]
    public void A_fire_without_a_decodable_EventIdentifier_is_delivered_with_a_Guid_of_its_own()
    {
        var w = new World();
        var parameters = ScriptedRprFederate.FireParameters(
            1, w.PeerName, w.PeerName, "", "", Gen.ToStruct(Gen.Tank), default, 1, 0, FuseTypeEnum16.Other, WarheadTypeEnum16.Other, 0, 0, default);
        parameters[RprFom2.WeaponFireParameters.EventIdentifier] = [1];

        w.Rig.Peer.SendFire(parameters);
        w.Rig.Peer.SendFire(parameters);
        w.Rig.Round();

        var fires = w.Rig.Inbox.Of<RprWeaponFire>().ToList();
        Assert.Equal(2, fires.Count);
        Assert.NotEqual(fires[0].EventId, fires[1].EventId); // nothing to correlate them by
        Assert.Equal(2, w.Rig.Gateway.DecodeErrors);
    }
}
