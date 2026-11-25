using System.Globalization;
using Fdp.Core;
using Fdp.Toolkit.Terrain;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The craters of every ground cell (DD-030): HROT's runtime <c>GroundDamage</c> entities - one per 64 m cell and deciding node, at most 16 craters each, a cell that overflows gets another entity -
/// united per cell, the way <see cref="BuildingDamageGather"/> unites a building's. The adapter's entity query leaves these entities out on purpose, so they are read here. Replica cells (another node's
/// craters, replicated to this one) are sent like any other HROT state. Sent when a cell's craters change; a cell whose craters are gone (a reload) is sent empty once. Main thread only.
/// <para>A crater is sent as HROT holds it: centre x, y, radius, depth, rim, birth time and kind. Its z is the BARE ground under the centre (<c>TerrainWorld.BareGroundHeightAt</c>, no crater in it: the
/// bowl is drawn from <c>Depth</c>), 0 when the world holds no terrain. One entity's craters keep HROT's slot order (the order they were made in); a cell held by several entities is sorted by
/// content (birth, then position and shape) so the order does not depend on the order the entities are visited, and a crater two entities both hold is sent once.</para>
/// </summary>
internal sealed class GroundDamageGather
{
    /// <summary>One cell's scratch: its craters of this gather, how many entities hold it.</summary>
    private sealed class Cell
    {
        public readonly List<Crater> Craters = new();
        public int Entities;
    }

    private readonly Dictionary<(int X, int Y), HrotGroundDamage> _sent = new();
    private readonly Dictionary<(int X, int Y), Cell> _cells = new();
    private readonly List<(int X, int Y)> _gone = new();
    private EntityRepository? _queryWorld;
    private EntityQuery? _query;

    public void Clear() => _sent.Clear();

    public void Gather(EntityRepository world, InMemoryInbound inbound)
    {
        Bind(world);
        foreach (var cell in _cells.Values)
        {
            cell.Craters.Clear();
            cell.Entities = 0;
        }

        if (_query is not null)
        {
            foreach (var e in _query)
            {
                ref readonly var d = ref world.GetComponentRO<GroundDamage>(e);
                int n = Math.Min((int)d.Count, GroundDamage.Capacity);
                if (n == 0)
                {
                    continue;
                }

                var key = (d.CellX, d.CellY);
                if (!_cells.TryGetValue(key, out var cell))
                {
                    _cells[key] = cell = new Cell();
                }

                cell.Entities++;
                for (int i = 0; i < n; i++)
                {
                    var c = d.Get(i);
                    if (!cell.Craters.Contains(c))
                    {
                        cell.Craters.Add(c);
                    }
                }
            }
        }

        TerrainWorld? terrain = world.HasSingletonManaged<TerrainWorld>() ? world.GetSingletonManaged<TerrainWorld>() : null;
        foreach (var (key, cell) in _cells)
        {
            if (cell.Entities == 0)
            {
                continue;
            }

            if (cell.Entities > 1)
            {
                cell.Craters.Sort(static (a, b) => Order(in a, in b));
            }

            if (_sent.TryGetValue(key, out var previous) && Matches(previous, cell.Craters, terrain))
            {
                continue;
            }

            var message = Map(key, cell.Craters, terrain);
            _sent[key] = message;
            inbound.Enqueue(in message);
        }

        _gone.Clear();
        foreach (var key in _sent.Keys)
        {
            if (!_cells.TryGetValue(key, out var cell) || cell.Entities == 0)
            {
                _gone.Add(key);
            }
        }

        foreach (var key in _gone)
        {
            _sent.Remove(key);
            inbound.Enqueue(new HrotGroundDamage(KeyOf(key), []));
        }
    }

    private static int Order(in Crater a, in Crater b)
    {
        int c = a.BornAt.Seconds.CompareTo(b.BornAt.Seconds);
        if (c != 0) return c;
        c = a.Centre.X.CompareTo(b.Centre.X);
        if (c != 0) return c;
        c = a.Centre.Y.CompareTo(b.Centre.Y);
        if (c != 0) return c;
        c = a.Radius.CompareTo(b.Radius);
        if (c != 0) return c;
        c = a.Depth.CompareTo(b.Depth);
        if (c != 0) return c;
        c = a.Rim.CompareTo(b.Rim);
        return c != 0 ? c : ((byte)a.Kind).CompareTo((byte)b.Kind);
    }

    internal static string KeyOf((int X, int Y) cell) => string.Create(CultureInfo.InvariantCulture, $"{cell.X},{cell.Y}");

    private static HrotCrater CraterOf(Crater c, TerrainWorld? terrain) =>
        new(new HrotVec3(c.Centre.X, c.Centre.Y, terrain?.BareGroundHeightAt(c.Centre.X, c.Centre.Y) ?? 0f), c.Radius, c.Depth, c.Rim, c.BornAt.Seconds, PivotEnum<HrotCraterKind>.Of(c.Kind));

    private static HrotGroundDamage Map((int X, int Y) cell, List<Crater> craters, TerrainWorld? terrain)
    {
        var rows = new HrotCrater[craters.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = CraterOf(craters[i], terrain);
        }

        return new HrotGroundDamage(KeyOf(cell), rows);
    }

    /// <summary>Whether what was sent is these craters - the rows <see cref="Map"/> builds, compared without building the message (a steady frame allocates nothing).</summary>
    private static bool Matches(in HrotGroundDamage sent, List<Crater> craters, TerrainWorld? terrain)
    {
        if (sent.Craters.Count != craters.Count)
        {
            return false;
        }

        for (int i = 0; i < craters.Count; i++)
        {
            if (!sent.Craters[i].Equals(CraterOf(craters[i], terrain)))
            {
                return false;
            }
        }

        return true;
    }

    private void Bind(EntityRepository world)
    {
        bool registered = world.IsComponentTypeRegistered<GroundDamage>();
        if (ReferenceEquals(_queryWorld, world) && (_query is not null || !registered))
        {
            return;
        }

        _queryWorld = world;
        _query = registered ? world.Query().With<GroundDamage>().Build() : null;
    }
}
