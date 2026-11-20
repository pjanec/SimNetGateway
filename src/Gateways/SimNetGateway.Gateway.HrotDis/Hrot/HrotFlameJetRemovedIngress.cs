using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotFlameJetRemoved: look data for sides that draw (DD-018). DIS has no use for it, so this side takes the message and does nothing; it is declared so that every
/// message of the contract has a handler and none is counted as unhandled.
/// </summary>
public sealed class HrotFlameJetRemovedIngress : IGatewayModule
{
    public string Name => nameof(HrotFlameJetRemovedIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotFlameJetRemoved>(Handle);

    private static void Handle(in HrotFlameJetRemoved m, IngressContext ctx)
    {
    }
}
