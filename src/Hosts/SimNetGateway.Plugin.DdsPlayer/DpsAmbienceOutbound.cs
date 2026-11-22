using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Writes <see cref="D.Ambience"/> (DD-028) beside <see cref="DpsOutbound"/>: one state instance per ambience layer, written once on the world partition when the host sends a new or changed layer
/// (the host decides what changed; this module only maps and writes), disposed when the host withdraws it. The world boundary ends the rest with the transport's writers. One instance per gateway runtime.
/// </summary>
public sealed class DpsAmbienceOutbound : IGatewayModule
{
    /// <summary>The bound of <see cref="D.Ambience.Kind"/>.</summary>
    public const int MaxKind = 32;

    private readonly DpsPorts _ports;
    private readonly HashSet<int> _written = new();

    internal DpsAmbienceOutbound(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsAmbienceOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotAmbience>(OnAmbience);
        setup.OnMessage<HrotAmbienceRemoved>(OnRemoved);
    }

    private void OnAmbience(in HrotAmbience m, IngressContext ctx)
    {
        string kind = m.Kind ?? string.Empty;
        _written.Add(m.Id);
        _ports.Ambience.Send(new DpsItem<D.Ambience>(new D.Ambience
        {
            Id = m.Id,
            Kind = kind.Length > MaxKind ? kind[..MaxKind] : kind,
            Intensity = float.IsFinite(m.Intensity) ? Math.Clamp(m.Intensity, 0f, 1f) : 0f,
            HasPosition = m.HasPosition,
            Position = new D.Vec3d { X = m.Position.X, Y = m.Position.Y, Z = m.Position.Z },
            Radius = float.IsFinite(m.Radius) ? m.Radius : 0f,
            UntilSim = m.UntilSim,
        }));
    }

    private void OnRemoved(in HrotAmbienceRemoved m, IngressContext ctx)
    {
        if (_written.Remove(m.Id))
        {
            _ports.Ambience.Send(new DpsItem<D.Ambience>(new D.Ambience { Id = m.Id, Kind = string.Empty }, true));
        }
    }
}
