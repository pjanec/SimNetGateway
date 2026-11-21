using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps the suggested camera view and the impacts on the static world (DD-019 3.1, 3.2) to their topics, beside <see cref="DpsOutbound"/>. The view goes out on first sight, on a
/// change (the pose beyond the entity thresholds, any other field) and on the heartbeat (simulation time of the sample); <c>CutSeq</c> grows when the source, the mode, the target, the shot or HROT's cut counter
/// changes, so a client blends within a cut and jumps on a new one. Impacts go out as they come. One instance per gateway runtime.
/// Round 7 (DD-022 3d): the camera is dead-reckoned like an entity. The rates (<c>Velocity</c>, <c>Acceleration</c>, <c>AngularVelocity</c> in the camera's body frame, <c>FovRate</c>) are derived from the
/// last few successive samples on simulation time and reset at a cut (and whenever simulation time does not advance); the send rule is the shared <see cref="DeadReckonedSendPolicy"/> (Common, R-114):
/// a view goes out when the client's prediction of the last one sent would miss the truth by more than the position or angle threshold, or at the heartbeat.
/// </summary>
public sealed class DpsViewOutbound : IGatewayModule
{
    private readonly DpsSettings _settings;
    private readonly DpsPorts _ports;
    private D.View _sent;
    private bool _hasSent;
    private uint _cutSeq;
    private int _sentCut;
    private readonly DeadReckonedSendPolicy _policy;
    private readonly List<Sample> _window = new(WindowSize);
    private SentModel _held;

    /// <summary>How many successive samples the rates are smoothed over (three pairs).</summary>
    private const int WindowSize = 4;

    /// <summary>The field of view may drift this much from its prediction before a sample is due (degrees).</summary>
    private const double FovThresholdDeg = 0.1;

    private readonly record struct Sample(double Time, Vec3 Position, Quaternion Rotation, double Fov);

    internal DpsViewOutbound(DpsSettings settings, DpsPorts ports)
    {
        _settings = settings;
        _ports = ports;
        _policy = new DeadReckonedSendPolicy(
            new DrAttributeSet(AttrId.None, AttrId.None, AttrId.None, AttrId.None, AttrId.None, AttrId.None, AttrId.None),
            SendPolicySettings.FromDegrees(settings.PositionThresholdM, settings.AngleThresholdDeg, settings.HeartbeatSeconds),
            PredictorRegistry.CreateDis());
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

        bool cut = !_hasSent || view.Source != _sent.Source || view.Mode != _sent.Mode || view.TargetId != _sent.TargetId || view.Shot != _sent.Shot
            || m.Cut != _sentCut;
        if (cut)
        {
            _cutSeq++;
            _window.Clear();   // the rates of the old shot say nothing about the new one
        }

        view.CutSeq = _cutSeq;
        var rotation = new Quaternion(m.Orientation.W, m.Orientation.X, m.Orientation.Y, m.Orientation.Z).Normalized();
        var position = new Vec3(m.Position.X, m.Position.Y, m.Position.Z);
        Push(new Sample(m.SimTime, position, rotation, m.FovDeg));
        var (velocity, acceleration, angular, fovRate) = Rates();
        view.Velocity = ToF(velocity);
        view.Acceleration = ToF(acceleration);
        view.AngularVelocity = ToF(angular);
        view.FovRate = (float)fovRate;

        if (!cut && !Due(in view, position, rotation, velocity))
        {
            return;
        }

        _sent = view;
        _sentCut = m.Cut;
        _hasSent = true;
        _held = SentModel.Of(position, ToV(view.Velocity), ToV(view.Acceleration), rotation.ToEuler(), ToV(view.AngularVelocity), (int)DisDeadReckoning.Rvw, m.SimTime);
        _ports.View.Send(new DpsItem<D.View>(view));
    }

    /// <summary>Is a sample due: the heartbeat, a binding value that changed, or the client's prediction of the last sample beyond a threshold from the truth (the shared policy).</summary>
    private bool Due(in D.View v, Vec3 position, Quaternion rotation, Vec3 velocity)
    {
        if (_policy.HeartbeatDue(in _held, v.SimTime) || v.Distance != _sent.Distance || v.Side != _sent.Side)
        {
            return true;
        }

        double dt = v.SimTime - _held.Time;
        if (Math.Abs(v.FovDeg - (_sent.FovDeg + _sent.FovRate * dt)) > FovThresholdDeg)
        {
            return true;
        }

        var truth = new PredictedState(position, velocity, rotation.ToEuler());
        var held = _policy.Held(in _held, v.SimTime);
        return _policy.Exceeds(in truth, in held);
    }

    private void Push(Sample s)
    {
        // simulation time that does not advance (a pause) or goes back (a seek) leaves nothing to derive a rate from
        if (_window.Count > 0 && !(s.Time > _window[^1].Time))
        {
            _window.Clear();
        }

        if (_window.Count == WindowSize)
        {
            _window.RemoveAt(0);
        }

        _window.Add(s);
    }

    /// <summary>
    /// The rates at the newest sample, from the pairs of successive samples in the window on simulation time: the newest pair's velocity moved to the newest sample by the trend of the pairs
    /// (the acceleration), the angular velocity in the camera's own frame from the relative rotation of the newest pair, and the field-of-view rate of the newest pair.
    /// </summary>
    private (Vec3 Velocity, Vec3 Acceleration, Vec3 Angular, double FovRate) Rates()
    {
        int pairs = _window.Count - 1;
        if (pairs < 1)
        {
            return (Vec3.Zero, Vec3.Zero, Vec3.Zero, 0.0);
        }

        var first = Pair(0);
        var last = Pair(pairs - 1);
        Vec3 acceleration = Vec3.Zero;
        Vec3 velocity = last.Velocity;
        if (pairs >= 2)
        {
            double span = last.Mid - first.Mid;
            acceleration = new Vec3((last.Velocity.X - first.Velocity.X) / span, (last.Velocity.Y - first.Velocity.Y) / span, (last.Velocity.Z - first.Velocity.Z) / span);
            double ahead = _window[^1].Time - last.Mid;
            velocity = new Vec3(last.Velocity.X + acceleration.X * ahead, last.Velocity.Y + acceleration.Y * ahead, last.Velocity.Z + acceleration.Z * ahead);
        }

        return (velocity, acceleration, last.Angular, last.FovRate);
    }

    private (Vec3 Velocity, Vec3 Angular, double FovRate, double Mid) Pair(int i)
    {
        var a = _window[i];
        var b = _window[i + 1];
        double dt = b.Time - a.Time;
        var velocity = new Vec3((b.Position.X - a.Position.X) / dt, (b.Position.Y - a.Position.Y) / dt, (b.Position.Z - a.Position.Z) / dt);
        var relative = (a.Rotation.Conjugate() * b.Rotation).WithNonNegativeW();   // a body-frame rotation: the camera's own axes
        double sine = relative.Vector.Length;
        double angle = 2.0 * Math.Atan2(sine, relative.W);
        var angular = sine < 1e-12 ? Vec3.Zero : new Vec3(relative.X / sine * angle / dt, relative.Y / sine * angle / dt, relative.Z / sine * angle / dt);
        return (velocity, angular, (b.Fov - a.Fov) / dt, (a.Time + b.Time) / 2.0);
    }

    private static D.Vec3f ToF(Vec3 v) => new() { X = (float)v.X, Y = (float)v.Y, Z = (float)v.Z };

    private static Vec3 ToV(D.Vec3f v) => new(v.X, v.Y, v.Z);

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
}
