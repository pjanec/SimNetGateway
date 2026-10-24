using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Engine.Tests;

public class CompositionTests
{
    private readonly record struct OutMsg(int N);

    private readonly record struct OtherMsg(int N);

    private record TestSettings : GatewaySettings
    {
        public int Factor { get; init; } = 2;
    }

    private record OtherSettings : GatewaySettings;

    /// <summary>Declares inbound "a", outbound&lt;OutMsg&gt; "a", an attribute and a module sending through the port.</summary>
    private sealed class TestProfile : IGatewayProfile
    {
        public string Name => "test";

        public OutboundPort<OutMsg> Out = null!;

        public RuleInfo? Always;

        public RuleInfo? Optional;

        public void Configure(GatewayBuilder builder)
        {
            var settings = builder.SettingsAs<TestSettings>();
            var attr = builder.Attributes.Register("x", AttrKind.Int);
            var inPort = builder.RequireInbound("a");
            Out = builder.RequireOutbound<OutMsg>("a");
            var port = Out;
            builder.AddModule(new LambdaModule("m", s =>
            {
                s.Produces(attr);
                s.Consumes(attr);
                s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) => port.Send(new OutMsg(m.N * settings.Factor)));
                Always = s.OnTickEnd().Named("always").Do(static _ => { });
                Optional = s.OnTickEnd().Named("optional").Disabled().Do(static _ => { });
            }));
            _ = inPort;
        }
    }

    private static GatewayBuilder NewBuilder(out TestProfile profile, TestSettings? settings = null, ManualClock? clock = null)
    {
        profile = new TestProfile();
        return new GatewayConfiguration("t", profile, settings ?? new TestSettings())
            .CreateBuilder(clock ?? new ManualClock());
    }

    private static void BindAll(GatewayBuilder b, out InMemoryInbound inbound, out InMemoryOutbound<OutMsg> outbound)
    {
        inbound = new InMemoryInbound(b.Sources.Register("a"), "a");
        outbound = new InMemoryOutbound<OutMsg>();
        b.BindInbound("a", inbound);
        b.BindOutbound("a", outbound);
    }

    [Fact]
    public void Build_succeeds_and_a_module_send_reaches_the_bound_recorder()
    {
        var b = NewBuilder(out var profile);
        BindAll(b, out var inbound, out var outbound);
        var host = b.Build();

        Assert.Same(profile, host.Profile);
        Assert.False(host.Validation.HasErrors);
        Assert.Single(host.InboundPorts);
        Assert.Single(host.OutboundPorts);
        Assert.Same(inbound, host.Inbound("a").Channel);
        Assert.True(host.Outbound<OutMsg>("a").IsBound);
        Assert.Same(profile.Out, host.Outbound<OutMsg>("a"));

        inbound.Enqueue(new PingMsg(21));
        host.Engine.Tick();

        Assert.Equal(42, Assert.Single(outbound.Messages).Message.N);
    }

    [Fact]
    public void Sending_through_an_unbound_port_throws()
    {
        var b = NewBuilder(out var profile);
        Assert.False(profile.Out.IsBound);
        Assert.Throws<InvalidOperationException>(() => profile.Out.Send(new OutMsg(1)));
    }

    [Fact]
    public void Requiring_the_same_port_twice_returns_the_same_port_and_types_must_agree()
    {
        var b = NewBuilder(out _);
        Assert.Same(b.RequireInbound("a"), b.RequireInbound("a"));
        Assert.Same(b.RequireOutbound<OutMsg>("a"), b.RequireOutbound<OutMsg>("a"));
        Assert.Throws<InvalidOperationException>(() => b.RequireOutbound<OtherMsg>("a"));
        // inbound and outbound names are separate namespaces
        Assert.Equal("a", b.RequireInbound("a").Name);
        Assert.Equal(b.Sources.Register("a"), b.RequireInbound("a").Source);
    }

    [Fact]
    public void Build_rejects_an_unbound_inbound_port()
    {
        var b = NewBuilder(out _);
        b.BindOutbound("a", new InMemoryOutbound<OutMsg>());
        var ex = Assert.Throws<GatewayBuildException>(() => b.Build());
        Assert.Contains("Inbound port 'a' is declared but not bound", ex.Message);
    }

    [Fact]
    public void Build_rejects_an_unbound_outbound_port()
    {
        var b = NewBuilder(out _);
        b.BindInbound("a", new InMemoryInbound(b.Sources.Register("a"), "a"));
        var ex = Assert.Throws<GatewayBuildException>(() => b.Build());
        Assert.Contains("Outbound port 'a'", ex.Message);
        Assert.Contains("not bound", ex.Message);
    }

    [Fact]
    public void Build_rejects_bound_but_undeclared_names()
    {
        var b = NewBuilder(out _);
        BindAll(b, out _, out _);
        b.BindInbound("zzz", new InMemoryInbound(b.Sources.Register("zzz"), "zzz"));
        b.BindOutbound("yyy", new InMemoryOutbound<OutMsg>());
        var ex = Assert.Throws<GatewayBuildException>(() => b.Build());
        Assert.Contains("'zzz'", ex.Message);
        Assert.Contains("'yyy'", ex.Message);
    }

    [Fact]
    public void Build_rejects_an_outbound_binding_of_the_wrong_message_type()
    {
        var b = NewBuilder(out _);
        b.BindInbound("a", new InMemoryInbound(b.Sources.Register("a"), "a"));
        b.BindOutbound("a", new InMemoryOutbound<OtherMsg>());
        var ex = Assert.Throws<GatewayBuildException>(() => b.Build());
        Assert.Contains("'a'", ex.Message);
        Assert.Contains(nameof(OutMsg), ex.Message);
        Assert.Contains(nameof(OtherMsg), ex.Message);
    }

    [Fact]
    public void Build_rejects_an_inbound_channel_with_the_wrong_source()
    {
        var b = NewBuilder(out _);
        b.BindInbound("a", new InMemoryInbound(b.Sources.Register("other"), "a"));
        b.BindOutbound("a", new InMemoryOutbound<OutMsg>());
        var ex = Assert.Throws<GatewayBuildException>(() => b.Build());
        Assert.Contains("source 'other'", ex.Message);
    }

    [Fact]
    public void Build_rejects_unknown_rule_names_in_the_settings()
    {
        var b = NewBuilder(out _, new TestSettings { DisabledRules = ["m/nope"], EnabledRules = ["x/y"] });
        BindAll(b, out _, out _);
        var ex = Assert.Throws<GatewayBuildException>(() => b.Build());
        Assert.Contains("m/nope", ex.Message);
        Assert.Contains("x/y", ex.Message);
    }

    [Fact]
    public void Build_rejects_a_validation_error()
    {
        var b = NewBuilder(out _);
        BindAll(b, out _, out _);
        b.AddModule(new LambdaModule("bad", s => s.Produces(new AttrId(99))));
        var ex = Assert.Throws<GatewayBuildException>(() => b.Build());
        Assert.Contains(ValidationCodes.UnknownAttribute, ex.Message);
    }

    [Fact]
    public void All_problems_are_listed_in_one_exception()
    {
        var b = NewBuilder(out _, new TestSettings { DisabledRules = ["m/nope"] });
        b.BindOutbound("a", new InMemoryOutbound<OtherMsg>());
        b.BindInbound("q", new InMemoryInbound(b.Sources.Register("q"), "q"));
        b.AddModule(new LambdaModule("bad", s => s.Produces(new AttrId(99))));
        var ex = Assert.Throws<GatewayBuildException>(() => b.Build());
        Assert.True(ex.Problems.Count >= 5, ex.Message);
        Assert.Contains("Inbound port 'a'", ex.Message);
        Assert.Contains("'q'", ex.Message);
        Assert.Contains(nameof(OtherMsg), ex.Message);
        Assert.Contains("m/nope", ex.Message);
        Assert.Contains(ValidationCodes.UnknownAttribute, ex.Message);
    }

    [Fact]
    public void Build_twice_and_builder_use_after_Build_throw()
    {
        var b = NewBuilder(out _);
        BindAll(b, out _, out _);
        b.Build();
        Assert.Throws<InvalidOperationException>(() => b.Build());
        Assert.Throws<InvalidOperationException>(() => b.RequireInbound("b"));
        Assert.Throws<InvalidOperationException>(() => b.RequireOutbound<OutMsg>("b"));
        Assert.Throws<InvalidOperationException>(() => b.AddModule(new LambdaModule("n", _ => { })));
        Assert.Throws<InvalidOperationException>(() => b.BindInbound("a", new InMemoryInbound(default, "a")));
        Assert.Throws<InvalidOperationException>(() => b.BindOutbound("a", new NullOutbound<OutMsg>()));
        Assert.Throws<InvalidOperationException>(() => b.SettingsAs<TestSettings>());
    }

    [Fact]
    public void A_failed_Build_also_closes_the_builder()
    {
        var b = NewBuilder(out _);
        Assert.Throws<GatewayBuildException>(() => b.Build());
        Assert.Throws<InvalidOperationException>(() => b.Build());
    }

    [Fact]
    public void DisabledRules_disable_and_EnabledRules_enable_a_Disabled_rule()
    {
        var b = NewBuilder(out var profile);
        BindAll(b, out _, out _);
        b.Build();
        Assert.True(profile.Always!.Enabled);
        Assert.False(profile.Optional!.Enabled);

        var b2 = NewBuilder(out var p2, new TestSettings { DisabledRules = ["m/always"], EnabledRules = ["m/optional"] });
        BindAll(b2, out _, out _);
        b2.Build();
        Assert.False(p2.Always!.Enabled);
        Assert.True(p2.Optional!.Enabled);
    }

    [Fact]
    public void Settings_variants_via_with_and_SettingsAs_mismatch()
    {
        var variant = new TestSettings() with { Factor = 5 };
        var b = NewBuilder(out _, variant);
        BindAll(b, out var inbound, out var outbound);
        var host = b.Build();
        Assert.Same(variant, host.Settings);
        inbound.Enqueue(new PingMsg(3));
        host.Engine.Tick();
        Assert.Equal(15, outbound.Messages[0].Message.N);

        var wrong = new GatewayBuilder(new ManualClock(), new OtherSettings());
        var ex = Assert.Throws<InvalidOperationException>(() => wrong.SettingsAs<TestSettings>());
        Assert.Contains(nameof(OtherSettings), ex.Message);
        Assert.Contains(nameof(TestSettings), ex.Message);
    }

    [Fact]
    public void Two_builders_in_one_process_are_independent()
    {
        var b1 = NewBuilder(out _);
        var b2 = NewBuilder(out _, new TestSettings { Factor = 10 });
        BindAll(b1, out var in1, out var out1);
        BindAll(b2, out var in2, out var out2);
        var h1 = b1.Build();
        var h2 = b2.Build();

        Assert.NotSame(h1.Engine, h2.Engine);
        in1.Enqueue(new PingMsg(1));
        h1.Engine.Tick();
        Assert.Single(out1.Messages);
        Assert.Empty(out2.Messages);
        Assert.Equal(1, h1.Engine.TickCount);
        Assert.Equal(0, h2.Engine.TickCount);

        in2.Enqueue(new PingMsg(1));
        h2.Engine.Tick();
        Assert.Equal(10, out2.Messages[0].Message.N);
        Assert.Equal(2, out1.Messages[0].Message.N);
    }
}
