using System.Numerics;
using System.Reflection;
using Fdp.Core;
using Fdp.ModuleHost.Abstractions;
using Fdp.Toolkit.Behavior.Diagnostics;
using Fdp.Toolkit.Diagnostics.Gizmos;
using Fdp.Toolkit.Diagnostics.Gizmos.Settings;
using Fdp.Toolkit.Diagnostics.Gizmos.Systems;
using Fdp.Toolkit.Replication.Components;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using IGizmoSource = SimNetGateway.Gateway.HrotDis.IGizmoSource;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The gateway as one more gizmo host of the node (DD-017 3.7): HROT's own gizmo projectors run into a private draw buffer, with a visibility policy set by the client,
/// and what they drew is handed out per layer. Everything is the host's own: its settings registry, projector registry, draw buffer and gizmo system, so the editor's map
/// (its selection, pins, layers and buffers) is never read or written. Nothing is built or run while there is no view; the view's rate caps the runs. Main thread only.
/// </summary>
internal sealed class HrotGizmoHost : IGizmoSource
{
    private const double DefaultRateHz = 10.0;
    private const double MaxRateHz = 60.0;

    private static readonly Lazy<(Dictionary<string, byte> ByName, Dictionary<byte, string> ById)> Layers = new(LoadLayers);

    private readonly Action<string> _log;
    private readonly HashSet<int> _selected = new();
    private readonly Dictionary<int, HashSet<string>> _pinned = new();
    private readonly Dictionary<AiOverlayFlags, FamilyPolicy> _policies = new();
    // the frame of the layers is built into these, reused every run (hot path: no allocation for a steady frame); the array Take returns is valid until the next Frame
    private GizmoLayerFrame[] _out = Array.Empty<GizmoLayerFrame>();
    private readonly Dictionary<byte, List<GizmoPrimitiveData>> _byId = new();
    private LayerMask256 _mask;
    private bool _ready;
    private readonly Dictionary<ulong, TextEntry> _texts = new();
    private const int MaxPoints = 3;
    private readonly List<HrotVec3[]>[] _pool = [new(), new(), new(), new()];   // point arrays by length (1 .. 3), handed out in order and reused by the next run
    private readonly int[] _poolUsed = new int[MaxPoints + 1];
    private const int MaxCachedTexts = 4096;

    private sealed class TextEntry
    {
        public ulong A, B, C, D;
        public string Text = string.Empty;
    }
    private GizmoViewRequest? _view;
    private HashSet<string> _layerNames = new(StringComparer.Ordinal);
    private EntityRepository? _builtFor;
    private DebugPrimitiveBuffer? _buffer;
    /// <summary>HROT's ground height for the 2-D primitives (set from the world each captured frame).</summary>
    internal Fdp.Toolkit.World.IWorldQuery? Ground { get; set; }
    private StatelessGizmoSystem? _system;
    private double _next = double.NegativeInfinity;
    private double _last = double.NaN;

    public HrotGizmoHost(Action<string>? log = null) => _log = log ?? (_ => { });

    /// <summary>How many times the projectors ran (tests and diagnostics).</summary>
    public int Runs { get; private set; }

    public void SetView(GizmoViewRequest? view)
    {
        _view = view;
        _selected.Clear();
        _pinned.Clear();
        _layerNames = new HashSet<string>(StringComparer.Ordinal);
        _ready = false;
        _out = Array.Empty<GizmoLayerFrame>();
        _byId.Clear();
        _mask = default;
        if (view is not { } v)
        {
            _next = double.NegativeInfinity;
            return;
        }

        foreach (string layer in v.Layers)
        {
            if (Layers.Value.ByName.ContainsKey(layer))
            {
                _layerNames.Add(layer);
            }
            else
            {
                _log($"dps: gizmo layer '{layer}' is not a layer of this HROT");
            }
        }

        foreach (int id in v.Selected)
        {
            _selected.Add(id);
        }

        foreach (var (entity, family) in v.Pinned)
        {
            if (!_pinned.TryGetValue(entity, out var families))
            {
                _pinned[entity] = families = new HashSet<string>(StringComparer.Ordinal);
            }

            families.Add(string.IsNullOrEmpty(family) ? "*" : family);
        }

        BuildOutputs();
        _next = double.NegativeInfinity;
    }

