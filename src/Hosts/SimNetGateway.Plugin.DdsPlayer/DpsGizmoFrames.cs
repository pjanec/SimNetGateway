using SimNetGateway.Gateway.HrotDis;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>Builds the <c>GizmoFrame</c> topic values of the layers into buffers kept between frames (T-059).</summary>
internal sealed class DpsGizmoFrames
{
    /// <summary>One layer's topic value, kept and refilled every frame: the primitive list grows and is never shrunk below what it must say, and every primitive keeps its own point list (hot path: 0 B for a steady frame).</summary>
    private sealed class LayerBuffer
    {
        public readonly List<D.GizmoPrimitive> Primitives = new();
        public readonly List<List<D.Vec3d>> PointLists = new();   // the point list of primitive i, kept when the frame shrinks
    }

    /// <summary>
    /// A hash of what <see cref="Build"/> would say for the first <paramref name="fit"/> primitives of <paramref name="frame"/> (T-061: a layer is written only when this changed, plus a heartbeat).
    /// FNV-1a over every field the topic value carries; no allocation. A collision would skip one change until the next heartbeat; with 64 bits that is not a concern.
    /// </summary>
    public static ulong ContentHash(GizmoLayerFrame frame, int fit)
    {
        ulong h = 14695981039346656037UL;
        h = Mix(h, (ulong)fit);
        var source = frame.Primitives;
        for (int i = 0; i < fit; i++)
        {
            var p = source[i];
            h = Mix(h, (ulong)p.Shape | (ulong)p.Space << 8 | (ulong)p.Row << 16 | (ulong)(uint)p.AnchorId << 32);
            h = Mix(h, p.ColourRgba | (ulong)p.FillRgba << 32);
            h = Mix(h, BitCast(p.Width) | (ulong)BitCast(p.CardX) << 32);
            h = Mix(h, BitCast(p.CardY) | (ulong)BitCast(p.CardW) << 32);
            h = Mix(h, BitCast(p.CardH));
            string text = p.Text;
            h = Mix(h, (ulong)text.Length);
            for (int k = 0; k < text.Length; k++)
            {
                h = Mix(h, text[k]);
            }

            int n = Math.Min(p.Points.Count, 64);
            h = Mix(h, (ulong)n);
            for (int k = 0; k < n; k++)
            {
                var x = p.Points[k];
                h = Mix(h, (ulong)BitConverter.DoubleToInt64Bits(x.X));
                h = Mix(h, (ulong)BitConverter.DoubleToInt64Bits(x.Y));
                h = Mix(h, (ulong)BitConverter.DoubleToInt64Bits(x.Z));
            }
        }

        return h;
    }

    private static ulong Mix(ulong h, ulong v) => (h ^ v) * 1099511628211UL;

    private static uint BitCast(float f) => (uint)BitConverter.SingleToInt32Bits(f);

    private readonly Dictionary<string, LayerBuffer> _buffers = new(StringComparer.Ordinal);

    /// <summary>The topic value of one layer: the first <paramref name="fit"/> primitives of <paramref name="frame"/> in this layer's reused buffers.</summary>
    public D.GizmoFrame Build(string producer, GizmoLayerFrame frame, int fit)
    {
        if (!_buffers.TryGetValue(frame.Layer, out var buffer))
        {
            _buffers[frame.Layer] = buffer = new LayerBuffer();
        }

        var list = buffer.Primitives;
        var source = frame.Primitives;
        for (int i = 0; i < fit; i++)
        {
            var p = source[i];
            if (i == buffer.PointLists.Count)
            {
                buffer.PointLists.Add(new List<D.Vec3d>());
            }

            var points = buffer.PointLists[i];
            points.Clear();
            int n = Math.Min(p.Points.Count, 64);
            for (int k = 0; k < n; k++)
            {
                var x = p.Points[k];
                points.Add(new D.Vec3d { X = x.X, Y = x.Y, Z = x.Z });
            }

            var value = new D.GizmoPrimitive
            {
                Shape = (D.GizmoShape)(byte)p.Shape,
                Points = points,
                ColourRgba = p.ColourRgba,
                Width = p.Width,
                Text = p.Text.Length > 64 ? p.Text[..64] : p.Text,
                Space = (D.GizmoSpace)(byte)p.Space,
                AnchorId = p.AnchorId,
                Row = p.Row,
                CardX = p.CardX,
                CardY = p.CardY,
                CardW = p.CardW,
                CardH = p.CardH,
                FillRgba = p.FillRgba,
            };
            if (i < list.Count)
            {
                list[i] = value;
            }
            else
            {
                list.Add(value);
            }
        }

        if (list.Count > fit)
        {
            list.RemoveRange(fit, list.Count - fit);   // the list says exactly this frame's primitives (the point lists stay in the pool)
        }

        return new D.GizmoFrame { Producer = producer, Layer = frame.Layer.Length > 32 ? frame.Layer[..32] : frame.Layer, SimTime = 0.0, Primitives = list };
    }
}
