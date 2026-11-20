using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotGroundMarks: look data for sides that draw (DD-018). DIS has no use for it, so this side takes the message and does nothing; it is declared so that every
/// message of the contract has a handler and none is counted as unhandled.
/// </summary>
public sealed class HrotGroundMarksIngress : IGatewayModule
{
    public string Name => nameof(HrotGroundMarksIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotGroundMarks>(Handle);

    private static void Handle(in HrotGroundMarks m, IngressContext ctx)
    {
    }
}
