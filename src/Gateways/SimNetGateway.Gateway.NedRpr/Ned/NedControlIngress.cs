using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>NedSimControl: the session commands of the engine (R-017).</summary>
public sealed class NedControlIngress : IGatewayModule
{
    public string Name => nameof(NedControlIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<NedSimControl>(Handle);

    private static void Handle(in NedSimControl m, IngressContext ctx)
    {
        switch (m.Kind)
        {
            case NedControlKind.Load:
                ctx.Command(SessionCommand.Load(m.Scenario ?? string.Empty, SessionMode.Live));
                break;
            case NedControlKind.LoadDebrief:
                ctx.Command(SessionCommand.Load(m.Scenario ?? string.Empty, SessionMode.Replay));
                break;
            case NedControlKind.Start:
                ctx.Command(SessionCommand.Start);
                break;
            case NedControlKind.Pause:
                ctx.Command(SessionCommand.Pause);
                break;
            case NedControlKind.Resume:
                ctx.Command(SessionCommand.Resume);
                break;
            case NedControlKind.Unload:
                ctx.Command(SessionCommand.Unload);
                break;
            default:
                ctx.Abort($"unknown control kind {(int)m.Kind}");
                break;
        }
    }
}
