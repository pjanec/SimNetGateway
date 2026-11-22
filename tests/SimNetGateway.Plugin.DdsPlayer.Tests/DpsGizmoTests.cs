using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Client = SimNetGateway.Plugin.DdsPlayer.Tests.DpsInboundTests.Client;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>The gizmos of the station: the client's view goes to the host's gizmo source, what it draws is published per layer, and the view follows the player rule.</summary>
public class DpsGizmoTests
{
    private sealed class FakeGizmos : IGizmoSource
    {
        public List<GizmoViewRequest?> Views { get; } = new();

        public Dictionary<string, GizmoPrimitiveData[]> Drawn { get; } = new();

        public void SetView(GizmoViewRequest? view) => Views.Add(view);

        public IReadOnlyList<GizmoLayerFrame> Take()
        {
            var layers = Views.LastOrDefault()?.Layers ?? [];
            return [.. layers.Select(l => new GizmoLayerFrame(l, Drawn.TryGetValue(l, out var p) ? p : []))];
        }
    }

    private static GizmoPrimitiveData Line() =>
        new(GizmoPrimitiveShape.LineStrip, [new HrotVec3(1, 2, 3), new HrotVec3(4, 5, 6)], 0xFF0000FF, 2f, string.Empty);

    private static string Key(D.GizmoFrame f) => f.Producer + "/" + f.Layer;

    [Fact]
    public void No_view_means_no_gizmo_work()
    {
        uint domain = Watch.NewDomain();
        var source = new FakeGizmos();
        using var frames = new Watch.Watch<D.GizmoFrame>(domain, "station-1", Key);
        using var rig = new Rig(domain, gizmoSource: source);

        DpsInboundTests.Frames(rig, 20);

        Assert.Empty(source.Views);
        frames.Pump();
        Assert.Empty(frames.Alive);
    }

    [Fact]
    public void A_view_is_passed_to_the_source_and_its_frames_are_published_per_layer()
    {
        uint domain = Watch.NewDomain();
        var source = new FakeGizmos();
        source.Drawn["Contacts"] = [Line(), new GizmoPrimitiveData(GizmoPrimitiveShape.Text, [new HrotVec3(7, 8, 9)], 0x00FF00FF, 0f, "W aiming")];
        using var frames = new Watch.Watch<D.GizmoFrame>(domain, "station-1", Key);
        using var rig = new Rig(domain, partition: "station-1", gizmoSource: source);
        using var client = new Client(domain);

        client.View(["Contacts", "Paths"], selected: [2001, 2002], pinned: [2003], families: ["TargetMemory", "Perception"], rateHz: 5f);

        Assert.True(DpsInboundTests.Until(rig, () => source.Views.Count == 1));
        var request = source.Views[0]!.Value;
        Assert.Equal(["Contacts", "Paths"], request.Layers.ToArray());
        Assert.Equal([2001, 2002], request.Selected.ToArray());
        Assert.Equal([(2003, "TargetMemory"), (2003, "Perception")], request.Pinned.ToArray());
        Assert.Equal(5.0, request.RateHz);

        Assert.True(frames.Until(w =>
        {
            rig.Step();
            return w.Alive.ContainsKey("station-1/Contacts") && w.Alive.ContainsKey("station-1/Paths");
        }));
        var contacts = frames.Alive["station-1/Contacts"];
        Assert.Equal(2, contacts.Primitives.Count);
        Assert.Equal(D.GizmoShape.GizmoShapeLineStrip, contacts.Primitives[0].Shape);
        Assert.Equal(new D.Vec3d { X = 4, Y = 5, Z = 6 }, contacts.Primitives[0].Points[1]);
        Assert.Equal((0xFF0000FFu, 2f), (contacts.Primitives[0].ColourRgba, contacts.Primitives[0].Width));
        Assert.Equal((D.GizmoShape.GizmoShapeText, "W aiming"), (contacts.Primitives[1].Shape, contacts.Primitives[1].Text));
        Assert.Empty(frames.Alive["station-1/Paths"].Primitives);   // a layer that drew nothing is an empty frame
    }

