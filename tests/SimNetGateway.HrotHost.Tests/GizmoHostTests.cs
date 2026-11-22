using System.Numerics;
using Fdp.Core;
using Fdp.Core.CommandHierarchy;
using Fdp.Interfaces;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Replication.Components;
using GizmoMap.Contracts.Symbology;
using Hrot.Map.Common.Components;
using Fdp.Toolkit.Behavior.Diagnostics;
using Fdp.Toolkit.Diagnostics.Gizmos;
using Fdp.Toolkit.Diagnostics.Gizmos.Settings;
using Fdp.Toolkit.Perception.Components;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>HROT's gizmo projectors run for the station into the host's own buffer: nothing without a view, client-chosen layers, selection and pins, a rate cap, and the editor's map untouched.</summary>
public class GizmoHostTests
{
    private sealed class Stage : PackHarness
    {
        public Stage()
            : base(dis: true)
        {
            RegisterTemplate(300, 3);
            if (!World.IsComponentTypeRegistered<TargetMemory>())
            {
                World.RegisterComponent<TargetMemory>();
            }

            Steps(3);
            Host = System.PlayerStation.Gizmos;
        }

        public HrotGizmoHost Host { get; }

        /// <summary>A unit that remembers one contact at (30, 40, 1): HROT's contacts gizmo draws a line from the unit to it.</summary>
        public unsafe Entity Observer(int id, Vector3 at)
        {
            var e = AddHrotEntity(id, at, tkb: 300);
            var memory = default(TargetMemory);
            memory.Count = 1;
            memory.EntityIds[0] = 9000 + id;
            memory.PositionsX[0] = at.X + 30f;
            memory.PositionsY[0] = at.Y + 40f;
            memory.PositionsZ[0] = 1f;
            memory.Freshness[0] = 1f;
            memory.LastSeenTick[0] = 0u;
            World.AddComponent(e, memory);
            return e;
        }
    }

    private static GizmoViewRequest View(string[] layers, int[]? selected = null, (int, string)[]? pinned = null, double rate = 10.0) =>
        new(layers, selected ?? [], pinned ?? [], rate);

    private static IEnumerable<HrotVec3> StartsOfLines(IReadOnlyList<GizmoLayerFrame> frames, string layer) =>
        frames.Single(f => f.Layer == layer).Primitives.Where(p => p.Shape == GizmoPrimitiveShape.LineStrip).Select(p => p.Points[0]);

    [Fact]
    public void No_view_runs_no_projectors()
    {
        using var h = new Stage();
        h.Observer(2001, Vector3.Zero);

        h.Host.Frame(h.World, 0.0);
        h.Host.Frame(h.World, 5.0);

        Assert.Equal(0, h.Host.Runs);
        Assert.Empty(h.Host.Take());

        h.Host.SetView(View(["Contacts"], [2001]));
        h.Host.Frame(h.World, 6.0);
        Assert.Equal(1, h.Host.Runs);

        h.Host.SetView(null);   // the client is gone: nothing runs again
        h.Host.Frame(h.World, 20.0);
        Assert.Equal(1, h.Host.Runs);
        Assert.Empty(h.Host.Take());
    }

    [Fact]
    public void Selected_unit_contacts_layer_is_captured()
    {
        using var h = new Stage();
        h.Observer(2001, new Vector3(0, 0, 0));
        h.Observer(2002, new Vector3(100, 100, 0));
        h.Host.SetView(View(["Contacts"], [2001]));

        h.Host.Frame(h.World, 1.0);

        var frames = h.Host.Take();
        Assert.Equal(["Contacts"], frames.Select(f => f.Layer).ToArray());
        var starts = StartsOfLines(frames, "Contacts").ToList();
        Assert.NotEmpty(starts);
        Assert.All(starts, s => Assert.Equal(new HrotVec3(0, 0, 0), s));   // the selected unit's line only
        var line = frames[0].Primitives.First(p => p.Shape == GizmoPrimitiveShape.LineStrip);
        Assert.Equal(new HrotVec3(30, 40, 1), line.Points[1]);
        Assert.NotEqual(0u, line.ColourRgba);
        Assert.Empty(h.Host.Take());   // taken once
    }

