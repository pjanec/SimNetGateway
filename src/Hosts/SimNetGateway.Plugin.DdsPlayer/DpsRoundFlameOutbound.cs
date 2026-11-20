using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps rounds, munition looks, flame jets, burning spots and ground heat marks (DD-018 3.3, 3.4) to their state topics, beside <see cref="DpsOutbound"/>. It
/// remembers which instances it wrote so that each is disposed when HROT's thing goes away (a round or holder with its entity, a jet when it goes out, a set that
/// became empty); the world boundary ends the rest with the transport's writers. One instance per gateway runtime.
/// </summary>
public sealed class DpsRoundFlameOutbound : IGatewayModule
{
    private const int MaxSpots = 8, MaxMarks = 16;

    private readonly DpsPorts _ports;
    private readonly HashSet<int> _rounds = new();
    private readonly HashSet<(int Id, int Mount)> _jets = new();
    private readonly HashSet<int> _spots = new();
    private readonly HashSet<int> _marks = new();

    internal DpsRoundFlameOutbound(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsRoundFlameOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotRound>(OnRound);
        setup.OnMessage<HrotMunitionLook>(OnMunitionLook);
        setup.OnMessage<HrotFlameJet>(OnFlameJet);
        setup.OnMessage<HrotFlameJetRemoved>(OnFlameJetRemoved);
        setup.OnMessage<HrotFireSpots>(OnFireSpots);
        setup.OnMessage<HrotGroundMarks>(OnGroundMarks);
        setup.OnMessage<HrotEntityRemoved>(OnRemoved);
    }

    private void OnRound(in HrotRound m, IngressContext ctx)
    {
        if (!Finite(m.Muzzle))
        {
            return;
        }

        _rounds.Add(m.Id);
        _ports.Round.Send(new DpsItem<D.Round>(new D.Round
        {
            EntityId = m.Id,
            MunitionTypeId = m.MunitionTkb,
            ShooterId = m.ShooterId,
            Muzzle = V3d(m.Muzzle),
            Lying = m.Lying,
            FuzeLeftS = m.FuzeLeftS,
            Stopped = m.Stopped,
        }));
    }

    private void OnMunitionLook(in HrotMunitionLook m, IngressContext ctx) =>
        _ports.MunitionLook.Send(new DpsItem<D.MunitionLook>(new D.MunitionLook
        {
            MunitionTypeId = m.MunitionTkb,
            Shape = LookNames.MunitionShapeOf(m.Shape),
            Length = m.Length,
            Diameter = m.Diameter,
            ColourRgba = m.Colour,
            SmokeTrail = m.SmokeTrail,
            Tracer = m.Tracer,
            TracerColourRgba = m.TracerColour,
            Sticks = m.Sticks,
            Bolt = m.Bolt,
        }));

    private void OnFlameJet(in HrotFlameJet m, IngressContext ctx)
    {
        if (!Finite(m.AimPoint))
        {
            return;
        }

        _jets.Add((m.Id, m.Mount));
        _ports.FlameJet.Send(new DpsItem<D.FlameJet>(new D.FlameJet
        {
            EntityId = m.Id,
            Mount = m.Mount,
            AimPoint = V3d(m.AimPoint),
            TargetId = m.TargetId,
            RangeM = m.RangeM,
            HalfAngleRad = m.HalfAngleRad,
            Intensity = m.Intensity,
            BurstStartedAt = m.BurstStartedAt,
            BurnSeconds = m.BurnSeconds,
        }));
    }

    private void OnFlameJetRemoved(in HrotFlameJetRemoved m, IngressContext ctx)
    {
        if (_jets.Remove((m.Id, m.Mount)))
        {
            _ports.FlameJet.Send(new DpsItem<D.FlameJet>(new D.FlameJet { EntityId = m.Id, Mount = m.Mount }, true));
        }
    }

    private void OnFireSpots(in HrotFireSpots m, IngressContext ctx)
    {
        if (m.Spots.Count == 0)
        {
            DisposeSpots(m.Id);
            return;
        }

        _spots.Add(m.Id);
        _ports.FireSpots.Send(new DpsItem<D.FireSpots>(new D.FireSpots
        {
            EntityId = m.Id,
            Spots =
            [
                .. m.Spots.Where(s => Finite(s.Position)).Take(MaxSpots).Select(s => new D.FireSpot
                {
                    Position = V3d(s.Position),
                    Radius = s.Radius,
                    Intensity = s.Intensity,
                    Since = s.Since,
                    Seconds = s.Seconds,
                }),
            ],
        }));
    }

    private void OnGroundMarks(in HrotGroundMarks m, IngressContext ctx)
    {
        if (m.Marks.Count == 0)
        {
            DisposeMarks(m.Id);
            return;
        }

        _marks.Add(m.Id);
        _ports.GroundMarks.Send(new DpsItem<D.GroundMarks>(new D.GroundMarks
        {
            EntityId = m.Id,
            Marks =
            [
                .. m.Marks.Where(k => Finite(k.Centre)).Take(MaxMarks).Select(k => new D.GroundMark
                {
                    Kind = LookNames.HeatMarkKindOf(k.Kind),
                    Centre = V3d(k.Centre),
                    Heading = k.Heading,
                    Length = k.Length,
                    Width = k.Width,
                    HotEnd = LookNames.HeatMarkEndOf(k.HotEnd),
                    StandSince = k.StandSince,
                    LeftAt = k.LeftAt,
                }),
            ],
        }));
    }

    private void OnRemoved(in HrotEntityRemoved m, IngressContext ctx)
    {
        if (_rounds.Remove(m.Id))
        {
            _ports.Round.Send(new DpsItem<D.Round>(new D.Round { EntityId = m.Id }, true));
        }

        int id = m.Id;
        foreach (var key in _jets.Where(k => k.Id == id).ToArray())
        {
            _jets.Remove(key);
            _ports.FlameJet.Send(new DpsItem<D.FlameJet>(new D.FlameJet { EntityId = key.Id, Mount = key.Mount }, true));
        }

        DisposeSpots(m.Id);
        DisposeMarks(m.Id);
    }

    private void DisposeSpots(int id)
    {
        if (_spots.Remove(id))
        {
            _ports.FireSpots.Send(new DpsItem<D.FireSpots>(new D.FireSpots { EntityId = id, Spots = [] }, true));
        }
    }

    private void DisposeMarks(int id)
    {
        if (_marks.Remove(id))
        {
            _ports.GroundMarks.Send(new DpsItem<D.GroundMarks>(new D.GroundMarks { EntityId = id, Marks = [] }, true));
        }
    }

    private static bool Finite(HrotVec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    private static D.Vec3d V3d(HrotVec3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
}
