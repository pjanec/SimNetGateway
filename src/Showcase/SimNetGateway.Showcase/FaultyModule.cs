using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Showcase;

/// <summary>
/// Test-only module (DD-002 act 7, S-7.3): an ingress handler for <see cref="NedIffStatus"/> that throws while it is
/// <see cref="Armed"/> and does nothing otherwise. Registered by the harness, never by the gateway profile.
/// </summary>
public sealed class FaultyModule : IGatewayModule
{
    public string Name => nameof(FaultyModule);

    /// <summary>True: the handler throws.</summary>
    public bool Armed { get; set; }

    /// <summary>How often the handler threw.</summary>
    public int Thrown { get; private set; }

    public void Register(ModuleSetup setup) => setup.OnMessage<NedIffStatus>(Handle);

    private void Handle(in NedIffStatus message, IngressContext ctx)
    {
        if (Armed)
        {
            Thrown++;
            throw new InvalidOperationException("FaultyModule: the handler for NedIffStatus failed on purpose.");
        }
    }
}