    [Fact]
    public void A_pinned_unit_draws_for_its_family_only()
    {
        using var h = new Stage();
        h.Observer(2001, new Vector3(0, 0, 0));
        h.Observer(2002, new Vector3(100, 100, 0));

        h.Host.SetView(View(["Contacts"], pinned: [(2002, nameof(AiOverlayFlags.Perception))]));   // another family: nothing
        h.Host.Frame(h.World, 1.0);
        Assert.Empty(h.Host.Take().Single().Primitives);

        h.Host.SetView(View(["Contacts"], pinned: [(2002, nameof(AiOverlayFlags.TargetMemory))]));
        h.Host.Frame(h.World, 2.0);
        Assert.All(StartsOfLines(h.Host.Take(), "Contacts"), s => Assert.Equal(new HrotVec3(100, 100, 0), s));

        h.Host.SetView(View(["Contacts"], pinned: [(2002, string.Empty)]));   // every family
        h.Host.Frame(h.World, 3.0);
        Assert.NotEmpty(StartsOfLines(h.Host.Take(), "Contacts"));
    }

    [Fact]
    public void An_unselected_unit_draws_nothing()
    {
        using var h = new Stage();
        h.Observer(2001, Vector3.Zero);
        h.Host.SetView(View(["Contacts"]));

        h.Host.Frame(h.World, 1.0);

        Assert.Empty(h.Host.Take().Single().Primitives);
    }

    [Fact]
    public void Layer_off_is_disposed()
    {
        using var h = new Stage();
        h.Observer(2001, Vector3.Zero);
        h.Host.SetView(View(["Contacts", "Paths"], [2001]));
        h.Host.Frame(h.World, 1.0);
        Assert.Equal(["Contacts", "Paths"], h.Host.Take().Select(f => f.Layer).ToArray());

        // Contacts is switched off: it is no longer in the frames, so the plugin disposes its instance
        h.Host.SetView(View(["Paths"], [2001]));
        h.Host.Frame(h.World, 2.0);

        Assert.Equal(["Paths"], h.Host.Take().Select(f => f.Layer).ToArray());
    }

    [Fact]
    public void An_unknown_layer_name_is_ignored()
    {
        using var h = new Stage();
        h.Observer(2001, Vector3.Zero);
        h.Host.SetView(View(["NoSuchLayer"], [2001]));

        h.Host.Frame(h.World, 1.0);

        Assert.Equal(0, h.Host.Runs);
        Assert.Empty(h.Host.Take());
    }

    [Fact]
    public void Frame_rate_is_capped_by_the_view()
    {
        using var h = new Stage();
        h.Observer(2001, Vector3.Zero);
        h.Host.SetView(View(["Contacts"], [2001], rate: 2.0));

        h.Host.Frame(h.World, 10.0);
        h.Host.Frame(h.World, 10.1);
        h.Host.Frame(h.World, 10.4);
        Assert.Equal(1, h.Host.Runs);

        h.Host.Frame(h.World, 10.6);   // 0.5 s later at 2 Hz
        Assert.Equal(2, h.Host.Runs);
        Assert.Single(h.Host.Take(), f => f.Layer == "Contacts");   // only the latest picture is kept

        h.Host.SetView(View(["Contacts"], [2001], rate: 1000.0));   // never faster than 60 Hz
        h.Host.Frame(h.World, 20.0);
        h.Host.Frame(h.World, 20.005);
        Assert.Equal(3, h.Host.Runs);
    }

