using System.Globalization;
using System.Numerics;
using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Perception.Signatures;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Terrain;
using Hrot.Core.Tkb;
using Hrot.Look.Effects;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

// DPS round 2 (DD-018 3.3, 3.4) and round 3 (DD-019 3.4): rounds with a look, the munition looks, flame jets, burning spots and ground heat marks. Every reading is HROT's own reader
// (MunitionTkbCatalog.VisualOf, FlameJets.TryGet, FireState, GroundHeatMarks); these gathers copy the answers into pivot messages and send a change only.
namespace SimNetGateway.HrotHost;

/// <summary>A round with a look (<c>LightweightRound</c>): sent when first seen and when it starts or stops lying or stops dead. Main thread only.</summary>
internal sealed class RoundGather
{
    private const float LyingSpeed = 0.2f;   // HROT's ProjectileLayer: a replica slower than this lies

    private readonly Dictionary<int, HrotRound> _sent = new();

    public void Forget(int id) => _sent.Remove(id);

    public void Clear() => _sent.Clear();

    public static bool IsRound(EntityRepository world, Entity e) =>
        world.IsComponentTypeRegistered<LightweightRound>() && world.HasComponent<LightweightRound>(e);

    public bool TryGather(EntityRepository world, ITkbDatabase? tkb, Entity e, int id, long munition, double simTime, out HrotRound message)
    {
        var position = (Vector3)world.GetComponentRO<SimTransform>(e).Position;
        int shooter = 0;
        var muzzle = position;
        float fuze = -1f;
        bool stopped = false, lying;
        if (world.IsComponentTypeRegistered<BallisticProjectile>() && world.HasComponent<BallisticProjectile>(e))
        {
            // the owner of the combat: its own round record (as ProjectileLayer reads it)
            ref readonly var round = ref world.GetComponentRO<BallisticProjectile>(e);
            shooter = LookIds.NetworkIdOf(world, round.Shooter);
            muzzle = (Vector3)round.Muzzle;
            stopped = round.StoppedTick != 0;
            lying = (round.Warhead & WarheadRound.Landed) != 0 || stopped;
            if ((round.Warhead & WarheadRound.TimeFuze) != 0)
            {
                fuze = round.FuzeRemaining;
            }
        }
        else
        {
            // a replicated copy: no round record, the muzzle is unknown, a round that does not move lies
            var velocity = world.IsComponentTypeRegistered<SimVelocity>() && world.HasComponent<SimVelocity>(e) ? world.GetComponentRO<SimVelocity>(e).Linear : Vector3.Zero;
            lying = velocity.Length() < LyingSpeed;
        }

        // fixed at the first sight, so a later change of lying or stopped keeps the launch time
        bool known = _sent.TryGetValue(id, out var previous);
        double launched = known ? previous.LaunchedAt : LaunchTime(world, e, simTime);
        message = new HrotRound(id, munition, shooter, new HrotVec3(muzzle.X, muzzle.Y, muzzle.Z), lying, fuze, stopped, launched, RoundLook.BurnOf(tkb, munition));
        if (known && previous.Lying == lying && previous.Stopped == stopped && previous.MunitionTkb == munition)
        {
            return false;
        }

        _sent[id] = message;
        return true;
    }

    /// <summary>
    /// The sim time the round was fired: HROT's own <c>LightweightRound.LaunchedAt</c> (set where the round is made, sent with its master, so a replica and a recording carry it).
    /// 0 = unknown (a sender or a recording from before HROT widened the marker): the first-seen time.
    /// </summary>
    private static double LaunchTime(EntityRepository world, Entity e, double simTime)
    {
        double launched = world.GetComponentRO<LightweightRound>(e).LaunchedAt.Seconds;
        return launched > 0.0 ? launched : simTime;
    }
}

/// <summary>The resolved look of a munition type (<c>MunitionTkbCatalog.VisualOf</c>, built-in looks included), once per type per runtime. Main thread only.</summary>
internal sealed class MunitionLookGather
{
    private const uint Opaque = 0xFF;

    private readonly HashSet<long> _sent = new();

    public void Clear() => _sent.Clear();

