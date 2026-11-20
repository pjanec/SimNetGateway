using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps the suggested camera view and the impacts on the static world (DD-019 3.1, 3.2) to their topics, beside <see cref="DpsOutbound"/>. The view goes out on first sight, on a
/// change (the pose beyond the entity thresholds, any other field) and on the heartbeat (simulation time of the sample); <c>CutSeq</c> grows when the source, the mode, the target or
/// the shot changes, so a client blends within a cut and jumps on a new one. Impacts go out as they come. One instance per gateway runtime.
/// </summary>
public sealed class DpsViewOutbound : IGatewayModule
{
    private readonly DpsSettings _settings;
    private readonly DpsPorts _ports;
    private D.View _sent;
    private HrotQuat _sentRotation;
    private bool _hasSent;
    private double _sentAt = double.NegativeInfinity;
    private uint _cutSeq;

    internal DpsViewOutbound(DpsSettings settings, DpsPorts ports)
    {
        _settings = settings;
        _ports = ports;
    }

    public string Name => nameof(DpsViewOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotView>(OnView);
        setup.OnMessage<HrotImpact>(OnImpact);
    }

    private void OnView(in HrotView m, IngressContext ctx)
    {
        if (!Finite(m.Position) || !Finite(m.Orientation) || !double.IsFinite(m.SimTime) || !float.IsFinite(m.Distance) || !float.IsFinite(m.Side) || !float.IsFinite(m.FovDeg))
        {
            return;
        }

        var view = new D.View
        {
            Source = LookNames.ByName<D.ViewSource>("ViewSource", m.Source),
            Mode = LookNames.ByName<D.ViewMode>("ViewMode", m.Mode),
            TargetId = m.TargetId,
            Distance = m.Distance,
            Side = m.Side,
            Position = new D.Vec3d { X = m.Position.X, Y = m.Position.Y, Z = m.Position.Z },
            Orientation = new D.Quat { X = (float)m.Orientation.X, Y = (float)m.Orientation.Y, Z = (float)m.Orientation.Z, W = (float)m.Orientation.W },
            FovDeg = m.FovDeg,
            Shot = m.Shot is { Length: > 64 } shot ? shot[..64] : m.Shot ?? string.Empty,
            SimTime = m.SimTime,
        };

        bool cut = !_hasSent || view.Source != _sent.Source || view.Mode != _sent.Mode || view.TargetId != _sent.TargetId || view.Shot != _sent.Shot;
        if (cut)
        {
            _cutSeq++;
        }

        view.CutSeq = _cutSeq;
        if (!cut && !Due(in view, m.Orientation))
        {
            return;
        }

        _sent = view;
        _sentRotation = m.Orientation;
        _hasSent = true;
        _sentAt = m.SimTime;
        _ports.View.Send(new DpsItem<D.View>(view));
    }

    private bool Due(in D.View v, HrotQuat rotation) =>
        v.SimTime - _sentAt >= _settings.HeartbeatSeconds
        || v.Distance != _sent.Distance || v.Side != _sent.Side || v.FovDeg != _sent.FovDeg
        || Dist(v.Position, _sent.Position) > _settings.PositionThresholdM
        || AngleDeg(rotation, _sentRotation) > _settings.AngleThresholdDeg;

    private void OnImpact(in HrotImpact m, IngressContext ctx)
    {
        if (!Finite(m.Position) || !Finite(m.Normal) || !double.IsFinite(m.SimTime))
        {
            return;
        }

        _ports.Impact.Send(new DpsItem<D.Impact>(new D.Impact
        {
            SimTime = m.SimTime,
            Position = new D.Vec3d { X = m.Position.X, Y = m.Position.Y, Z = m.Position.Z },
            Normal = new D.Vec3f { X = (float)m.Normal.X, Y = (float)m.Normal.Y, Z = (float)m.Normal.Z },
            Surface = LookNames.EffectSurfaceOf(m.Surface),
            Kind = LookNames.ImpactKindOf(m.Kind),
            MunitionTypeId = m.MunitionTkb,
            ShooterId = m.ShooterId,
            Material = m.Material is { Length: > 32 } material ? material[..32] : m.Material ?? string.Empty,
        }));
    }

    private static bool Finite(HrotVec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    private static bool Finite(HrotQuat q) => double.IsFinite(q.X) && double.IsFinite(q.Y) && double.IsFinite(q.Z) && double.IsFinite(q.W);

    private static double Dist(D.Vec3d a, D.Vec3d b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double AngleDeg(HrotQuat a, HrotQuat b)
    {
        double dot = Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        return 2.0 * Math.Acos(Math.Min(1.0, dot)) * (180.0 / Math.PI);
    }
}
