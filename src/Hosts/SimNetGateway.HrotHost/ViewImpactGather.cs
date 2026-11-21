using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Contracts;
using Hrot.Look.Effects;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

// DPS round 3 (DD-019 3.1): the suggested camera view. The impacts of rounds are made from the effect entities (T-051, EffectGather).
namespace SimNetGateway.HrotHost;

/// <summary>
/// Where the host reads what HROT suggests the camera shows (DD-019 3.1). The shipped source is <see cref="HrotCameraViewSource"/>, over HROT's own <c>CameraSuggestion</c> singleton;
/// the seam stays so that a host (or a test) can put another source in its place.
/// </summary>
public interface ISuggestedViewSource
{
    /// <summary>The suggested view of this frame; false = HROT suggests nothing now. Main thread only.</summary>
    bool TryRead(EntityRepository world, out HrotView view);
}

/// <summary>The suggested view: one <see cref="HrotView"/> per gather while the source answers, one "None" sample when it stops. Main thread only.</summary>
internal sealed class ViewGather
{
    private bool _active;

    public void Clear() => _active = false;

    public void Gather(EntityRepository world, ISuggestedViewSource? source, InMemoryInbound inbound, double simTime)
    {
        if (source is not null && source.TryRead(world, out var view))
        {
            _active = true;
            inbound.Enqueue(in view);
        }
        else if (_active)
        {
            _active = false;
            inbound.Enqueue(new HrotView("None", string.Empty, 0, 0f, 0f, HrotVec3.Zero, HrotQuat.Identity, 0f, string.Empty, simTime));
        }
    }
}