    public bool TryGather(ITkbDatabase? tkb, long munition, out HrotMunitionLook message)
    {
        message = default;
        if (munition == 0 || _sent.Contains(munition) || MunitionTkbCatalog.VisualOf(tkb, munition) is not { } look)
        {
            return false;   // no look (ball ammo) is not remembered: the TKB may gain one before the next shot
        }

        _sent.Add(munition);
        message = new HrotMunitionLook(munition, look.Shape.ToString(), look.Length, look.Diameter, Rgba(look.ColorHex, 0x4B5320FF), look.SmokeTrail, look.Tracer,
            Rgba(look.TracerColorHex, 0xFF9A30FF), look.Sticks, look.Bolt);
        return true;
    }

    /// <summary>HROT's "#RRGGBB" (or "#RRGGBBAA") as 0xRRGGBBAA.</summary>
    internal static uint Rgba(string? hex, uint fallback)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return fallback;
        }

        var digits = hex[0] == '#' ? hex.AsSpan(1) : hex.AsSpan();
        if (digits.Length == 6 && uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            return (rgb << 8) | Opaque;
        }

        return digits.Length == 8 && uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgba) ? rgba : fallback;
    }
}

/// <summary>The lit flame jets (<c>FlameJets.TryGet</c> per unit): written while lit and when they change, removed when they go out. Main thread only.</summary>
internal sealed class FlameJetGather
{
    private const double AimEpsilonM = 0.25;

    private sealed class Book
    {
        public HrotFlameJet Sent;
        public bool Seen;
    }

    private readonly Dictionary<(int Id, int Mount), Book> _book = new();
    private readonly List<(int Id, int Mount)> _gone = new();
    private EntityRepository? _queryWorld;
    private EntityQuery? _query;

    public void Clear() => _book.Clear();

    public void Gather(EntityRepository world, InMemoryInbound inbound, double simTime)
    {
        Bind(world);
        foreach (var b in _book.Values)
        {
            b.Seen = false;
        }

        if (_query is not null)
        {
            foreach (var e in _query)
            {
                int id = world.GetComponentRO<NetworkIdentity>(e).Value;
                if (id == 0 || !FlameJets.TryGet(world, e, simTime, out var jet))
                {
                    continue;
                }

                var aim = (Vector3)jet.AimPoint;
                var message = new HrotFlameJet(id, jet.Mount, new HrotVec3(aim.X, aim.Y, aim.Z), LookIds.NetworkIdOf(world, jet.Target), jet.RangeM, jet.HalfAngleRad, jet.Intensity,
                    jet.BurstStartedAt, jet.BurnSeconds);
                var key = (id, jet.Mount);
                if (_book.TryGetValue(key, out var book))
                {
                    book.Seen = true;
                    if (!Differs(in book.Sent, in message))
                    {
                        continue;
                    }

                    book.Sent = message;
                }
                else
                {
                    _book[key] = new Book { Sent = message, Seen = true };
                }

                inbound.Enqueue(in message);
            }
        }

        _gone.Clear();
        foreach (var (key, b) in _book)
        {
            if (!b.Seen)
            {
                _gone.Add(key);
            }
        }

        foreach (var key in _gone)
        {
            _book.Remove(key);
            inbound.Enqueue(new HrotFlameJetRemoved(key.Id, key.Mount));
        }
    }

    private static bool Differs(in HrotFlameJet a, in HrotFlameJet b) =>
        a.TargetId != b.TargetId || a.RangeM != b.RangeM || a.HalfAngleRad != b.HalfAngleRad || a.Intensity != b.Intensity || a.BurstStartedAt != b.BurstStartedAt
        || a.BurnSeconds != b.BurnSeconds || Dist(a.AimPoint, b.AimPoint) > AimEpsilonM;

