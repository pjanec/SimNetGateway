using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotAnimal: data for sides that draw (DD-018). DIS has no use for it, so this side takes the message and does nothing; it is declared so that every
/// message of the contract has a handler and none is counted as unhandled.
/// </summary>
public sealed class HrotAnimalIngress : IGatewayModule
{
    public string Name => nameof(HrotAnimalIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotAnimal>(Handle);

    private static void Handle(in HrotAnimal m, IngressContext ctx)
    {
    }
}
