using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// DIS controls HROT's time only when the configuration says so (<c>dis.disControlsHrot</c>, default off, R-087). Rule "Request": a
/// Pause or Resume session command whose source is the DIS side (raised by the DIS modules of H2b for Stop/Freeze and
/// Start/Resume PDUs) becomes a <see cref="TimeRequest"/>. Rule "Veto": without that setting every DIS session command is refused,
/// so the engine session only ever mirrors HROT (<see cref="HrotSessionIngress"/>).
/// </summary>
public sealed class TimeRequestEgress : IGatewayModule
{
    private readonly HrotDisSources _sources;
    private readonly bool _disControlsHrot;
    private readonly IOutboundChannel<IGatewayToHrotMessage> _output;

    public TimeRequestEgress(HrotDisSources sources, HrotDisServices services, IOutboundChannel<IGatewayToHrotMessage> output)
    {
        _sources = sources;
        _disControlsHrot = services.Config.Dis is { DisControlsHrot: true };
        _output = output;
    }

    public string Name => nameof(TimeRequestEgress);

    public void Register(ModuleSetup setup)
    {
        setup.OnSessionRequest()
            .Named("Veto")
            .When(t => t.Source == _sources.Dis && !_disControlsHrot)
            .Do(t => t.Veto("the DIS side does not control HROT's time (dis.disControlsHrot is false)"));

        setup.OnSessionChanged()
            .Named("Request")
            .When(t => t.Source == _sources.Dis && _disControlsHrot &&
                       t.Command.Kind is SessionCommandKind.Pause or SessionCommandKind.Resume)
            .Do(t => _output.Send(new TimeRequest(t.Command.Kind == SessionCommandKind.Pause ? TimeRequestKind.Pause : TimeRequestKind.Resume)));
    }
}
