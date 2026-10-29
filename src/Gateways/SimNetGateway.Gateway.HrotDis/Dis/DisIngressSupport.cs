using DISnet;
using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>What every DIS ingress module shares: the exercise and echo filters, and the resolution of entity ids to pivot entities.</summary>
internal static class DisIngressSupport
{
    /// <summary>
    /// True when the PDU must be ignored: another exercise, or it carries the gateway's own site and application (a looped-back
    /// echo of what the gateway sent; the transport filters it too, the gateway does not rely on that). Counted either way.
    /// </summary>
    public static bool Reject(HrotDisServices services, byte exercise, ushort site, ushort application)
    {
        if (exercise != services.Config.Dis!.ExerciseId)
        {
            services.DisIngress.WrongExercise++;
            return true;
        }

        if (services.IsOwn(site, application))
        {
            services.DisIngress.Echoes++;
            return true;
        }

        return false;
    }

    /// <summary>Resolves an entity id of a PDU to its pivot entity (any origin: a Fire may name a HROT entity by the id the gateway gave it); false for "no entity" and unknown ids.</summary>
    public static bool TryResolve(IngressContext ctx, HrotDisMaps maps, EntityID id, out EntityId entity)
    {
        entity = default;
        if (DisConversions.IsNoEntity(id))
        {
            return false;
        }

        return maps.DisEntity.TryResolve(ctx.Changes, DisConversions.KeyOf(id), out entity) && ctx.State.Find(entity) is not null;
    }
}
