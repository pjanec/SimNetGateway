using System.Collections.Concurrent;
using CycloneDDS.Runtime;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The gizmos of the station (DD-017 3.7): the client's <c>GizmoView</c> goes to the host's gizmo source and what the source draws is written as <c>GizmoFrame</c>, one
/// instance per layer (<c>Producer</c> = the station's partition, <c>Layer</c> = the product's layer name); a layer that is no longer wanted, or a view that is gone, is
/// disposed. The view follows the station's player rule: the player's (the client the station accepted) while a player holds the station, otherwise the first client that
/// wrote one; another client's view is dropped with one warning. Losing the viewer's liveliness drops the view, so no projector keeps running for a client that left.
/// A listener only records; all work is in <see cref="Pump"/> on the host's main thread.
/// </summary>
internal sealed class DpsGizmos : IDisposable
{
    private readonly record struct Liveliness(long Handle, int AliveChange, int NotAliveChange, uint AliveCount);

    private readonly IGizmoSource? _source;
    private readonly DpsInbound _inbound;
    private readonly DpsChannel<D.GizmoFrame> _frames;
    private readonly string _producer;
    private readonly Action<string> _log;
    private readonly DdsReader<D.GizmoView> _reader;
    private readonly ConcurrentQueue<Liveliness> _liveliness = new();
    private readonly HashSet<string> _published = new(StringComparer.Ordinal);
    private readonly HashSet<ulong> _warned = new();
    private ulong _viewer;
    private long _viewerHandle;

    public DpsGizmos(DdsParticipant participant, DpsSettings settings, string partition, IGizmoSource? source, DpsInbound inbound, DpsChannel<D.GizmoFrame> frames,
        Action<string> log)
    {
        _source = source;
        _inbound = inbound;
        _frames = frames;
        _producer = partition.Length > 64 ? partition[..64] : partition;
        _log = log;
        _reader = new DdsReader<D.GizmoView>(participant, null, new DdsReaderOptions { LivelinessLeaseDuration = TimeSpan.FromSeconds(settings.LeaseSeconds) }, null);
        _reader.LivelinessChanged += (_, st) => _liveliness.Enqueue(new Liveliness(st.LastPublicationHandle, st.AliveCountChange, st.NotAliveCountChange, st.AliveCount));
    }

    public void Pump()
    {
        if (_source is null)
        {
            using (_reader.Take(16))
            {
            }

            while (_liveliness.TryDequeue(out _))
            {
            }

            return;
        }

        // the viewer going away ends its view
        while (_liveliness.TryDequeue(out var e))
        {
            if ((e.AliveChange < 0 || e.NotAliveChange > 0) && _viewer != 0 && (e.Handle == _viewerHandle || e.AliveCount == 0))
            {
                Drop();
            }
        }

        // a player took the station: a viewer that is not the player loses its view
        if (_inbound.Owner != 0 && _viewer != 0 && _viewer != _inbound.Owner)
        {
            Drop();
        }

        D.GizmoView? latest = null;
        using (var loan = _reader.Take(16))
        {
            foreach (var sample in loan)
            {
                if (!sample.IsValid)
                {
                    continue;
                }

                var view = sample.Data;
                if (view.ClientId == 0)
                {
                    if (_warned.Add(0))
                    {
                        _log("dps: a GizmoView without a client id is ignored (a client puts a nonzero ClientId on every player sample)");
                    }

                    continue;
                }

                ulong allowed = _inbound.Owner != 0 ? _inbound.Owner : _viewer;
                if (allowed != 0 && view.ClientId != allowed)
                {
                    if (_warned.Add(view.ClientId))
                    {
                        _log($"dps: a second gizmo view (client id {view.ClientId}) is ignored while another client holds the view of this station");
                    }

                    continue;
                }

                _viewer = view.ClientId;
                _viewerHandle = sample.Info.PublicationHandle;
                latest = view;
            }
        }

        if (latest is { } wanted)
        {
            var request = ToRequest(in wanted);
            _source.SetView(request);
            DisposeUnwanted(request.Layers);
        }

        if (_viewer == 0)
        {
            return;
        }

        foreach (var frame in _source.Take())
        {
            _published.Add(frame.Layer);
            _frames.Send(new DpsItem<D.GizmoFrame>(ToFrame(frame)));
        }
    }

    /// <summary>No view any more: the source stops, every layer instance is disposed.</summary>
    private void Drop()
    {
        _source!.SetView(null);
        DisposeUnwanted([]);
        _viewer = 0;
        _viewerHandle = 0;
    }

    private void DisposeUnwanted(IReadOnlyList<string> wanted)
    {
        foreach (string layer in _published.ToArray())
        {
            if (!wanted.Contains(layer, StringComparer.Ordinal))
            {
                _frames.Send(new DpsItem<D.GizmoFrame>(new D.GizmoFrame { Producer = _producer, Layer = layer, Primitives = [] }, true));
                _published.Remove(layer);
            }
        }
    }

    private static GizmoViewRequest ToRequest(in D.GizmoView v)
    {
        var layers = (v.Layers ?? []).Select(l => l.Name).Where(n => !string.IsNullOrEmpty(n)).ToArray();
        var selected = (v.SelectedIds ?? []).ToArray();
        var families = (v.PinnedFamilies ?? []).Select(f => f.Name).Where(n => !string.IsNullOrEmpty(n)).ToArray();
        var pinned = new List<(int, string)>();
        foreach (int id in v.PinnedIds ?? [])
        {
            if (families.Length == 0)
            {
                pinned.Add((id, string.Empty));
            }

            foreach (string family in families)
            {
                pinned.Add((id, family));
            }
        }

        return new GizmoViewRequest(layers, selected, pinned, v.RateHz);
    }

    private D.GizmoFrame ToFrame(GizmoLayerFrame frame) => new()
    {
        Producer = _producer,
        Layer = frame.Layer.Length > 32 ? frame.Layer[..32] : frame.Layer,
        SimTime = 0.0,
        Primitives = [.. frame.Primitives.Take(4096).Select(p => new D.GizmoPrimitive
        {
            Shape = (D.GizmoShape)(byte)p.Shape,
            Points = [.. p.Points.Take(64).Select(x => new D.Vec3d { X = x.X, Y = x.Y, Z = x.Z })],
            ColourRgba = p.ColourRgba,
            Width = p.Width,
            Text = p.Text.Length > 64 ? p.Text[..64] : p.Text,
        })],
    };

    public void Dispose() => _reader.Dispose();
}