    [Fact]
    public void Editor_map_settings_are_untouched()
    {
        using var h = new Stage();
        h.Observer(2001, Vector3.Zero);

        // the editor's own map machinery, as its map builds it
        var editorSettings = new GizmoSettingsRegistry();
        GizmoFamilies.Register(editorSettings);
        var editorBuffer = new DebugPrimitiveBuffer(256);
        var editorLayers = default(LayerMask256);
        editorLayers.SetBit(DebugTraceLayers.Paths);
        editorBuffer.SetActiveLayers(in editorLayers);
        var scopesBefore = GizmoFamilies.All.Select(f => GizmoFamilies.ScopeOf(editorSettings, f)).ToArray();

        h.Host.SetView(View(["Contacts", "Eqs"], [2001], [(2001, string.Empty)], 30.0));
        h.Host.Frame(h.World, 1.0);
        h.Host.SetView(View(["Contacts"], [2001]));
        h.Host.Frame(h.World, 2.0);

        Assert.Equal(scopesBefore, GizmoFamilies.All.Select(f => GizmoFamilies.ScopeOf(editorSettings, f)).ToArray());
        Assert.True(editorBuffer.IsLayerActive(DebugTraceLayers.Paths));
        Assert.False(editorBuffer.IsLayerActive(DebugTraceLayers.Contacts));
        Assert.Equal(0, editorBuffer.Count);
        Assert.Equal(2, h.Host.Runs);
    }

    // ---- entity cards (T-052, DD-024) -------------------------------------------------------------------------------------

    /// <summary>A commander: a body-less unit (HROT's <c>UnitEntity</c>, a unit DIS kind in its template) at its members' centroid; a unit has no health.</summary>
    private static Entity Commander(Stage h, int id, Vector3 at)
    {
        if (!h.World.IsComponentTypeRegistered<UnitEntity>())
        {
            h.World.RegisterComponent<UnitEntity>();
        }

        if (!h.TestTkb.TryGetByType(400, out _))
        {
            h.RegisterTemplate(400, 11);   // HROT's unit kind: the symbol of the card's cell is derived from it
        }

        h.World.SetSingletonManaged<ITkbDatabase>(h.TestTkb);
        var e = h.AddHrotEntity(id, at, "Alpha squad", tkb: 400);
        h.World.RemoveComponent<Health>(e);
        h.World.AddComponent(e, new UnitEntity { CommandDisruptionSeconds = 6f });
        return e;
    }

    private static List<GizmoPrimitiveData> CardsOf(IReadOnlyList<GizmoLayerFrame> frames, string layer) => frames.Single(f => f.Layer == layer).Primitives.ToList();

    private static HashSet<int> Anchors(IEnumerable<GizmoPrimitiveData> primitives) => primitives.Where(p => p.Space == GizmoPrimitiveSpace.EntityCard).Select(p => p.AnchorId).ToHashSet();

    /// <summary>The card helpers' own primitives (Frame, Bar, Tick, Text, Symbol) drawn into a buffer, as the product's projectors emit them.</summary>
    private static DebugPrimitive[] HelperPrimitives(int net, byte layer = 11)
    {
        var buffer = new DebugPrimitiveBuffer(64);
        var mask = default(LayerMask256);
        mask.SetBit(DebugTraceLayers.Labels);
        mask.SetBit(DebugTraceLayers.UnitCards);
        mask.SetBit(DebugTraceLayers.UnitSymbols);
        buffer.SetActiveLayers(in mask);
        buffer.EndFrame(0f);
        var card = buffer.Card(net, layer);
        card.Row(0).Frame(new Rgba32(10, 20, 30, 255)).Symbol(new SymbolCode(SymbolAffiliation.Hostile, SymbolRole.Armour, SymbolEchelon.Platoon, SymbolMobility.Tracked), 22f);
        card.Row(10).Bar(0.7f, new Rgba32(0, 255, 0, 255), new Rgba32(60, 60, 60, 255)).Tick(0.25f, new Rgba32(255, 0, 0, 255), 6f);
        card.Row(20).Text("T-72 #1043", new Rgba32(255, 255, 255, 255), 13f, 3f);
        return buffer.GetFrame().ToArray();
    }

