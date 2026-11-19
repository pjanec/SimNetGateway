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
    private readonly List<GizmoLayerFrame> _frames = new();
    private GizmoViewRequest? _view;
    private HashSet<string> _layerNames = new(StringComparer.Ordinal);
    private EntityRepository? _builtFor;
    private DebugPrimitiveBuffer? _buffer;
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
        _frames.Clear();
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

        _next = double.NegativeInfinity;
    }

    public IReadOnlyList<GizmoLayerFrame> Take()
    {
        if (_frames.Count == 0)
        {
            return Array.Empty<GizmoLayerFrame>();
        }

        var taken = _frames.ToArray();
        _frames.Clear();
        return taken;
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

        var mask = default(LayerMask256);
        foreach (string name in _layerNames)
        {
            mask.SetBit(Layers.Value.ByName[name]);
        }

        _buffer!.SetActiveLayers(in mask);
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
        var byLayer = new Dictionary<string, List<GizmoPrimitiveData>>(StringComparer.Ordinal);
        foreach (string name in _layerNames)
        {
            byLayer[name] = new List<GizmoPrimitiveData>();
        }

        var frame = _buffer!.GetFrame();
        for (int i = 0; i < frame.Length; i++)
        {
            ref readonly var p = ref frame[i];
            if (!Layers.Value.ById.TryGetValue(p.DebugLayer, out var layer) || !byLayer.TryGetValue(layer, out var list))
            {
                continue;
            }

            if (Convert(in p) is { } data)
            {
                list.Add(data);
            }
        }

        _frames.Clear();
        foreach (string name in _layerNames.Order(StringComparer.Ordinal))
        {
            _frames.Add(new GizmoLayerFrame(name, byLayer[name]));
        }
    }

    private GizmoPrimitiveData? Convert(in DebugPrimitive p)
    {
        uint colour = ((uint)p.Color.R << 24) | ((uint)p.Color.G << 16) | ((uint)p.Color.B << 8) | p.Color.A;
        switch (p.Shape)
        {
            case DebugPrimitiveShape.Line:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.LineStrip, [V(p.LineStart), V(p.LineEnd)], colour, p.Thickness, string.Empty);
            case DebugPrimitiveShape.Arrow:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Arrow, [V(p.ArrowFrom), V(p.ArrowTo)], colour, p.Thickness, string.Empty);
            case DebugPrimitiveShape.Sphere:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Circle, [V(p.SphereCenter)], colour, p.SphereRadius, string.Empty);
            case DebugPrimitiveShape.FilledTriangle:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Polygon,
                    [new HrotVec3(p.TriA.X, p.TriA.Y, 0), new HrotVec3(p.TriB.X, p.TriB.Y, 0), new HrotVec3(p.TriC.X, p.TriC.Y, 0)], colour, 0f, string.Empty);
            case DebugPrimitiveShape.Box2D:
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Box,
                    [new HrotVec3(p.BoxCenterX, p.BoxCenterY, 0), new HrotVec3(p.BoxExtentX, p.BoxExtentY, p.BoxAngleDeg)], colour, p.Thickness, string.Empty);
            case DebugPrimitiveShape.Text:
                string text = p.StringHash != 0 ? _buffer!.InternMap.TryResolve(p.StringHash) ?? string.Empty : p.TextContent.ToString();
                return new GizmoPrimitiveData(GizmoPrimitiveShape.Text, [new HrotVec3(p.TextX, p.TextY, 0)], colour, 0f, text);
            default:
                return null;   // meta primitives (bindings, masks) and shapes a client cannot draw from a point list
        }
    }

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
