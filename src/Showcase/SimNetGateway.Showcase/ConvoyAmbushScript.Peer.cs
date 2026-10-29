using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Showcase;

// Helpers of the checks that are about what the RPR side RECEIVED through the RTI (DD-008 section 12b): the scripted federate
// discovered, reflected and decoded; the checks read its view, not the gateway's outbound recorder.
public sealed partial class ConvoyAmbushScript
{
    /// <summary>
    /// The RPR federate received exactly one full update of a new object, in the tick it was sent, and decoded what the gateway said:
    /// the object was discovered under the handle the RTI assigned, each of the five attributes arrived once, the spatial state is
    /// at the position of the update.
    /// </summary>
    private static void PeerSeesFirstUpdate(Verdict v, ScenarioContext ctx, RprObjectUpdate u, long tick, Vec3 position)
    {
        var seen = ctx.Peer.DiscoveredObject(u.Handle.Value);
        v.That(seen is not null, "the RPR federate did not discover an object under the handle the RTI assigned");
        if (seen is null)
        {
            return;
        }

        v.Equal(tick, seen.LastReflectTick, "tick at which the RPR federate received the update");
        foreach (string attribute in new[] { "EntityType", "EntityIdentifier", "Marking", "ForceIdentifier", "Spatial" })
        {
            v.Equal(1, seen.Count(attribute), "reflections of " + attribute + " at the RPR federate (one full update)");
        }

        v.That(seen.Damage is null, "a damage state reached the RPR federate although the update has none");
        v.That(seen.Spatial is { } s && Dist(new Vec3(s.WorldLocation.X, s.WorldLocation.Y, s.WorldLocation.Z), position) < 1e-6,
            "the position the RPR federate decoded is not the position of the update");
        foreach (string difference in RtiMirror.Differences(u, seen))
        {
            v.That(false, "RPR federate: " + difference);
        }
    }

    /// <summary>Everything the gateway ever sent about the object is what the RPR federate holds of it.</summary>
    private static void PeerHoldsWhatWasSent(Verdict v, ScenarioContext ctx, RprObjectHandle handle, string what)
    {
        foreach (string difference in ctx.Checker.MirrorDifferences(handle))
        {
            v.That(false, $"RPR federate, {what}: {difference}");
        }
    }

    /// <summary>The weapon fires the RPR federate received in a window of runner time (inclusive, resolved to ticks like the recorders).</summary>
    private static List<PeerFire> PeerFires(ScenarioContext ctx, double from, double to) =>
        ctx.Peer.Fires.Where(f => f.Tick >= ScenarioContext.TickOf(from) && f.Tick <= ScenarioContext.TickOf(to)).ToList();

    /// <summary>The detonations the RPR federate received in a window of runner time.</summary>
    private static List<PeerDetonation> PeerDetonations(ScenarioContext ctx, double from, double to) =>
        ctx.Peer.Detonations.Where(d => d.Tick >= ScenarioContext.TickOf(from) && d.Tick <= ScenarioContext.TickOf(to)).ToList();

    /// <summary>The instance handles of the objects whose removal the RPR federate received in a window of runner time.</summary>
    private static HashSet<uint> PeerRemoved(ScenarioContext ctx, double from, double to) =>
        ctx.Peer.RemovedHandles.Where(r => r.Tick >= ScenarioContext.TickOf(from) && r.Tick <= ScenarioContext.TickOf(to))
            .Select(r => (uint)r.Handle).ToHashSet();

    /// <summary>The RTI name of the object a handle names, as the RPR federate knows it (empty when it does not).</summary>
    private static string PeerName(ScenarioContext ctx, RprObjectHandle? handle) =>
        handle is { } h ? ctx.Peer.NameOfHandle(h.Value) ?? string.Empty : string.Empty;

    /// <summary>The FOM structure of a DIS entity type.</summary>
    private static EntityTypeStruct ToFom(DisEntityType t) => new(t.Kind, t.Domain, t.Country, t.Category, t.Subcategory, t.Specific, t.Extra);
}