    [Fact]
    public void Every_card_helper_primitive_arrives_as_a_card_primitive_with_the_products_fields()
    {
        using var h = new Stage();
        h.Host.AnchorMapped = id => id == 1043;
        var primitives = HelperPrimitives(1043);
        Assert.Equal(7, primitives.Length);   // frame (background, outline), symbol, bar (back, fill), tick, text

        foreach (var p in primitives)
        {
            var data = h.Host.Convert(in p);
            Assert.NotNull(data);
            var d = data!.Value;
            Assert.Equal(GizmoPrimitiveSpace.EntityCard, d.Space);
            Assert.Equal(1043, d.AnchorId);   // the DPS id of the anchor
            Assert.Equal(p.ZIndex, d.Row);
            Assert.Empty(d.Points);   // nothing is a ground point
            switch (p.Shape)
            {
                case DebugPrimitiveShape.Box2D:
                    Assert.Equal(GizmoPrimitiveShape.Box, d.Shape);
                    Assert.Equal((p.BoxCenterX, p.BoxCenterY, p.BoxExtentX, p.BoxExtentY), (d.CardX, d.CardY, d.CardW, d.CardH));
                    Assert.Equal(((uint)p.FillColor.R << 24) | ((uint)p.FillColor.G << 16) | ((uint)p.FillColor.B << 8) | p.FillColor.A, d.FillRgba);
                    Assert.Equal(((uint)p.Color.R << 24) | ((uint)p.Color.G << 16) | ((uint)p.Color.B << 8) | p.Color.A, d.ColourRgba);
                    Assert.Equal(p.Thickness, d.Width);
                    break;
                case DebugPrimitiveShape.Text:
                    Assert.Equal(GizmoPrimitiveShape.Text, d.Shape);
                    Assert.Equal("T-72 #1043", d.Text);   // the inline text, not the intern hash that shares the anchor's four bytes
                    Assert.Equal((3f, 0f, 13f), (d.CardX, d.CardY, d.Width));   // the font size is in whole pixels
                    break;
                case DebugPrimitiveShape.Icon:
                    Assert.Equal(GizmoPrimitiveShape.Icon, d.Shape);
                    Assert.Equal(p.TextContent.ToString(), d.Text);
                    Assert.True(SymbolCode.TryParse(d.Text, out var symbol));   // the symbol code arrives intact
                    Assert.Equal((SymbolAffiliation.Hostile, SymbolRole.Armour, SymbolEchelon.Platoon, SymbolMobility.Tracked), (symbol.Affiliation, symbol.Role, symbol.Echelon, symbol.Mobility));
                    Assert.Equal(22f, d.CardH);
                    break;
                default:
                    Assert.Fail("an unexpected shape " + p.Shape);
                    break;
            }
        }

        // the rows and the bar's geometry as the design reads them
        var rows = primitives.Select(p => h.Host.Convert(in p)!.Value).ToList();
        Assert.Equal([0, 0, 0, 10, 10, 10, 20], rows.Select(r => (int)r.Row).ToArray());
        var tick = rows[5];
        Assert.Equal((0.25f, 3f, 0.006f, 3f), (tick.CardX, tick.CardY, tick.CardW, tick.CardH));
        Assert.Equal(1.0f, rows[1].Width);   // the frame's outline is one pixel and has no fill
        Assert.Equal(0u, rows[1].FillRgba);
        Assert.NotEqual(0u, rows[0].FillRgba);
    }

