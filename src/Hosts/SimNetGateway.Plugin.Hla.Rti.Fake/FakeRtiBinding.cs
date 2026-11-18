using SimNetGateway.Protocol.Rpr.Hla;
using SimNetGateway.Transport.FakeRti;
using SimNetGateway.Transport.HlaRpr;

namespace SimNetGateway.Plugin.Hla.Rti.Fake;

/// <summary>The fake RTI: federates created through one binding object meet in its hub, inside the process (nothing leaves it).</summary>
public sealed class FakeRtiBinding : IRtiBinding
{
    /// <summary>"fake".</summary>
    public const string RtiName = "fake";

    /// <summary>The hub the ambassadors of this binding join.</summary>
    public FakeRtiHub Hub { get; } = new();

    public string Name => RtiName;

    public IRtiAmbassador CreateAmbassador() => Hub.CreateAmbassador();
}
