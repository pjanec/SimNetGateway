using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

public class ProfileTests
{
    private static readonly string[] ModuleNames =
    [
        "NedEntityMasterIngress", "NedGeoSpatialIngress", "NedDamageIngress", "NedWeaponStatusIngress", "NedDisposeIngress",
        "NedWeaponFireIngress", "NedDetonationIngress", "NedControlIngress",
        "NedFormationIngress", "RprAggregateEntityIngress",
        "RprObjectUpdateIngress", "RprObjectRemovedIngress", "RprWeaponFireIngress", "RprObjectRegisteredIngress",
        "RprUpdateRequestedResponder",
        "NedIdentityEgress", "NedSpatialEgress", "NedDamageEgress", "NedDamageNotificationEgress", "NedLifecycleEgress",
        "NedFormationEgress", "NedWeaponFireEgress",
        "RprEntityEgress", "CustomerDamagePercent", "RprHeartbeatEgress", "RprWeaponFireEgress", "RprDetonationEgress", "RprControlEgress",
        "DisplayPositionEgress", "DrExtrapolation", "RprStaleReaper", "RprAggregateLinker",
    ];

    private static readonly string[] RuleNames =
    [
        "NedIdentityEgress/Master", "NedSpatialEgress/GeoSpatial", "NedDamageEgress/Damage", "NedDamageNotificationEgress/Notify",
        "NedLifecycleEgress/Withdraw", "NedLifecycleEgress/Dispose",
        "NedFormationEgress/MarkDirty", "NedFormationEgress/Flush", "NedFormationEgress/Dispose",
        "NedWeaponFireEgress/Fire",
        "RprEntityEgress/Identity", "RprEntityEgress/Spatial", "RprEntityEgress/Damage", "RprEntityEgress/Flush",
        "RprEntityEgress/Registered", "RprEntityEgress/Remove", "CustomerDamagePercent/Damage",
        "RprUpdateRequestedResponder/Send",
        "RprHeartbeatEgress/Heartbeat",
        "RprWeaponFireEgress/Fire", "RprWeaponFireEgress/Released", "RprWeaponFireEgress/ReleaseHeld",
        "RprDetonationEgress/Detonation", "RprDetonationEgress/Released", "RprDetonationEgress/ReleaseHeld",
        "RprControlEgress/Control",
        "DisplayPositionEgress/Position", "DisplayPositionEgress/Remove",
        "DrExtrapolation/Extrapolate", "RprStaleReaper/Reap",
        "RprAggregateLinker/LinkLateMember", "RprAggregateLinker/UnlinkMembers",
    ];