    [Fact]
    public void World_primitives_are_converted_exactly_as_before()
    {
        using var h = new Stage();
        h.Host.Ground = null;
        var box = default(DebugPrimitive);
        box.Shape = DebugPrimitiveShape.Box2D;
        box.Space = CoordinateSpace.World;
        box.Color = new Rgba32(1, 2, 3, 4);
        box.BoxCenterX = 5f;
        box.BoxCenterY = 6f;
        box.BoxExtentX = 7f;
        box.BoxExtentY = 8f;
        box.BoxAngleDeg = 9f;
        box.ThicknessU16 = 25;
        var text = default(DebugPrimitive);
        text.Shape = DebugPrimitiveShape.Text;
        text.Space = CoordinateSpace.World;
        text.Color = new Rgba32(10, 20, 30, 40);
        text.TextX = 11f;
        text.TextY = 12f;
        text.TextContent = new Fdp.Toolkit.Diagnostics.Gizmos.FixedString32("hello");

        var converted0 = h.Host.Convert(in box)!.Value;
        Assert.Equal((GizmoPrimitiveShape.Box, 0x01020304u, 2.5f, string.Empty), (converted0.Shape, converted0.ColourRgba, converted0.Width, converted0.Text));
        Assert.Equal([new HrotVec3(5, 6, 0), new HrotVec3(7, 8, 9)], converted0.Points.ToArray());
        Assert.Equal(new GizmoPrimitiveData(GizmoPrimitiveShape.Box, converted0.Points, 0x01020304u, 2.5f, string.Empty), converted0);   // and no card field set
        var converted = h.Host.Convert(in text)!.Value;
        Assert.Equal((GizmoPrimitiveShape.Text, new HrotVec3(11, 12, 0), 0x0A141E28u, "hello"), (converted.Shape, converted.Points[0], converted.ColourRgba, converted.Text));
        Assert.Equal(GizmoPrimitiveSpace.World, converted.Space);
        Assert.Equal((0, 0), (converted.AnchorId, (int)converted.Row));
    }

    [Fact]
    public void Unit_cards_and_symbols_carry_the_commanders_and_men_cards_come_with_the_labels_layer()
    {
        using var h = new Stage();
        Commander(h, 2001, new Vector3(5, 5, 0));
        h.AddHrotEntity(2002, new Vector3(1, 1, 0), "Rifleman 1", tkb: 300);

        // mode 2: UnitCards + UnitSymbols - the commander's card and symbol, no man's card
        h.Host.SetView(View(["UnitCards", "UnitSymbols"]));
        h.Host.Frame(h.World, 1.0);
        var frames = h.Host.Take();
        Assert.Equal(["UnitCards", "UnitSymbols"], frames.Select(f => f.Layer).ToArray());
        var unitCards = CardsOf(frames, "UnitCards");
        Assert.NotEmpty(unitCards);
        Assert.Equal([2001], Anchors(unitCards));
        Assert.Contains(unitCards, p => p.Shape == GizmoPrimitiveShape.Text && p.Text == "Alpha squad #2001");
        // the symbol is the card's left cell: an Icon of row 0 in the UnitCards frame (the card's own layer), drawn while UnitSymbols is subscribed too
        var cell = Assert.Single(unitCards, p => p.Shape == GizmoPrimitiveShape.Icon);
        Assert.Equal((GizmoPrimitiveSpace.EntityCard, 2001, (byte)0), (cell.Space, cell.AnchorId, cell.Row));
        Assert.True(SymbolCode.TryParse(cell.Text, out _));
        Assert.Empty(CardsOf(frames, "UnitSymbols"));   // the standalone map symbols are no card primitives
        Assert.All(frames.SelectMany(f => f.Primitives), p => Assert.Equal(GizmoPrimitiveSpace.EntityCard, p.Space));   // nothing fell out as a ground box or text

        // UnitCards alone: the card without the symbol cell
        h.Host.SetView(View(["UnitCards"]));
        h.Host.Frame(h.World, 1.5);
        var cardOnly = CardsOf(h.Host.Take(), "UnitCards");
        Assert.Equal([2001], Anchors(cardOnly));
        Assert.DoesNotContain(cardOnly, p => p.Shape == GizmoPrimitiveShape.Icon);

        // mode 3: + Labels - the men's cards as well
        h.Host.SetView(View(["UnitCards", "UnitSymbols", "Labels"]));
        h.Host.Frame(h.World, 2.0);
        frames = h.Host.Take();
        Assert.Equal([2001], Anchors(CardsOf(frames, "UnitCards")));
        Assert.Equal([2002], Anchors(CardsOf(frames, "Labels")));
        Assert.Contains(CardsOf(frames, "Labels"), p => p.Shape == GizmoPrimitiveShape.Text && p.Text == "Rifleman 1 #2002");

        // mode 1: no view, nothing is drawn at all
        h.Host.SetView(null);
        h.Host.Frame(h.World, 3.0);
        Assert.Empty(h.Host.Take());
    }

