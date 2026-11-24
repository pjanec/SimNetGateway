using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Perception.Signatures;
using Fdp.Toolkit.Terrain;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The damage of every building (DD-018 3.6a): HROT's runtime damage entities (<c>BuildingDamage</c>: wall breaches and collapse, <c>WindowDamage</c>: hit windows) united per
/// building the way HROT's own scenario save does it (<c>TerrainObjectsSection</c>: the building's key from <c>BuildingDamages.KeyOf</c>, a panel as its index within the building
/// through <c>BuildingPanelIndex.ToLocal</c>, distinct breaches, the harder-hit window wins). The adapter's entity query leaves these entities out on purpose, so they are read here.
/// Sent when a building's damage changes; a building whose damage is gone (a reload) is sent empty once. Main thread only.
/// <para>DD-030: HROT's own record of WHEN and WHERE FROM travels with what it stamps (<c>MadeAt</c> and the blast point per breach, <c>HitAt</c> per window, <c>CollapsedAt</c>) - copied, the
/// gateway stamps nothing. <see cref="SavedBuildingDamage"/> unites the shapes but holds no stamps, so a parallel <see cref="Stamps"/> per building follows the same union rules: a breach two
/// damage entities both hold keeps the later time, a window keeps the stamp of the entry with more hits, the collapse time is the latest.</para>
/// <para>A steady frame (nothing changed) allocates nothing: the per-building scratch (<see cref="Slot"/>) and the keys are kept and reused, the union is compared with what was sent field by field,
/// and a message is built only when something differs (T-074).</para>
/// </summary>
internal sealed class BuildingDamageGather
{
    /// <summary>One building's scratch: the united damage of this gather, its stamps, and whether an entity touched it.</summary>
    private sealed class Slot(string key)
    {
        public readonly string Key = key;
        public readonly SavedBuildingDamage Saved = new();
        public readonly Stamps Stamps = new();
        public bool Touched;

        // DD-033: HROT's fire on the building (FireState spots with a duration): the earliest start, the latest end, and the one spot's own duration when only one burned
        public double FireSince, FireEnd;
        public float FireFirstSeconds;
        public int FireSpots;

        /// <summary>The fire as sent: since 0 / seconds 0 = never burned; one spot = its own start and duration; several = earliest start to latest end.</summary>
        public double SentFireSince => FireSpots > 0 ? FireSince : 0d;

        public float SentFireSeconds => FireSpots == 0 ? 0f : FireSpots == 1 ? FireFirstSeconds : (float)(FireEnd - FireSince);

        public bool HasFire => FireSpots > 0;

        public void AddFire(in FireState fire)
        {
            int n = Math.Min((int)fire.Count, FireState.Capacity);
            for (int i = 0; i < n; i++)
            {
                var spot = fire.Get(i);
                if (spot.Seconds <= 0f)
                {
                    continue;   // a slot that never burned
                }

                double since = spot.Since.Seconds, end = since + spot.Seconds;
                if (FireSpots == 0 || since < FireSince)
                {
                    FireSince = since;
                    FireFirstSeconds = spot.Seconds;
                }

                FireEnd = FireSpots == 0 ? end : Math.Max(FireEnd, end);
                FireSpots++;
            }
        }

        public void Reset()
        {
            Saved.Breaches.Clear();
            Saved.Windows.Clear();
            Saved.Collapsed = false;
            Stamps.Clear();
            Touched = false;
            FireSince = FireEnd = 0d;
            FireFirstSeconds = 0f;
            FireSpots = 0;
        }
    }

    private readonly Dictionary<string, HrotBuildingDamage> _sent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly List<string> _gone = new();
    private EntityRepository? _queryWorld;
    private EntityQuery? _breachQuery, _windowQuery;
    private TerrainWorld? _keysFor;
    private Slot?[] _byGroup = [];
    private bool _fireRegistered;

    public void Clear() => _sent.Clear();