    /// <summary>The per-layer lists, the sorted frame array and the layer mask, built once per view.</summary>
    private void BuildOutputs()
    {
        var names = _layerNames.ToArray();
        Array.Sort(names, StringComparer.Ordinal);
        _out = new GizmoLayerFrame[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            var list = new List<GizmoPrimitiveData>();
            _out[i] = new GizmoLayerFrame(names[i], list);
            byte id = Layers.Value.ByName[names[i]];
            _mask.SetBit(id);
            if (Layers.Value.ById.TryGetValue(id, out var owner) && owner == names[i])
            {
                _byId[id] = list;   // a layer row two names share belongs to the first; the other's frame stays empty
            }
        }
    }

    /// <summary>The frames drawn since the last call (empty when none is due). The returned array and its lists are REUSED by the next run: read them before it.</summary>
    public IReadOnlyList<GizmoLayerFrame> Take()
    {
        if (!_ready)
        {
            return Array.Empty<GizmoLayerFrame>();
        }

        _ready = false;
        return _out;
    }

    /// <summary>Once per host frame: runs the projectors when a view exists and its rate says a frame is due. <paramref name="now"/> is wall seconds.</summary>
    internal void Frame(EntityRepository world, double now)
    {
        if (_view is not { } view || _layerNames.Count == 0 || now < _next)
        {
            return;
        }

        double rate = view.RateHz > 0 ? Math.Min(view.RateHz, MaxRateHz) : DefaultRateHz;
        float dt = double.IsNaN(_last) ? 0f : (float)Math.Max(0.0, now - _last);
        _last = now;
        _next = now + 1.0 / rate;

        if (!ReferenceEquals(_builtFor, world) || _system is null)
        {
            Build(world);
        }

        _buffer!.SetActiveLayers(in _mask);
        _buffer.EndFrame(dt);
        _system!.Execute(world, dt);
        Runs++;
        Capture();
    }

    private void Build(EntityRepository world)
    {
        // the host's own objects: nothing of the editor's map is shared
        var settings = new GizmoSettingsRegistry();
        var registry = new StatelessGizmoRegistry();
        _policies.Clear();
        GizmoReflectionRegistrar.RegisterAll(new GizmoRegistry(), registry, settings, PolicyOf, _ => null, line => _log("dps: " + line));
        _buffer = new DebugPrimitiveBuffer(8192);
        _system = new StatelessGizmoSystem(registry, _buffer) { FreshEntitiesPerFrame = 256, MaxHeldFrames = 3 };
        _builtFor = world;
    }

    private IGizmoVisibilityPolicy? PolicyOf(Type projector)
    {
        var family = projector.GetCustomAttribute<GizmoProjectorAttribute>()?.Family ?? AiOverlayFlags.None;
        if (family == AiOverlayFlags.None)
        {
            return null;
        }

        if (!_policies.TryGetValue(family, out var policy))
        {
            _policies[family] = policy = new FamilyPolicy(this, family);
        }

        return policy;
    }

    private void Capture()
    {
        for (int i = 0; i < _out.Length; i++)
        {
            ((List<GizmoPrimitiveData>)_out[i].Primitives).Clear();
        }

        Array.Clear(_poolUsed);

        Ground = Fdp.Toolkit.World.WorldQuery.Of(_builtFor!);   // HROT's own ground height, for the 2-D primitives
        var frame = _buffer!.GetFrame();
        for (int i = 0; i < frame.Length; i++)
        {
            ref readonly var p = ref frame[i];
            if (!_byId.TryGetValue(p.DebugLayer, out var list))
            {
                continue;
            }

            if (Convert(in p) is { } data)
            {
                list.Add(data);
            }
        }

        _ready = true;
    }