    [Fact]
    public void Card_primitives_are_published_with_their_card_fields_and_world_primitives_without()
    {
        uint domain = Watch.NewDomain();
        var source = new FakeGizmos();
        source.Drawn["UnitCards"] =
        [
            new GizmoPrimitiveData(GizmoPrimitiveShape.Box, [], 0x11223344, 1f, string.Empty, GizmoPrimitiveSpace.EntityCard, 2001, 10, 0.35f, 2f, 0.3f, 2.5f, 0x00FF00FF),
            new GizmoPrimitiveData(GizmoPrimitiveShape.Text, [], 0xFFFFFFFF, 13f, "Alpha squad #2001", GizmoPrimitiveSpace.EntityCard, 2001, 20, 3f),
            new GizmoPrimitiveData(GizmoPrimitiveShape.Icon, [], 0xFFFFFFFF, 0f, "SFGPUCI-------", GizmoPrimitiveSpace.EntityCard, 2001, 0, CardH: 22f),
            Line(),
        ];
        using var frames = new Watch.Watch<D.GizmoFrame>(domain, "station-1", Key);
        using var rig = new Rig(domain, partition: "station-1", gizmoSource: source);
        using var client = new Client(domain);
        client.View(["UnitCards"]);

        Assert.True(frames.Until(w =>
        {
            rig.Step();
            return w.Alive.ContainsKey("station-1/UnitCards");
        }));

        var primitives = frames.Alive["station-1/UnitCards"].Primitives;
        Assert.Equal(4, primitives.Count);
        var box = primitives[0];
        Assert.Equal((D.GizmoShape.GizmoShapeBox, D.GizmoSpace.GizmoSpaceEntityCard, 2001, (byte)10), (box.Shape, box.Space, box.AnchorId, box.Row));
        Assert.Equal((0.35f, 2f, 0.3f, 2.5f, 0x00FF00FFu, 0x11223344u, 1f), (box.CardX, box.CardY, box.CardW, box.CardH, box.FillRgba, box.ColourRgba, box.Width));
        Assert.Empty(box.Points);
        Assert.Equal(("Alpha squad #2001", 3f, 13f, (byte)20), (primitives[1].Text, primitives[1].CardX, primitives[1].Width, primitives[1].Row));
        Assert.Equal((D.GizmoShape.GizmoShapeIcon, "SFGPUCI-------", 22f, (byte)0), (primitives[2].Shape, primitives[2].Text, primitives[2].CardH, primitives[2].Row));
        var line = primitives[3];
        Assert.Equal((D.GizmoSpace.GizmoSpaceWorld, 0, (byte)0, 0u), (line.Space, line.AnchorId, line.Row, line.FillRgba));   // a world primitive carries no card field
        Assert.Equal(2, line.Points.Count);
    }

    [Fact]
    public void Layer_off_is_disposed()
    {
        uint domain = Watch.NewDomain();
        var source = new FakeGizmos();
        source.Drawn["Contacts"] = [Line()];
        using var frames = new Watch.Watch<D.GizmoFrame>(domain, "station-1", Key);
        using var rig = new Rig(domain, gizmoSource: source);
        using var client = new Client(domain);
        client.View(["Contacts", "Paths"]);
        Assert.True(frames.Until(w =>
        {
            rig.Step();
            return w.Alive.Count == 2;
        }));

        client.View(["Paths"]);   // Contacts is switched off

        Assert.True(frames.Until(w =>
        {
            rig.Step();
            return w.Alive.Count == 1 && w.Alive.ContainsKey("station-1/Paths");
        }));
    }

