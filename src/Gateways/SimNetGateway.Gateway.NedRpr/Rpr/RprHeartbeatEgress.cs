using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Rule "Heartbeat" (RPR's protocol timing, DD-001 6.5): every check period, a full update with the position predicted to
/// now (R-028) for each published entity for which nothing was sent for the heartbeat period. It stays quiet while updates flow.
/// </summary>
public sealed class RprHeartbeatEgress : IGatewayModule
{
    private readonly RprPublication _publication;
    private readonly TimeSpan _period;
    private readonly TimeSpan _checkPeriod;

    public RprHeartbeatEgress(RprPublication publication, TimeSpan period, TimeSpan checkPeriod)
    {
        _publication = publication;
        _period = period;
        _checkPeriod = checkPeriod;
    }

    public string Name => nameof(RprHeartbeatEgress);

    public void Register(ModuleSetup setup)
    {
        _publication.Attach(setup);
        setup.Every(_checkPeriod)
            .Named("Heartbeat")
            .Do(t => _publication.SendHeartbeats(t.WallTime, _period.TotalSeconds));
    }
}
