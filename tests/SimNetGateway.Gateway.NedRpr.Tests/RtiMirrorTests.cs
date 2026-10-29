using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;
using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// The comparison of what the gateway sent with what the scripted federate decoded (<see cref="RtiMirror"/>, the core of G-9): a
/// federate that holds exactly what was sent has no difference; every kind of deviation is named. The expectations are written
/// out here from the FOM, so a comparison that always says "equal" fails these tests.
/// </summary>
public class RtiMirrorTests
{
    private static readonly DisEntityType Uav = new(1, 2, 225, 50, 1, 0, 0);

    private static readonly RprObjectUpdate Sent = new(new RprObjectHandle(1))
    {
        Marking = "Hawk",
        EntityType = Uav,
        ForceId = RprForceId.Friendly,
        EntityIdentifier = new DisEntityId(1, 3001, 5),
        DamageState = RprDamageState.SlightDamage,
        WorldLocation = new WorldLocation(3_986_154.8, 993_860.0, 4_863_172.1),
        Orientation = new RprOrientation(0.5, 0.25, 0.125),
        VelocityVector = new RprVector(-12.5, 48.5, 0.25),
        DeadReckoningAlgorithm = 2,
    };

    /// <summary>The federate's view of an object that holds exactly <paramref name="u"/> (FPW variant when the algorithm is 2).</summary>
    private static PeerObject Holding(RprObjectUpdate u)
    {
        var o = new PeerObject(new ObjectInstanceHandle(1), "HLAobject_1", RprFom2.ObjectClasses.PhysicalEntity);
        if (u.Marking is not null) { o.Marking = MarkingStruct.FromAscii(u.Marking); }
        if (u.EntityType is { } t) { o.EntityType = new EntityTypeStruct(t.Kind, t.Domain, t.Country, t.Category, t.Subcategory, t.Specific, t.Extra); }
        if (u.ForceId is { } f) { o.Force = (ForceIdentifierEnum8)(byte)f; }
        if (u.EntityIdentifier is { } id) { o.EntityIdentifier = new EntityIdentifierStruct(new FederateIdentifierStruct(id.Site, id.Application), id.Entity); }
        if (u.DamageState is { } d) { o.Damage = (DamageStatusEnum32)(byte)d; }
        if (u.WorldLocation is { } l)
        {
            var ori = u.Orientation ?? default;
            var vel = u.VelocityVector ?? default;
            o.Spatial = new SpatialVariantStruct(
                (DeadReckoningAlgorithmEnum8)(u.DeadReckoningAlgorithm ?? 1), new WorldLocationStruct(l.X, l.Y, l.Z), false,
                new OrientationStruct((float)ori.Psi, (float)ori.Theta, (float)ori.Phi),
                new VelocityVectorStruct((float)vel.X, (float)vel.Y, (float)vel.Z), default, default);
        }

        return o;
    }

    [Fact]
    public void A_federate_that_holds_what_was_sent_has_no_difference()
    {
        Assert.Empty(RtiMirror.Differences(Sent, Holding(Sent)));
    }

    [Fact]
    public void A_field_the_message_does_not_carry_is_not_compared()
    {
        var onlyMarking = new RprObjectUpdate(Sent.Handle) { Marking = "Hawk" };
        var seen = Holding(Sent);
        seen.Force = ForceIdentifierEnum8.Neutral; // not part of what was sent

        Assert.Empty(RtiMirror.Differences(onlyMarking, seen));
    }

