using DISnet;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Dis7;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// With <c>dis.hrotControlsDis</c> (default on) the HROT session is announced to the exercise (DD-006 6.5, DD-007 section 6): rule
/// "Siman" sends a Start/Resume PDU when HROT starts or resumes time (Loaded to Running, Paused to Running) and a Stop/Freeze PDU
/// (reason recess) when it pauses. Only session changes that came from the HROT side are announced - a pause that DIS itself
/// requested is not echoed back - and only in Live mode (stepping through a replay is not the exercise stopping). Loading and
/// unloading send nothing. Both PDUs go to all sites, applications and entities; the originating id is the gateway (site,
/// application, entity 0).
/// </summary>
public sealed class DisSessionEgress : IGatewayModule
{
    /// <summary>The Stop/Freeze reason "recess" (SISO-REF-010 reason enumeration).</summary>
    public const byte ReasonRecess = 1;

    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;
    private readonly IOutboundChannel<Pdu> _output;
    private IGatewayContext _gateway = null!;
    private uint _requestId;

    public DisSessionEgress(HrotDisSources sources, HrotDisServices services, IOutboundChannel<Pdu> output)
    {
        _sources = sources;
        _services = services;
        _output = output;
    }

    public string Name => nameof(DisSessionEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        bool enabled = _services.Config.Dis is { HrotControlsDis: true };
        setup.OnSessionChanged()
            .Named("Siman")
            .When(t => enabled && t.Source == _sources.Hrot && _gateway.Session.Mode == SessionMode.Live &&
                       t.Command.Kind is SessionCommandKind.Start or SessionCommandKind.Resume or SessionCommandKind.Pause)
            .Do(OnChanged);
    }

    private void OnChanged(SessionTrigger t)
    {
        byte exercise = (byte)_services.Config.Dis!.ExerciseId;
        double wall = _gateway.WallTime;
        var originator = DisConversions.ToEntityId(new DisEntityKey(_services.OwnSite, _services.OwnApplication, 0));
        Pdu message;
        if (t.Command.Kind == SessionCommandKind.Pause)
        {
            message = new StopFreezePdu
            {
                ExerciseID = exercise,
                Timestamp = DisTimestamp.EncodeRelative(wall),
                OriginatingEntityID = originator,
                ReceivingEntityID = DisConversions.AllEntities(),
                RealWorldTime = DisConversions.ClockOf(wall),
                Reason = ReasonRecess,
                FrozenBehavior = 0,
                RequestID = ++_requestId,
            };
        }
        else
        {
            message = new StartResumePdu
            {
                ExerciseID = exercise,
                Timestamp = DisTimestamp.EncodeRelative(wall),
                OriginatingEntityID = originator,
                ReceivingEntityID = DisConversions.AllEntities(),
                RealWorldTime = DisConversions.ClockOf(wall),
                SimulationTime = DisConversions.ClockOf(_gateway.SimTime),
                RequestID = ++_requestId,
            };
        }

        _output.Send(in message);
    }
}
