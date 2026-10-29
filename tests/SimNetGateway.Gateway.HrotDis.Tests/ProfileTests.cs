using DISnet;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using EngineConfiguration = SimNetGateway.Engine.GatewayConfiguration;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>The profile and the configurations registry (R-051, R-052): ports, modules, the configuration decides what runs.</summary>
public class ProfileTests
{
    [Fact]
    public void The_registry_builds_the_profile_from_a_gateway_configuration()
    {
        Assert.Equal("hrot-dis/default", HrotDisConfigurations.Default.Name);
        Assert.IsType<HrotDisProfile>(HrotDisConfigurations.Default.Profile);
        Assert.Contains(HrotDisConfigurations.Default, HrotDisConfigurations.All);
        var settings = Assert.IsType<HrotDisSettings>(HrotDisConfigurations.Default.Settings);
        Assert.True(settings.Config.DisRuns);
        Assert.Equal(1, settings.Config.Dis!.ExerciseId);

        var config = GatewayConfiguration.CreateDefault(9);
        var built = HrotDisConfigurations.For(config, "mine");
        Assert.Equal("mine", built.Name);
        Assert.Same(config, Assert.IsType<HrotDisSettings>(built.Settings).Config);
        Assert.Equal("hrot-dis", built.Profile.Name);
    }

    [Fact]
    public void The_default_configuration_builds_a_gateway_with_the_hrot_and_dis_ports()
    {
        var builder = HrotDisConfigurations.Default.CreateBuilder(new ManualClock());
        var hrot = builder.RequireInbound("hrot");
        var dis = builder.RequireInbound("dis");
        builder.BindInbound("hrot", new InMemoryInbound(hrot.Source, "hrot"));
        builder.BindInbound("dis", new InMemoryInbound(dis.Source, "dis"));
        builder.BindOutbound<IGatewayToHrotMessage>("hrot", new InMemoryOutbound<IGatewayToHrotMessage>());
        builder.BindOutbound<Pdu>("dis", new InMemoryOutbound<Pdu>());
        var binding = builder.Build();
        Assert.Empty(binding.Validation.Errors);
        Assert.Equal(["hrot", "dis"], binding.InboundPorts.Select(p => p.Name).ToArray());
        Assert.Equal(["dis", "hrot"], binding.OutboundPorts.Select(p => p.Name).Order().ToArray());
        Assert.Equal(typeof(IGatewayToHrotMessage), binding.OutboundPorts.Single(p => p.Name == "hrot").MessageType);
        Assert.Equal(typeof(Pdu), binding.OutboundPorts.Single(p => p.Name == "dis").MessageType);
    }

    [Fact]
    public void A_wrongly_typed_dis_channel_is_a_build_problem()
    {
        var builder = HrotDisConfigurations.Default.CreateBuilder(new ManualClock());
        var hrot = builder.RequireInbound("hrot");
        var dis = builder.RequireInbound("dis");
        builder.BindInbound("hrot", new InMemoryInbound(hrot.Source, "hrot"));
        builder.BindInbound("dis", new InMemoryInbound(dis.Source, "dis"));
        builder.BindOutbound<IGatewayToHrotMessage>("hrot", new InMemoryOutbound<IGatewayToHrotMessage>());
        builder.BindOutbound<string>("dis", new InMemoryOutbound<string>());
        Assert.Throws<GatewayBuildException>(() => builder.Build());
    }

