using System.Reflection;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

public class ProtocolTests
{
    private static IEnumerable<Type> MessageTypes(Assembly assembly, Type marker) =>
        assembly.GetExportedTypes().Where(t => marker.IsAssignableFrom(t) && t != marker);

    [Fact]
    public void Protocol_assemblies_reference_nothing_but_the_runtime()
    {
        foreach (var assembly in new[] { typeof(INedMessage).Assembly, typeof(IRprMessage).Assembly, typeof(IDisplayMessage).Assembly })
        {
            var references = assembly.GetReferencedAssemblies().Select(r => r.Name!).ToList();
            Assert.All(references, r => Assert.False(r.StartsWith("SimNetGateway", StringComparison.Ordinal), assembly.GetName().Name + " -> " + r));
        }
    }

    [Fact]
    public void Namespaces_equal_the_project_names()
    {
        Assert.Equal("SimNetGateway.Protocol.Ned", typeof(INedMessage).Namespace);
        Assert.Equal("SimNetGateway.Protocol.Rpr", typeof(IRprMessage).Namespace);
        Assert.Equal("SimNetGateway.Protocol.Display", typeof(IDisplayMessage).Namespace);
        foreach (var t in typeof(INedMessage).Assembly.GetExportedTypes())
        {
            Assert.Equal("SimNetGateway.Protocol.Ned", t.Namespace);
        }

        // DD-008 section 2: Protocol.Rpr also holds the 1516e port (Hla), its encodings (Hla.Encoding) and the RPR FOM 2.0 (Fom)
        string[] rprNamespaces =
        [
            "SimNetGateway.Protocol.Rpr", "SimNetGateway.Protocol.Rpr.Hla", "SimNetGateway.Protocol.Rpr.Hla.Encoding",
            "SimNetGateway.Protocol.Rpr.Fom",
        ];
        foreach (var t in typeof(IRprMessage).Assembly.GetExportedTypes())
        {
            Assert.Contains(t.Namespace, rprNamespaces);
        }
        Assert.All(
            typeof(IRprMessage).Assembly.GetExportedTypes().Where(t => typeof(IRprMessage).IsAssignableFrom(t)),
            t => Assert.Equal("SimNetGateway.Protocol.Rpr", t.Namespace));

        foreach (var t in typeof(IDisplayMessage).Assembly.GetExportedTypes())
        {
            Assert.Equal("SimNetGateway.Protocol.Display", t.Namespace);
        }
    }

    [Fact]
    public void The_message_inventory_is_complete_per_side()
    {
        Assert.Equal(
            new[]
            {
                "NedDamageNotification", "NedDetonation", "NedEntityDamage", "NedEntityDisposed", "NedEntityMaster", "NedFireAmmunition",
                "NedFormation", "NedGeoSpatial", "NedIffStatus", "NedSimControl", "NedWeaponFire", "NedWeaponStatus",
            },
            MessageTypes(typeof(INedMessage).Assembly, typeof(INedMessage)).Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "RprAggregateEntity", "RprMunitionDetonation", "RprObjectRegistered", "RprObjectRemoved", "RprObjectUpdate", "RprRegisterObject", "RprSimulationControl", "RprWeaponFire" },
            MessageTypes(typeof(IRprMessage).Assembly, typeof(IRprMessage)).Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "DisplayEntityPosition", "DisplayEntityRemoved" },
            MessageTypes(typeof(IDisplayMessage).Assembly, typeof(IDisplayMessage)).Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Messages_without_collections_are_value_types_and_the_update_is_a_class()
    {
        foreach (var t in MessageTypes(typeof(INedMessage).Assembly, typeof(INedMessage)).Where(t => t != typeof(NedFormation)))
        {
            Assert.True(t.IsValueType, t.Name);
        }

        Assert.True(typeof(NedFormation).IsClass); // it carries the member array
        Assert.True(typeof(NedFormation).IsSealed);

        foreach (var t in MessageTypes(typeof(IDisplayMessage).Assembly, typeof(IDisplayMessage)))
        {
            Assert.True(t.IsValueType, t.Name);
        }

        foreach (var t in MessageTypes(typeof(IRprMessage).Assembly, typeof(IRprMessage))
                     .Where(t => t != typeof(RprObjectUpdate) && t != typeof(RprAggregateEntity)))
        {
            Assert.True(t.IsValueType, t.Name);
        }

        Assert.True(typeof(RprObjectUpdate).IsClass);
        Assert.True(typeof(RprObjectUpdate).IsSealed);
        Assert.True(typeof(RprAggregateEntity).IsClass); // it carries the member id array
        Assert.True(typeof(RprAggregateEntity).IsSealed);
    }

