using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// HrotTypeRecord: data for sides that draw (DD-018). DIS has no use for it, so this side takes the message and does nothing; it is declared so that every
/// message of the contract has a handler and none is counted as unhandled.
/// </summary>
public sealed class HrotTypeRecordIngress : IGatewayModule
{
    public string Name => nameof(HrotTypeRecordIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotTypeRecord>(Handle);

    private static void Handle(in HrotTypeRecord m, IngressContext ctx)
    {
    }
}
