using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The simulation clock on DPS (DD-022 3b, 3c), beside <see cref="DpsOutbound"/>: maps the host's <see cref="HrotClockAnchor"/> to <c>SimClock</c> (one sample per anchor, none periodic)
/// and each <see cref="HrotStep"/> to a <c>ClockStep</c> plus a <c>SimClock</c> (Stepped, at the step's target). In normal stepping a step is never acknowledged
/// (<c>AckRequired</c> false, <c>Samples</c> 0). The host enqueues a step after the frame's world state, and the engine delivers messages in the order they arrive, so a client
/// reads a step's state before its <c>ClockStep</c> (D-B3). The probe answer is <see cref="DpsClockEcho"/>, on a thread of its own. One instance per gateway runtime.
/// </summary>
public sealed class DpsClockModule : IGatewayModule
{
    private readonly DpsPorts _ports;
    private uint _seq;
    private long _stepSeq, _wallTicks, _epoch;
    private float _fixedDelta;

    internal DpsClockModule(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsClockModule);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotClockAnchor>(OnAnchor);
        setup.OnMessage<HrotStep>(OnStep);
    }

    private void OnAnchor(in HrotClockAnchor a, IngressContext ctx)
    {
        if (!double.IsFinite(a.AnchorSimTime) || !float.IsFinite(a.TimeScale))
        {
            return;
        }

        _wallTicks = a.AnchorWallTicks;
        _epoch = a.TimelineEpoch;
        _fixedDelta = a.FixedDelta;
        Send(a.Stepped, a.AnchorSimTime, a.TimeScale);
    }

    private void OnStep(in HrotStep step, IngressContext ctx)
    {
        if (!double.IsFinite(step.TargetSimTime) || !float.IsFinite(step.TimeScale))
        {
            return;
        }

        _stepSeq = step.StepSeq;
        _epoch = step.TimelineEpoch;
        _fixedDelta = step.FixedDelta;
        Send(true, step.TargetSimTime, step.TimeScale);
        _ports.ClockStep.Send(new DpsItem<D.ClockStep>(new D.ClockStep
        {
            StepSeq = step.StepSeq,
            FixedDelta = step.FixedDelta,
            TargetSimTime = step.TargetSimTime,
            TimeScale = step.TimeScale,
            TimelineEpoch = step.TimelineEpoch,
            AckRequired = false,
            Samples = 0,
        }));
    }

    private void Send(bool stepped, double simTime, float scale) =>
        _ports.SimClock.Send(new DpsItem<D.SimClock>(new D.SimClock
        {
            Mode = stepped ? D.ClockMode.ClockModeStepped : D.ClockMode.ClockModeContinuous,
            AnchorWallTicks = _wallTicks,
            AnchorSimTime = simTime,
            TimeScale = scale,
            FixedDelta = _fixedDelta,
            StepSeq = _stepSeq,
            TimelineEpoch = _epoch,
            Seq = ++_seq,
        }));
}
