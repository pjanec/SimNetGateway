using System.Numerics;
using Fdp.Core;
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
}
