using Fdp.Core;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Combat.Events;
using Fdp.Toolkit.Replication;
using Fdp.Toolkit.Replication.Services;
using Fdp.Toolkit.ScenarioRules;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

// DPS round 2 (DD-018 3.1): what HROT's player HUD reads besides the own unit - the kill feed (the man-downed event), the player messages, the rule statuses (the
// objective tree as RulesReader reads it) and the exercise result. Every reading is HROT's own; these gathers copy it into pivot messages and send a change only.
namespace SimNetGateway.HrotHost;

/// <summary>Kills, messages, rule statuses and the exercise result. Main thread only.</summary>
internal sealed class HudGather
{
    private const int MaxText = 256;

    private readonly Dictionary<int, HrotStatus> _statuses = new();
    private readonly HashSet<int> _seen = new();
    private readonly List<int> _gone = new();
    private HrotExercise _exercise;
    private bool _hasExercise;
    private EntityRepository? _queryWorld;
    private EntityQuery? _statusQuery, _exerciseQuery;

    public void Clear()
    {
        _statuses.Clear();
        _hasExercise = false;
    }

    /// <summary>The events of last frame: man-downed (local and remote) and player messages (all of them, with their addressing).</summary>
    public void GatherEvents(EntityRepository world, InMemoryInbound inbound, double simTime)
    {
        if (world.Bus.IsRegistered<ManDownedEvent>())
        {
            var downed = world.ReadEvents<ManDownedEvent>();
            for (int i = 0; i < downed.Length; i++)
            {
                ref readonly var m = ref downed[i];
                int part = m.HasBodyPart != 0 ? m.BodyPart + 1 : 0;
                inbound.Enqueue(new HrotManDowned(simTime, LookIds.NetworkIdOf(world, m.Victim), LookIds.NetworkIdOf(world, m.Source), LookIds.NetworkIdOf(world, m.OriginalSource),
                    PivotEnum<HrotInjury>.Of(m.State), part, DisForce(m.VictimSide), m.HasBodyPart != 0 ? BodyPartOf(m.BodyPart) : HrotBodyPart.Unknown));
            }
        }

        if (world.Bus.IsRegisteredManaged<PlayerMessageEvent>())
        {
            var messages = world.ReadManagedEvents<PlayerMessageEvent>();
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                string text = m.Text ?? string.Empty;
                string to = m.ToKind ?? string.Empty;
                var address = AddressOf(to);
                // a message to a side names HROT's force id: it crosses as the contract's force number like every side
                int value = string.Equals(to, "Side", StringComparison.Ordinal) ? DisForce((ForceId)(byte)m.ToValue) : m.ToValue;
                inbound.Enqueue(new HrotPlayerMessage(m.MessageId, m.OriginNodeId.Value, address, value, text.Length <= MaxText ? text : text[..MaxText], m.Seconds.Seconds, m.Priority,
                    m.SentAt.Seconds, m.Source));
            }
        }
    }

    /// <summary>The rule statuses (the replicated objective tree) and the exercise result: what changed, and the removal of a status whose entity went.</summary>
    public void GatherState(EntityRepository world, InMemoryInbound inbound)
    {
        Bind(world);
        _seen.Clear();
        if (_statusQuery is not null)
        {
            bool links = world.IsComponentTypeRegistered<ObjectiveLinks>();
            foreach (var e in _statusQuery)
            {
                int id = NetworkIdResolver.RuntimeNetworkIdOf(world, e);
                if (id == 0)
                {
                    continue;   // no durable identity: it cannot be asked or linked (as RulesReader)
                }

                _seen.Add(id);
                var links_ = links && world.HasComponent<ObjectiveLinks>(e) ? world.GetComponentRO<ObjectiveLinks>(e) : default;
                var message = Map(id, in world.GetComponentRO<ObjectiveStatus>(e), links_.Subject.NetworkId, links_.Parent.NetworkId);
                if (_statuses.TryGetValue(id, out var sent) && sent.Equals(message))
                {
                    continue;
                }

                _statuses[id] = message;
                inbound.Enqueue(in message);
            }
        }

        _gone.Clear();
        foreach (int id in _statuses.Keys)
        {
            if (!_seen.Contains(id))
            {
                _gone.Add(id);
            }
        }

        foreach (int id in _gone)
        {
            _statuses.Remove(id);
            inbound.Enqueue(new HrotStatusRemoved(id));
        }

        if (_exerciseQuery is not null)
        {
            foreach (var e in _exerciseQuery)
            {
                var r = world.GetComponentRO<ExerciseResult>(e);
                var message = new HrotExercise(PivotEnum<HrotExercisePhase>.Of(r.Phase), r.EndedAt.Seconds, r.Reason.ToString(), Side(r.Friend), Side(r.Hostile), Side(r.Neutral));
                if (!_hasExercise || !_exercise.Equals(message))
                {
                    _exercise = message;
                    _hasExercise = true;
                    inbound.Enqueue(in message);
                }

                break;   // one result, on the World Environment
            }
        }
    }

    /// <summary>HROT's address kind (a name on its event) as the contract's address; an unknown name is the unknown value.</summary>
    private static HrotMessageAddress AddressOf(string name) => name switch
    {
        "All" => HrotMessageAddress.All,
        "Side" => HrotMessageAddress.Side,
        "Station" => HrotMessageAddress.Station,
        "Unit" => HrotMessageAddress.Unit,
        "Instructor" => HrotMessageAddress.Instructor,
        _ => HrotMessageAddress.Unknown,
    };

    private static HrotSideResult Side(in SideResult r) => new(PivotEnum<HrotSideOutcome>.Of(r.Outcome), r.Points, r.Score);

    /// <summary>HROT's person part id (<c>PersonPart</c>, a block figure's order) as the contract's part, by NAME: a part this contract does not know is the unknown value.</summary>
    private static HrotBodyPart BodyPartOf(byte part) => part switch
    {
        Fdp.Toolkit.Physics.BodyParts.PersonPart.Head => HrotBodyPart.Head,
        Fdp.Toolkit.Physics.BodyParts.PersonPart.Torso => HrotBodyPart.Torso,
        Fdp.Toolkit.Physics.BodyParts.PersonPart.UpperArmL => HrotBodyPart.UpperArmL,
        Fdp.Toolkit.Physics.BodyParts.PersonPart.ForearmL => HrotBodyPart.ForearmL,
        Fdp.Toolkit.Physics.BodyParts.PersonPart.UpperArmR => HrotBodyPart.UpperArmR,
        Fdp.Toolkit.Physics.BodyParts.PersonPart.ForearmR => HrotBodyPart.ForearmR,
        Fdp.Toolkit.Physics.BodyParts.PersonPart.ThighL => HrotBodyPart.ThighL,
        Fdp.Toolkit.Physics.BodyParts.PersonPart.ShinL => HrotBodyPart.ShinL,
        Fdp.Toolkit.Physics.BodyParts.PersonPart.ThighR => HrotBodyPart.ThighR,
        Fdp.Toolkit.Physics.BodyParts.PersonPart.ShinR => HrotBodyPart.ShinR,
        _ => HrotBodyPart.Unknown,
    };

    private static HrotStatus Map(int id, in ObjectiveStatus s, int subject, int parent) =>
        new(id, PivotEnum<HrotObjectiveRole>.Of(s.Role), PivotEnum<HrotObjectiveState>.Of(s.State), PivotEnum<HrotTriggerState>.Of(s.TriggerState), s.Closed != 0, DisForce(s.Owner), s.ProgressCur, s.ProgressNeed, s.Value, s.Target, s.Score, s.Pass,
            s.Points, s.ChangedAt.Seconds, s.Reason.ToString(), s.Title.ToString(), s.Kind.ToString(), s.Group.ToString(), s.Phase, s.Required != 0, s.Scored != 0, s.Weight,
            DisMask(s.VisibleSides), s.StepCur, s.StepCount, s.HoldFor, s.Deadline, s.ExpectedAt.Seconds, s.Capture != 0, s.Challenger == ForceId.Neutral ? (byte)0 : DisForce(s.Challenger),
            new HrotVec3(s.AreaCenter.X, s.AreaCenter.Y, s.AreaCenter.Z), s.AreaRadius, s.EndsExercise != 0, subject, parent);

    /// <summary>HROT's force id as the contract's force number (1 friendly, 2 opposing, 3 neutral, as everywhere in the pivot).</summary>
    internal static byte DisForce(ForceId f) => f switch { ForceId.Friend => 1, ForceId.Hostile => 2, _ => 3 };

    /// <summary>HROT's visible-sides mask (bit n = HROT force n) as a mask by the contract's force numbers.</summary>
    internal static byte DisMask(byte hrotMask)
    {
        byte mask = 0;
        foreach (var f in new[] { ForceId.Neutral, ForceId.Friend, ForceId.Hostile })
        {
            if ((hrotMask & (1 << (int)f)) != 0)
            {
                mask |= (byte)(1 << DisForce(f));
            }
        }

        return mask;
    }

    private void Bind(EntityRepository world)
    {
        if (ReferenceEquals(_queryWorld, world) && (_statusQuery is not null || !world.IsComponentTypeRegistered<ObjectiveStatus>()) && (_exerciseQuery is not null || !world.IsComponentTypeRegistered<ExerciseResult>()))
        {
            return;
        }

        _queryWorld = world;
        _statusQuery = world.IsComponentTypeRegistered<ObjectiveStatus>() && world.IsComponentTypeRegistered<NetworkIdentity>() ? world.Query().With<ObjectiveStatus>().Build() : null;
        _exerciseQuery = world.IsComponentTypeRegistered<ExerciseResult>() ? world.Query().With<ExerciseResult>().Build() : null;
    }
}
