using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>What <see cref="IngressEntities.Resolve{TKey}"/> found for a wire key.</summary>
internal enum EntityResolution
{
    /// <summary>Unknown key and creation not requested.</summary>
    Missing,

    /// <summary>The key resolves to an entity of this side (committed, or created earlier in this transaction).</summary>
    Existing,

    /// <summary>The key was unknown; an entity was created and bound in this transaction.</summary>
    Created,

    /// <summary>The key resolves to an entity of another origin: our own publication looped back (R-022). Do nothing.</summary>
    Echo,
}

/// <summary>Resolve-or-create with the echo rule that every ingress of the gateway shares (DD-001 7).</summary>
internal static class IngressEntities
{
    public static EntityResolution Resolve<TKey>(IngressContext ctx, IdentityMap<TKey> map, in TKey key, bool create,
        out EntityId id)
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
}