    internal static double Dist(HrotVec3 a, HrotVec3 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void Bind(EntityRepository world)
    {
        if (_query is not null && ReferenceEquals(_queryWorld, world))
        {
            return;
        }

        _queryWorld = world;
        _query = world.IsComponentTypeRegistered<WeaponPosture>() && world.IsComponentTypeRegistered<WeaponAmmo>() && world.IsComponentTypeRegistered<NetworkIdentity>()
            && world.IsComponentTypeRegistered<ReplicationRoute>()
            ? world.Query().With<WeaponPosture>().With<WeaponAmmo>().With<SimTransform>().With<NetworkIdentity>().Without<ReplicationRoute>().Build()
            : null;
    }
}

/// <summary>
/// The burning spots of every holder (<c>FireState</c>: vehicle, building record, terrain cell record) in local ENU metres, the holder's transform applied as HROT's
/// <c>EffectLayer.HolderPoint</c> does; only spots that burn now. Sent when the set changes, an empty set when none burns any more. Main thread only.
/// </summary>
internal sealed class FireSpotGather
{
    private const double PositionEpsilonM = 0.1;

    private sealed class Book
    {
        public HrotFireSpot[] Sent = [];
        public bool Seen;
    }

    private readonly Dictionary<int, Book> _book = new();
    private readonly List<HrotFireSpot> _spots = new();
    private readonly List<int> _gone = new();
    private EntityRepository? _queryWorld;
    private EntityQuery? _query;

    public void Clear() => _book.Clear();

    public void Gather(EntityRepository world, InMemoryInbound inbound, double simTime)
    {
        Bind(world);
        foreach (var b in _book.Values)
        {
            b.Seen = false;
        }

        if (_query is not null)
        {
            foreach (var e in _query)
            {
                int id = world.GetComponentRO<NetworkIdentity>(e).Value;
                if (id == 0)
                {
                    continue;
                }

                Read(world, e, simTime);
                if (_spots.Count == 0)
                {
                    continue;   // none burns: unseen, removed below
                }

                if (_book.TryGetValue(id, out var book))
                {
                    book.Seen = true;
                    if (!Differs(book.Sent, _spots))
                    {
                        continue;
                    }
                }
                else
                {
                    _book[id] = book = new Book { Seen = true };
                }

                book.Sent = _spots.ToArray();
                inbound.Enqueue(new HrotFireSpots(id, book.Sent));
            }
        }

        _gone.Clear();
        foreach (var (id, b) in _book)
        {
            if (!b.Seen)
            {
                _gone.Add(id);
            }
        }

        foreach (int id in _gone)
        {
            _book.Remove(id);
            inbound.Enqueue(new HrotFireSpots(id, []));
        }
    }

    private void Read(EntityRepository world, Entity holder, double now)
    {
        _spots.Clear();
        ref readonly var fire = ref world.GetComponentRO<FireState>(holder);
        int n = Math.Min((int)fire.Count, FireState.Capacity);
        for (int i = 0; i < n; i++)
        {
            var spot = fire.Get(i);
            if (!spot.IsBurning(now))
            {
                continue;
            }

            var at = HolderPoint(world, holder, spot.X, spot.Y, spot.Z);
            _spots.Add(new HrotFireSpot(new HrotVec3(at.X, at.Y, at.Z), spot.Radius, spot.Intensity, spot.Since.Seconds, spot.Seconds));
        }
    }

    /// <summary>A holder's point in the world: a vehicle's spots are in its frame (rotated, then offset); a holder with no transform: the point as it is (HROT's rule).</summary>
    internal static Vector3 HolderPoint(EntityRepository world, Entity holder, float x, float y, float z)
    {
        var p = new Vector3(x, y, z);
        if (!world.HasComponent<SimTransform>(holder))
        {
            return p;
        }

        ref readonly var t = ref world.GetComponentRO<SimTransform>(holder);
        Quaternion q = t.Rotation;
        if (q.LengthSquared() < 0.5f)
        {
            q = Quaternion.Identity;
        }

        return (Vector3)t.Position + Vector3.Transform(p, q);
    }

    private static bool Differs(HrotFireSpot[] sent, List<HrotFireSpot> now)
    {
        if (sent.Length != now.Count)
        {
            return true;
        }

        for (int i = 0; i < sent.Length; i++)
        {
            var a = sent[i];
            var b = now[i];
            if (a.Radius != b.Radius || a.Intensity != b.Intensity || a.Since != b.Since || a.Seconds != b.Seconds || FlameJetGather.Dist(a.Position, b.Position) > PositionEpsilonM)
            {
                return true;
            }
        }

        return false;
    }

