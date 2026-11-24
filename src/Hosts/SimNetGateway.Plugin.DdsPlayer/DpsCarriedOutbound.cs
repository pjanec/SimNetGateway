using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps what carries a non-person entity (DD-032) to its state topic: one <c>Carried</c> instance per carried entity, written when the carrier, the seat or the stowed state changes and
/// disposed when the host says the entity is not carried any more (carrier 0: it was dropped, concealed or left). A person never arrives here. One instance per gateway runtime.
/// </summary>
public sealed class DpsCarriedOutbound : IGatewayModule
{
    private readonly DpsPorts _ports;
    private readonly Dictionary<int, HrotCarried> _written = new();

    internal DpsCarriedOutbound(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsCarriedOutbound);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotCarried>(OnCarried);

    private void OnCarried(in HrotCarried m, IngressContext ctx)
    {
        if (m.CarrierId == 0)
        {
            if (_written.Remove(m.Id))
            {
                _ports.Carried.Send(new DpsItem<D.Carried>(new D.Carried { EntityId = m.Id }, true));
            }

            return;
        }

        if (_written.TryGetValue(m.Id, out var last) && last == m)
        {
            return;
        }

        _written[m.Id] = m;
        _ports.Carried.Send(new DpsItem<D.Carried>(new D.Carried { EntityId = m.Id, CarrierId = m.CarrierId, SeatIndex = m.Seat, Stowed = m.Stowed }));
    }
}
