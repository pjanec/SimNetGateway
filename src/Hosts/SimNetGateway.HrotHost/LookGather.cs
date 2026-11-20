using System.Numerics;
using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Behavior;
using Fdp.Toolkit.Combat.Contracts;
using Fdp.Toolkit.Combat.Events;
using Fdp.Toolkit.Physics.BodyParts;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Time;
using Hrot.Look;
using Hrot.Look.Effects;
using Hrot.Look.People;
using Hrot.Look.Vehicles;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

// HROT's look library (Hrot.Look) decides how the simulation looks; these gathers only call it and copy its answers into pivot messages (DD-017 3.10). No
// rule of HROT is copied here.
namespace SimNetGateway.HrotHost;

/// <summary>
/// Effects from STATE (HROT R-11007, DD-019 9 Round 6): every effect entity HROT's spawner made (live) or a recording holds (replay), rebuilt into an <see cref="EffectSpec"/> by
/// <c>EffectRules.SpecOf</c> and sent once, keyed on the entity and its birth time (never re-sent on a later frame). Entering or leaving a replay, a seek or any jump of the clock
/// only takes what is there as already sent. One path for live and replay: it needs the node to run HROT's effect spawner (every map host does).
/// </summary>
internal sealed class EffectGather
{
    private readonly HashSet<(Entity Entity, double BornAt)> _seen = new();
    private readonly HashSet<(Entity Entity, double BornAt)> _present = new();
    private EntityQuery? _query;
    private EntityRepository? _queryWorld;
    private bool _synced;
    private bool _replaying;
    private double _time;

    /// <summary>Effects sent since the last <see cref="Clear"/>.</summary>
    public long Sent { get; private set; }

    public void Clear()
    {
        _seen.Clear();
        _present.Clear();
        _synced = false;
        _query = null;
        _queryWorld = null;
        Sent = 0;
    }

    public int Gather(EntityRepository world, ITkbDatabase? tkb, InMemoryInbound inbound, double simNow, bool replaying, double jumpSeconds)
    {
        if (!world.IsComponentTypeRegistered<Lifespan>() || !world.IsComponentTypeRegistered<TkbIdentity>() || !world.IsComponentTypeRegistered<SimTransform>())
        {
            return 0;
        }

        if (!ReferenceEquals(_queryWorld, world))
        {
            _queryWorld = world;
            _query = world.Query().With<Lifespan>().With<TkbIdentity>().With<SimTransform>().Build();
        }

        bool baseline = !_synced || replaying != _replaying || simNow < _time - 1e-6 || simNow - _time > jumpSeconds;
        _synced = true;
        _replaying = replaying;
        _time = simNow;
        _present.Clear();
        int sent = 0;
        foreach (var e in _query!)
        {
            var key = (e, (double)world.GetComponentRO<Lifespan>(e).BornAtSimSeconds);
            _present.Add(key);
            if (!_seen.Add(key) || baseline || !EffectRules.SpecOf(world, e, tkb, out var spec))
            {
                continue;
            }

            Publish(world, in spec, inbound);
            sent++;
        }

        _seen.IntersectWith(_present);
        Sent += sent;
        return sent;
    }

    private static void Publish(EntityRepository world, in EffectSpec s, InMemoryInbound inbound)
    {
        int anchor = 0;
        byte mount = 0;
        if (s.Anchor is { } a)
        {
            anchor = LookIds.NetworkIdOf(world, a.Shooter);
            mount = (byte)a.WeaponIndex;
        }

        // HROT's enumerations go as their names (the plugins never decode HROT's numbers)
        var msg = new HrotEffect(s.Kind.ToString(), s.SizeClass.ToString(), s.Surface.ToString(), (ulong)s.MunitionDis.Value, s.EffectType, new HrotVec3(s.At.X, s.At.Y, s.At.Z),
            new HrotQuat(s.Rotation.X, s.Rotation.Y, s.Rotation.Z, s.Rotation.W), anchor, mount, s.Lifetime, s.Size, s.StartedAt);
        inbound.Enqueue(in msg);
    }
}

/// <summary>A person's outfit: <c>PersonLook.Of</c> when something it depends on may have changed, sent once and again only when the outfit differs.</summary>
internal sealed class AppearanceGather
{
    private sealed class Book
    {
        public long Type;
        public ForceId Side;
        public uint Colour;
        public bool HasColour;
        public PersonAppearance Last;
        public bool HasSent;
    }

    private readonly Dictionary<int, Book> _book = new();

    public void Forget(int id) => _book.Remove(id);

    public void Clear() => _book.Clear();