    public static IEnumerable<object[]> Deviations() =>
    [
        ["marking", (Action<PeerObject>)(o => o.Marking = MarkingStruct.FromAscii("Hawx"))],
        ["marking", (Action<PeerObject>)(o => o.Marking = null)],
        ["entity type", (Action<PeerObject>)(o => o.EntityType = o.EntityType!.Value with { Category = 51 })],
        ["force", (Action<PeerObject>)(o => o.Force = ForceIdentifierEnum8.Opposing)],
        ["entity identifier", (Action<PeerObject>)(o => o.EntityIdentifier = o.EntityIdentifier!.Value with { EntityNumber = 6 })],
        ["damage state", (Action<PeerObject>)(o => o.Damage = DamageStatusEnum32.Destroyed)],
        ["location", (Action<PeerObject>)(o => o.Spatial = o.Spatial!.Value with { WorldLocation = new WorldLocationStruct(1, 2, 3) })],
        ["orientation", (Action<PeerObject>)(o => o.Spatial = o.Spatial!.Value with { Orientation = default })],
        ["velocity", (Action<PeerObject>)(o => o.Spatial = o.Spatial!.Value with { Velocity = default })],
        ["dead reckoning algorithm", (Action<PeerObject>)(o => o.Spatial = o.Spatial!.Value with { Algorithm = DeadReckoningAlgorithmEnum8.Static })],
        ["spatial state never arrived", (Action<PeerObject>)(o => o.Spatial = null)],
    ];

    [Theory]
    [MemberData(nameof(Deviations))]
    public void Every_deviation_of_a_decoded_attribute_is_named(string what, Action<PeerObject> deviate)
    {
        var seen = Holding(Sent);
        deviate(seen);

        var differences = RtiMirror.Differences(Sent, seen);

        Assert.Contains(differences, d => d.Contains(what, StringComparison.Ordinal));
    }

    [Fact]
    public void A_velocity_is_only_compared_when_the_variant_carries_one()
    {
        var staticSent = Sent with { DeadReckoningAlgorithm = 1 };
        var seen = Holding(staticSent);
        seen.Spatial = seen.Spatial!.Value with { Velocity = default }; // a Static variant decodes without a velocity

        Assert.Empty(RtiMirror.Differences(staticSent, seen));
    }

    [Fact]
    public void Without_an_algorithm_the_variant_is_static_without_a_velocity_and_FPW_with_one()
    {
        var noAlgorithm = Sent with { DeadReckoningAlgorithm = null };
        var fpw = Holding(noAlgorithm);
        fpw.Spatial = fpw.Spatial!.Value with { Algorithm = DeadReckoningAlgorithmEnum8.DRM_FPW };
        Assert.Empty(RtiMirror.Differences(noAlgorithm, fpw));

        var noVelocity = noAlgorithm with { VelocityVector = null };
        var stat = Holding(noVelocity);
        stat.Spatial = stat.Spatial!.Value with { Algorithm = DeadReckoningAlgorithmEnum8.Static };
        Assert.Empty(RtiMirror.Differences(noVelocity, stat));

        stat.Spatial = stat.Spatial.Value with { Algorithm = DeadReckoningAlgorithmEnum8.DRM_FPW };
        Assert.NotEmpty(RtiMirror.Differences(noVelocity, stat));
    }

    [Fact]
    public void Merge_lets_every_field_of_a_later_update_override_and_keeps_the_rest()
    {
        var later = new RprObjectUpdate(Sent.Handle) { Marking = "Hawk2", WorldLocation = new WorldLocation(1, 2, 3), DamagePercent = 40 };

        var merged = RtiMirror.Merge(Sent, later);

        Assert.Equal("Hawk2", merged.Marking);
        Assert.Equal(new WorldLocation(1, 2, 3), merged.WorldLocation);
        Assert.Equal(Sent.EntityType, merged.EntityType);
        Assert.Equal(Sent.VelocityVector, merged.VelocityVector);
        Assert.Equal(Sent.DeadReckoningAlgorithm, merged.DeadReckoningAlgorithm);
        Assert.Equal((byte)40, merged.DamagePercent);
        Assert.Same(later, RtiMirror.Merge(null, later));
    }

    [Fact]
    public void Only_an_update_with_an_hla_attribute_carries_attributes()
    {
        Assert.False(RtiMirror.CarriesAttributes(new RprObjectUpdate(Sent.Handle) { DamagePercent = 10 }));
        Assert.False(RtiMirror.CarriesAttributes(new RprObjectUpdate(Sent.Handle)));
        Assert.True(RtiMirror.CarriesAttributes(new RprObjectUpdate(Sent.Handle) { DeadReckoningAlgorithm = 1 }));
        Assert.True(RtiMirror.CarriesAttributes(new RprObjectUpdate(Sent.Handle) { DamageState = RprDamageState.Destroyed }));
    }

