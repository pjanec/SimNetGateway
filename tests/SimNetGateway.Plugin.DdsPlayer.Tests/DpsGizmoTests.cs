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
}