    public void Gather(EntityRepository world, InMemoryInbound inbound)
    {
        Bind(world);
        foreach (var slot in _slots.Values)
        {
            slot.Reset();
        }

        if ((_breachQuery is not null || _windowQuery is not null) && world.HasSingletonManaged<TerrainWorld>() && world.GetSingletonManaged<TerrainWorld>() is { } terrain)
        {
            Unite(world, terrain);
        }

        foreach (var slot in _slots.Values)
        {
            if (!slot.Touched || (slot.Saved.IsEmpty && !slot.HasFire))   // an entity that holds nothing is no damage
            {
                continue;
            }

            slot.Saved.Sort();
            if (_sent.TryGetValue(slot.Key, out var previous) && Matches(in previous, slot))
            {
                continue;
            }

            var message = Map(slot);
            _sent[slot.Key] = message;
            inbound.Enqueue(in message);
        }

        _gone.Clear();
        foreach (string key in _sent.Keys)
        {
            if (!_slots.TryGetValue(key, out var slot) || !slot.Touched || (slot.Saved.IsEmpty && !slot.HasFire))
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

    private void Unite(EntityRepository world, TerrainWorld terrain)
    {
        int groups = WallGroups.Count(terrain);
        if (!ReferenceEquals(_keysFor, terrain) || _byGroup.Length != groups)
        {
            // a new terrain: its building keys, once (BuildingDamages.KeyOf builds a string)
            _keysFor = terrain;
            _byGroup = new Slot?[groups];
        }

        Slot Of(int building)
        {
            var slot = _byGroup[building];
            if (slot is null)
            {
                string key = BuildingDamages.KeyOf(terrain, building);
                if (!_slots.TryGetValue(key, out slot))
                {
                    _slots[key] = slot = new Slot(key);
                }

                _byGroup[building] = slot;
            }

            slot.Touched = true;
            return slot;
        }

        if (_breachQuery is not null)
        {
            foreach (var e in _breachQuery)
            {
                ref readonly var d = ref world.GetComponentRO<BuildingDamage>(e);
                if ((uint)d.Building >= (uint)groups)
                {
                    continue;
                }

                var slot = Of(d.Building);
                for (int i = 0; i < d.Count && i < BuildingDamage.Capacity; i++)
                {
                    var b = d.Get(i);
                    int local = BuildingPanelIndex.ToLocal(terrain, b.Panel);
                    if (local >= 0 && WallGroups.Of(terrain, b.Panel) == d.Building)
                    {
                        var within = b with { Panel = local };
                        slot.Saved.AddDistinct(within);
                        slot.Stamps.AddBreach(within, d.MadeAtOf(i).Seconds, d.BlastOf(i));
                    }
                }

                if (_fireRegistered && world.HasComponent<FireState>(e))
                {
                    slot.AddFire(in world.GetComponentRO<FireState>(e));   // DD-033: the same record's fire
                }

                if (d.Collapsed != 0)
                {
                    slot.Saved.Collapsed = true;
                    slot.Stamps.CollapsedAt = Math.Max(slot.Stamps.CollapsedAt, d.CollapsedAt);
                }

                if (_windowQuery is not null && world.HasComponent<WindowDamage>(e))   // the same entity's windows
                {
                    AddWindows(slot, in world.GetComponentRO<WindowDamage>(e), terrain, d.Building);
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

                ref readonly var w = ref world.GetComponentRO<WindowDamage>(e);
                if ((uint)w.Building < (uint)groups)
                {
                    AddWindows(Of(w.Building), in w, terrain, w.Building);
                }
            }
        }
    }

    private static void AddWindows(Slot slot, in WindowDamage w, TerrainWorld terrain, int building)
    {
        for (int i = 0; i < w.Count && i < WindowDamage.Capacity; i++)
        {
            var x = w.Get(i);
            int local = BuildingPanelIndex.ToLocal(terrain, x.Panel);
            if (local >= 0 && WallGroups.Of(terrain, x.Panel) == building)
            {
                var within = x with { Panel = local };
                slot.Saved.AddWindow(within);
                slot.Stamps.AddWindow(within, w.HitAtOf(i).Seconds);
            }
        }
    }

    private static HrotBreach BreachOf(WallBreach b, Stamps t)
    {
        var (at, blast) = t.Breach(b);
        return new HrotBreach(b.Panel, b.From, b.To, b.Bottom, b.Top, KindName(b.Kind), at, new HrotVec3(blast.X, blast.Y, blast.Z));
    }

    /// <summary>The kind's NAME without boxing it (<c>Enum.ToString()</c> boxes: a steady frame must allocate nothing); a kind HROT appends later is named the slow way.</summary>
    private static string KindName(BreachKind kind) => kind switch { BreachKind.Hole => nameof(BreachKind.Hole), BreachKind.Passage => nameof(BreachKind.Passage), _ => kind.ToString() };

    private static HrotWindowDamage WindowOf(WindowEntry w, Stamps t) => new(w.Panel, w.Opening, w.Hits, w.U0, w.V0, w.U1, w.V1, t.HitAt(w));

    private static HrotBuildingDamage Map(Slot slot)
    {
        var saved = slot.Saved;
        var breaches = new HrotBreach[saved.Breaches.Count];
        for (int i = 0; i < breaches.Length; i++)
        {
            breaches[i] = BreachOf(saved.Breaches[i], slot.Stamps);
        }

        var windows = new HrotWindowDamage[saved.Windows.Count];
        for (int i = 0; i < windows.Length; i++)
        {
            windows[i] = WindowOf(saved.Windows[i], slot.Stamps);
        }

        return new HrotBuildingDamage(slot.Key, saved.Collapsed, breaches, windows, saved.Collapsed ? slot.Stamps.CollapsedAt : 0d, slot.SentFireSince, slot.SentFireSeconds);
    }

    /// <summary>Whether what was sent is what this gather united - the same fields <see cref="Map"/> builds, compared without building them.</summary>
    private static bool Matches(in HrotBuildingDamage p, Slot slot)
    {
        var saved = slot.Saved;
        if (p.Collapsed != saved.Collapsed || p.CollapsedAt != (saved.Collapsed ? slot.Stamps.CollapsedAt : 0d) || p.FireSince != slot.SentFireSince || p.FireSeconds != slot.SentFireSeconds || p.Breaches.Count != saved.Breaches.Count || p.Windows.Count != saved.Windows.Count)
        {
            return false;
        }

        for (int i = 0; i < saved.Breaches.Count; i++)
        {
            if (!p.Breaches[i].Equals(BreachOf(saved.Breaches[i], slot.Stamps)))
            {
                return false;
            }
        }

        for (int i = 0; i < saved.Windows.Count; i++)
        {
            if (!p.Windows[i].Equals(WindowOf(saved.Windows[i], slot.Stamps)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>HROT's stamps of one building's merged damage, keyed by what they stamp (the breach, or the window's panel and opening within the building).</summary>
    private sealed class Stamps
    {
        private readonly Dictionary<WallBreach, (double At, Vector3 Blast)> _breaches = new();
        private readonly Dictionary<(int Panel, int Opening), (byte Hits, double At)> _windows = new();

        public double CollapsedAt;

        public void Clear()
        {
            _breaches.Clear();
            _windows.Clear();
            CollapsedAt = 0d;
        }

        /// <summary>The same breach in two damage entities keeps the later stamp (the one a widening moved).</summary>
        public void AddBreach(in WallBreach b, double at, Vector3 blast)
        {
            if (!_breaches.TryGetValue(b, out var have) || at > have.At)
            {
                _breaches[b] = (at, blast);
            }
        }

        /// <summary>The stamp follows the entry <see cref="SavedBuildingDamage.AddWindow"/> keeps: the one with more hits, the first of equals.</summary>
        public void AddWindow(in WindowEntry w, double at)
        {
            var key = (w.Panel, (int)w.Opening);
            if (!_windows.TryGetValue(key, out var have) || w.Hits > have.Hits)
            {
                _windows[key] = (w.Hits, at);
            }
        }

        public (double At, Vector3 Blast) Breach(WallBreach b) => _breaches.TryGetValue(b, out var v) ? v : default;

        public double HitAt(WindowEntry w) => _windows.TryGetValue((w.Panel, (int)w.Opening), out var v) ? v.At : 0d;
    }

    private void Bind(EntityRepository world)
    {
        bool breaches = world.IsComponentTypeRegistered<BuildingDamage>(), windows = world.IsComponentTypeRegistered<WindowDamage>(), fire = world.IsComponentTypeRegistered<FireState>();
        if (ReferenceEquals(_queryWorld, world) && (_breachQuery is not null || !breaches) && (_windowQuery is not null || !windows) && _fireRegistered == fire)
        {
            return;
        }

        _queryWorld = world;
        _fireRegistered = fire;
        _breachQuery = breaches ? world.Query().With<BuildingDamage>().Build() : null;
        _windowQuery = windows ? world.Query().With<WindowDamage>().Build() : null;
    }
}