    // ---- interactions -------------------------------------------------------------------------------------------------

    private static PeerFire FireOf(RprWeaponFire sent, string firing, string target) => new(
        new EventIdentifierStruct(1, firing), firing, target, string.Empty,
        new EntityTypeStruct(sent.Munition.Kind, sent.Munition.Domain, sent.Munition.Country, sent.Munition.Category, sent.Munition.Subcategory,
            sent.Munition.Specific, sent.Munition.Extra),
        new WorldLocationStruct(sent.Location.X, sent.Location.Y, sent.Location.Z), (ushort)sent.Quantity, 0, FuseTypeEnum16.Other,
        WarheadTypeEnum16.Other, 0f, 0, default);

    [Fact]
    public void A_fire_is_compared_field_by_field_with_object_references_as_rti_names()
    {
        var rig = new SimNetGateway.Transport.FakeRti.FakeRtiHub();
        var peer = new ScriptedRprFederate(rig, "RPR-Mirror", "Peer");
        peer.DeclareForGateway();
        peer.Inject(new RprObjectUpdate(new RprObjectHandle(0x601)) { Marking = "T-1", EntityType = Uav, WorldLocation = new WorldLocation(1, 2, 3) });
        var handle = peer.ActualHandle(0x601)!.Value;
        string name = peer.NameOfHandle(handle.Value)!;
        var sent = new RprWeaponFire(Guid.NewGuid(), handle, null, new WorldLocation(10, 20, 30), Uav, 3);

        Assert.Empty(RtiMirror.Differences(sent, FireOf(sent, name, string.Empty), peer));

        Assert.Contains(RtiMirror.Differences(sent, FireOf(sent, "HLAobject_99", string.Empty), peer), d => d.Contains("firing object", StringComparison.Ordinal));
        Assert.Contains(RtiMirror.Differences(sent, FireOf(sent, name, name), peer), d => d.Contains("target object", StringComparison.Ordinal));
        Assert.Contains(RtiMirror.Differences(sent, FireOf(sent, name, string.Empty) with { Quantity = 2 }, peer), d => d.Contains("quantity", StringComparison.Ordinal));
        Assert.Contains(RtiMirror.Differences(sent, FireOf(sent, name, string.Empty) with { Location = default }, peer), d => d.Contains("location", StringComparison.Ordinal));
        Assert.Contains(RtiMirror.Differences(sent, FireOf(sent, name, string.Empty) with { Munition = default }, peer), d => d.Contains("munition", StringComparison.Ordinal));
    }

    [Fact]
    public void A_detonation_result_is_compared_in_the_numbering_of_the_fom()
    {
        var rig = new SimNetGateway.Transport.FakeRti.FakeRtiHub();
        var peer = new ScriptedRprFederate(rig, "RPR-Mirror", "Peer");
        var sent = new RprMunitionDetonation(Guid.NewGuid(), null, null, new WorldLocation(1, 2, 3), Uav, RprDetonationResult.GroundImpact);
        var seen = new PeerDetonation(new EventIdentifierStruct(1, string.Empty), string.Empty, string.Empty, string.Empty,
            new EntityTypeStruct(1, 2, 225, 50, 1, 0, 0), new WorldLocationStruct(1, 2, 3), 1, 0, FuseTypeEnum16.Other, WarheadTypeEnum16.Other,
            DetonationResultCodeEnum8.GroundImpact, default, default, 0);

        Assert.Empty(RtiMirror.Differences(sent, seen, peer));
        Assert.Contains(RtiMirror.Differences(sent, seen with { Result = DetonationResultCodeEnum8.EntityImpact }, peer),
            d => d.Contains("result", StringComparison.Ordinal));
    }
}