    [Fact]
    public void A_lost_viewer_ends_the_view_and_disposes_the_layers()
    {
        uint domain = Watch.NewDomain();
        var source = new FakeGizmos();
        source.Drawn["Contacts"] = [Line()];
        using var frames = new Watch.Watch<D.GizmoFrame>(domain, "station-1", Key);
        using var rig = new Rig(domain, gizmoSource: source);
        var client = new Client(domain);
        client.View(["Contacts"]);
        Assert.True(frames.Until(w =>
        {
            rig.Step();
            return w.Alive.Count == 1;
        }));

        client.Dispose();

        Assert.True(DpsInboundTests.Until(rig, () => source.Views.Count == 2 && source.Views[1] is null), "the source is told that nobody wants gizmos");
        Assert.True(frames.Until(w =>
        {
            rig.Step();
            return w.Alive.Count == 0;
        }));
    }

    [Fact]
    public void The_view_follows_the_player_and_a_second_viewer_is_ignored_with_one_warning()
    {
        uint domain = Watch.NewDomain();
        var source = new FakeGizmos();
        var station = new DpsInboundTests.RecordingStation();
        var log = new List<string>();
        using var rig = new Rig(domain, station: station, gizmoSource: source, log: log.Add);
        using var viewer = new Client(domain, id: 10);
        viewer.View(["Contacts"]);
        Assert.True(DpsInboundTests.Until(rig, () => source.Views.Count == 1));   // no player yet: the first client's view

        using var other = new Client(domain, id: 11);
        other.View(["Paths"]);
        DpsInboundTests.Frames(rig, 20);
        other.View(["Eqs"]);
        DpsInboundTests.Frames(rig, 20);
        Assert.Single(source.Views);
        Assert.Equal(1, log.Count(l => l.Contains("client id 11")));

        // a player joins: its view replaces the viewer's
        using var player = new Client(domain, id: 20);
        player.Join(1, 1201);
        Assert.True(DpsInboundTests.Until(rig, () => station.Joins.Count == 1));
        Assert.True(DpsInboundTests.Until(rig, () => source.Views.Count == 2 && source.Views[1] is null), "the viewer's view ends when a player holds the station");
        player.View(["Sectors"]);
        Assert.True(DpsInboundTests.Until(rig, () => source.Views.Count == 3));
        Assert.Equal(["Sectors"], source.Views[2]!.Value.Layers.ToArray());

        viewer.View(["Contacts"]);   // the old viewer is a foreign client now
        DpsInboundTests.Frames(rig, 20);
        Assert.Equal(3, source.Views.Count);
    }

    [Fact]
    public void No_gizmo_source_means_the_view_is_ignored()
    {
        uint domain = Watch.NewDomain();
        using var frames = new Watch.Watch<D.GizmoFrame>(domain, "station-1", Key);
        using var rig = new Rig(domain, gizmoSource: null);
        using var client = new Client(domain);

        client.View(["Contacts"]);
        DpsInboundTests.Frames(rig, 20);

        frames.Pump();
        Assert.Empty(frames.Alive);
    }

    /// <summary>A world boundary (or the node's teardown) disposes the transport: the source's projectors stop with it instead of running for a reader that is gone.</summary>
    [Fact]
    public void Disposing_the_transport_ends_the_view_at_the_source()
    {
        uint domain = Watch.NewDomain();
        var source = new FakeGizmos();
        var rig = new Rig(domain, gizmoSource: source);
        using var client = new Client(domain);
        client.View(["Contacts"]);
        Assert.True(DpsInboundTests.Until(rig, () => source.Views.Count == 1 && source.Views[0] is not null));

        rig.Dispose();

        Assert.Null(source.Views[^1]);
    }

    private static GizmoPrimitiveData CardPrim(int anchor, byte row) =>
        new(GizmoPrimitiveShape.Box, [], 0xFFu, 1f, string.Empty, GizmoPrimitiveSpace.EntityCard, anchor, row);

