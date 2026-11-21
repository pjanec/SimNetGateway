using CycloneDDS.Runtime;
using SimNetGateway.Gateway.HrotDis;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// One player position of the gateway (DD-023): everything that belongs to ONE client - its <c>PlayerSlot</c>, <c>OwnUnit</c> and <c>GizmoFrame</c> writers, and the topics its client
/// writes (<c>JoinRequest</c> / <c>ControlFrame</c> / <c>Leave</c> through <see cref="DpsInbound"/>, <c>GizmoView</c>, <c>ClockProbe</c> answered by <see cref="DpsClockEcho"/>,
/// <c>ClientWorld</c> into the host service's table) - read on the seat's own partition <c>&lt;base&gt;-n</c> (D-10: never on the world's <c>&lt;base&gt;-*</c>, so a client with
/// no seat can neither play nor hold up a load). With one seat the "own" participant is the world participant itself (D-6). Main thread only (the echo thread touches only its
/// own reader and writer).
/// </summary>
internal sealed class DpsSeat : IDisposable
{
    private readonly DdsParticipant _participant;
    private readonly bool _ownsParticipant;
    private readonly List<IDisposable> _channels = new();
    private readonly DpsInbound _inbound;
    private readonly DpsPlayerSlot _slot;
    private readonly DpsGizmos _gizmos;
    private readonly DpsClientWorldInbound? _clientWorlds;
    private readonly DpsClockEcho _echo;

    /// <param name="index">The seat number, 1 ..: the station is <c>node | index &lt;&lt; 16</c>.</param>
    /// <param name="partition">The partition this seat's topics are written and read in.</param>
    /// <param name="participant">The participant to use: the world's (one seat) or a new one in <paramref name="partition"/> (<paramref name="ownsParticipant"/>).</param>
    public DpsSeat(int index, string partition, DdsParticipant participant, bool ownsParticipant, DpsSettings settings, IPlayerStation? station, IGizmoSource? gizmoSource,
        DpsHostService? service, Func<long>? stationTicks, Action<string> log)
    {
        Index = index;
        Partition = partition;
        Station = station;
        _participant = participant;
        _ownsParticipant = ownsParticipant;
        try
        {
            PlayerSlot = Add(new DpsChannel<D.PlayerSlot>(_participant));
            OwnUnit = Add(new DpsChannel<D.OwnUnit>(_participant));
            _inbound = new DpsInbound(_participant, settings, station, log);
            _slot = new DpsPlayerSlot(station, PlayerSlot, OwnUnit);
            GizmoFrame = Add(new DpsChannel<D.GizmoFrame>(_participant));
            _gizmos = new DpsGizmos(_participant, settings, partition, gizmoSource, _inbound, GizmoFrame, log);
            if (service is not null)
            {
                _clientWorlds = new DpsClientWorldInbound(_participant, settings, service.Worlds);
            }

            _echo = new DpsClockEcho(_participant, stationTicks ?? DpsClockEcho.DefaultClock.Ticks, log);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The seat number, 1 ...</summary>
    public int Index { get; }

    /// <summary>The partition of this seat's topics.</summary>
    public string Partition { get; }

    /// <summary>The player station behind this seat (null = a host with no player mode).</summary>
    public IPlayerStation? Station { get; }

    internal DpsChannel<D.PlayerSlot> PlayerSlot { get; } = null!;

    internal DpsChannel<D.OwnUnit> OwnUnit { get; } = null!;

    internal DpsChannel<D.GizmoFrame> GizmoFrame { get; } = null!;

    /// <summary>The client id of the seat's player (0 = nobody plays).</summary>
    internal ulong InboundOwner => _inbound?.Owner ?? 0;

    /// <summary>Clock probes answered on this seat.</summary>
    internal int ProbesAnswered => _echo?.Answered ?? 0;

    /// <summary>Once per frame: the seat's client topics are read, the slot and the unit are written on change.</summary>
    public void Pump()
    {
        _clientWorlds?.Pump();
        _inbound.Pump();
        _gizmos.Pump();
        _slot.Pump();
    }

    private DpsChannel<T> Add<T>(DpsChannel<T> channel)
        where T : struct
    {
        _channels.Add(channel);
        return channel;
    }

    public void Dispose()
    {
        _echo?.Dispose();
        _clientWorlds?.Dispose();
        _gizmos?.Dispose();
        _inbound?.Dispose();
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }

        _channels.Clear();
        if (_ownsParticipant)
        {
            _participant.Dispose();
        }
    }
}