    public bool TryGather(EntityRepository world, ITkbDatabase? tkb, Entity e, int id, out HrotPersonAppearance message)
    {
        message = default;
        if (tkb is null || !world.IsComponentTypeRegistered<TkbIdentity>() || !world.HasComponent<TkbIdentity>(e))
        {
            return false;
        }

        long type = world.GetComponentRO<TkbIdentity>(e).TkbType;
        var side = PersonLook.SideOf(world, e);
        var colour = PersonLook.ColourOf(world, e, default);
        uint packed = Pack(colour);
        bool hasColour = PersonLook.HasEntityColour(world, e);
        if (_book.TryGetValue(id, out var book) && book.Type == type && book.Side == side && book.Colour == packed && book.HasColour == hasColour)
        {
            return false;   // nothing the outfit is drawn from changed
        }

        book ??= _book[id] = new Book();
        book.Type = type;
        book.Side = side;
        book.Colour = packed;
        book.HasColour = hasColour;
        if (PersonLook.Of(world, e, tkb) is not { } look)
        {
            return false;
        }

        if (book.HasSent && book.Last.Equals(look))
        {
            return false;   // HROT's outfit is the same: nothing to name, nothing to send
        }

        book.Last = look;
        book.HasSent = true;
        message = Map(id, in look);
        return true;
    }

    private static HrotPersonAppearance Map(int id, in PersonAppearance a)
    {
        var o = a.Outfit;
        byte side = a.Side switch { ForceId.Friend => 1, ForceId.Hostile => 2, _ => 3 };
        return new HrotPersonAppearance(id, a.Seed, side,
            Pack(o.Top), Pack(o.Sleeve), Pack(o.Bottom), Pack(o.Shin), Pack(o.Skin), Pack(o.Hair), Pack(o.Tie), Pack(o.HeadCloth), Pack(o.OverColour), Pack(o.ExtraColour),
            o.HairStyle.ToString(), o.Legwear.ToString(), o.Camo.ToString(), o.TopKind.ToString(), o.HeadKind.ToString(), o.OverKind.ToString(), o.ExtraKind.ToString(),
            o.Face.Beard.ToString(),
            o.Suit, o.Child, o.Soldier, o.Police, o.Riot, o.Woman, o.Dressed, o.FacePaint,
            o.Face.EyeSpacing, o.Face.MouthHalfWidth, o.Face.BrowHalfHeight, Pack(o.Face.EyeColour), Pack(o.Face.Lips));
    }

    private static uint Pack(Rgba c) => ((uint)c.R << 24) | ((uint)c.G << 16) | ((uint)c.B << 8) | c.A;
}

/// <summary>A vehicle's look: part conditions, kill kind, flames and smoke, openings, lights (with one <see cref="BrakeLights"/> tracker); sent when it changes.</summary>
internal sealed class VehicleLookGather
{
    private const float ProgressSteps = 50f;

    private readonly Dictionary<int, HrotVehicleLook> _sent = new();
    private readonly BrakeLights _brakes = new();
    private readonly List<(byte Part, byte State)> _parts = new();
    private readonly List<(byte Opening, float Progress)> _openings = new();
    private readonly List<byte> _crew = new();
    private readonly List<HrotOpeningMotion> _motions = new();

    public void Forget(int id) => _sent.Remove(id);

    public void Clear() => _sent.Clear();

    public bool TryGather(EntityRepository world, ITkbDatabase? tkb, Entity e, int id, double now, out HrotVehicleLook message)
    {
        _parts.Clear();
        for (byte part = 0; part < PartDamage.MaxParts; part++)
        {
            var state = VehicleLook.PartState(world, e, part);
            if (state != PartLook.Intact)
            {
                _parts.Add((part, (byte)state));
            }
        }

        _openings.Clear();
        _crew.Clear();
        _motions.Clear();
        if (tkb is not null && world.IsComponentTypeRegistered<TkbIdentity>() && world.HasComponent<TkbIdentity>(e)
            && tkb.TryGetByType(world.GetComponentRO<TkbIdentity>(e).TkbType, out var template))
        {
            var catalog = VehicleOpeningsCatalog.Of(template);
            for (int slot = 0; slot < catalog.Length; slot++)
            {
                var opening = catalog[slot];
                float progress = MathF.Round(VehicleLook.OpeningProgress(world, e, opening.Id, now) * ProgressSteps) / ProgressSteps;
                _openings.Add((opening.Id, progress));
                _motions.Add(MotionOf(world, e, slot, opening));
                if (VehicleLook.CrewExposed(world, e, opening.Id))
                {
                    _crew.Add(opening.Id);
                }
            }
        }

        string destruction = VehicleLook.Destruction(world, e).ToString();   // HROT's destruction kind by name
        bool turret = VehicleLook.TurretThrown(world, e), burning = VehicleLook.Burning(world, e, now), smoking = VehicleLook.Smoking(world, e, now);
        uint lights = VehicleLook.Lights(world, e, now, _brakes);
        bool flail = Hrot.UI.Common.Map3D.FlailActivity.IsOn(world, e);   // HROT's own reader (the drum and the loop ask the same place)

        // the rotor / engine level spools at a constant rate: HROT's spool is sent as data (target, level at the change, when, seconds per full scale) and only a change of it
        // sends a message; a spool the rule does not describe this way would stay continuous (every HROT spool is this rule: HeatSource state + Since + spin seconds)
        float rotor = VehicleLook.RotorSpeed(world, e, now);
        var (rotorTarget, rotorFrom, rotorAt, rotorSeconds) = SpoolOf(world, tkb, e, id, now);
        bool known = _sent.TryGetValue(id, out var previous);
        if (known && string.Equals(previous.Destruction, destruction, StringComparison.Ordinal) && previous.TurretThrown == turret && previous.Burning == burning
            && previous.Smoking == smoking && previous.Lights == lights && previous.Parts.SequenceEqual(_parts) && SameMotions(previous.OpeningMotions, _motions)
            && previous.RotorTarget == rotorTarget && previous.RotorFrom == rotorFrom && previous.RotorChangedAt == rotorAt && previous.RotorSpoolSeconds == rotorSeconds
            && (previous.CrewExposed ?? []).SequenceEqual(_crew) && previous.FlailActive == flail)
        {
            message = default;
            return false;   // the steady path allocates nothing
        }

        message = new HrotVehicleLook(id, destruction, turret, burning, smoking, lights, _parts.ToArray(), _openings.ToArray(), rotor, _crew.Count == 0 ? null : _crew.ToArray(), flail,
            rotorTarget, rotorFrom, rotorAt, rotorSeconds, _motions.ToArray());
        _sent[id] = message;
        return true;
    }