    [Fact]
    public void The_sides_do_not_share_markers()
    {
        Assert.False(typeof(INedMessage).IsAssignableFrom(typeof(RprObjectUpdate)));
        Assert.False(typeof(IRprMessage).IsAssignableFrom(typeof(NedGeoSpatial)));
        Assert.False(typeof(IDisplayMessage).IsAssignableFrom(typeof(NedGeoSpatial)));
    }

    [Fact]
    public void Records_have_value_equality_and_keep_their_fields()
    {
        var a = new NedGeoSpatial(5, 50.1, 14.2, 300, 90, 5, -3, 12.5);
        var b = new NedGeoSpatial(5, 50.1, 14.2, 300, 90, 5, -3, 12.5);
        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { Speed = 12.6 });
        Assert.Equal(50.1, a.LatitudeDeg);
        Assert.Equal(-3, a.RollDeg);

        var g = Guid.NewGuid();
        var fire = new NedWeaponFire(g, 1, 0, 501, 1, 2, 3, 4);
        Assert.Equal(g, fire.FireId);
        Assert.Equal(0UL, fire.Target);
        Assert.Equal(new NedSimControl(NedControlKind.Load, "x"), new NedSimControl(NedControlKind.Load, "x"));
        Assert.Null(new NedSimControl(NedControlKind.Start, null).Scenario);
    }

    [Fact]
    public void An_object_update_reflects_only_the_attributes_that_are_present()
    {
        var u = new RprObjectUpdate(new RprObjectHandle(0x2A01)) { Marking = "T1", DamageState = RprDamageState.Destroyed };
        Assert.Equal("T1", u.Marking);
        Assert.Equal(RprDamageState.Destroyed, u.DamageState);
        Assert.Null(u.EntityType);
        Assert.Null(u.ForceId);
        Assert.Null(u.EntityIdentifier);
        Assert.Null(u.WorldLocation);
        Assert.Null(u.Orientation);
        Assert.Null(u.VelocityVector);
        Assert.Null(u.AccelerationVector);
        Assert.Null(u.AngularVelocity);
        Assert.Null(u.DeadReckoningAlgorithm);
        Assert.Equal(u, u with { });
        Assert.NotEqual(u, u with { Marking = "T2" });
    }

    [Fact]
    public void Fire_and_detonation_use_null_for_no_object()
    {
        var fire = new RprWeaponFire(Guid.Empty, null, new RprObjectHandle(2), default, default, 1);
        Assert.Null(fire.FiringObject);
        Assert.Equal(2u, fire.TargetObject!.Value.Value);
        var det = new RprMunitionDetonation(Guid.Empty, new RprObjectHandle(3), null, default, default, RprDetonationResult.Detonation);
        Assert.Null(det.TargetObject);
    }

    [Fact]
    public void The_enum_numbering_is_as_specified_and_underlying_type_is_byte()
    {
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(NedForce)));
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(NedDetonationResult)));
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(RprDamageState)));
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(RprForceId)));
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(RprDetonationResult)));

        Assert.Equal(new byte[] { 0, 1, 2, 3 }, new[] { NedForce.Unknown, NedForce.Friendly, NedForce.Opposing, NedForce.Neutral }.Select(x => (byte)x));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, new[] { RprForceId.Other, RprForceId.Friendly, RprForceId.Opposing, RprForceId.Neutral }.Select(x => (byte)x));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, new[] { RprDamageState.NoDamage, RprDamageState.SlightDamage, RprDamageState.ModerateDamage, RprDamageState.Destroyed }.Select(x => (byte)x));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, new[] { NedDetonationResult.Other, NedDetonationResult.EntityImpact, NedDetonationResult.GroundImpact, NedDetonationResult.Detonation }.Select(x => (byte)x));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, new[] { RprDetonationResult.Other, RprDetonationResult.EntityImpact, RprDetonationResult.GroundImpact, RprDetonationResult.Detonation }.Select(x => (byte)x));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Enum.GetValues<NedControlKind>().Select(x => (int)x));
        Assert.Equal(new[] { NedControlKind.Load, NedControlKind.LoadDebrief, NedControlKind.Start, NedControlKind.Pause, NedControlKind.Resume, NedControlKind.Unload }, Enum.GetValues<NedControlKind>());
        Assert.Equal(2, Enum.GetValues<RprControlKind>().Length);
    }
}
