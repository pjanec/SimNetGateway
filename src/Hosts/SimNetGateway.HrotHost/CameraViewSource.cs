using Fdp.Core;
using Fdp.Toolkit.Time;
using Hrot.Look;
using Hrot.Map.Common.Components;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The suggested view as HROT states it (DD-019 3.1): HROT's own <c>CameraSuggestion</c> world singleton, read through its look library's <c>CameraAdvice.TryGet</c>. The map
/// writes it every frame while its 3-D view is on, so a node with no map (or a map showing 2-D) suggests nothing. HROT's names pass through as names; the pose is HROT's world
/// frame as every other gathered pose is.
/// </summary>
public sealed class HrotCameraViewSource : ISuggestedViewSource
{
    public bool TryRead(EntityRepository world, out HrotView view)
    {
        if (!CameraAdvice.TryGet(world, out var s))
        {
            view = default;
            return false;
        }

        // the director names its follow unit by network id; the other drivers bind an entity
        int target = s.BoundNetId != 0 ? s.BoundNetId : LookIds.NetworkIdOf(world, s.Bound);
        view = new HrotView(
            s.Source.ToString(),
            s.Mode == CameraMode.FixedKey ? "Fixed" : s.Mode.ToString(),   // the contract's name for HROT's FixedKey
            target,
            s.Distance,
            s.ShoulderSide,
            new HrotVec3(s.Position.X, s.Position.Y, s.Position.Z),
            new HrotQuat(s.Orientation.X, s.Orientation.Y, s.Orientation.Z, s.Orientation.W),
            s.FovyDegrees,
            s.Take.ToString(),
            SimClock.TotalTimeOf(world),
            s.Cut);
        return true;
    }
}
