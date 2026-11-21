using Fdp.Core;
using Fdp.Toolkit.Terrain;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The terrain resident in the HROT world as a pivot message (T-048): the name the scenario resolved it by (<c>TerrainDefinition.ResidentName</c>) and the content hashes of the
/// base terrain and of the overlay merged into it (<c>ResolvedBaseHash</c>, <c>ResolvedOverlayHash</c>), all read from the managed <c>TerrainDefinition</c> singleton the terrain
/// loader commits. No terrain (no singleton, or none resolved) is the all-empty message.
/// </summary>
internal static class HrotTerrainGather
{
    public static HrotTerrain Read(EntityRepository world)
    {
        if (TerrainDefinition.ResidentName(world) is not { } name)
        {
            return default;
        }

        var def = world.GetSingletonManaged<TerrainDefinition>();
        return new HrotTerrain(name, def?.ResolvedBaseHash ?? string.Empty, def?.ResolvedOverlayHash ?? string.Empty);
    }
}
