using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotMoverRemoved: ambient traffic for sides that draw (DD-018). DIS has no use for it, so this side takes the message and does nothing; it is declared so that every
/// message of the contract has a handler and none is counted as unhandled.
/// </summary>
public sealed class HrotMoverRemovedIngress : IGatewayModule
{
    public string Name => nameof(HrotMoverRemovedIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotMoverRemoved>(Handle);

    private static void Handle(in HrotMoverRemoved m, IngressContext ctx)
    {
    }
}
