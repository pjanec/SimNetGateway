using System.Numerics;
using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Behavior;
using Fdp.Toolkit.Physics.BodyParts;
using Fdp.Toolkit.Replication.Components;
using Hrot.Look;
using Hrot.Look.Effects;
using Hrot.Look.People;
using Hrot.Look.Vehicles;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

// HROT's look library (Hrot.Look) decides how the simulation looks; these gathers only call it and copy its answers into pivot messages (DD-017 3.10). No
// rule of HROT is copied here.
namespace SimNetGateway.HrotHost;

/// <summary>Effects of the frame: <c>EffectRules.ForFrame</c> once per frame into one reused list, one <see cref="HrotEffect"/> per spec.</summary>
internal sealed class EffectGather
{
    private readonly List<EffectSpec> _specs = new();

    public int Gather(EntityRepository world, ITkbDatabase? tkb, InMemoryInbound inbound)
    {
        _specs.Clear();
        EffectRules.ForFrame(world, tkb, _specs);
        for (int i = 0; i < _specs.Count; i++)
        {
            var s = _specs[i];
            int anchor = 0;
            byte mount = 0;
            if (s.Anchor is { } a)
            {
                anchor = LookIds.NetworkIdOf(world, a.Shooter);
                mount = (byte)a.WeaponIndex;
            }

            var msg = new HrotEffect((byte)s.Kind, (byte)s.SizeClass, (byte)s.Surface, (ulong)s.MunitionDis.Value, s.EffectType, new HrotVec3(s.At.X, s.At.Y, s.At.Z),
                new HrotQuat(s.Rotation.X, s.Rotation.Y, s.Rotation.Z, s.Rotation.W), anchor, mount, s.Lifetime, s.Size);
            inbound.Enqueue(in msg);
        }

        return _specs.Count;
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
        public HrotPersonAppearance Sent;
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

        message = Map(id, in look);
        if (book.HasSent && book.Sent.Equals(message))
        {
            return false;
        }

        book.Sent = message;
        book.HasSent = true;
        return true;
    }

    private static HrotPersonAppearance Map(int id, in PersonAppearance a)
    {
        var o = a.Outfit;
        byte side = a.Side switch { ForceId.Friend => 1, ForceId.Hostile => 2, _ => 3 };
        return new HrotPersonAppearance(id, a.Seed, side,
            Pack(o.Top), Pack(o.Sleeve), Pack(o.Bottom), Pack(o.Shin), Pack(o.Skin), Pack(o.Hair), Pack(o.Tie), Pack(o.HeadCloth), Pack(o.OverColour), Pack(o.ExtraColour),
            (byte)o.HairStyle, (byte)o.Legwear, (byte)o.Camo, (byte)o.TopKind, (byte)o.HeadKind, (byte)o.OverKind, (byte)o.ExtraKind, (byte)o.Face.Beard,
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
        if (tkb is not null && world.IsComponentTypeRegistered<TkbIdentity>() && world.HasComponent<TkbIdentity>(e)
            && tkb.TryGetByType(world.GetComponentRO<TkbIdentity>(e).TkbType, out var template))
        {
            foreach (var opening in VehicleOpeningsCatalog.Of(template))
            {
                float progress = MathF.Round(VehicleLook.OpeningProgress(world, e, opening.Id, now) * ProgressSteps) / ProgressSteps;
                _openings.Add((opening.Id, progress));
            }
        }

        byte destruction = (byte)VehicleLook.Destruction(world, e);
        bool turret = VehicleLook.TurretThrown(world, e), burning = VehicleLook.Burning(world, e, now), smoking = VehicleLook.Smoking(world, e, now);
        uint lights = VehicleLook.Lights(world, e, now, _brakes);
        if (_sent.TryGetValue(id, out var previous) && previous.Destruction == destruction && previous.TurretThrown == turret && previous.Burning == burning
            && previous.Smoking == smoking && previous.Lights == lights && previous.Parts.SequenceEqual(_parts) && previous.Openings.SequenceEqual(_openings))
        {
            message = default;
            return false;   // the steady path allocates nothing
        }

        message = new HrotVehicleLook(id, destruction, turret, burning, smoking, lights, _parts.ToArray(), _openings.ToArray());
        _sent[id] = message;
        return true;
    }
}

internal static class LookIds
{
    public static int NetworkIdOf(EntityRepository world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.IsComponentTypeRegistered<NetworkIdentity>() && world.HasComponent<NetworkIdentity>(entity)
            ? world.GetComponentRO<NetworkIdentity>(entity).Value : 0;
}
