using System.Text.Json;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>Settings of the test side (the "ext" section).</summary>
public sealed record ExtSettings(bool Enabled = true, int Level = 1);

/// <summary>A message on the test side's port: one entity state.</summary>
public readonly record struct ExtMessage(string Name, double X);

/// <summary>Writes the entity of an <see cref="ExtMessage"/> into the pivot as the test side's origin, exactly as an ingress module of a side does.</summary>
public sealed class ExtIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly SideContext _context;
    private readonly List<ExtMessage> _received;

    public ExtIngress(SideContext context, List<ExtMessage> received)
    {
        _schema = context.Schema;
        _context = context;
        _received = received;
    }

    public string Name => nameof(ExtIngress);

    public void Register(ModuleSetup setup)
    {
        var sc = _schema;
        setup.Produces(sc.EntityType, sc.Marking, sc.Force, sc.Position, sc.Orientation, sc.Velocity, sc.Acceleration, sc.AngularVelocity,
            sc.DrAlgorithm, sc.StateTime, sc.Damage);
        setup.OnMessage<ExtMessage>(Handle);
    }

    private void Handle(in ExtMessage m, IngressContext ctx)
    {
        _received.Add(m);
        var sc = _schema;
        var map = _context.Map<long>("ext.id");
        var id = ctx.Changes.CreateEntity();
        map.Bind(ctx.Changes, id, 7);
        ctx.Changes.Set(id, sc.EntityType, AttrValue.From(new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack()));
        ctx.Changes.Set(id, sc.Marking, AttrValue.From(m.Name));
        ctx.Changes.Set(id, sc.Force, AttrValue.From(2L));
        ctx.Changes.Set(id, sc.Position, AttrValue.From(new Vec3(m.X, 0, 0)));
        ctx.Changes.Set(id, sc.Orientation, AttrValue.From(Vec3.Zero));
        ctx.Changes.Set(id, sc.Velocity, AttrValue.From(Vec3.Zero));
        ctx.Changes.Set(id, sc.Acceleration, AttrValue.From(Vec3.Zero));
        ctx.Changes.Set(id, sc.AngularVelocity, AttrValue.From(Vec3.Zero));
        ctx.Changes.Set(id, sc.DrAlgorithm, AttrValue.From((long)DrAlgorithms.Fpw));
        ctx.Changes.Set(id, sc.StateTime, AttrValue.From(1.0));
        ctx.Changes.Set(id, sc.Damage, AttrValue.From(0L));
    }
}

/// <summary>A side defined outside the gateway project, using only its public types (DD-011 section 3a).</summary>
public sealed class ExtSide : IProtocolSide
{
    public List<ExtMessage> Received { get; } = new();

    public string Key => "ext";

    public object? Parse(ConfigSection section)
    {
        var settings = new ExtSettings(section.Boolean("enabled", true), (int)section.Integer("level", 1, 0, 100));
        section.Finish();
        return settings;
    }

    public void Write(Utf8JsonWriter w, object settings)
    {
        var s = (ExtSettings)settings;
        w.WriteBoolean("enabled", s.Enabled);
        w.WriteNumber("level", s.Level);
    }

    public bool Runs(object settings) => ((ExtSettings)settings).Enabled;

    public void Declare(SideDeclaration d, object settings)
    {
        d.Origin = d.Builder.RequireInbound("ext").Source;
        d.RequestMap<long>("ext.id");
    }

    public void Register(SideContext c, object settings) => c.Builder.AddModule(new ExtIngress(c, Received));
}

/// <summary>A side from another project plugs into the profile through <see cref="SideRegistry"/> (T-026a, DD-011 section 3a).</summary>
public class ExternalSideTests
{
    private static HrotDisHarness Build(ExtSide side, out InMemoryInbound extIn, GatewayConfiguration? config = null)
    {
        var registry = SideRegistry.Builtin.With(side);
        InMemoryInbound? inbound = null;
        var h = new HrotDisHarness(config ?? GatewayConfiguration.CreateDefault().WithSide("ext", new ExtSettings()),
            beforeBuild: b =>
            {
                var port = b.RequireInbound("ext");
                inbound = new InMemoryInbound(port.Source, "ext");
                b.BindInbound("ext", inbound);
            },
            sides: registry);
        extIn = inbound!;
        h.ExtraReplicaOrigins.Add(h.Engine.Sources.Register("ext"));
        return h;
    }

