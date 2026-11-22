using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Physics.Components;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Weather;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The weather (DD-028), read from HROT's own state: the <c>WeatherConditions</c> of the World Environment entity (the one <c>WeatherField.GlobalOf</c> finds: the global weather, empty outline) and of every
/// Weather Region entity (a <c>WeatherConditions</c> with an outline, <c>WeatherField.TryOutline</c>, and a position; the outline is relative to the entity's position and is sent in the simulation frame).
/// Each is sent as <see cref="HrotWeather"/> when it is new or a mapped value differs from what was last sent, and <see cref="HrotWeatherRemoved"/> once when the entity is gone or torn down. A world with
/// no World Environment sends no global row (the client keeps its own default; DD-028 section 9). The surface wind is HROT's LOWEST wind level, the cloud its LOWEST layer; a field HROT has no source for gets
/// the default of DD-028 section 9. A steady gather allocates nothing (the outline array is made when a row is sent). Main thread only.
/// </summary>
internal sealed class WeatherGather
{
    /// <summary>The most outline points the topic carries (<c>Weather.Outline</c> bound); a longer outline is sampled evenly down to it.</summary>
    public const int MaxOutline = 32;

    /// <summary>The fields a row carries, apart from its outline.</summary>
    private readonly record struct Row(
        float FeatherM, float WindFromDeg, float WindSpeedMps, float WindGustMps, byte Precipitation, float PrecipitationMmPerHour, float VisibilityM,
        float FogTopM, float FogVisibilityM, byte CloudOctas, float CloudBaseM, float GroundTemperatureC, float RelativeHumidity);

    private sealed class Sent
    {
        public Row Row;
        public Vector2[]? Points;   // the outline as read (relative), null for the global weather
        public Vector3 Origin;
        public int Stamp;
    }

    private readonly Dictionary<int, Sent> _sent = new();
    private readonly List<int> _gone = new();
    private EntityRepository? _world;
    private EntityQuery? _query;
    private int _stamp;

    /// <summary>How many <see cref="HrotWeather"/> were sent, for the tests and the counters.</summary>
    public int SentCount { get; private set; }

    /// <summary>How many were withdrawn.</summary>
    public int Withdrawn { get; private set; }

    /// <summary>A world boundary: forget everything, so the next gather states every weather of the new world.</summary>
    public void Clear()
    {
        _sent.Clear();
        _world = null;
        _query = null;
    }

    public void Gather(EntityRepository world, InMemoryInbound inbound)
    {
        if (!ReferenceEquals(_world, world))
        {
            Clear();
            _world = world;
            _query = world.IsComponentTypeRegistered<WeatherConditions>() ? world.Query().With<WeatherConditions>().Build() : null;
        }

        if (_query is null)
        {
            return;
        }

        _stamp++;
        WeatherField.GlobalOf(world, out var environment);
        if (!environment.IsNull && world.IsAlive(environment) && world.GetLifecycleState(environment) != EntityLifecycle.TearDown)
        {
            Consider(world, inbound, environment, global: true);
        }

        foreach (var e in _query)
        {
            if (e == environment || world.GetLifecycleState(e) == EntityLifecycle.TearDown || !world.HasComponent<SimTransform>(e))
            {
                continue;
            }

            Consider(world, inbound, e, global: false);
        }

        _gone.Clear();
        foreach (var (id, s) in _sent)
        {
            if (s.Stamp != _stamp)
            {
                _gone.Add(id);
            }
        }

        foreach (int id in _gone)
        {
            _sent.Remove(id);
            inbound.Enqueue(new HrotWeatherRemoved(id));
            Withdrawn++;
        }
    }