    [Fact]
    public void A_card_whose_anchor_is_no_dps_entity_is_dropped_and_counted_never_drawn_on_the_ground()
    {
        using var h = new Stage();
        h.AddHrotEntity(2002, new Vector3(1, 1, 0), "Rifleman 1", tkb: 300);
        h.Host.SetView(View(["Labels"]));
        h.Host.Frame(h.World, 1.0);
        int perMan = CardsOf(h.Host.Take(), "Labels").Count;   // what one mapped man's card is made of
        Assert.True(perMan > 0);
        Assert.Equal(0, h.Host.UnmappedCardPrimitives);

        // an entity with a network id the node's map does not know, and a replica (not published under its own id)
        var ghost = h.AddHrotEntity(2003, Vector3.Zero, "Ghost", tkb: 300);
        h.Map.Unregister(2003, 0);
        var replica = h.AddHrotEntity(2004, Vector3.Zero, "Replica", tkb: 300);
        h.World.AddComponent(replica, new ReplicationRoute());
        Assert.True(h.World.IsAlive(ghost));

        h.Host.Frame(h.World, 2.0);

        var labels = CardsOf(h.Host.Take(), "Labels");
        Assert.Equal([2002], Anchors(labels));
        Assert.Equal(perMan, labels.Count);   // only the mapped man's own card
        Assert.All(labels, p => Assert.Equal(GizmoPrimitiveSpace.EntityCard, p.Space));
        Assert.DoesNotContain(labels, p => p.Points.Count > 0);   // no ground point from a card primitive
        Assert.Equal(2 * perMan, h.Host.UnmappedCardPrimitives);   // both unmapped anchors, counted
    }

    /// <summary>
    /// T-059 (HOT path): a steady 3-layer view of 100 men and 10 commanders (540 primitives, mode 3 + symbols) costs the host's capture nothing: the layer lists, the frame array, the point arrays and the card strings are reused
    /// (<c>Take</c> 0 B). What a run still allocates (measured 9 840 B, 18 B a primitive) is HROT's own <c>StatelessGizmoSystem.Execute</c> (its projectors format the card names), asserted not to grow past 12 000 B.
    /// Mutation: the card text made with <c>ToString()</c> per primitive again.
    /// </summary>
    [Fact]
    public void A_steady_three_layer_view_of_110_cards_allocates_only_HROTs_projector_cost()
    {
        using var h = new Stage();
        for (int k = 0; k < 100; k++)
        {
            h.AddHrotEntity(10000 + k, new Vector3(k % 10, k / 10, 0), "Man " + k, tkb: 300);
        }

        for (int k = 0; k < 10; k++)
        {
            Commander(h, 20000 + k, new Vector3(k, 0, 0));
        }

        h.Host.SetView(View(["UnitCards", "UnitSymbols", "Labels"]));
        double t = 1.0;
        for (int f = 0; f < 30; f++)
        {
            h.Host.Frame(h.World, t);
            t += 1.0;
            h.Host.Take();
        }

        long frameBytes = 0, takeBytes = 0;
        const int runs = 50;
        int prims = 0;
        for (int f = 0; f < runs; f++)
        {
            long a = GC.GetAllocatedBytesForCurrentThread();
            h.Host.Frame(h.World, t);
            t += 1.0;
            long b = GC.GetAllocatedBytesForCurrentThread();
            var frames = h.Host.Take();
            long c = GC.GetAllocatedBytesForCurrentThread();
            frameBytes += b - a;
            takeBytes += c - b;
            prims = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                prims += frames[i].Primitives.Count;
            }
        }

        Assert.Equal(540, prims);
        Assert.Equal(0L, takeBytes);
        Assert.InRange(frameBytes / runs, 0, 12_000);
    }
}
