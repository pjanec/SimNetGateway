using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// The seam through which a plugin shows the product's debug gizmos (paths, contacts, sensor cones, ...) to a client (DD-017 3.7): the plugin hands in what the
/// client wants, the host runs the product's gizmo projectors for it (never disturbing the host's own map) and returns what they drew, per layer. Nothing runs while
/// there is no view. Main thread only.
/// </summary>
public interface IGizmoSource
{
    /// <summary>The client's wish; null = nobody wants gizmos (no projector runs and <see cref="Take"/> returns nothing).</summary>
    void SetView(GizmoViewRequest? view);

    /// <summary>
    /// What was drawn since the last call: one frame per requested layer that was produced (a layer with nothing drawn has an empty frame, so a client clears it).
    /// ⭐ The returned list and the lists inside it may be REUSED by the next run of the source (T-059: a steady frame allocates nothing): read them before the next call of the host's frame.
    /// Empty when no frame is due yet (the view's rate caps how often the projectors run) or there is no view.
    /// </summary>
    IReadOnlyList<GizmoLayerFrame> Take();
}

/// <summary>What a client wants of the gizmos: the product's layer names, the selected units (network ids), pinned units with a family ("" = every family) and the rate in Hz.</summary>
public readonly record struct GizmoViewRequest(IReadOnlyList<string> Layers, IReadOnlyList<int> Selected, IReadOnlyList<(int Entity, string Family)> Pinned, double RateHz);

/// <summary>The complete picture of one layer for one frame; <see cref="Primitives"/> is empty when the layer drew nothing.</summary>
public readonly record struct GizmoLayerFrame(string Layer, IReadOnlyList<GizmoPrimitiveData> Primitives);

/// <summary>Shapes of a drawn primitive (the order of the DPS shapes).</summary>
public enum GizmoPrimitiveShape : byte
{
    LineStrip = 0,
    Arrow,
    Circle,
    Polygon,
    Box,
    Text,
    Icon,
}

/// <summary>
/// One primitive in local metres (x east, y north, z up). LineStrip and Arrow: the points; Circle: one point (the centre) and <see cref="Width"/> = the radius;
/// Polygon: the corners; Box: the centre and a second point (extent x, extent y, angle in degrees); Text: one point and <see cref="Text"/>. Otherwise
/// <see cref="Width"/> is the line width.
/// </summary>
/// <remarks>
/// A card primitive (DD-024: <see cref="Space"/> = EntityCard) has no points: <see cref="AnchorId"/> is the DPS entity id of its card's entity, <see cref="Row"/> its row (0 = card-wide).
/// Text: <see cref="CardX"/>/<see cref="CardY"/> in pixels from the row's left and top, <see cref="Width"/> the font size in pixels. Box: <see cref="CardX"/>/<see cref="CardW"/> the centre and
/// half-width as fractions of the card, <see cref="CardY"/>/<see cref="CardH"/> the centre offset and half-height in pixels, <see cref="FillRgba"/> the fill, <see cref="ColourRgba"/> the outline
/// of <see cref="Width"/> pixels. Icon: <see cref="Text"/> the symbol code, <see cref="CardH"/> its height in pixels.
/// </remarks>
public readonly record struct GizmoPrimitiveData(GizmoPrimitiveShape Shape, IReadOnlyList<HrotVec3> Points, uint ColourRgba, float Width, string Text,
    GizmoPrimitiveSpace Space = GizmoPrimitiveSpace.World, int AnchorId = 0, byte Row = 0, float CardX = 0f, float CardY = 0f, float CardW = 0f, float CardH = 0f, uint FillRgba = 0u);

/// <summary>Where a primitive is drawn (the order of the DPS spaces).</summary>
public enum GizmoPrimitiveSpace : byte
{
    World = 0,
    EntityCard,
}
