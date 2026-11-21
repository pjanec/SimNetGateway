using Fdp.Core;
using Fdp.ModuleHost.Time;
using Fdp.Toolkit.Time.Domain;
using Fdp.Toolkit.Time.Messages;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The simulation clock of the node as pivot messages (DD-022 3b, 3c): HROT's time events on the node's bus, read-only (<c>Read</c> / <c>ReadManaged</c> do not consume, other readers are
/// unaffected), become <see cref="HrotClockAnchor"/> (the master's time-mode switch: boot, pause, resume, snap, jump) and <see cref="HrotStep"/> (the advance-frame order of a step).
/// HROT announces nothing when the scale of a running clock changes (finding F-H1): this re-anchors itself at that frame, from the controller's own state, so a client's formula stays
/// continuous across the change. A gateway that starts late (a world boundary rebuilds it) takes the controller's current state as its first anchor. Only the bus domain events are used,
/// never HROT's wire DTOs (R-134). A step is held back by <see cref="Read"/> and sent by <see cref="Flush"/> after the frame's world gather, so a client reads a step's state before
/// its clock step (D-B3).
/// </summary>
internal sealed class HrotClockGather
{
    /// <summary>The station's wall tick and the controller's tick may differ by this much (100 ns) before the controller's pair is not trusted as a real-clock pair (after stepping its wall ticks are synthetic).</summary>
    private const long TrustedDifferenceTicks = 2 * TimeSpan.TicksPerSecond;

    private readonly HrotServices _services;
    private readonly List<HrotStep> _steps = new();
    private bool _anchored;
    private bool _stepped;
    private float _scale = 1f;
    private float _fixedDelta;

    public HrotClockGather(HrotServices services) => _services = services;

    /// <summary>True when the last <see cref="Read"/> found steps, so the world gather of this frame must run before they are sent.</summary>
    public bool HasSteps => _steps.Count > 0;

    /// <summary>The gateway runtime is new: its first frame anchors the clock again (a client of the new runtime has no anchor yet).</summary>
    public void Reset()
    {
        _anchored = false;
        _steps.Clear();
    }

    /// <summary>Reads this frame's time events: anchors are sent at once, steps are kept for <see cref="Flush"/>.</summary>
    public void Read(InMemoryInbound inbound)
    {
        var bus = _services.EventBus;
        var controller = _services.TimeController();
        if (bus is null && controller is null)
        {
            return;
        }

        long epoch = controller?.TimelineEpoch ?? 0;
        bool anchorSent = false;

        if (bus is not null && bus.IsRegistered<SwitchTimeModeEvent>())
        {
            SwitchTimeModeEvent last = default;
            bool any = false;
            foreach (ref readonly var e in bus.Read<SwitchTimeModeEvent>())
            {
                last = e;   // several in one frame (a snap, then a resume): the last one is where the clock is
                any = true;
            }

            if (any)
            {
                float scale = last.TimeScale > 0f ? last.TimeScale : controller?.GetTimeScale() ?? _scale;
                long wall = last.BarrierWallTicks > 0 ? last.BarrierWallTicks : _services.WallTicks();
                Send(inbound, new HrotClockAnchor(last.TargetMode == TimeMode.Deterministic, wall, last.SimTimeSnapshot, scale, last.FixedDelta, epoch));
                anchorSent = true;
            }
        }

        if (!anchorSent && !_anchored && controller is not null)
        {
            // no event was seen (the clock booted before this gateway did): the controller's own state is the anchor
            Send(inbound, FromController(controller, epoch, stepped: controller.GetMode() == TimeMode.Deterministic));
            anchorSent = true;
        }

        if (bus is not null)
        {
            var intents = bus.ReadManaged<AdvanceFrameIntent>();
            for (int i = 0; i < intents.Count; i++)
            {
                var step = intents[i];
                float scale = controller?.GetTimeScale() ?? _scale;
                _steps.Add(new HrotStep(step.FrameID, step.FixedDelta, step.TargetSimTime, scale, epoch));
                _stepped = true;
                _scale = scale;
            }
        }

        // F-H1: a running clock whose scale changed has no anchor from HROT
        if (!anchorSent && _anchored && !_stepped && controller is not null && controller.GetMode() == TimeMode.Continuous && controller.GetTimeScale() != _scale)
        {
            Send(inbound, FromController(controller, epoch, stepped: false));
        }
    }

    /// <summary>Sends the steps <see cref="Read"/> kept; call after the frame's world state was gathered.</summary>
    public void Flush(InMemoryInbound inbound)
    {
        foreach (var step in _steps)
        {
            inbound.Enqueue(step);
        }

        _steps.Clear();
    }

    private HrotClockAnchor FromController(ITimeController controller, long epoch, bool stepped)
    {
        var state = controller.GetCurrentState();
        long now = _services.WallTicks();
        // the controller's (wall, sim) pair belongs to its last update: use it while its wall ticks are the real clock's (running, or just resumed); else the station's now
        long wall = Math.Abs(state.TotalWallTicks - now) <= TrustedDifferenceTicks ? state.TotalWallTicks : now;
        return new HrotClockAnchor(stepped, wall, state.TotalTime, controller.GetTimeScale(), _fixedDelta, epoch);
    }

    private void Send(InMemoryInbound inbound, HrotClockAnchor anchor)
    {
        _anchored = true;
        _stepped = anchor.Stepped;
        _scale = anchor.TimeScale;
        _fixedDelta = anchor.FixedDelta;
        inbound.Enqueue(anchor);
    }
}
