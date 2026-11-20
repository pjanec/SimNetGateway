using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps the ambient traffic (DD-018 3.6b) to its topics, beside <see cref="DpsOutbound"/>: a <c>Mover</c> instance per mover (disposed when the host says it is gone), the
/// batches of knots as <c>MoverTracks</c> events (at most 4096 knots, the contract's bound), and a <c>TrafficLights</c> instance per signal head. The world boundary ends
/// the instances with the transport's writers. One instance per gateway runtime.
/// </summary>
public sealed class DpsTrafficOutbound : IGatewayModule
{
    public const int MaxKnots = 4096;

    private readonly DpsPorts _ports;
    private readonly HashSet<int> _movers = new();
    private readonly HashSet<int> _lights = new();

    internal DpsTrafficOutbound(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsTrafficOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotMover>(OnMover);
        setup.OnMessage<HrotMoverRemoved>(OnMoverRemoved);
        setup.OnMessage<HrotMoverTracks>(OnTracks);
        setup.OnMessage<HrotTrafficLight>(OnLight);
        setup.OnMessage<HrotTrafficLightRemoved>(OnLightRemoved);
    }

    private void OnMover(in HrotMover m, IngressContext ctx)
    {
        _movers.Add(m.Id);
        _ports.Mover.Send(new DpsItem<D.Mover>(new D.Mover
        {
            MoverId = m.Id,
            Kind = LookNames.ByName<D.MoverKind>("MoverKind", m.Kind),
            TypeId = m.TypeId,
            ColourRgba = m.Colour,
            Seed = m.Seed,
            Length = m.Length,
            Width = m.Width,
            Source = LookNames.ByName<D.MoverSource>("MoverSource", m.Source),
        }));
    }

    private void OnMoverRemoved(in HrotMoverRemoved m, IngressContext ctx)
    {
        if (_movers.Remove(m.Id))
        {
            _ports.Mover.Send(new DpsItem<D.Mover>(new D.Mover { MoverId = m.Id }, true));
        }
    }

    private void OnTracks(in HrotMoverTracks m, IngressContext ctx)
    {
        if (m.Knots.Count == 0 || !double.IsFinite(m.SimTime))
        {
            return;
        }

        _ports.MoverTracks.Send(new DpsItem<D.MoverTracks>(new D.MoverTracks
        {
            SimTime = m.SimTime,
            Knots =
            [
                .. m.Knots.Take(MaxKnots).Select(k => new D.MoverKnot
                {
                    MoverId = k.MoverId, Dt = k.Dt, X = k.X, Y = k.Y, Z = k.Z, HeadingCentiDeg = k.HeadingCentiDeg, PitchCentiDeg = k.PitchCentiDeg, Speed = k.Speed,
                }),
            ],
        }));
    }

    private void OnLight(in HrotTrafficLight m, IngressContext ctx)
    {
        _lights.Add(m.SignalId);
        _ports.TrafficLights.Send(new DpsItem<D.TrafficLights>(new D.TrafficLights
        {
            SignalId = m.SignalId,
            Position = new D.Vec3d { X = m.Position.X, Y = m.Position.Y, Z = m.Position.Z },
            HeadingDeg = m.HeadingDeg,
            State = LookNames.ByName<D.TrafficLightState>("TrafficLightState", m.State),
        }));
    }

    private void OnLightRemoved(in HrotTrafficLightRemoved m, IngressContext ctx)
    {
        if (_lights.Remove(m.SignalId))
        {
            _ports.TrafficLights.Send(new DpsItem<D.TrafficLights>(new D.TrafficLights { SignalId = m.SignalId }, true));
        }
    }
}
