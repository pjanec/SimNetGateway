using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>The sources of the NED-RPR gateway: the three sides and the two processors (R-061: one per peer).</summary>
public sealed record NedRprSources(SourceId Ned, SourceId Rpr, SourceId Display, SourceId ProcDr, SourceId ProcReaper);

/// <summary>
/// The four identity maps of the NED-RPR gateway (R-040): NED entity ids (range allocator), RPR object handles
/// (external, delivered by the RTI), RPR entity identifiers (number pool with quarantine) and display labels.
/// </summary>
public sealed class NedRprMaps
{
    public NedRprMaps(
        IdentityMap<ulong> nedEntity,
        IdentityMap<RprObjectHandle> rprHandle,
        IdentityMap<DisEntityId> rprEntityId,
        IdentityMap<string> displayLabel)
    {
        NedEntity = nedEntity;
        RprHandle = rprHandle;
        RprEntityId = rprEntityId;
        DisplayLabel = displayLabel;
    }

    /// <summary>"ned.entity": wire ids from NED are bound, ids for entities introduced to NED come from the range allocator.</summary>
    public IdentityMap<ulong> NedEntity { get; }

    /// <summary>"rpr.handle": bound when the RTI answers a registration, or when a handle is first seen on the wire.</summary>
    public IdentityMap<RprObjectHandle> RprHandle { get; }

    /// <summary>"rpr.entityId": allocated for entities published to RPR, bound from the wire for RPR entities.</summary>
    public IdentityMap<DisEntityId> RprEntityId { get; }

    /// <summary>"display.label": bound by the display egress on the first position of an entity.</summary>
    public IdentityMap<string> DisplayLabel { get; }

    public static NedRprMaps Create(IdentityRegistry identities, NedRprSources sources, NedRprSettings settings, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);

        var nedEntity = identities.Create<ulong>(sources.Ned, "ned.entity",
            new RangeAllocator(settings.NedIdRange.First, settings.NedIdRange.Last));
        var rprHandle = identities.Create<RprObjectHandle>(sources.Rpr, "rpr.handle");
        ushort site = settings.DisSite;
        ushort application = settings.DisApplication;
        var rprEntityId = identities.Create<DisEntityId>(sources.Rpr, "rpr.entityId",
            new MappedAllocator<DisEntityId>(
                new NumberPool(NedRprConversions.MinEntityNumber, NedRprConversions.MaxEntityNumber, clock, settings.DisQuarantineSeconds),
                number => new DisEntityId(site, application, (ushort)number),
                NedRprConversions.EntityNumber));
        var displayLabel = identities.Create<string>(sources.Display, "display.label");
        return new NedRprMaps(nedEntity, rprHandle, rprEntityId, displayLabel);
    }
}