    /// <summary>
    /// T-058: past the frame's 4096 primitives the frame is cut at the end of a WHOLE entity card (five primitives each: no half card), a frame within the bound is untouched, and world primitives are cut at the bound.
    /// (Lossless delivery of more is the contract's choice, DD-024 section 9.) Mutation: the cut falls in the middle of a card.
    /// </summary>
    [Fact]
    public void A_layer_past_the_frame_bound_is_cut_at_a_whole_card()
    {
        var cards = new List<GizmoPrimitiveData>();
        for (int a = 1; a <= 1000; a++)
        {
            for (byte row = 0; row < 5; row++)
            {
                cards.Add(CardPrim(a, row));
            }
        }

        int fit = DpsGizmos.Fit(cards);
        Assert.Equal(4095, fit);   // 819 cards of 5; the 820th does not fit and is left out whole
        Assert.Equal(0, fit % 5);
        Assert.Equal(819, cards.Take(fit).Select(p => p.AnchorId).Distinct().Count());
        Assert.Equal(300, DpsGizmos.Fit(cards.Take(300).ToList()));   // within the bound: all of it
        var world = Enumerable.Range(0, 5000).Select(_ => new GizmoPrimitiveData(GizmoPrimitiveShape.Box, [], 1u, 1f, string.Empty)).ToList();
        Assert.Equal(4096, DpsGizmos.Fit(world));
    }

    /// <summary>
    /// T-061 (R-145 D): a layer is written only when its content changed, plus a heartbeat. A steady layer writes nothing between heartbeats, a change writes once, the heartbeat repeats it,
    /// and a new view says everything again. Mutations: the change check always true (a write every frame); no heartbeat (a calm layer never repeats); the hash blind to the text (a change is missed).
    /// </summary>
    [Fact]
    public void A_steady_layer_writes_nothing_between_heartbeats_and_a_change_writes_once()
    {
        uint domain = Watch.NewDomain();
        var source = new StableGizmos { Frames = [Cards("UnitCards", 3, 100)] };
        using var frames = new Watch.Watch<D.GizmoFrame>(domain, "station-1", Key);
        using var rig = new Rig(domain, gizmoSource: source, heartbeatSeconds: 3.0);
        using var client = new Client(domain);
        client.View(["UnitCards"], rateHz: 10f);
        Assert.True(frames.Until(w => { rig.Step(); return w.Events.Count >= 1; }));

        // calm: many frames, no new sample (well inside the heartbeat)
        var calm = System.Diagnostics.Stopwatch.StartNew();
        while (calm.ElapsedMilliseconds < 700)
        {
            rig.Step();
            frames.Pump();
            Thread.Sleep(5);
        }

        Assert.Single(frames.Events);

        // a change (one card's text, SAME length) is written once
        source.Frames = [Cards("UnitCards", 3, 100, "Riflemam")];
        Assert.True(frames.Until(w => { rig.Step(); return w.Events.Count >= 2; }, 1500));   // well before the heartbeat: the change itself wrote it
        int afterChange = frames.Events.Count;
        Assert.Equal(2, afterChange);
        Assert.Contains(frames.Events[1].Primitives, p => p.Text.StartsWith("Riflemam", StringComparison.Ordinal));

        // the heartbeat repeats the unchanged layer
        Assert.True(frames.Until(w => { rig.Step(); return w.Events.Count >= 3; }, 6000));
    }

