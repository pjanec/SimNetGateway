using System.Globalization;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

// Helpers of the checks: typed views of the recorders, the analytic truth of the moving entities, small formatters.
public sealed partial class ConvoyAmbushScript
{
    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static Vec3 Ecef(WorldLocation w) => new(w.X, w.Y, w.Z);

    private static double Dist(Vec3 a, Vec3 b) => (a - b).Length;

    private static bool IsHeartbeat(RprObjectUpdate u) => u.EntityIdentifier is not null;

    /// <summary>The names of the present properties of an update, e.g. "Marking,EntityType,WorldLocation".</summary>
    private static string Shape(RprObjectUpdate u)
    {
        var parts = new List<string>();
        if (u.Marking is not null) parts.Add("Marking");
        if (u.EntityType is not null) parts.Add("EntityType");
        if (u.ForceId is not null) parts.Add("ForceId");
        if (u.EntityIdentifier is not null) parts.Add("EntityIdentifier");
        if (u.WorldLocation is not null) parts.Add("WorldLocation");
        if (u.Orientation is not null) parts.Add("Orientation");
        if (u.VelocityVector is not null) parts.Add("VelocityVector");
        if (u.AccelerationVector is not null) parts.Add("AccelerationVector");
        if (u.AngularVelocity is not null) parts.Add("AngularVelocity");
        if (u.DeadReckoningAlgorithm is not null) parts.Add("DeadReckoningAlgorithm");
        if (u.DamageState is not null) parts.Add("DamageState");
        if (u.DamagePercent is not null) parts.Add("DamagePercent");
        return string.Join(",", parts);
    }

    /// <summary>The updates of one handle, oldest first.</summary>
    private static List<Recorded<RprObjectUpdate>> UpdatesOf(IEnumerable<Recorded<RprObjectUpdate>> source, RprObjectHandle handle) =>
        source.Where(r => r.Message.Handle == handle).ToList();

    private static List<Recorded<TOut>> Select<TIn, TOut>(IEnumerable<Recorded<TIn>> source)
        where TOut : TIn =>
        source.Where(r => r.Message is TOut)
            .Select(r => new Recorded<TOut>((TOut)(object)r.Message!, r.Tick, r.WallTime, r.SimTime, r.Sequence, r.Correlation))
            .ToList();

    /// <summary>
    /// The RPR messages of the window without the registration traffic and without heartbeats (the periodic full updates of the
    /// 5 s timer, which come on wall time whatever else happens: "RPR got X and nothing else" is meant without them).
    /// </summary>
    private static List<Recorded<IRprMessage>> RprEvents(ScenarioContext ctx, double from, double to) =>
        ctx.RprPayloadBetween(from, to).Where(r => r.Message is not RprObjectUpdate u || !IsHeartbeat(u)).ToList();

    private static string Names(IEnumerable<string> names) => "{" + string.Join(", ", names.OrderBy(n => n, StringComparer.Ordinal)) + "}";

    private static string NameOf(ScenarioContext ctx, EntityRecord record) =>
        record.TryGet(ctx.Schema.Name, out var name) ? name.AsString() : "#" + record.Id.Value;

    private static string TimeOf(Recorded<INedMessage> r) => F($"r={r.WallTime:F3}");

    // ---- the analytic truth of the moving entities ----------------------------------------------------------------------

    /// <summary>The last reported state of a mover: ECEF position and velocity at the sim time of the input that carried them.</summary>
    private readonly record struct Report(Vec3 Position, Vec3 Velocity, double SimTime, long Tick)
    {
        /// <summary>The position at <paramref name="simTime"/> at constant velocity (FPW), the model both sides share.</summary>
        public Vec3 At(double simTime) => Position + Velocity * (simTime - SimTime);
    }

    private static Report ReportOf(NedGeoSpatial g, InputRecord input)
    {
        NedRprConversions.GeoToPivot(in g, out var p, out _, out var v);
        return new Report(p, v, input.SimTime, input.Tick);
    }

    /// <summary>The latest geo-spatial of a NED id that the gateway had processed by <paramref name="tick"/>.</summary>
    private static bool TryReportOfEntity(ScenarioContext ctx, ulong id, long tick, out Report report)
    {
        report = default;
        bool found = false;
        foreach (var input in ctx.InputsOf<NedGeoSpatial>("NED"))
        {
            var g = (NedGeoSpatial)input.Message;
            if (g.Id == id && input.Tick <= tick)
            {
                report = ReportOf(g, input);
                found = true;
            }
        }

        return found;
    }

    /// <summary>The latest formation message of the unit that named the member and that the gateway had processed by <paramref name="tick"/>.</summary>
    private static bool TryReportOfMember(ScenarioContext ctx, ulong formation, int index, long tick, out Report report)
    {
        report = default;
        bool found = false;
        foreach (var input in ctx.InputsOf<NedFormation>("NED"))
        {
            var f = (NedFormation)input.Message;
            if (f.Id != formation || input.Tick > tick)
            {
                continue;
            }

            foreach (var m in f.Members)
            {
                if (m.Index == index)
                {
                    NedRprConversions.GeoToPivot(m.LatitudeDeg, m.LongitudeDeg, m.Altitude, m.HeadingDeg, m.PitchDeg, m.RollDeg, m.Speed,
                        out var p, out _, out var v);
                    report = new Report(p, v, input.SimTime, input.Tick);
                    found = true;
                }
            }
        }

        return found;
    }

    private static Vec3 DisplayEcef(DisplayEntityPosition d) => NedRprConversions.GeodeticToEcef(d.LatitudeDeg, d.LongitudeDeg, d.Altitude);

    // ---- state snapshots -------------------------------------------------------------------------------------------------

    /// <summary>A text of every slot version of every live entity (without the derived attributes the DR processor keeps writing).</summary>
    private static string Snapshot(ScenarioContext ctx)
    {
        var s = ctx.Schema;
        var parts = new List<string>();
        foreach (var entity in ctx.Engine.State.Entities)
        {
            var slots = new List<string>();
            foreach (var (attr, slot) in entity.Attributes)
            {
                if (attr == s.PredictedPosition || attr == s.PredictedAt)
                {
                    continue;
                }

                slots.Add($"{attr.Value}:{slot.Meta.Version}");
            }

            slots.Sort(StringComparer.Ordinal);
            parts.Add($"{entity.Id.Value}[{string.Join(",", slots)}]");
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }

    private static int CountRecords(ScenarioContext ctx, SourceId source, double fromWall)
    {
        int n = 0;
        foreach (var record in ctx.Engine.State.Journal.Since(fromWall))
        {
            if (record.Source == source)
            {
                n++;
            }
        }

        return n;
    }

    private static List<DiagnosticEntry> DiagnosticsSince(ScenarioContext ctx, int skip)
    {
        var list = new List<DiagnosticEntry>();
        int i = 0;
        foreach (var entry in ctx.Engine.Diagnostics.Recent)
        {
            if (i++ >= skip)
            {
                list.Add(entry);
            }
        }

        return list;
    }

    private static SourceId SourceNamed(ScenarioContext ctx, string name) =>
        ctx.Engine.Sources.TryGet(name, out var id) ? id : throw new KeyNotFoundException("No source " + name);

    private static string Hex(RprObjectHandle? handle) => handle is { } h ? MessageText.H(h) : "none";
}
