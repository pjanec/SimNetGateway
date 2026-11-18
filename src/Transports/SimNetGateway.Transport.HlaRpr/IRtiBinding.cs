using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.HlaRpr;

/// <summary>
/// One RTI product behind the 1516e-mirroring port: a plugin of the HLA plugin implements it and is found by <see cref="Name"/>, the value of the <c>hla.rti</c> setting.
/// </summary>
public interface IRtiBinding
{
    /// <summary>The name <c>hla.rti</c> selects the binding by (case-sensitive).</summary>
    string Name { get; }

    /// <summary>A new ambassador, not connected yet (the connection connects it with itself as the callback target).</summary>
    IRtiAmbassador CreateAmbassador();
}