    [Fact]
    public void A_message_on_the_external_side_port_reaches_its_module_and_its_entity_is_a_replica_for_hrot()
    {
        var side = new ExtSide();
        var h = Build(side, out var extIn);
        h.GoLive();
        Assert.Contains("ext", h.Binding.InboundPorts.Select(p => p.Name));
        Assert.Equal(["hrot", "dis", "ext"], h.Binding.InboundPorts.Select(p => p.Name).ToArray());
        Assert.Equal(["hrot.entity", "dis.entity", "ext.id"], h.Engine.Identities.All.Select(m => m.Name).ToArray());

        extIn.Enqueue(new ExtMessage("E-1", 10));
        h.Hrot(new HrotSession(ClusterPhase.Live, true, 1, 0)); // a stimulus, so that the quiet-tick check of the harness allows the replica state
        h.Step();

        Assert.Equal([new ExtMessage("E-1", 10)], side.Received);
        var state = Assert.Single(h.Sent<ReplicaState>());
        Assert.Equal("E-1", state.Marking);
        var ext = h.Engine.Sources.Register("ext");
        var entity = Assert.Single(h.Entities(ext));
        Assert.True(h.HrotIds.TryResolve(state.Id, out var bound) && bound == entity.Id);
        Assert.NotEqual(h.DisSource, ext);
        Assert.Equal(ext, ((IdentityMap<long>)h.Engine.Identities.Find("ext.id")!).Side);
    }

    [Fact]
    public void The_external_side_origin_is_a_replica_origin_like_dis_and_removal_is_reported()
    {
        var side = new ExtSide();
        var h = Build(side, out var extIn);
        h.GoLive();
        extIn.Enqueue(new ExtMessage("E-2", 1));
        h.Hrot(new HrotSession(ClusterPhase.Live, true, 1, 0));
        h.Step();
        var state = Assert.Single(h.Sent<ReplicaState>());
        var ext = h.Engine.Sources.Register("ext");
        var entity = Assert.Single(h.Entities(ext));

        var changes = h.Engine.BeginChange(ext);
        changes.DestroyEntity(entity.Id);
        changes.Commit();
        h.Step();
        Assert.Equal(state.Id, Assert.Single(h.Sent<ReplicaRemoved>()).Id);
    }

    [Fact]
    public void An_external_side_that_does_not_run_declares_nothing()
    {
        var off = GatewayConfiguration.CreateDefault().WithSide("ext", new ExtSettings(Enabled: false));
        var h = new HrotDisHarness(off, sides: SideRegistry.Builtin.With(new ExtSide()));
        Assert.Equal(["hrot", "dis"], h.Binding.InboundPorts.Select(p => p.Name).ToArray());
        Assert.Null(h.Engine.Identities.Find("ext.id"));
    }

    [Fact]
    public void No_running_side_at_all_is_still_not_instantiated()
    {
        var registry = SideRegistry.Builtin.With(new ExtSide());
        var none = new GatewayConfiguration(new SharedConfiguration(), new HrotConfiguration(), null, null)
            .WithSide("ext", new ExtSettings(Enabled: false));
        var ex = Assert.Throws<InvalidOperationException>(() => HrotDisConfigurations.For(none, sides: registry).CreateBuilder(new ManualClock()));
        Assert.Contains("not instantiated", ex.Message);
        Assert.False(none.AnySideEnabledIn(registry));
        var alone = new GatewayConfiguration(new SharedConfiguration(), new HrotConfiguration(), null, null).WithSide("ext", new ExtSettings());
        Assert.True(alone.AnySideEnabledIn(registry));
        Assert.False(alone.AnySideEnabled); // the property knows the built-in sides only
    }

    [Fact]
    public void The_registry_rejects_a_duplicate_and_a_reserved_key()
    {
        var registry = SideRegistry.Builtin.With(new ExtSide());
        Assert.Throws<ArgumentException>(() => registry.With(new ExtSide()));
        Assert.Throws<ArgumentException>(() => SideRegistry.Builtin.With(new DisSide()));
        Assert.Equal(["dis", "hla"], SideRegistry.Builtin.Sides.Select(s => s.Key).ToArray());
        Assert.Equal(["dis", "hla", "ext"], registry.Sides.Select(s => s.Key).ToArray());
        Assert.IsType<HlaSide>(registry.Find("hla"));
        Assert.Null(SideRegistry.Builtin.Find("ext"));
    }

    // ---- configuration file ---------------------------------------------------------------------------------------------

    [Fact]
    public void An_unclaimed_top_level_key_is_exactly_one_error_naming_it_and_a_claimed_key_goes_to_Sides()
    {
        const string json = """{ "dis": { "exerciseId": 1 }, "ext": { "level": 5 } }""";
        var plain = GatewayConfigurationFile.Parse(json, "test.json");
        Assert.Equal(ConfigurationLoadKind.Invalid, plain.Kind);
        Assert.Equal(["test.json: ext: unknown key"], plain.Errors);

        var registry = SideRegistry.Builtin.With(new ExtSide());
        var ok = GatewayConfigurationFile.Parse(json, "test.json", registry);
        Assert.Equal(ConfigurationLoadKind.Loaded, ok.Kind);
        Assert.Equal(new ExtSettings(true, 5), ok.Configuration!.Sides["ext"]);
        Assert.Equal(new ExtSettings(true, 5), ok.Configuration.SettingsOf("ext"));
        Assert.True(ok.Configuration.DisRuns);

        var other = GatewayConfigurationFile.Parse("""{ "dis": { "exerciseId": 1 }, "nope": {} }""", "test.json", registry);
        Assert.Equal(["test.json: nope: unknown key"], other.Errors);
    }