    internal GizmoPrimitiveData? Convert(in DebugPrimitive p)
    {
        uint colour = Rgba(p.Color);
        if (p.Space == CoordinateSpace.EntityCard)
        {
            return ConvertCard(in p, colour);
        }

        switch (p.Shape)
        {
            case DebugPrimitiveShape.Line:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.LineStrip, Pts(V(p.LineStart), V(p.LineEnd)), colour, p.Thickness, string.Empty);
            case DebugPrimitiveShape.Arrow:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Arrow, Pts(V(p.ArrowFrom), V(p.ArrowTo)), colour, p.Thickness, string.Empty);
            case DebugPrimitiveShape.Sphere:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Circle, Pts(V(p.SphereCenter)), colour, p.SphereRadius, string.Empty);
            case DebugPrimitiveShape.FilledTriangle:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Polygon,
                    Pts(new HrotVec3(p.TriA.X, p.TriA.Y, 0), new HrotVec3(p.TriB.X, p.TriB.Y, 0), new HrotVec3(p.TriC.X, p.TriC.Y, 0)), colour, 0f, string.Empty);
            case DebugPrimitiveShape.Box2D:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Box,
                    Pts(new HrotVec3(p.BoxCenterX, p.BoxCenterY, GroundAt(p.BoxCenterX, p.BoxCenterY)), new HrotVec3(p.BoxExtentX, p.BoxExtentY, p.BoxAngleDeg)), colour, p.Thickness, string.Empty);
            case DebugPrimitiveShape.Text:
                string text = p.StringHash != 0 ? _buffer!.InternMap.TryResolve(p.StringHash) ?? string.Empty : TextOf(in p.TextContent);
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Text, Pts(new HrotVec3(p.TextX, p.TextY, GroundAt(p.TextX, p.TextY))), colour, 0f, text);
            default:
                return null;   // meta primitives (bindings, masks) and shapes a client cannot draw from a point list
        }
    }

    /// <summary>A reused point array of <paramref name="a"/> (and <paramref name="b"/>, <paramref name="c"/>) for this run.</summary>
    private HrotVec3[] Pts(HrotVec3 a, HrotVec3? b = null, HrotVec3? c = null)
    {
        int n = c is null ? (b is null ? 1 : 2) : 3;
        var list = _pool[n];
        int at = _poolUsed[n]++;
        if (at == list.Count)
        {
            list.Add(new HrotVec3[n]);
        }

        var array = list[at];
        array[0] = a;
        if (n > 1)
        {
            array[1] = b!.Value;
        }

        if (n > 2)
        {
            array[2] = c!.Value;
        }

        return array;
    }

    /// <summary>The string of a primitive's inline text, made once per distinct content (a steady frame draws the same texts).</summary>
    private unsafe string TextOf(in Fdp.Toolkit.Diagnostics.Gizmos.FixedString32 content)
    {
        var local = content;
        var words = new ReadOnlySpan<ulong>(&local, 4);
        ulong key = 14695981039346656037UL;
        for (int i = 0; i < 4; i++)
        {
            key = (key ^ words[i]) * 1099511628211UL;
        }

        if (_texts.TryGetValue(key, out var entry) && entry.A == words[0] && entry.B == words[1] && entry.C == words[2] && entry.D == words[3])
        {
            return entry.Text;
        }

        if (_texts.Count >= MaxCachedTexts)
        {
            _texts.Clear();
        }

        _texts[key] = new TextEntry { A = words[0], B = words[1], C = words[2], D = words[3], Text = local.ToString() };
        return local.ToString();
    }

    private static uint Rgba(Rgba32 c) => ((uint)c.R << 24) | ((uint)c.G << 16) | ((uint)c.B << 8) | c.A;

    /// <summary>How many card primitives were dropped because their anchor is no DPS entity (tests and diagnostics).</summary>
    public int UnmappedCardPrimitives { get; private set; }

    /// <summary>
    /// Whether a network id is a DPS entity id of this station (DPS publishes every entity under its network id, so the id is the same number); null = every non-zero id.
    /// The station sets it from the node's entity map.
    /// </summary>
    internal Func<int, bool>? AnchorMapped { get; set; }

    /// <summary>
    /// A card primitive (DD-024), never a world one: the card's fields as the product set them, no decision about content. An anchor that is no DPS entity drops the primitive (counted);
    /// a shape a card does not use is dropped too. The text of a card comes from its inline content (the intern hash shares the anchor's four bytes for card primitives).
    /// </summary>
    private GizmoPrimitiveData? ConvertCard(in DebugPrimitive p, uint colour)
    {
        int anchor = p.AnchorIndex;
        if (anchor == 0 || !(AnchorMapped?.Invoke(anchor) ?? true))
        {
            UnmappedCardPrimitives++;
            return null;
        }

        switch (p.Shape)
        {
            case DebugPrimitiveShape.Text:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Text, [], colour, p.ThicknessU16, TextOf(in p.TextContent),   // a card text's size is in whole pixels, not tenths
                    GizmoPrimitiveSpace.EntityCard, anchor, p.ZIndex, p.TextX, p.TextY);
            case DebugPrimitiveShape.Box2D:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Box, [], colour, p.Thickness, string.Empty,
                    GizmoPrimitiveSpace.EntityCard, anchor, p.ZIndex, p.BoxCenterX, p.BoxCenterY, p.BoxExtentX, p.BoxExtentY, Rgba(p.FillColor));
            case DebugPrimitiveShape.Icon:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Icon, [], colour, 0f, TextOf(in p.TextContent),
                    GizmoPrimitiveSpace.EntityCard, anchor, p.ZIndex, CardH: p.Thickness);
            default:
                return null;
        }
    }

    /// <summary>The ground height at a 2-D primitive's point (a box, a text): a client never sees z = 0 for them. 0 when the world holds no terrain.</summary>
    private double GroundAt(float x, float y) => Ground?.GroundHeightAt(x, y) ?? 0f;

    private static HrotVec3 V(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The product's layer rows by name: every public byte constant of <c>DebugTraceLayers</c> (a name that two layers share keeps the first).</summary>
    private static (Dictionary<string, byte>, Dictionary<byte, string>) LoadLayers()
    {
        var byName = new Dictionary<string, byte>(StringComparer.Ordinal);
        var byId = new Dictionary<byte, string>();
        foreach (var field in typeof(DebugTraceLayers).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.FieldType == typeof(byte) && field.GetRawConstantValue() is byte id)
            {
                byName.TryAdd(field.Name, id);
                byId.TryAdd(id, field.Name);
            }
        }

        return (byName, byId);
    }

    /// <summary>
    /// Visibility of a family projector's drawing for an entity: its unit (a part follows its unit) is selected, or pinned for this family (or for every family).
    /// The state lives here, never in components of the shared world, so the editor's own selection and pins decide nothing.
    /// </summary>
    private sealed class FamilyPolicy : IGizmoVisibilityPolicy
    {
        private readonly HrotGizmoHost _host;
        private readonly string _family;

        public FamilyPolicy(HrotGizmoHost host, AiOverlayFlags family)
        {
            _host = host;
            _family = family.ToString();
        }

        public bool IsGloballyEnabled(ISimulationView view) => _host._selected.Count > 0 || _host._pinned.Count > 0;

        public bool IsEntityVisible(ISimulationView view, Entity entity)
        {
            int id = NetworkIdOf(view, UnitOf(view, entity));
            return id != 0 && (_host._selected.Contains(id) || (_host._pinned.TryGetValue(id, out var families) && (families.Contains("*") || families.Contains(_family))));
        }

        private static int NetworkIdOf(ISimulationView view, Entity e) =>
            view is EntityRepository repo && repo.IsComponentTypeRegistered<NetworkIdentity>() && view.IsAlive(e) && view.HasComponent<NetworkIdentity>(e)
                ? view.GetComponentRO<NetworkIdentity>(e).Value : 0;

        /// <summary>A part (a unit's sensor child, ...) follows the unit it hangs on.</summary>
        private static Entity UnitOf(ISimulationView view, Entity entity)
        {
            if (view is not EntityRepository repo || !repo.IsComponentTypeRegistered<PartMetadata>())
            {
                return entity;
            }

            for (int hop = 0; hop < 4 && view.IsAlive(entity) && view.HasComponent<PartMetadata>(entity); hop++)
            {
                var parent = view.GetComponentRO<PartMetadata>(entity).ParentEntity;
                if (parent.IsNull || !view.IsAlive(parent))
                {
                    break;
                }

                entity = parent;
            }

            return entity;
        }
    }
}