    /// <summary>T-061: a calm 3-layer view of 100 cards between heartbeats costs the gateway 0 B a frame (hash over the source, no topic value built, nothing written).</summary>
    [Fact]
    public void A_calm_three_layer_view_costs_nothing_between_heartbeats()
    {
        uint domain = Watch.NewDomain();
        var source = new StableGizmos();
        using var frames = new Watch.Watch<D.GizmoFrame>(domain, "station-1", Key);
        using var rig = new Rig(domain, gizmoSource: source, heartbeatSeconds: 600);
        using var client = new Client(domain);
        string[] layers = ["Labels", "UnitCards", "UnitSymbols"];
        source.Frames = [Cards("Labels", 100, 1000), Cards("UnitCards", 100, 2000), Cards("UnitSymbols", 100, 3000)];
        client.View(layers, rateHz: 10f);
        Assert.True(frames.Until(w => { rig.Step(); return w.Alive.Count == 3; }));
        for (int i = 0; i < 50; i++)
        {
            rig.Step();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            rig.Transport.Pump();
        }

        Assert.Equal(0L, (GC.GetAllocatedBytesForCurrentThread() - before) / 300);
    }

    [Fact]
    public void A_new_view_says_every_layer_again()
    {
        uint domain = Watch.NewDomain();
        var source = new StableGizmos { Frames = [Cards("UnitCards", 2, 100)] };
        using var frames = new Watch.Watch<D.GizmoFrame>(domain, "station-1", Key);
        using var rig = new Rig(domain, gizmoSource: source, heartbeatSeconds: 60);
        using var client = new Client(domain);
        client.View(["UnitCards"], rateHz: 10f);
        Assert.True(frames.Until(w => { rig.Step(); return w.Events.Count >= 1; }));
        client.View(["UnitCards"], rateHz: 20f);
        Assert.True(frames.Until(w => { rig.Step(); return w.Events.Count >= 2; }));
    }

    /// <summary>A source that hands back the same frames every run, as the host's reused frame array does.</summary>
    private sealed class StableGizmos : IGizmoSource
    {
        public GizmoLayerFrame[] Frames { get; set; } = [];

        public bool Active { get; private set; }

        public void SetView(GizmoViewRequest? view) => Active = view is not null;

        public IReadOnlyList<GizmoLayerFrame> Take() => Active ? Frames : [];
    }

    private static GizmoLayerFrame Cards(string layer, int cards, int firstAnchor, string? nameOverride = null)
    {
        var list = new List<GizmoPrimitiveData>();
        for (int c = 0; c < cards; c++)
        {
            int a = firstAnchor + c;
            list.Add(new GizmoPrimitiveData(GizmoPrimitiveShape.Box, [], 0x0A141EFFu, 1f, string.Empty, GizmoPrimitiveSpace.EntityCard, a, 0, 0.5f, 8f, 0.5f, 8f, 0x000000A0u));
            list.Add(new GizmoPrimitiveData(GizmoPrimitiveShape.Box, [], 0xFFFFFFFFu, 1f, string.Empty, GizmoPrimitiveSpace.EntityCard, a, 0, 0.5f, 8f, 0.5f, 8f));
            list.Add(new GizmoPrimitiveData(GizmoPrimitiveShape.Box, [], 0x00FF00FFu, 0f, string.Empty, GizmoPrimitiveSpace.EntityCard, a, 10, 0.35f, 4f, 0.3f, 3f, 0x00FF00FFu));
            list.Add(new GizmoPrimitiveData(GizmoPrimitiveShape.Text, [], 0xFFFFFFFFu, 13f, (nameOverride ?? "Rifleman") + " " + a, GizmoPrimitiveSpace.EntityCard, a, 20, 3f, 0f));
            list.Add(new GizmoPrimitiveData(GizmoPrimitiveShape.Icon, [], 0xFFFFFFFFu, 0f, "SHGPUCI---", GizmoPrimitiveSpace.EntityCard, a, 0, CardH: 22f));
        }

        list.Add(Line());   // a world primitive with points too
        return new GizmoLayerFrame(layer, list);
    }