    [Fact]
    public void The_default_configuration_is_named_and_registered()
    {
        Assert.Equal("ned-rpr/default", NedRprConfigurations.Default.Name);
        Assert.Equal("ned-rpr", NedRprConfigurations.Default.Profile.Name);
        Assert.IsType<NedRprSettings>(NedRprConfigurations.Default.Settings);
        Assert.Contains(NedRprConfigurations.Default, NedRprConfigurations.All);
        Assert.Equal(NedRprConfigurations.All.Count, NedRprConfigurations.All.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void Validate_reports_no_errors_and_no_warnings_for_the_default_configuration()
    {
        var h = new GatewayHarness();

        var report = h.Engine.Validate();

        Assert.Empty(report.Errors);
        Assert.Empty(report.Warnings);
        Assert.Empty(h.Binding.Validation.Errors);
        Assert.Empty(h.Binding.Validation.Warnings);
    }

    [Fact]
    public void The_profile_declares_exactly_the_specified_ports_with_their_message_types()
    {
        var h = new GatewayHarness();

        Assert.Equal(["ned", "rpr"], h.Binding.InboundPorts.Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(typeof(INedMessage), h.Binding.Outbound<INedMessage>("ned").MessageType);
        Assert.Equal(typeof(IRprMessage), h.Binding.Outbound<IRprMessage>("rpr").MessageType);
        Assert.Equal(typeof(IDisplayMessage), h.Binding.Outbound<IDisplayMessage>("display").MessageType);
        Assert.Equal(3, h.Binding.OutboundPorts.Count);
    }

    [Theory]
    [InlineData("ned")]
    [InlineData("rpr")]
    public void Build_rejects_the_profile_when_an_inbound_port_is_left_unbound(string unbound)
    {
        var builder = NedRprConfigurations.Default.CreateBuilder(new ManualClock());
        foreach (var name in new[] { "ned", "rpr" })
        {
            if (name != unbound)
            {
                builder.BindInbound(name, new InMemoryInbound(builder.RequireInbound(name).Source, name));
            }
        }

        builder.BindOutbound<INedMessage>("ned", new InMemoryOutbound<INedMessage>());
        builder.BindOutbound<IRprMessage>("rpr", new InMemoryOutbound<IRprMessage>());
        builder.BindOutbound<IDisplayMessage>("display", new InMemoryOutbound<IDisplayMessage>());

        var ex = Assert.Throws<GatewayBuildException>(() => builder.Build());

        Assert.Contains($"Inbound port '{unbound}' is declared but not bound", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ned")]
    [InlineData("rpr")]
    [InlineData("display")]
    public void Build_rejects_the_profile_when_an_outbound_port_is_left_unbound(string unbound)
    {
        var builder = NedRprConfigurations.Default.CreateBuilder(new ManualClock());
        builder.BindInbound("ned", new InMemoryInbound(builder.RequireInbound("ned").Source, "ned"));
        builder.BindInbound("rpr", new InMemoryInbound(builder.RequireInbound("rpr").Source, "rpr"));
        if (unbound != "ned")
        {
            builder.BindOutbound<INedMessage>("ned", new InMemoryOutbound<INedMessage>());
        }

        if (unbound != "rpr")
        {
            builder.BindOutbound<IRprMessage>("rpr", new InMemoryOutbound<IRprMessage>());
        }

        if (unbound != "display")
        {
            builder.BindOutbound<IDisplayMessage>("display", new InMemoryOutbound<IDisplayMessage>());
        }

        var ex = Assert.Throws<GatewayBuildException>(() => builder.Build());

        Assert.Contains($"Outbound port '{unbound}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("declared but not bound", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_rejects_an_outbound_port_bound_to_the_wrong_message_type()
    {
        var builder = NedRprConfigurations.Default.CreateBuilder(new ManualClock());
        builder.BindInbound("ned", new InMemoryInbound(builder.RequireInbound("ned").Source, "ned"));
        builder.BindInbound("rpr", new InMemoryInbound(builder.RequireInbound("rpr").Source, "rpr"));
        builder.BindOutbound<INedMessage>("ned", new InMemoryOutbound<INedMessage>());
        builder.BindOutbound<INedMessage>("rpr", new InMemoryOutbound<INedMessage>()); // wrong type
        builder.BindOutbound<IDisplayMessage>("display", new InMemoryOutbound<IDisplayMessage>());

        var ex = Assert.Throws<GatewayBuildException>(() => builder.Build());

        Assert.Contains("rpr", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_module_of_the_design_is_registered_exactly_once_with_its_stable_name()
    {
        var h = new GatewayHarness();

        var modules = h.Engine.Rules.Select(r => r.Module).Concat(h.Engine.Productions.Select(p => p.Module)).Distinct().ToHashSet();
        foreach (var name in ModuleNames)
        {
            // Ingress modules have no rule: they appear through the vocabulary they produce, or are checked by their handlers.
            Assert.True(modules.Contains(name) || IsIngressOnly(name), $"module {name} is not registered");
        }

        foreach (var rule in RuleNames)
        {
            Assert.NotNull(h.Engine.FindRule(rule));
        }

        Assert.Equal(RuleNames.Length, h.Engine.Rules.Count);
        Assert.Equal(RuleNames.Length, h.Engine.Rules.Select(r => r.FullName).Distinct().Count());
    }

    private static bool IsIngressOnly(string name) => name.EndsWith("Ingress", StringComparison.Ordinal);

    [Fact]
    public void Every_ingress_module_handles_its_message_type()
    {
        var h = new GatewayHarness();

        // A message type without a handler is counted as unhandled; none of the eleven protocol inputs may be.
        h.Ned(T.Master(T.Hawk, "Hawk"));
        h.Ned(T.Geo(T.Hawk));
        h.Ned(new NedEntityDamage(T.Hawk, 1));
        h.Ned(new NedWeaponStatus(T.Hawk, 501, 10));
        h.Ned(new NedWeaponFire(Guid.NewGuid(), T.Hawk, 0, 0, 50, 14, 300, 1));
        h.Ned(new NedDetonation(Guid.NewGuid(), 0, 501, 50, 14, 300, NedDetonationResult.GroundImpact, -1));
        h.Ned(new NedSimControl(NedControlKind.Load, "x"));
        h.Ned(new NedEntityDisposed(T.Hawk));
        h.Rpr(T.RprFull(0x500, "Red", 400, 50, 14, 0, 0, 0, 5));
        h.Rpr(new RprWeaponFire(Guid.NewGuid(), null, null, default, default, 1));
        h.Rpr(new RprObjectRegistered(999, new RprObjectHandle(1)));
        h.Rpr(new RprObjectRemoved(new RprObjectHandle(0x500)));
        h.Checker.Expect("unknown token");
        h.Checker.Expect("Unknown DIS munition");
        h.Step();

        Assert.Equal(0, h.Engine.Diagnostics.UnhandledMessages);
    }

    [Fact]
    public void The_settings_record_has_the_specified_defaults()
    {
        var s = new NedRprSettings();

        Assert.Equal(TimeSpan.FromSeconds(5), s.HeartbeatPeriod);
        Assert.Equal(TimeSpan.FromSeconds(1), s.HeartbeatCheckPeriod);
        Assert.Equal(TimeSpan.FromSeconds(0.1), s.DrProcessorPeriod);
        Assert.Equal(TimeSpan.FromSeconds(12), s.StaleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), s.ReaperCheckPeriod);
        Assert.Equal(new NedIdRange(1_000_000, 1_999_999), s.NedIdRange);
        Assert.Equal(1, s.DisSite);
        Assert.Equal(3001, s.DisApplication);
        Assert.Equal(60, s.DisQuarantineSeconds);
    }

    [Fact]
    public void The_four_identity_maps_exist_with_their_key_types_and_sides()
    {
        var h = new GatewayHarness();

        Assert.Equal(typeof(ulong), h.Engine.Identities.Find("ned.entity")!.KeyType);
        Assert.Equal(typeof(RprObjectHandle), h.Engine.Identities.Find("rpr.handle")!.KeyType);
        Assert.Equal(typeof(DisEntityId), h.Engine.Identities.Find("rpr.entityId")!.KeyType);
        Assert.Equal(typeof(string), h.Engine.Identities.Find("display.label")!.KeyType);
        Assert.Equal(h.NedSource, h.NedIds.Side);
        Assert.Equal(h.RprSource, h.RprHandles.Side);
        Assert.Equal(h.RprSource, h.RprEntityIds.Side);
    }

    [Fact]
    public void The_sources_of_the_sides_and_processors_are_registered()
    {
        var h = new GatewayHarness();

        foreach (var name in new[] { "ned", "rpr", "display", "proc.dr", "proc.reaper" })
        {
            Assert.True(h.Engine.Sources.TryGet(name, out _), name);
        }
    }

    [Fact]
    public void The_schema_dump_lists_producers_and_consumers_of_the_gateway_vocabulary()
    {
        var h = new GatewayHarness();

        var dump = h.Engine.Validate().Dump();

        Assert.Contains("Status.Damage", dump, StringComparison.Ordinal);
        Assert.Contains("produced by: NedDamageIngress, NedDetonationIngress, NedFormationIngress, RprObjectUpdateIngress", dump, StringComparison.Ordinal);
        Assert.Contains("Derived.PredictedPosition", dump, StringComparison.Ordinal);
        Assert.Contains("produced by: DrExtrapolation", dump, StringComparison.Ordinal);
        Assert.Contains("consumed by: DisplayPositionEgress", dump, StringComparison.Ordinal);
    }
}
