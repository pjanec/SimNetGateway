using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Contracts;
using Hrot.Look.Effects;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

// DPS round 3 (DD-019 3.1, 3.2): the suggested camera view and the impacts of rounds on the static world.
namespace SimNetGateway.HrotHost;

/// <summary>
/// Where the host reads what HROT suggests the camera shows (DD-019 3.1). HROT has no engine-level source of it yet: the film director, the follow camera and the
/// operator's camera live in <c>Hrot.Presentation</c> (the map canvas's view telemetry is per canvas, published by a drawing layer), which the host must not reference
/// (R-050). The seam exists so that a HROT engine-level singleton can be wired in with no change to the host; no implementation is shipped, so the host emits no view.
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

/// <summary>
/// The surfaces rounds marked this frame (<see cref="RoundImpactNotification"/>): one <see cref="HrotImpact"/> per event. The surface class is the one HROT's
/// <c>EffectRules.ForImpact</c> gives its puff and mark (Ground within a quarter metre of the terrain, else Building), read from the specs that function returns, so the
/// rule stays HROT's; empty when HROT draws no effect for the round. HROT's event carries no material, so that is empty too. Main thread only.
/// </summary>
internal sealed class ImpactGather
{
    private readonly List<EffectSpec> _specs = new();

    public int Gather(EntityRepository world, ITkbDatabase? tkb, TkbGather types, InMemoryInbound inbound, double simTime)
    {
        if (!world.Bus.IsRegistered<RoundImpactNotification>())
        {
            return 0;
        }

        var hits = world.ReadEvents<RoundImpactNotification>();
        for (int i = 0; i < hits.Length; i++)
        {
            ref readonly var hit = ref hits[i];
            _specs.Clear();
            EffectRules.ForImpact(in hit, world, tkb, _specs);
            string surface = _specs.Count > 0 ? _specs[0].Surface.ToString() : string.Empty;
            types.Reference(hit.Ammo);
            inbound.Enqueue(new HrotImpact(simTime, new HrotVec3(hit.X, hit.Y, hit.Z), new HrotVec3(hit.NormalX, hit.NormalY, hit.NormalZ), surface, ((ImpactKind)hit.Kind).ToString(),
                hit.Ammo, LookIds.NetworkIdOf(world, hit.Shooter), string.Empty));
        }

        return hits.Length;
    }
}