    /// <summary>
    /// T-059 (HOT path): building the topic values of a steady 3-layer view of 100 cards each (1 503 primitives) allocates 0 bytes: the values are reused (lists grown, never shrunk, each primitive keeps its point list), no LINQ, no
    /// per-frame strings; a layer that shrinks or grows says exactly its primitives. Mutation: one LINQ Select back in the builder.
    /// </summary>
    [Fact]
    public void Building_the_topic_values_of_a_steady_three_layer_view_of_cards_allocates_nothing()
    {
        var builder = new DpsGizmoFrames();
        var frames = new[] { Cards("Labels", 100, 1000), Cards("UnitCards", 100, 2000), Cards("UnitSymbols", 100, 3000) };
        foreach (var f in frames)
        {
            builder.Build("station-1", f, f.Primitives.Count);   // the buffers grow once
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            foreach (var f in frames)
            {
                builder.Build("station-1", f, f.Primitives.Count);
            }
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);

        // the content is the primitives', whole: 100 cards of 5 and the world line with its two points
        var built = builder.Build("station-1", frames[0], frames[0].Primitives.Count);
        Assert.Equal((501, "station-1", "Labels"), (built.Primitives.Count, built.Producer, built.Layer));
        Assert.Equal((D.GizmoSpace.GizmoSpaceEntityCard, 1000, (byte)10), (built.Primitives[2].Space, built.Primitives[2].AnchorId, built.Primitives[2].Row));
        Assert.Equal("Rifleman 1003", built.Primitives[3 + 5 * 3].Text);
        Assert.Equal([1.0, 4.0], [built.Primitives[500].Points[0].X, built.Primitives[500].Points[1].X]);

        // a layer that shrinks and grows says exactly its primitives, no stale tail
        var small = Cards("Labels", 10, 1000);
        Assert.Equal(51, builder.Build("station-1", small, small.Primitives.Count).Primitives.Count);
        Assert.Equal(501, builder.Build("station-1", frames[0], frames[0].Primitives.Count).Primitives.Count);
        Assert.Empty(builder.Build("station-1", new GizmoLayerFrame("Labels", []), 0).Primitives);
    }

    /// <summary>
    /// T-059: the whole transport pump of a steady 3-layer view of 100 cards. Measured: the topic values cost 0 B (above); what the pump allocates in addition to the same view drawing nothing is the CycloneDDS writer's own cost of
    /// marshalling a managed sample (32 B per non-empty text string: the generated `Encoding.UTF8.GetBytes`, nothing per point; T-056 note; the reader has no floor): asserted not to grow past 24 B a primitive.
    /// </summary>
    [Fact]
    public void A_steady_three_layer_view_costs_the_ddswriters_floor_and_nothing_of_the_gateways_own()
    {
        uint domain = Watch.NewDomain();
        var source = new StableGizmos();
        using var frames = new Watch.Watch<D.GizmoFrame>(domain, "station-1", Key);
        using var rig = new Rig(domain, gizmoSource: source);
        using var client = new Client(domain);
        string[] layers = ["Labels", "UnitCards", "UnitSymbols"];
        source.Frames = [.. layers.Select(l => new GizmoLayerFrame(l, []))];
        client.View(layers, rateHz: 10f);
        Assert.True(frames.Until(w => { rig.Step(); return w.Alive.Count == 3; }));

        long Measure(int n)
        {
            for (int i = 0; i < 30; i++)
            {
                rig.Step();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < n; i++)
            {
                rig.Transport.Pump();
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / n;
        }

        long floor = Measure(200);
        source.Frames = [Cards("Labels", 100, 1000), Cards("UnitCards", 100, 2000), Cards("UnitSymbols", 100, 3000)];
        rig.Step();   // the buffers grow once
        long loaded = Measure(200);
        int primitives = source.Frames.Sum(f => f.Primitives.Count);
        Assert.InRange(loaded - floor, 0, 24L * primitives);
        Assert.True(frames.Until(w => { rig.Step(); return w.Alive.Values.Any(f => f.Layer == "Labels" && f.Primitives.Count == 501); }));   // and it arrived: 100 cards x 5 + the world line
    }
}
