using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps the size of a standing static obstacle, a roadblock (DD-035, T-084), to its state topic: one <c>Obstacle</c> instance per obstacle, written when the host sends it (it stands, or its size changed) and disposed when the host
/// says it is gone (removed at zero health, for example by a bulldozer). The obstacle stays an <c>Entity</c> too. One instance per gateway runtime.
/// </summary>
public sealed class DpsObstacleOutbound : IGatewayModule
{
    private readonly DpsPorts _ports;
    private readonly HashSet<int> _written = new();

    internal DpsObstacleOutbound(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsObstacleOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotObstacle>(OnObstacle);
        setup.OnMessage<HrotObstacleEnded>(OnEnded);
    }

    private void OnObstacle(in HrotObstacle m, IngressContext ctx)
    {
        _written.Add(m.Id);
        _ports.Obstacle.Send(new DpsItem<D.Obstacle>(new D.Obstacle { EntityId = m.Id, Length = m.Length, Width = m.Width, Height = m.Height }));
    }

    private void OnEnded(in HrotObstacleEnded m, IngressContext ctx)
    {
        if (_written.Remove(m.Id))
        {
            _ports.Obstacle.Send(new DpsItem<D.Obstacle>(new D.Obstacle { EntityId = m.Id }, true));
        }
    }
}