    [Fact]
    public void Without_a_dis_section_there_are_no_dis_ports()
    {
        var config = GatewayConfiguration.CreateDefault() with { Dis = null, Hla = new HlaConfiguration { Federation = "F" } };
        var h = new HrotDisHarness(config);
        // the HLA side runs (T-024, DD-009): its "rpr" port is declared, the dis ports are not
        Assert.Equal(["hrot", "rpr"], h.Binding.InboundPorts.Select(p => p.Name).Order().ToArray());
        Assert.Equal(["hrot", "rpr"], h.Binding.OutboundPorts.Select(p => p.Name).Order().ToArray());
        Assert.Null(h.DisOut);
        // the HROT half still works
        h.GoLive();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 2, 3)));
        Assert.NotNull(h.EntityOfHrot(1));
    }

    [Fact]
    public void A_disabled_dis_side_declares_no_dis_ports_either()
    {
        var config = GatewayConfiguration.CreateDefault() with
        {
            Dis = new DisConfiguration { ExerciseId = 1, Enabled = false },
            Hla = new HlaConfiguration { Federation = "F" },
        };
        var h = new HrotDisHarness(config);
        Assert.Equal(["hrot", "rpr"], h.Binding.InboundPorts.Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public void A_configuration_without_a_running_side_is_not_instantiated()
    {
        var none = GatewayConfiguration.CreateDefault() with { Dis = new DisConfiguration { ExerciseId = 1, Enabled = false } };
        var ex = Assert.Throws<InvalidOperationException>(() => HrotDisConfigurations.For(none).CreateBuilder(new ManualClock()));
        Assert.Contains("not instantiated", ex.Message);
        Assert.Throws<InvalidOperationException>(() => HrotDisConfigurations.For(new GatewayConfiguration(new SharedConfiguration(), new HrotConfiguration(), null, null)).CreateBuilder(new ManualClock()));
    }

    [Fact]
    public void The_gateway_builds_without_validation_errors_and_every_message_of_the_contract_has_a_handler()
    {
        var h = new HrotDisHarness();
        Assert.Empty(h.Binding.Validation.Errors);
        h.GoLive();
        var messages = new IHrotToGatewayMessage[]
        {
            new HrotOrigin(50, 14, 300, 1),
            new HrotSession(ClusterPhase.Live, true, 1, 0),
            HrotDisHarness.State(1),
            new HrotTurretPose(1, 1, 0, 0),
            new HrotFire(1, 0, 501, 0, default, default, 1),
            new HrotDetonation(1, 0, 501, 0, default, default),
            new HrotEntityRemoved(1),
        };
        Assert.Equal(typeof(IHrotToGatewayMessage).Assembly.GetExportedTypes().Count(t => !t.IsInterface && typeof(IHrotToGatewayMessage).IsAssignableFrom(t)), messages.Length);
        foreach (var message in messages)
        {
            switch (message)
            {
                case HrotOrigin m: h.HrotStep(m); break;
                case HrotSession m: h.HrotStep(m); break;
                case HrotEntityState m: h.HrotStep(m); break;
                case HrotTurretPose m: h.HrotStep(m); break;
                case HrotFire m: h.HrotStep(m); break;
                case HrotDetonation m: h.HrotStep(m); break;
                case HrotEntityRemoved m: h.HrotStep(m); break;
                default: throw new InvalidOperationException(message.GetType().Name);
            }
        }

        Assert.Equal(0, h.Engine.Diagnostics.UnhandledMessages);
    }

    [Fact]
    public void Each_message_kind_has_its_own_small_module()
    {
        var h = new HrotDisHarness();
        var modules = h.Engine.Rules.Select(r => r.Module).Distinct().ToList();
        string[] expected =
        [
            nameof(ReplicaStateEgress), nameof(ReplicaRemovedEgress), nameof(InjectFireEgress), nameof(InjectDetonationEgress), nameof(TimeRequestEgress),
        ];
        foreach (var name in expected)
        {
            Assert.Contains(name, modules);
        }

        Assert.Equal(["ReplicaStateEgress/Rebase", "ReplicaStateEgress/Reconcile", "ReplicaStateEgress/State"],
            h.Engine.Rules.Where(r => r.Module == nameof(ReplicaStateEgress)).Select(r => r.FullName).Order().ToArray());
    }

    [Fact]
    public void The_hrot_id_allocator_port_is_the_injected_one()
    {
        var allocator = new HrotIdRangeAllocator(7_000, 7_001);
        var builder = HrotDisConfigurations.For(GatewayConfiguration.CreateDefault(), "x", allocator).CreateBuilder(new ManualClock());
        var map = (IdentityMap<long>)builder.Identities.Find("hrot.entity")!;
        Assert.Same(allocator, map.Allocator);

        var defaults = HrotDisConfigurations.Default.CreateBuilder(new ManualClock());
        var defaultMap = (IdentityMap<long>)defaults.Identities.Find("hrot.entity")!;
        Assert.IsType<HrotIdRangeAllocator>(defaultMap.Allocator);
        Assert.NotSame(map.Allocator, defaultMap.Allocator);
        // two gateways built from the static default do not share an allocator
        var again = (IdentityMap<long>)HrotDisConfigurations.Default.CreateBuilder(new ManualClock()).Identities.Find("hrot.entity")!;
        Assert.NotSame(defaultMap.Allocator, again.Allocator);
    }

    [Fact]
    public void The_schema_registers_the_attributes_and_events_of_the_design_with_stable_ids()
    {
        var h = new HrotDisHarness();
        var s = h.Schema;
        var again = new PivotSchema(h.Engine.Attributes, h.Engine.EventTypes);
        Assert.Equal(s.Position, again.Position);
        var attributes = h.Engine.Attributes.ToDictionary(a => a.Name);
        (string Name, AttrKind Kind)[] expected =
        [
            ("Identity.EntityType", AttrKind.Int), ("Identity.Marking", AttrKind.String), ("Identity.Force", AttrKind.Int),
            ("Spatial.Position", AttrKind.Vec3), ("Spatial.Orientation", AttrKind.Vec3), ("Spatial.Velocity", AttrKind.Vec3),
            ("Spatial.Acceleration", AttrKind.Vec3), ("Spatial.AngularVelocity", AttrKind.Vec3), ("Spatial.DrAlgorithm", AttrKind.Int),
            ("Spatial.StateTime", AttrKind.Double), ("Status.Damage", AttrKind.Int), ("Status.Deactivated", AttrKind.Bool),
            ("Articulation.Parts", AttrKind.Object), ("Structure.Parent", AttrKind.EntityRef), ("Hrot.TkbType", AttrKind.Int),
            ("Hrot.Health", AttrKind.Vec3), ("Hrot.OwnerNode", AttrKind.Int), ("Hrot.MasterFlags", AttrKind.Int),
            ("Event.Id", AttrKind.Object), ("Event.Firing", AttrKind.EntityRef), ("Event.Target", AttrKind.EntityRef),
            ("Event.MunitionEntity", AttrKind.EntityRef), ("Event.MunitionType", AttrKind.Int), ("Event.MunitionTkb", AttrKind.Int),
            ("Event.Warhead", AttrKind.Int), ("Event.Fuse", AttrKind.Int), ("Event.Quantity", AttrKind.Int), ("Event.Rate", AttrKind.Int),
            ("Event.Location", AttrKind.Vec3), ("Event.Velocity", AttrKind.Vec3), ("Event.Range", AttrKind.Double),
            ("Event.RelativeLocation", AttrKind.Vec3), ("Event.Result", AttrKind.Int),
        ];
        foreach (var (name, kind) in expected)
        {
            Assert.True(attributes.TryGetValue(name, out var def), name + " is not registered");
            Assert.Equal(kind, def!.Kind);
        }

        Assert.Equal(AttrOptions.SpatialIndex, attributes["Spatial.Position"].Options);
        Assert.Equal(1000.0, attributes["Spatial.Position"].CellSize);
        Assert.Equal(AttrOptions.IndexReferences, attributes["Structure.Parent"].Options);
        Assert.Equal("WeaponFire", h.Engine.EventTypes.NameOf(s.WeaponFire));
        Assert.Equal("MunitionDetonation", h.Engine.EventTypes.NameOf(s.MunitionDetonation));
    }
}
