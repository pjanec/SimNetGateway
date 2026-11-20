using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Behavior.Possession;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Events;
using Fdp.Toolkit.Replication;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.ScenarioRules;
using Fdp.Toolkit.Tkb.Domain;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>DPS round 2, the HUD (DD-018 3.1): kills, player messages, rule statuses and the exercise result, and the HUD's words, on a staged HROT world.</summary>
public class HudGatherTests
{
    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public List<T> Of<T>() => All.OfType<T>().ToList();
    }

    private sealed class Stage : PackHarness
    {
        private double _now = 1000;

        public Stage()
            : base(dis: true)
        {
            RegisterTemplate(300, 3);
            foreach (var register in new Action[]
            {
                () => World.RegisterComponent<ObjectiveStatus>(), () => World.RegisterComponent<ObjectiveLinks>(), () => World.RegisterComponent<ExerciseResult>(),
            })
            {
                register();
            }

            Steps(3);
        }

        public Capture Gather(double simTime)
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            _now += 0.1;
            Adapter.GatherEntities(World, inbound, _now, simTime);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }

        public Capture GatherEvents()
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            Adapter.GatherEvents(World, inbound);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }

        public void PublishMessage(PlayerMessageEvent message)
        {
            World.Bus.PublishManaged(message);
            World.Bus.SwapBuffers();
        }

        public Entity AddStatus(int id, ObjectiveStatus status, int subject = 0, int parent = 0)
        {
            var e = World.CreateEntity();
            World.AddComponent(e, new NetworkIdentity(id));
            World.AddComponent(e, status);
            World.AddComponent(e, new ObjectiveLinks { Subject = new EntityRef(subject), Parent = new EntityRef(parent) });
            return e;
        }
    }

    private static ObjectiveStatus Objective(ObjectiveState state = ObjectiveState.Active) => new()
    {
        Role = ObjectiveRole.Objective, State = state, Owner = ForceId.Friend, ProgressCur = 3, ProgressNeed = 10, Value = 0.5f, Target = 1f, Score = 0.25f, Pass = 0.8f, Points = 5f,
        ChangedAt = new SimInstant(12.5), Reason = new FixedString64("held 3 s"), Title = new FixedString64("Hold the bridge"), Kind = new FixedString32("HoldArea"),
        Group = new FixedString32("mission"), Phase = 2, Required = 1, Scored = 1, Weight = 2f, VisibleSides = 0b110, HoldFor = 60f, Deadline = 900f, ExpectedAt = new SimInstant(130.0),
        Capture = 1, Challenger = ForceId.Hostile, AreaCenter = new WorldPoint(100, 200, 0), AreaRadius = 50f, EndsExercise = 0, TriggerState = TriggerState.Armed, StepCur = 1, StepCount = 3,
    };

    // ---- kills and messages ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_man_downed_event_is_a_kill_with_network_ids_state_body_part_and_side_local_or_remote()
    {
        using var h = new Stage();
        var victim = h.AddHrotEntity(1201, Vector3.Zero, tkb: 300);
        var killer = h.AddHrotEntity(1202, new Vector3(5, 0, 0), tkb: 300);
        var thrower = h.AddHrotEntity(1203, new Vector3(9, 0, 0), tkb: 300);
        h.PublishForNextFrame(new ManDownedEvent
        {
            Victim = victim, Source = killer, OriginalSource = thrower, State = InjuryState.Dead, HasBodyPart = 1, BodyPart = 3, VictimSide = ForceId.Hostile,
        });

        var kill = Assert.Single(h.GatherEvents().Of<HrotManDowned>());

        Assert.Equal((1201, 1202, 1203, "Dead", 4, 2), (kill.VictimId, kill.SourceId, kill.OriginalSourceId, kill.State, kill.BodyPart, kill.VictimSide));
        Assert.Equal("ShinR", kill.BodyPartName);   // HROT's own name of its part 3, which the gateway passes on by name

        // a remote kill (it arrived over the network: the source may be unknown, the part not stated)
        h.PublishForNextFrame(new ManDownedEvent { Victim = victim, Source = Entity.Null, OriginalSource = Entity.Null, State = InjuryState.Incapacitated, HasBodyPart = 0, VictimSide = ForceId.Neutral, IsRemote = true });
        var remote = Assert.Single(h.GatherEvents().Of<HrotManDowned>());
        Assert.Equal((1201, 0, 0, "Incapacitated", 0, 3), (remote.VictimId, remote.SourceId, remote.OriginalSourceId, remote.State, remote.BodyPart, remote.VictimSide));
        Assert.Equal(string.Empty, remote.BodyPartName);

        // a frame without events sends none
        h.World.Bus.SwapBuffers();
        Assert.Empty(h.GatherEvents().Of<HrotManDowned>());
    }

    [Fact]
    public void A_player_message_is_sent_with_its_addressing_and_a_long_text_is_cut()
    {
        using var h = new Stage();
        h.PublishMessage(new PlayerMessageEvent
        {
            MessageId = 7, OriginNodeId = (NodeId)12, ToKind = "Side", ToValue = (int)ForceId.Hostile, Text = "Hold the line", Seconds = 4f, Priority = 2, SentAt = new SimInstant(90.5), Source = 3001,
        });

        var first = Assert.Single(h.GatherEvents().Of<HrotPlayerMessage>());
        Assert.Equal((7, 12, "Side", 2, "Hold the line", 4f, (byte)2, 90.5, 3001), (first.MessageId, first.OriginNode, first.To, first.ToValue, first.Text, first.Seconds, first.Priority, first.SentAt,
            first.SourceId));   // a side crosses as the contract's force number: Hostile is 2

        h.PublishMessage(new PlayerMessageEvent { MessageId = 8, ToKind = "Unit", ToValue = 1201, Text = new string('t', 300) });

        var second = Assert.Single(h.GatherEvents().Of<HrotPlayerMessage>());
        Assert.Equal(("Unit", 1201, 256), (second.To, second.ToValue, second.Text.Length));
        h.World.Bus.SwapBuffers();
        Assert.Empty(h.GatherEvents().Of<HrotPlayerMessage>());
    }

    // ---- statuses and the exercise --------------------------------------------------------------------------------------------

    [Fact]
    public void A_rule_status_is_sent_when_it_changes_and_removed_when_its_entity_goes()
    {
        using var h = new Stage();
        var rule = h.AddStatus(3001, Objective(), subject: 77, parent: 5);

        var first = Assert.Single(h.Gather(10.0).Of<HrotStatus>());

        Assert.Equal((3001, "Objective", "Active", "Armed", false, (byte)1), (first.Id, first.Role, first.State, first.TriggerState, first.Closed, first.Owner));
        Assert.Equal((3, 10, 0.5f, 1f, 0.25f, 0.8f, 5f, 12.5), (first.ProgressCur, first.ProgressNeed, first.Value, first.Target, first.Score, first.Pass, first.Points, first.ChangedAt));
        Assert.Equal(("held 3 s", "Hold the bridge", "HoldArea", "mission", (byte)2, true, true, 2f), (first.Reason, first.Title, first.Kind, first.Group, first.Phase, first.Required, first.Scored, first.Weight));
        Assert.Equal((60f, 900f, 130.0, true, (byte)2, 50f, false, (byte)1, (byte)3, 77, 5), (first.HoldFor, first.Deadline, first.ExpectedAt, first.Capture, first.Challenger, first.AreaRadius,
            first.EndsExercise, first.StepCur, first.StepCount, first.SubjectId, first.ParentId));
        Assert.Equal(new HrotVec3(100, 200, 0), first.AreaCentre);
        // HROT's visible mask (bit 1 Friend, bit 2 Hostile) crosses as a mask by the contract's force numbers (bit 1 friendly, bit 2 opposing)
        Assert.Equal((byte)0b110, first.VisibleSides);
        Assert.Equal((byte)0b1000, HudGather.DisMask(0b001));   // HROT's Neutral (bit 0) is the contract's side 3
        Assert.Equal((byte)0b1110, HudGather.DisMask(0b111));

        Assert.Empty(h.Gather(11.0).Of<HrotStatus>());   // unchanged: nothing

        var changed = Objective(ObjectiveState.Achieved);
        changed.ProgressCur = 10;
        h.World.SetComponent(rule, changed);
        var update = Assert.Single(h.Gather(12.0).Of<HrotStatus>());
        Assert.Equal(("Achieved", 10), (update.State, update.ProgressCur));

        h.World.SetComponent(rule, new ObjectiveLinks { Subject = new EntityRef(78), Parent = new EntityRef(5) });   // a link alone is a change
        Assert.Equal(78, Assert.Single(h.Gather(13.0).Of<HrotStatus>()).SubjectId);

        h.World.DestroyEntity(rule);
        var gone = h.Gather(14.0);
        Assert.Equal(3001, Assert.Single(gone.Of<HrotStatusRemoved>()).Id);
        Assert.Empty(h.Gather(15.0).Of<HrotStatusRemoved>());
    }

    [Fact]
    public void A_status_with_no_network_id_is_skipped_and_the_statuses_are_resent_after_a_world_boundary()
    {
        using var h = new Stage();
        var unnamed = h.World.CreateEntity();   // no durable identity: it cannot be asked or linked
        h.World.AddComponent(unnamed, Objective());
        h.AddStatus(3001, Objective());
        Assert.Equal([3001], h.Gather(10.0).Of<HrotStatus>().Select(s => s.Id).ToArray());

        h.Adapter.Reset(h.World);

        Assert.Equal([3001], h.Gather(11.0).Of<HrotStatus>().Select(s => s.Id).ToArray());
    }

    [Fact]
    public void The_exercise_result_is_sent_once_and_on_change()
    {
        using var h = new Stage();
        var e = h.World.CreateEntity();
        h.World.AddComponent(e, new ExerciseResult { Phase = ExercisePhase.Running });

        var first = Assert.Single(h.Gather(10.0).Of<HrotExercise>());
        Assert.Equal(("Running", 0.0, string.Empty, "None"), (first.Phase, first.EndedAt, first.Reason, first.Friendly.Outcome));
        Assert.Empty(h.Gather(11.0).Of<HrotExercise>());

        h.World.SetComponent(e, new ExerciseResult
        {
            Phase = ExercisePhase.Ended, EndedAt = new SimInstant(1800.5), Reason = new FixedString64("Time is up"), Friend = new SideResult { Outcome = SideOutcome.Win, Points = 40f, Score = 0.9f },
            Hostile = new SideResult { Outcome = SideOutcome.Lose, Points = 10f, Score = 0.2f },
        });
        var ended = Assert.Single(h.Gather(12.0).Of<HrotExercise>());
        Assert.Equal(("Ended", 1800.5, "Time is up"), (ended.Phase, ended.EndedAt, ended.Reason));
        Assert.Equal(("Win", 40f, 0.9f), (ended.Friendly.Outcome, ended.Friendly.Points, ended.Friendly.Score));
        Assert.Equal(("Lose", 10f, 0.2f, "None"), (ended.Opposing.Outcome, ended.Opposing.Points, ended.Opposing.Score, ended.Neutral.Outcome));
    }

    // ---- the HUD's words ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_hud_words_are_those_of_hrots_hud()
    {
        (WeaponCategory Category, string Name)[] kinds =
        [
            (WeaponCategory.SmallArms, "RIFLE"), (WeaponCategory.MachineGun, "MG"), (WeaponCategory.Autocannon, "AUTOCANNON"), (WeaponCategory.TankGun, "CANNON"), (WeaponCategory.Atgm, "MISSILE"),
            (WeaponCategory.Mortar, "MORTAR"), (WeaponCategory.Grenade, "GRENADE"), (WeaponCategory.Bomb, "BOMB"), (WeaponCategory.Rocket, "ROCKETS"), (WeaponCategory.Howitzer, "HOWITZER"),
            (WeaponCategory.Sam, "SAM"), (WeaponCategory.Unknown, "WEAPON"), (WeaponCategory.Flamethrower, "WEAPON"), (WeaponCategory.Bow, "WEAPON"),
        ];
        foreach (var (category, name) in kinds)
        {
            Assert.Equal(name, HudNames.KindOf(category));
        }


        using var w = new EntityRepository();
        w.RegisterComponent<TkbIdentity>();
        var db = new Fdp.Toolkit.Tkb.TkbDatabase();
        db.Register(new Fdp.Interfaces.TkbTemplate("5.56 mm round", 6100));
        db.Register(new Fdp.Interfaces.TkbTemplate("Mortar bomb", 6101));
        db.Register(new Fdp.Interfaces.TkbTemplate("BMP-2", 100));
        w.SetSingletonManaged<Fdp.Interfaces.ITkbDatabase>(db);
        Assert.Equal("5.56 mm", HudNames.RoundName(w, 6100));   // a trailing " round" dropped
        Assert.Equal("Mortar bomb", HudNames.RoundName(w, 6101));
        Assert.Null(HudNames.RoundName(w, 0));
        Assert.Null(HudNames.RoundName(w, 999));

        var vehicle = w.CreateEntity();
        w.AddComponent(vehicle, new TkbIdentity { TkbType = new TkbTypeId(100) });
        Assert.Equal("Drive BMP-2", HudNames.Label(w, new UseAction(UseVerbKind.Drive, "Drive", 1, true, UseReason.Ok, vehicle, 3f)));
        Assert.Equal("Open door", HudNames.Label(w, new UseAction(UseVerbKind.OpenDoor, "Open door", 4, true, UseReason.Ok, vehicle, 3f)));   // a verb that names no type keeps its label
        Assert.Equal("Drive", HudNames.Label(w, new UseAction(UseVerbKind.Drive, "Drive", 1, true, UseReason.Ok, Entity.Null, 3f)));
    }

    /// <summary>
    /// DPS round 2 HUD: these HROT enumerations cross by name and the dps plugin maps them (<c>LookNames.ByName</c>, the contract's enumerators carry the same names). A value HROT
    /// appends or renames fails this test until the contract and the plugin know it.
    /// </summary>
    [Fact]
    public void The_hud_enumerations_the_plugins_map_by_name_are_unchanged()
    {
        Assert.Equal(["Sight", "Lock"], Enum.GetNames<Fdp.Toolkit.Combat.OperatorFireMode>());
        Assert.Equal(["NoOutcome", "Running", "Done", "Refused"], Enum.GetNames<UseOutcome>());
        Assert.Equal(["Ok", "OutOfReach", "Locked", "DoorRefuses", "NotDown", "SeatTaken", "Unfit", "Carrying", "NothingToDo", "Failed", "OtherSide"], Enum.GetNames<UseReason>());
        Assert.Equal(["None", "Searched", "Tracked"], Enum.GetNames<Fdp.Toolkit.Perception.Radar.RadarWarningLevel>());
        Assert.Equal(["Unguided", "Command", "FireAndForget", "InfraRed", "Radar"], Enum.GetNames<GuidanceKind>());
        Assert.Equal(["Objective", "Measure", "Trigger"], Enum.GetNames<ObjectiveRole>());
        Assert.Equal(["Pending", "Active", "Achieved", "Failed", "Inactive", "Skipped"], Enum.GetNames<ObjectiveState>());
        Assert.Equal(["Disarmed", "Armed", "Holding", "Waiting", "Acting", "Fired", "Cooldown", "Done"], Enum.GetNames<TriggerState>());
        Assert.Equal(["Running", "Closing", "Ended"], Enum.GetNames<ExercisePhase>());
        Assert.Equal(["None", "Win", "Lose", "Draw"], Enum.GetNames<SideOutcome>());
        Assert.Equal(["Ok", "Wounded", "Incapacitated", "Dead"], Enum.GetNames<InjuryState>());
    }
}