    /// <summary>
    /// An opening's timed motion from HROT's slot (<c>VehicleOpenings</c>: state + the time it began, progress derived by <c>VehicleOpenings.Progress</c> with the type's open
    /// seconds): Opening and Open go to 1, Closing and Closed to 0.
    /// </summary>
    private static HrotOpeningMotion MotionOf(EntityRepository world, Entity e, int slot, Fdp.Toolkit.Tkb.Domain.OpeningDto opening)
    {
        if (!world.IsComponentTypeRegistered<Fdp.Toolkit.Behavior.Components.VehicleOpenings>() || !world.HasComponent<Fdp.Toolkit.Behavior.Components.VehicleOpenings>(e))
        {
            return new HrotOpeningMotion(opening.Id, 0f, 0.0, opening.OpenSeconds);
        }

        ref readonly var openings = ref world.GetComponentRO<Fdp.Toolkit.Behavior.Components.VehicleOpenings>(e);
        if ((uint)slot >= openings.Count)
        {
            return new HrotOpeningMotion(opening.Id, 0f, 0.0, opening.OpenSeconds);
        }

        var s = openings.Slots[slot];
        float target = s.State is Fdp.Toolkit.Behavior.Components.OpeningState.Opening or Fdp.Toolkit.Behavior.Components.OpeningState.Open ? 1f : 0f;
        return new HrotOpeningMotion(opening.Id, target, s.Since, opening.OpenSeconds);
    }

    private static bool SameMotions(IReadOnlyList<HrotOpeningMotion>? a, List<HrotOpeningMotion> b)
    {
        if ((a?.Count ?? 0) != b.Count)
        {
            return false;
        }

        for (int i = 0; i < b.Count; i++)
        {
            if (a![i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The rotor / engine spool as data: the speed it moves to (HROT's own <c>VehicleLook.RotorSpeed</c> long after the change), the speed at the change (the same function
    /// at the change's time), when, and the seconds per full scale (the type's spin-up when rising, spin-down when falling). A state held from the start (no change time) is
    /// at its target at once.
    /// </summary>
    private static (float Target, float From, double ChangedAt, float Seconds) SpoolOf(EntityRepository world, ITkbDatabase? tkb, Entity e, int id, double now)
    {
        if (!world.IsComponentTypeRegistered<Fdp.Toolkit.Perception.Signatures.HeatSource>() || !world.HasComponent<Fdp.Toolkit.Perception.Signatures.HeatSource>(e))
        {
            return (0f, 0f, 0.0, 0f);
        }

        var src = world.GetComponentRO<Fdp.Toolkit.Perception.Signatures.HeatSource>(e);
        if (!src.IsEngine)
        {
            return (0f, 0f, 0.0, 0f);
        }

        double since = src.Since.Seconds;
        float target = VehicleLook.RotorSpeed(world, e, Math.Max(now, since) + 1.0e9);
        if (since <= 0.0)
        {
            return (target, target, 0.0, 0f);
        }

        float from = VehicleLook.RotorSpeed(world, e, since);
        Fdp.Interfaces.TkbTemplate? template = null;
        if (tkb is not null && world.IsComponentTypeRegistered<TkbIdentity>() && world.HasComponent<TkbIdentity>(e))
        {
            tkb.TryGetByType(world.GetComponentRO<TkbIdentity>(e).TkbType, out template);
        }

        var plant = Fdp.Toolkit.Perception.Signatures.ThermalDefaults.Of(template);
        float seconds = target >= from ? plant?.SpinUpSeconds ?? 0f : plant?.SpinDownSeconds ?? 0f;
        return (target, from, since, from == target ? 0f : seconds);
    }
}

internal static class LookIds
{
    public static int NetworkIdOf(EntityRepository world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.IsComponentTypeRegistered<NetworkIdentity>() && world.HasComponent<NetworkIdentity>(entity)
            ? world.GetComponentRO<NetworkIdentity>(entity).Value : 0;
}
