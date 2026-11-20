using Fdp.Core;
using Fdp.Toolkit.Terrain;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The damage of every building (DD-018 3.6a): HROT's runtime damage entities (<c>BuildingDamage</c>: wall breaches and collapse, <c>WindowDamage</c>: hit windows) united per
/// building the way HROT's own scenario save does it (<c>TerrainObjectsSection</c>: the building's key from <c>BuildingDamages.KeyOf</c>, a panel as its index within the building
/// through <c>BuildingPanelIndex.ToLocal</c>, distinct breaches, the harder-hit window wins). The adapter's entity query leaves these entities out on purpose, so they are read here.
/// Sent when a building's damage changes; a building whose damage is gone (a reload) is sent empty once. Main thread only.
/// </summary>
internal sealed class BuildingDamageGather
{
    private readonly Dictionary<string, HrotBuildingDamage> _sent = new(StringComparer.Ordinal);
    private readonly List<string> _gone = new();
    private EntityRepository? _queryWorld;
    private EntityQuery? _breachQuery, _windowQuery;

    public void Clear() => _sent.Clear();

    public void Gather(EntityRepository world, InMemoryInbound inbound)
    {
        Bind(world);
        Dictionary<string, SavedBuildingDamage>? united = null;
        if ((_breachQuery is not null || _windowQuery is not null) && world.HasSingletonManaged<TerrainWorld>() && world.GetSingletonManaged<TerrainWorld>() is { } terrain)
        {
            united = Unite(world, terrain);
        }

        if (united is not null)
        {
            foreach (var (key, saved) in united)
            {
                saved.Sort();
                var message = Map(key, saved);
                if (_sent.TryGetValue(key, out var previous) && Same(in previous, in message))
                {
                    continue;
                }

                _sent[key] = message;
                inbound.Enqueue(in message);
            }
        }

        _gone.Clear();
        foreach (string key in _sent.Keys)
        {
            if (united is null || !united.ContainsKey(key))
            {
                _gone.Add(key);
            }
        }

        foreach (string key in _gone)
        {
            _sent.Remove(key);
            inbound.Enqueue(new HrotBuildingDamage(key, false, [], []));
        }
    }

    private Dictionary<string, SavedBuildingDamage>? Unite(EntityRepository world, TerrainWorld terrain)
    {
        Dictionary<string, SavedBuildingDamage>? result = null;
        int groups = WallGroups.Count(terrain);

        SavedBuildingDamage Of(int building)
        {
            result ??= new Dictionary<string, SavedBuildingDamage>(StringComparer.Ordinal);
            string key = BuildingDamages.KeyOf(terrain, building);
            if (!result.TryGetValue(key, out var saved))
            {
                result[key] = saved = new SavedBuildingDamage();
            }

            return saved;
        }

        if (_breachQuery is not null)
        {
            foreach (var e in _breachQuery)
            {
                var d = world.GetComponentRO<BuildingDamage>(e);
                if ((uint)d.Building >= (uint)groups)
                {
                    continue;
                }

                var saved = Of(d.Building);
                for (int i = 0; i < d.Count && i < BuildingDamage.Capacity; i++)
                {
                    var b = d.Get(i);
                    int local = BuildingPanelIndex.ToLocal(terrain, b.Panel);
                    if (local >= 0 && WallGroups.Of(terrain, b.Panel) == d.Building)
                    {
                        saved.AddDistinct(b with { Panel = local });
                    }
                }

                if (d.Collapsed != 0)
                {
                    saved.Collapsed = true;
                }

                if (_windowQuery is not null && world.HasComponent<WindowDamage>(e))   // the same entity's windows
                {
                    AddWindows(saved, world.GetComponentRO<WindowDamage>(e), terrain, d.Building);
                }
            }
        }

        if (_windowQuery is not null)
        {
            foreach (var e in _windowQuery)
            {
                if (_breachQuery is not null && world.HasComponent<BuildingDamage>(e))
                {
                    continue;   // read with its breaches above
                }

                var w = world.GetComponentRO<WindowDamage>(e);
                if ((uint)w.Building < (uint)groups)
                {
                    AddWindows(Of(w.Building), w, terrain, w.Building);
                }
            }
        }

        // an entity that holds nothing is no damage
        if (result is not null)
        {
            foreach (string key in result.Where(p => p.Value.IsEmpty).Select(p => p.Key).ToArray())
            {
                result.Remove(key);
            }
        }

        return result;
    }

    private static void AddWindows(SavedBuildingDamage saved, in WindowDamage w, TerrainWorld terrain, int building)
    {
        for (int i = 0; i < w.Count && i < WindowDamage.Capacity; i++)
        {
            var x = w.Get(i);
            int local = BuildingPanelIndex.ToLocal(terrain, x.Panel);
            if (local >= 0 && WallGroups.Of(terrain, x.Panel) == building)
            {
                saved.AddWindow(x with { Panel = local });
            }
        }
    }

    private static HrotBuildingDamage Map(string key, SavedBuildingDamage s) =>
        new(key, s.Collapsed, [.. s.Breaches.Select(b => new HrotBreach(b.Panel, b.From, b.To, b.Bottom, b.Top, b.Kind.ToString()))],
            [.. s.Windows.Select(w => new HrotWindowDamage(w.Panel, w.Opening, w.Hits, w.U0, w.V0, w.U1, w.V1))]);

    private static bool Same(in HrotBuildingDamage a, in HrotBuildingDamage b) =>
        a.Collapsed == b.Collapsed && a.Breaches.SequenceEqual(b.Breaches) && a.Windows.SequenceEqual(b.Windows);

    private void Bind(EntityRepository world)
    {
        bool breaches = world.IsComponentTypeRegistered<BuildingDamage>(), windows = world.IsComponentTypeRegistered<WindowDamage>();
        if (ReferenceEquals(_queryWorld, world) && (_breachQuery is not null || !breaches) && (_windowQuery is not null || !windows))
        {
            return;
        }

        _queryWorld = world;
        _breachQuery = breaches ? world.Query().With<BuildingDamage>().Build() : null;
        _windowQuery = windows ? world.Query().With<WindowDamage>().Build() : null;
    }
}
