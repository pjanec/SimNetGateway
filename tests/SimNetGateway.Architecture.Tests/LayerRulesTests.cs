namespace SimNetGateway.Architecture.Tests;

/// <summary>
/// The rule table itself: forbidden references are reported, allowed ones are not. This keeps
/// the "an intentionally forbidden reference fails" check of PLAN stage 0 permanently in the suite.
/// </summary>
public class LayerRulesTests
{
    [Theory]
    [InlineData(Layer.Engine, Layer.Common)]
    [InlineData(Layer.Engine, Layer.Protocol)]
    [InlineData(Layer.EngineSupport, Layer.Gateway)]
    [InlineData(Layer.Common, Layer.Protocol)]
    [InlineData(Layer.Common, Layer.Gateway)]
    [InlineData(Layer.Protocol, Layer.Engine)]
    [InlineData(Layer.Protocol, Layer.Protocol)]
    [InlineData(Layer.Gateway, Layer.Transport)]
    [InlineData(Layer.Gateway, Layer.Host)]
    [InlineData(Layer.Gateway, Layer.Gateway)]
    [InlineData(Layer.Gateway, Layer.EngineSupport)]
    [InlineData(Layer.Transport, Layer.Gateway)]
    [InlineData(Layer.Transport, Layer.Common)]
    [InlineData(Layer.Showcase, Layer.Transport)]
    [InlineData(Layer.Showcase, Layer.Host)]
    [InlineData(Layer.Host, Layer.Host)]
    public void Forbidden_reference_is_reported(Layer from, Layer to)
    {
        var violations = LayerRules.Check("From", from, [new Reference("To", to)], []);

        Assert.Single(violations);
    }

    [Theory]
    [InlineData(Layer.EngineSupport, Layer.Engine)]
    [InlineData(Layer.Common, Layer.Engine)]
    [InlineData(Layer.Gateway, Layer.Engine)]
    [InlineData(Layer.Gateway, Layer.Common)]
    [InlineData(Layer.Gateway, Layer.Protocol)]
    [InlineData(Layer.Transport, Layer.Engine)]
    [InlineData(Layer.Transport, Layer.Protocol)]
    [InlineData(Layer.Showcase, Layer.Gateway)]
    [InlineData(Layer.Showcase, Layer.EngineSupport)]
    [InlineData(Layer.Host, Layer.Gateway)]
    [InlineData(Layer.Host, Layer.Transport)]
    [InlineData(Layer.Host, Layer.Showcase)]
    public void Allowed_reference_is_accepted(Layer from, Layer to)
    {
        var violations = LayerRules.Check("From", from, [new Reference("To", to)], []);

        Assert.Empty(violations);
    }

    [Fact]
    public void Transport_may_reference_only_one_protocol()
    {
        Reference[] references =
        [
            new("SimNetGateway.Engine", Layer.Engine),
            new("SimNetGateway.Protocol.Ned", Layer.Protocol),
            new("SimNetGateway.Protocol.Rpr", Layer.Protocol),
        ];

        var violations = LayerRules.Check("SimNetGateway.Transport.Both", Layer.Transport, references, []);

        Assert.Single(violations);
    }

    [Fact]
    public void Reference_outside_the_known_layers_is_reported()
    {
        var violations = LayerRules.Check("SimNetGateway.Common", Layer.Common,
            [new Reference("SimNetGateway.Engine.Tests", null)], []);

        Assert.Single(violations);
    }

    [Theory]
    [InlineData(Layer.Engine)]
    [InlineData(Layer.Protocol)]
    public void Bcl_only_layer_may_not_use_packages(Layer layer)
    {
        var violations = LayerRules.Check("Project", layer, [], ["Some.Package"]);

        Assert.Single(violations);
    }

    [Fact]
    public void Common_may_use_packages()
    {
        Assert.Empty(LayerRules.Check("SimNetGateway.Common", Layer.Common, [], ["Some.Package"]));
    }
}