    private void Bind(EntityRepository world)
    {
        if (_query is not null && ReferenceEquals(_queryWorld, world))
        {
            return;
        }

        _queryWorld = world;
        _query = world.IsComponentTypeRegistered<FireState>() && world.IsComponentTypeRegistered<NetworkIdentity>() && world.IsComponentTypeRegistered<ReplicationRoute>()
            ? world.Query().With<FireState>().With<NetworkIdentity>().Without<ReplicationRoute>().Build()
            : null;
    }
}

/// <summary>
/// The heat marks of every terrain cell record (<c>GroundHeatMarks</c>). Sent when the set changes, an empty set when a cell holds none any more. The mark's z is the ground height under its centre (HROT's own world query, <c>WorldQuery.GroundHeightAt</c>), 0 when the world holds no terrain. Main thread only.
/// </summary>
internal sealed class GroundMarkGather
{
    private sealed class Book
    {
        public HrotGroundMark[] Sent = [];
        public bool Seen;
    }

    private readonly Dictionary<int, Book> _book = new();
    private readonly List<HrotGroundMark> _marks = new();
    private readonly List<int> _gone = new();
    private EntityRepository? _queryWorld;
    private EntityQuery? _query;

    public void Clear() => _book.Clear();

    public void Gather(EntityRepository world, InMemoryInbound inbound)
    {
        Bind(world);
        Fdp.Toolkit.World.IWorldQuery? height = null;
        foreach (var b in _book.Values)
        {
            b.Seen = false;
        }

        if (_query is not null)
        {
            foreach (var e in _query)
            {
                int id = world.GetComponentRO<NetworkIdentity>(e).Value;
                if (id == 0)
                {
                    continue;
                }

                height ??= Fdp.Toolkit.World.WorldQuery.Of(world);
                Read(world, e, height);
                if (_marks.Count == 0)
                {
                    continue;
                }

                if (_book.TryGetValue(id, out var book))
                {
                    book.Seen = true;
                    if (Same(book.Sent, _marks))
                    {
                        continue;
                    }
                }
                else
                {
                    _book[id] = book = new Book { Seen = true };
                }

                book.Sent = _marks.ToArray();
                inbound.Enqueue(new HrotGroundMarks(id, book.Sent));
            }
        }

        _gone.Clear();
        foreach (var (id, b) in _book)
        {
            if (!b.Seen)
            {
                _gone.Add(id);
            }
        }

        foreach (int id in _gone)
        {
            _book.Remove(id);
            inbound.Enqueue(new HrotGroundMarks(id, []));
        }
    }

    private static bool Same(HrotGroundMark[] sent, List<HrotGroundMark> now)
    {
        if (sent.Length != now.Count)
        {
            return false;
        }

        for (int i = 0; i < sent.Length; i++)
        {
            if (!sent[i].Equals(now[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void Read(EntityRepository world, Entity cell, Fdp.Toolkit.World.IWorldQuery? height)
    {
        _marks.Clear();
        ref readonly var marks = ref world.GetComponentRO<GroundHeatMarks>(cell);
        int n = Math.Min((int)marks.Count, GroundHeatMarks.Capacity);
        for (int i = 0; i < n; i++)
        {
            var m = marks.Get(i);
            if (m.Kind == HeatMarkKind.NoMark)
            {
                continue;
            }

            _marks.Add(new HrotGroundMark(m.Kind.ToString(), new HrotVec3(m.At.X, m.At.Y, height?.GroundHeightAt(m.At.X, m.At.Y) ?? 0f), m.Heading, m.Length, m.Width, m.HotEnd.ToString(), m.StandSince.Seconds, m.LeftAt.Seconds));
        }
    }

    private void Bind(EntityRepository world)
    {
        if (_query is not null && ReferenceEquals(_queryWorld, world))
        {
            return;
        }

        _queryWorld = world;
        _query = world.IsComponentTypeRegistered<GroundHeatMarks>() && world.IsComponentTypeRegistered<NetworkIdentity>() && world.IsComponentTypeRegistered<ReplicationRoute>()
            ? world.Query().With<GroundHeatMarks>().With<NetworkIdentity>().Without<ReplicationRoute>().Build()
            : null;
    }
}
