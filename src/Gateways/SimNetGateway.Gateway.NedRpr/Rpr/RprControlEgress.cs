using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>Rule "Control": a session change from another source as RprSimulationControl (Start/Resume = StartResume, Pause = StopFreeze).</summary>
public sealed class RprControlEgress : IGatewayModule
{
    private readonly SourceId _rpr;
    private readonly IOutboundChannel<IRprMessage> _output;

    public RprControlEgress(NedRprSources sources, IOutboundChannel<IRprMessage> output)
    {
        _rpr = sources.Rpr;
        _output = output;
    }

    public string Name => nameof(RprControlEgress);

    public void Register(ModuleSetup setup) =>
        setup.OnSessionChanged()
            .Named("Control")
            .When(t => t.Source != _rpr)
            .Do(t =>
            {
                switch (t.Command.Kind)
                {
                    case SessionCommandKind.Start:
                    case SessionCommandKind.Resume:
                        _output.Send(new RprSimulationControl(RprControlKind.StartResume));
                        break;
                    case SessionCommandKind.Pause:
                        _output.Send(new RprSimulationControl(RprControlKind.StopFreeze));
                        break;
                }
            });
}
