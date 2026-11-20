using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps the HUD messages (DD-018 3.1) to their topics, beside <see cref="DpsOutbound"/>: kills and player messages as events, rule statuses as state (disposed when the
/// rule entity goes) and the exercise result as the one state instance. The world boundary ends the state with the transport's writers. One instance per gateway runtime.
/// </summary>
public sealed class DpsHudOutbound : IGatewayModule
{
    private const int MaxText = 256;

    private readonly DpsPorts _ports;
    private readonly HashSet<int> _statuses = new();

    internal DpsHudOutbound(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsHudOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotManDowned>(OnManDowned);
        setup.OnMessage<HrotPlayerMessage>(OnPlayerMessage);
        setup.OnMessage<HrotStatus>(OnStatus);
        setup.OnMessage<HrotStatusRemoved>(OnStatusRemoved);
        setup.OnMessage<HrotExercise>(OnExercise);
    }

    private void OnManDowned(in HrotManDowned m, IngressContext ctx) =>
        _ports.ManDowned.Send(new DpsItem<D.ManDowned>(new D.ManDowned
        {
            SimTime = m.SimTime,
            VictimId = m.VictimId,
            SourceId = m.SourceId,
            OriginalSourceId = m.OriginalSourceId,
            State = LookNames.ByName<D.Injury>("Injury", m.State),
            BodyPart = (byte)Math.Clamp(m.BodyPart, 0, 255),
            Part = LookNames.ByName<D.BodyPart>("BodyPart", m.BodyPartName),
            VictimSide = (byte)Math.Clamp(m.VictimSide, 0, 255),
        }));

    private void OnPlayerMessage(in HrotPlayerMessage m, IngressContext ctx)
    {
        string text = m.Text ?? string.Empty;
        _ports.PlayerMessage.Send(new DpsItem<D.PlayerMessage>(new D.PlayerMessage
        {
            MessageId = m.MessageId,
            OriginNode = m.OriginNode,
            To = LookNames.ByName<D.MessageAddress>("MessageAddress", m.To),
            ToValue = m.ToValue,
            Text = text.Length <= MaxText ? text : text[..MaxText],
            Seconds = m.Seconds,
            Priority = m.Priority,
            SentAt = m.SentAt,
            SourceId = m.SourceId,
        }));
    }

    private void OnStatus(in HrotStatus m, IngressContext ctx)
    {
        _statuses.Add(m.Id);
        _ports.Status.Send(new DpsItem<D.Status>(new D.Status
        {
            StatusId = m.Id,
            Role = LookNames.ByName<D.ObjectiveRole>("ObjectiveRole", m.Role),
            State = LookNames.ByName<D.ObjectiveState>("ObjectiveState", m.State),
            TriggerState = LookNames.ByName<D.TriggerState>("TriggerState", m.TriggerState),
            Closed = m.Closed,
            Owner = m.Owner,
            ProgressCur = m.ProgressCur,
            ProgressNeed = m.ProgressNeed,
            Value = m.Value,
            Target = m.Target,
            Score = m.Score,
            Pass = m.Pass,
            Points = m.Points,
            ChangedAt = m.ChangedAt,
            Reason = Cut(m.Reason, 64),
            Title = Cut(m.Title, 64),
            Kind = Cut(m.Kind, 32),
            Group = Cut(m.Group, 32),
            Phase = m.Phase,
            Required = m.Required,
            Scored = m.Scored,
            Weight = m.Weight,
            VisibleSides = m.VisibleSides,
            StepCur = m.StepCur,
            StepCount = m.StepCount,
            HoldFor = m.HoldFor,
            Deadline = m.Deadline,
            ExpectedAt = m.ExpectedAt,
            Capture = m.Capture,
            Challenger = m.Challenger,
            AreaCentre = new D.Vec3d { X = m.AreaCentre.X, Y = m.AreaCentre.Y, Z = m.AreaCentre.Z },
            AreaRadius = m.AreaRadius,
            EndsExercise = m.EndsExercise,
            SubjectId = m.SubjectId,
            ParentId = m.ParentId,
        }));
    }

    private void OnStatusRemoved(in HrotStatusRemoved m, IngressContext ctx)
    {
        if (_statuses.Remove(m.Id))
        {
            _ports.Status.Send(new DpsItem<D.Status>(new D.Status { StatusId = m.Id, Reason = string.Empty, Title = string.Empty, Kind = string.Empty, Group = string.Empty }, true));
        }
    }

    private void OnExercise(in HrotExercise m, IngressContext ctx) =>
        _ports.Exercise.Send(new DpsItem<D.Exercise>(new D.Exercise
        {
            Phase = LookNames.ByName<D.ExercisePhase>("ExercisePhase", m.Phase),
            EndedAt = m.EndedAt,
            Reason = Cut(m.Reason, 64),
            Friendly = Side(m.Friendly),
            Opposing = Side(m.Opposing),
            Neutral = Side(m.Neutral),
        }));

    private static D.SideResult Side(HrotSideResult r) => new() { Outcome = LookNames.ByName<D.SideOutcome>("SideOutcome", r.Outcome), Points = r.Points, Score = r.Score };

    private static string Cut(string? s, int max) => s is null ? string.Empty : s.Length <= max ? s : s[..max];
}
