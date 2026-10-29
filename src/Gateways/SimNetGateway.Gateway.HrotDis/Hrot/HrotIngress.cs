using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>What <see cref="HrotIngress.Resolve"/> found for a HROT entity id.</summary>
internal enum EntityResolution
{
    /// <summary>Unknown id and creation not requested.</summary>
    Missing,

    /// <summary>The id resolves to an entity of the HROT side (committed, or created earlier in this transaction).</summary>
    Existing,

    /// <summary>The id was unknown; an entity was created and bound in this transaction.</summary>
    Created,

    /// <summary>The id resolves to an entity of another origin: a DIS replica that HROT reports back (R-022). Do nothing.</summary>
    Echo,
}

/// <summary>Resolve-or-create with the echo rule that every HROT ingress shares (DD-001 7, DD-006 3.2).</summary>
internal static class HrotIngress
{
    public static EntityResolution Resolve(IngressContext ctx, HrotDisMaps maps, long hrotId, bool create, out EntityId id) =>
        Resolve(ctx, maps.HrotEntity, hrotId, create, out id);

    /// <summary>The same for any wire key (a DIS entity id, an RPR object handle): an entity of another origin than the ingress source is an echo.</summary>
    public static EntityResolution Resolve<TKey>(IngressContext ctx, IdentityMap<TKey> map, in TKey key, bool create, out EntityId id)
        where TKey : notnull, IEquatable<TKey>
    {
        if (map.TryResolve(ctx.Changes, key, out id))
        {
            var record = ctx.State.Find(id);
            if (record is not null && record.Origin != ctx.Source)
            {
                return EntityResolution.Echo;
            }

            return EntityResolution.Existing;
        }

        if (!create)
        {
            return EntityResolution.Missing;
        }

        id = ctx.Changes.CreateEntity();
        map.Bind(ctx.Changes, id, key);
        return EntityResolution.Created;
    }

    /// <summary>The entity of any origin that an event names by HROT id (a target may be a DIS replica); false for 0 and unknown ids.</summary>
    public static bool TryFind(IngressContext ctx, HrotDisMaps maps, long hrotId, out EntityId id)
    {
        id = default;
        return hrotId != 0 && maps.HrotEntity.TryResolve(ctx.Changes, hrotId, out id) && ctx.State.Find(id) is not null;
    }
}
