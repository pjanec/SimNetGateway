using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Routes a player message to the seats it addresses (DD-023 D-15, T-054): each seat's station is asked whether the message is for it (<see cref="IPlayerStation.Addressed"/>, which the host answers with HROT's one
/// addressing rule - no filter of its own here) and the message is written on THAT seat's participant. A seat with no station (a host with no player mode) gets every message. Index loops: no allocation per message.
/// </summary>
internal sealed class DpsMessageRouter : IOutboundChannel<DpsItem<D.PlayerMessage>>
{
    private readonly IReadOnlyList<DpsSeat> _seats;

    public DpsMessageRouter(IReadOnlyList<DpsSeat> seats) => _seats = seats;

    public void Send(in DpsItem<D.PlayerMessage> item)
    {
        var message = item.Value;
        string kind = message.To switch
        {
            D.MessageAddress.MessageAddressAll => "All",
            D.MessageAddress.MessageAddressSide => "Side",
            D.MessageAddress.MessageAddressStation => "Station",
            D.MessageAddress.MessageAddressUnit => "Unit",
            D.MessageAddress.MessageAddressInstructor => "Instructor",
            _ => "Unknown",
        };
        for (int i = 0; i < _seats.Count; i++)
        {
            var seat = _seats[i];
            if (seat.Station is not { } station || station.Addressed(kind, message.ToValue))
            {
                seat.PlayerMessage.Send(item);
            }
        }
    }
}
