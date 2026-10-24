using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.NedRpr;

namespace SimNetGateway.Viewer;

/// <summary>One entity on the map: plain values, a copy of the repository's state at the refresh (R-038).</summary>
public readonly record struct MapPoint(
    EntityId Id,
    string Name,
    SourceId Origin,
    string OriginName,
    double LatitudeDeg,
    double LongitudeDeg,
    bool HasPrediction,
    double PredictedLatitudeDeg,
    double PredictedLongitudeDeg,
    double ErrorMetres,
    int ParentIndex,
    bool IsStatic,
    bool IsStale);

/// <summary>
/// A formation link: from a member (index into <see cref="MapModel.Points"/>) to its parent. A parent that has no position of its
/// own (a unit or aggregate) is anchored at the centroid of its members that have one.
/// </summary>
public readonly record struct MapLink(
    int ChildIndex, EntityId Parent, string ParentName, bool ParentOnMap, double AnchorLatitudeDeg, double AnchorLongitudeDeg);

/// <summary>
/// The Map panel's model (DD-005 section 3): every live entity with a position, projected from ECEF to latitude and longitude
/// through <see cref="Wgs84"/> (Common), with the predicted position (<c>Derived.PredictedPosition</c>) and the length of the error
/// vector between the received and the predicted position, the formation link (index of the parent point) and the flags
/// "static" and "stale candidate" (R-068). Refreshed every frame (R-075); the lists are reused, nothing of the engine is kept.
/// </summary>
public sealed class MapModel
{
    /// <summary>Speed below which an entity counts as static (metres per second).</summary>
    public const double StaticSpeed = 0.05;

    /// <summary>Seconds without a write by the owner side after which an entity is a stale candidate.</summary>
    public const double StaleSeconds = 10.0;

    private readonly GatewayEngine _engine;
    private readonly PivotSchema _schema;
    private readonly List<MapPoint> _points = new();
    private readonly Dictionary<EntityId, int> _index = new();
    private readonly List<(int Point, EntityId Parent)> _parents = new();
    private readonly Dictionary<SourceId, string> _originNames = new();
    private readonly List<MapLink> _links = new();
    private readonly Dictionary<EntityId, (double Lat, double Lon, int Count)> _centroids = new();

    public MapModel(GatewayEngine engine, PivotSchema schema)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(schema);
        _engine = engine;
        _schema = schema;
    }

    /// <summary>The entities with a position, in repository order.</summary>
    public IReadOnlyList<MapPoint> Points => _points;

    /// <summary>The links from members to their parents, one per member that has a position and a parent.</summary>
    public IReadOnlyList<MapLink> Links => _links;

    /// <summary>Live entities at the last refresh, with or without a position.</summary>
    public int EntityCount { get; private set; }

    public bool HasBounds { get; private set; }

    public double MinLatitudeDeg { get; private set; }

    public double MaxLatitudeDeg { get; private set; }

    public double MinLongitudeDeg { get; private set; }

    public double MaxLongitudeDeg { get; private set; }

    private string NameOf(EntityId id) =>
        _engine.State.Find(id) is { } entity && entity.TryGet(_schema.Name, out var n) && n.Kind == AttrKind.String ? n.AsString() : "#" + id.Value;

    public int IndexOf(EntityId id) => _index.TryGetValue(id, out int i) ? i : -1;

    public void Refresh()
    {
        _points.Clear();
        _index.Clear();
        _parents.Clear();
        _links.Clear();
        _centroids.Clear();
        EntityCount = 0;
        HasBounds = false;
        double minLat = 0, maxLat = 0, minLon = 0, maxLon = 0;
        double now = _engine.WallTime;
        foreach (var entity in _engine.State.Entities)
        {
            EntityCount++;
            if (!entity.TryGet(_schema.Position, out var position) || position.Kind != AttrKind.Vec3)
            {
                continue;
            }

            var ecef = position.AsVec3();
            var geo = Wgs84.ToGeodetic(ecef);
            double lat = geo.LatitudeDegrees, lon = geo.LongitudeDegrees;
            bool hasPrediction = false;
            double predictedLat = 0, predictedLon = 0, error = 0;
            if (entity.TryGet(_schema.PredictedPosition, out var predicted) && predicted.Kind == AttrKind.Vec3)
            {
                var p = predicted.AsVec3();
                var pg = Wgs84.ToGeodetic(p);
                hasPrediction = true;
                predictedLat = pg.LatitudeDegrees;
                predictedLon = pg.LongitudeDegrees;
                double dx = p.X - ecef.X, dy = p.Y - ecef.Y, dz = p.Z - ecef.Z;
                error = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            bool isStatic = false;
            if (entity.TryGet(_schema.Velocity, out var velocity) && velocity.Kind == AttrKind.Vec3)
            {
                var v = velocity.AsVec3();
                isStatic = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z) < StaticSpeed;
            }

            string name = entity.TryGet(_schema.Name, out var n) && n.Kind == AttrKind.String ? n.AsString() : "#" + entity.Id.Value;
            if (!_originNames.TryGetValue(entity.Origin, out string? originName))
            {
                originName = _engine.Sources.NameOf(entity.Origin);
                _originNames[entity.Origin] = originName;
            }

            if (entity.TryGet(_schema.Parent, out var parent) && parent.Kind == AttrKind.EntityRef)
            {
                _parents.Add((_points.Count, parent.AsEntity()));
            }

            bool stale = entity.LastOriginWriteWallTime > 0 && now - entity.LastOriginWriteWallTime > StaleSeconds;
            _index[entity.Id] = _points.Count;
            _points.Add(new MapPoint(entity.Id, name, entity.Origin, originName, lat, lon, hasPrediction, predictedLat, predictedLon,
                error, -1, isStatic, stale));
            if (!HasBounds)
            {
                minLat = maxLat = lat;
                minLon = maxLon = lon;
                HasBounds = true;
            }
            else
            {
                minLat = Math.Min(minLat, lat);
                maxLat = Math.Max(maxLat, lat);
                minLon = Math.Min(minLon, lon);
                maxLon = Math.Max(maxLon, lon);
            }
        }

        foreach (var (point, parent) in _parents)
        {
            if (_index.TryGetValue(parent, out int parentIndex))
            {
                _points[point] = _points[point] with { ParentIndex = parentIndex };
            }
            else
            {
                var p = _points[point];
                _centroids.TryGetValue(parent, out var sum);
                _centroids[parent] = (sum.Lat + p.LatitudeDeg, sum.Lon + p.LongitudeDeg, sum.Count + 1);
            }
        }

        foreach (var (point, parent) in _parents)
        {
            if (_index.TryGetValue(parent, out int parentIndex))
            {
                var anchor = _points[parentIndex];
                _links.Add(new MapLink(point, parent, anchor.Name, true, anchor.LatitudeDeg, anchor.LongitudeDeg));
            }
            else
            {
                var sum = _centroids[parent];
                _links.Add(new MapLink(point, parent, NameOf(parent), false, sum.Lat / sum.Count, sum.Lon / sum.Count));
            }
        }

        MinLatitudeDeg = minLat;
        MaxLatitudeDeg = maxLat;
        MinLongitudeDeg = minLon;
        MaxLongitudeDeg = maxLon;
    }
}
