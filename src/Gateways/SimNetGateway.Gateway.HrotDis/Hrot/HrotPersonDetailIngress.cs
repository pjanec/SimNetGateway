using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotPersonDetail: the semantic state of a person's body (DD-017 3.5). DIS has no use for it, so this side takes the message and does nothing; it is
/// declared so that every message of the contract has a handler and none is counted as unhandled. The sides that publish it read it from the message itself.
/// </summary>
public sealed class HrotPersonDetailIngress : IGatewayModule
{
    public string Name => nameof(HrotPersonDetailIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotPersonDetail>(Handle);

    private static void Handle(in HrotPersonDetail m, IngressContext ctx)
    {
    }
}
