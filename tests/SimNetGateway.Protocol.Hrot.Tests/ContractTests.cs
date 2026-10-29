using System.Reflection;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Protocol.Hrot.Tests;

/// <summary>The gateway &lt;-&gt; pack contract of DD-006 section 0: exactly the documented messages, immutable values, directions.</summary>
public class ContractTests
{
    private static readonly Assembly Contract = typeof(HrotOrigin).Assembly;

    private static IEnumerable<Type> Messages =>
        Contract.GetExportedTypes().Where(t => typeof(IHrotMessage).IsAssignableFrom(t) && !t.IsInterface);

    [Fact]
    public void The_pack_to_gateway_messages_are_exactly_those_of_the_contract_table()
    {
        string[] expected =
            ["HrotDetonation", "HrotEntityRemoved", "HrotEntityState", "HrotFire", "HrotOrigin", "HrotSession", "HrotTurretPose"];
        var actual = Messages.Where(t => typeof(IHrotToGatewayMessage).IsAssignableFrom(t)).Select(t => t.Name).Order().ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void The_gateway_to_pack_messages_are_exactly_those_of_the_contract_table()
    {
        string[] expected = ["InjectDetonation", "InjectFire", "ReplicaRemoved", "ReplicaState", "TimeRequest"];
        var actual = Messages.Where(t => typeof(IGatewayToHrotMessage).IsAssignableFrom(t)).Select(t => t.Name).Order().ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Every_message_goes_one_way_only_and_is_an_immutable_record_struct()
    {
        foreach (var type in Messages)
        {
            bool toGateway = typeof(IHrotToGatewayMessage).IsAssignableFrom(type);
            bool toPack = typeof(IGatewayToHrotMessage).IsAssignableFrom(type);
            Assert.True(toGateway ^ toPack, $"{type.Name} must go one way only");
            AssertImmutableValue(type);
        }

        foreach (var type in new[] { typeof(HrotVec3), typeof(HrotQuat), typeof(HrotDrSnapshot) })
        {
            AssertImmutableValue(type);
        }
    }

    private static void AssertImmutableValue(Type type)
    {
        Assert.True(type.IsValueType, $"{type.Name} must be a struct");
        Assert.True(type.GetCustomAttributes().Any(a => a.GetType().Name == "IsReadOnlyAttribute"), $"{type.Name} must be readonly");
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(property.GetSetMethod()?.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit") ?? true,
                $"{type.Name}.{property.Name} must be init-only");
        }

        Assert.NotNull(type.GetMethod("Equals", [type])); // a record struct: compared by value
    }

    [Fact]
    public void The_protocol_references_nothing_but_the_framework()
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var reference in Contract.GetReferencedAssemblies())
        {
            Assert.True(File.Exists(Path.Join(frameworkDir, reference.Name + ".dll")), $"{reference.Name} is not part of the shared framework");
        }
    }

    [Fact]
    public void The_cluster_phases_are_the_seven_of_the_design()
    {
        Assert.Equal(["Idle", "Edit", "Preview", "Live", "Replay", "Review", "Degraded"], Enum.GetNames<ClusterPhase>());
        Assert.Equal(["Pause", "Resume"], Enum.GetNames<TimeRequestKind>());
    }

    [Fact]
    public void Messages_compare_by_value_and_the_default_quaternion_is_not_the_identity()
    {
        var a = new HrotFire(1, 2, 3, 4, new HrotVec3(1, 2, 3), new HrotVec3(4, 5, 6), 7);
        var b = new HrotFire(1, 2, 3, 4, new HrotVec3(1, 2, 3), new HrotVec3(4, 5, 6), 7);
        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { Quantity = 8 });
        Assert.Equal(new HrotQuat(0, 0, 0, 1), HrotQuat.Identity);
        Assert.NotEqual(default, HrotQuat.Identity);
        Assert.Equal(default, HrotVec3.Zero);
    }

    [Fact]
    public void The_entity_state_carries_the_fields_of_the_contract_table()
    {
        string[] expected =
        [
            "Id", "TkbType", "DisType", "Marking", "Force", "CommanderId", "Position", "Rotation", "Velocity", "AngularVelocity",
            "HealthCurrent", "HealthMax", "SimTime",
        ];
        Assert.Equal(expected, typeof(HrotEntityState).GetProperties().Where(p => p.Name != "EqualityContract").Select(p => p.Name).ToArray());
        string[] replica = ["Id", "TkbType", "DisType", "Marking", "Force", "State", "HealthCurrent", "HealthMax", "Deactivated"];
        Assert.Equal(replica, typeof(ReplicaState).GetProperties().Where(p => p.Name != "EqualityContract").Select(p => p.Name).ToArray());
        string[] snapshot = ["Position", "Velocity", "Acceleration", "Rotation", "AngularVelocity", "DrAlgorithm", "StateTime"];
        Assert.Equal(snapshot, typeof(HrotDrSnapshot).GetProperties().Where(p => p.Name != "EqualityContract").Select(p => p.Name).ToArray());
    }
}
