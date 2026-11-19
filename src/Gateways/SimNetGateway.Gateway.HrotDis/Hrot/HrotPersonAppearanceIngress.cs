using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotPersonAppearance: look data for sides that draw (DD-017 3.10). DIS has no use for it, so this side takes the message and does nothing; it is declared so that every
/// message of the contract has a handler and none is counted as unhandled.
/// </summary>
public sealed class HrotPersonAppearanceIngress : IGatewayModule
{
    public string Name => nameof(HrotPersonAppearanceIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotPersonAppearance>(Handle);

    private static void Handle(in HrotPersonAppearance m, IngressContext ctx)
    {
    }
}