    [Fact]
    public void A_key_of_an_external_side_is_checked_by_that_side()
    {
        var registry = SideRegistry.Builtin.With(new ExtSide());
        var bad = GatewayConfigurationFile.Parse("""{ "ext": { "level": 500, "color": 1 } }""", "test.json", registry);
        Assert.Equal(ConfigurationLoadKind.Invalid, bad.Kind);
        Assert.Equal(["test.json: ext.level: must be in 0 .. 100, got 500", "test.json: ext.color: unknown key"], bad.Errors);
    }

    [Fact]
    public void An_external_side_round_trips_through_json_with_the_builtin_ones()
    {
        var registry = SideRegistry.Builtin.With(new ExtSide());
        var config = GatewayConfiguration.CreateDefault(9).WithSide("ext", new ExtSettings(false, 42));
        var json = GatewayConfigurationFile.ToJson(config, registry);
        var again = GatewayConfigurationFile.Parse(json, "test.json", registry);
        Assert.Equal(ConfigurationLoadKind.Loaded, again.Kind);
        Assert.Equal(config, again.Configuration);
        Assert.Equal(json, GatewayConfigurationFile.ToJson(again.Configuration!, registry));
        // a side without settings writes no section
        Assert.DoesNotContain("\"ext\"", GatewayConfigurationFile.ToJson(GatewayConfiguration.CreateDefault(), registry));
    }

    [Fact]
    public void Seeded_random_builtin_settings_round_trip_through_json_with_and_without_an_external_side()
    {
        var registry = SideRegistry.Builtin.With(new ExtSide());
        for (int seed = 1; seed <= 200; seed++)
        {
            var r = new Random(seed);
            double Num(double max) => Math.Round(r.NextDouble() * max + 0.01, 3);
            var config = new GatewayConfiguration(
                new SharedConfiguration
                {
                    Site = r.Next(0, 65536), Application = r.Next(0, 65536), EntityMaxHealth = Num(500),
                    DamageThresholds = new DamageThresholds { Slight = 0.5 + r.Next(0, 5) / 10.0, Moderate = r.Next(0, 5) / 10.0 },
                    DefaultTkbForUnknown = r.Next(0, 100000), ForwardUnitEntities = r.Next(2) == 0, SendEventsInReplay = r.Next(2) == 0,
                },
                new HrotConfiguration { SampleRateHz = Num(60), ReplicaOwnerNodeId = r.Next(0, 70000), AllowZeroOrigin = r.Next(2) == 0 },
                r.Next(3) == 0 ? null : new DisConfiguration
                {
                    Enabled = r.Next(2) == 0, ExerciseId = r.Next(0, 256), Port = r.Next(1, 65536), Address = $"239.{r.Next(256)}.{r.Next(256)}.{r.Next(256)}",
                    PositionThresholdM = Num(10), OrientationThresholdDeg = Num(10), HeartbeatS = Num(20), TimeoutS = Num(60),
                    HrotControlsDis = r.Next(2) == 0, DisControlsHrot = r.Next(2) == 0,
                },
                r.Next(3) == 0 ? null : new HlaConfiguration
                {
                    Enabled = r.Next(2) == 0, Rti = "rti" + r.Next(9), Federation = "F" + r.Next(99), FederateName = "N" + r.Next(9), FederateType = "T" + r.Next(9),
                    FomModules = Enumerable.Range(0, r.Next(1, 4)).Select(i => $"M{i}.xml").ToArray(), CreateFederation = r.Next(2) == 0,
                    PositionThresholdM = Num(10), OrientationThresholdDeg = Num(10), HeartbeatS = Num(20), TimeoutS = Num(60),
                });
            Assert.Equal(config, GatewayConfigurationFile.Parse(GatewayConfigurationFile.ToJson(config), "s").Configuration);
            if (r.Next(2) == 0)
            {
                config = config.WithSide("ext", new ExtSettings(r.Next(2) == 0, r.Next(0, 101)));
            }

            var parsed = GatewayConfigurationFile.Parse(GatewayConfigurationFile.ToJson(config, registry), "s", registry);
            Assert.True(parsed.Kind == ConfigurationLoadKind.Loaded, $"seed {seed}: {string.Join("; ", parsed.Errors)}");
            Assert.Equal(config, parsed.Configuration);
        }
    }
}