    private void Consider(EntityRepository world, InMemoryInbound inbound, Entity e, bool global)
    {
        PointsView? outline = null;
        Vector3 origin = Vector3.Zero;
        if (!global)
        {
            if (!WeatherField.TryOutline(world, e, out var o))
            {
                return;   // a weather entity with no area is not a region (WeatherField ignores it too)
            }

            outline = new PointsView(o.Points);
            origin = (Vector3)world.GetComponentRO<SimTransform>(e).Position;
        }

        int id = world.HasComponent<NetworkIdentity>(e) ? world.GetComponentRO<NetworkIdentity>(e).Value : e.Index;
        var row = RowOf(in world.GetComponentRO<WeatherConditions>(e), global);
        bool isNew = !_sent.TryGetValue(id, out var s);
        if (!isNew && s!.Row.Equals(row) && SameArea(s, outline, origin))
        {
            s.Stamp = _stamp;
            return;
        }

        if (isNew)
        {
            s = new Sent();
            _sent[id] = s;
        }

        s!.Row = row;
        s.Origin = origin;
        s.Stamp = _stamp;
        HrotVec3[]? points = null;
        if (outline is not null)
        {
            int n = outline.Value.Count;
            if (s.Points is null || s.Points.Length != n)
            {
                s.Points = new Vector2[n];
            }

            for (int i = 0; i < n; i++)
            {
                s.Points[i] = outline.Value[i];
            }

            int take = Math.Min(n, MaxOutline);
            points = new HrotVec3[take];
            for (int i = 0; i < take; i++)
            {
                var p = s.Points[take == n ? i : (int)((long)i * n / take)];
                points[i] = new HrotVec3(origin.X + p.X, origin.Y + p.Y, origin.Z);
            }
        }
        else
        {
            s.Points = null;
        }

        inbound.Enqueue(new HrotWeather(id, points, row.FeatherM, row.WindFromDeg, row.WindSpeedMps, row.WindGustMps, row.Precipitation, row.PrecipitationMmPerHour, row.VisibilityM,
            row.FogTopM, row.FogVisibilityM, row.CloudOctas, row.CloudBaseM, row.GroundTemperatureC, row.RelativeHumidity));
        SentCount++;
    }

    private static bool SameArea(Sent s, PointsView? outline, Vector3 origin)
    {
        if (outline is null)
        {
            return s.Points is null;
        }

        if (s.Points is null || s.Origin != origin || s.Points.Length != outline.Value.Count)
        {
            return false;
        }

        for (int i = 0; i < s.Points.Length; i++)
        {
            if (s.Points[i] != outline.Value[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>HROT's value as the topic carries it (DD-028 D-2 and section 9).</summary>
    private static unsafe Row RowOf(in WeatherConditions w, bool global)
    {
        // the surface wind: the used level with the LOWEST altitude (HROT keeps them ascending; not trusted)
        float from = 0f, speed = 0f, gust = 0f;
        int low = -1;
        for (int i = 0; i < w.WindCount && i < WeatherConditions.MaxWindLevels; i++)
        {
            if (low < 0 || w.WindAltitudeM[i] < w.WindAltitudeM[low])
            {
                low = i;
            }
        }

        if (low >= 0)
        {
            from = Finite(w.WindFromDeg[low]);
            speed = Finite(w.WindSpeedMps[low]);
            gust = Finite(w.WindGustMps[low]);
        }

        // the lowest cloud layer
        byte octas = 0;
        float baseM = 0f;
        int lowCloud = -1;
        for (int i = 0; i < w.CloudCount && i < WeatherConditions.MaxCloudLayers; i++)
        {
            if (lowCloud < 0 || w.CloudBaseM[i] < w.CloudBaseM[lowCloud])
            {
                lowCloud = i;
            }
        }

        if (lowCloud >= 0)
        {
            octas = Math.Min(w.CloudOctas[lowCloud], (byte)8);
            baseM = Finite(w.CloudBaseM[lowCloud]);
        }

        float fogTop = Finite(w.FogTopM);
        return new Row(
            global ? 0f : Finite(w.FeatherM), from, speed, gust,
            w.Precipitation <= (byte)PrecipitationKind.Hail ? w.Precipitation : (byte)0, Finite(w.PrecipitationMmPerHour), Finite(w.VisibilityM),
            fogTop, fogTop > 0f ? Finite(w.FogVisibilityM) : 0f,   // inside-fog visibility means something only when there is a fog layer
            octas, baseM, Finite(w.GroundTemperatureC), Math.Clamp(Finite(w.RelativeHumidity), 0f, 1f));
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : 0f;

    /// <summary>A thin view over HROT's outline list, so the call sites read by index (no enumerator).</summary>
    private readonly struct PointsView(IReadOnlyList<Vector2> points)
    {
        public int Count => points.Count;

        public Vector2 this[int i] => points[i];
    }
}
