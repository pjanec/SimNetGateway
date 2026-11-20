using System.Diagnostics;
using CycloneDDS.Runtime;
using SimNetGateway.DpsClient;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.DpsClient.Tests;

/// <summary>The reference client of DPS round 2 (DD-018): the new caches, the delayed mover playback, the HUD text and the message and objective filters.</summary>
public class DpsRoundTwoTests
{
    private static uint NewDomain() => DpsTestDomains.Next();

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 6000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }

    /// <summary>The gateway side: one writer per round-2 topic, in one partition.</summary>
    private sealed class Gateway : IDisposable
    {
        private readonly DdsParticipant _participant;

        public Gateway(uint domain, string partition)
        {
            _participant = new DdsParticipant(domain, defaultPartition: partition);
            Session = new DdsWriter<D.Session>(_participant);
            Slot = new DdsWriter<D.PlayerSlot>(_participant);
            Own = new DdsWriter<D.OwnUnit>(_participant);
            Entities = new DdsWriter<D.Entity>(_participant);
            Round = new DdsWriter<D.Round>(_participant);
            Look = new DdsWriter<D.MunitionLook>(_participant);
            Jet = new DdsWriter<D.FlameJet>(_participant);
            Spots = new DdsWriter<D.FireSpots>(_participant);
            Marks = new DdsWriter<D.GroundMarks>(_participant);
            Animal = new DdsWriter<D.Animal>(_participant);
            Type = new DdsWriter<D.TkbType>(_participant);
            Building = new DdsWriter<D.BuildingDamage>(_participant);
            Status = new DdsWriter<D.Status>(_participant);
            Exercise = new DdsWriter<D.Exercise>(_participant);
            Lights = new DdsWriter<D.TrafficLights>(_participant);
            Mover = new DdsWriter<D.Mover>(_participant);
            Tracks = new DdsWriter<D.MoverTracks>(_participant);
            Kills = new DdsWriter<D.ManDowned>(_participant);
            Messages = new DdsWriter<D.PlayerMessage>(_participant);
        }

        public DdsWriter<D.Session> Session { get; }

        public DdsWriter<D.PlayerSlot> Slot { get; }

        public DdsWriter<D.OwnUnit> Own { get; }

        public DdsWriter<D.Entity> Entities { get; }

        public DdsWriter<D.Round> Round { get; }

        public DdsWriter<D.MunitionLook> Look { get; }

        public DdsWriter<D.FlameJet> Jet { get; }

        public DdsWriter<D.FireSpots> Spots { get; }

        public DdsWriter<D.GroundMarks> Marks { get; }

        public DdsWriter<D.Animal> Animal { get; }

        public DdsWriter<D.TkbType> Type { get; }

        public DdsWriter<D.BuildingDamage> Building { get; }

        public DdsWriter<D.Status> Status { get; }

        public DdsWriter<D.Exercise> Exercise { get; }

        public DdsWriter<D.TrafficLights> Lights { get; }

        public DdsWriter<D.Mover> Mover { get; }

        public DdsWriter<D.MoverTracks> Tracks { get; }

        public DdsWriter<D.ManDowned> Kills { get; }

        public DdsWriter<D.PlayerMessage> Messages { get; }

        public void Dispose() => _participant.Dispose();
    }

    private static bool Until(DpsWorld world, Func<bool> condition, int timeoutMs = 6000) => WaitFor(() =>
    {
        world.Pump();
        return condition();
    }, timeoutMs);

    private static D.Status Rule(int id, string title = "Hold the bridge", byte owner = 1, byte visible = 0, int parent = 0, D.ObjectiveRole role = D.ObjectiveRole.ObjectiveRoleObjective) => new()
    {
        StatusId = id, Role = role, State = D.ObjectiveState.ObjectiveStateActive, Owner = owner, VisibleSides = visible, ParentId = parent, Title = title, Reason = string.Empty, Kind = string.Empty,
        Group = string.Empty,
    };

    // ---- the caches -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_new_state_topics_are_kept_and_dropped_when_not_alive_and_counted()
    {
        uint domain = NewDomain();
        using var gw = new Gateway(domain, "r1");
        using var world = new DpsWorld(domain, "r1");

        gw.Round.Write(new D.Round { EntityId = 21, MunitionTypeId = 6, ShooterId = 12, Lying = true });
        gw.Look.Write(new D.MunitionLook { MunitionTypeId = 6, Shape = D.MunitionShape.MunitionShapeShell, Tracer = true });
        gw.Jet.Write(new D.FlameJet { EntityId = 12, Mount = 0, RangeM = 60f });
        gw.Jet.Write(new D.FlameJet { EntityId = 12, Mount = 1, RangeM = 20f });
        gw.Spots.Write(new D.FireSpots { EntityId = 11, Spots = [new D.FireSpot { Radius = 2f }] });
        gw.Marks.Write(new D.GroundMarks { EntityId = 31, Marks = [new D.GroundMark { Kind = D.HeatMarkKind.HeatMarkKindBurnedArea }] });
        gw.Animal.Write(new D.Animal { EntityId = 3001, Mode = D.AnimalMode.AnimalModeAlert });
        gw.Type.Write(new D.TkbType { TypeId = 5_000_000_001L, Name = "T-72", CategoryPath = "", DisType = "", Schema = "hrot/1", Json = "{}" });
        gw.Building.Write(new D.BuildingDamage { Building = "range/H2", Collapsed = true, Breaches = [], Windows = [] });
        gw.Status.Write(Rule(3001));
        gw.Exercise.Write(new D.Exercise { Phase = D.ExercisePhase.ExercisePhaseEnded, Reason = "Time is up" });
        gw.Lights.Write(new D.TrafficLights { SignalId = 4, State = D.TrafficLightState.TrafficLightStateRed });

        Assert.True(Until(world, () => world.Rounds.Count == 1 && world.MunitionLooks.Count == 1 && world.FlameJets.Count == 2 && world.FireSpots.Count == 1 && world.GroundMarks.Count == 1
            && world.Animals.Count == 1 && world.TkbTypes.Count == 1 && world.BuildingDamage.Count == 1 && world.Statuses.Count == 1 && world.Exercise is not null && world.TrafficLights.Count == 1));
        Assert.Equal((12, 6L), (world.Rounds[21].ShooterId, world.Rounds[21].MunitionTypeId));
        Assert.True(world.MunitionLooks[6].Tracer);
        Assert.Equal(60f, world.FlameJets[(12, 0)].RangeM);   // a jet is keyed by entity and mount
        Assert.Equal(20f, world.FlameJets[(12, 1)].RangeM);
        Assert.Equal(D.AnimalMode.AnimalModeAlert, world.Animals[3001].Mode);
        Assert.Equal("T-72", world.TkbTypes[5_000_000_001L].Name);
        Assert.True(world.BuildingDamage["range/H2"].Collapsed);
        Assert.Equal(D.ExercisePhase.ExercisePhaseEnded, world.Exercise!.Value.Phase);
        Assert.Equal(D.TrafficLightState.TrafficLightStateRed, world.TrafficLights[4].State);
        foreach (var topic in new[] { DpsProtocol_Round, "DPS_MunitionLook", "DPS_FlameJet", "DPS_FireSpots", "DPS_GroundMarks", "DPS_Animal", "DPS_TkbType", "DPS_BuildingDamage", "DPS_Status", "DPS_Exercise", "DPS_TrafficLights" })
        {
            Assert.True(world.Received[topic] >= 1, topic);   // the headless summary counts them
        }

        // not alive = gone (the keyless Exercise topic has no instance to dispose: it ends with the gateway's writers at a world boundary)
        gw.Round.DisposeInstance(new D.Round { EntityId = 21 });
        gw.Jet.DisposeInstance(new D.FlameJet { EntityId = 12, Mount = 0 });
        gw.Building.DisposeInstance(new D.BuildingDamage { Building = "range/H2", Breaches = [], Windows = [] });
        gw.Status.DisposeInstance(Rule(3001));
        Assert.True(Until(world, () => world.Rounds.Count == 0), "round");
        Assert.True(Until(world, () => world.FlameJets.Count == 1), "jet");
        Assert.True(Until(world, () => world.BuildingDamage.Count == 0), "building");
        Assert.True(Until(world, () => world.Statuses.Count == 0), "status");
        Assert.True(world.FlameJets.ContainsKey((12, 1)));
    }

    private const string DpsProtocol_Round = "DPS_Round";

    [Fact]
    public void A_new_world_epoch_drops_every_new_cache()
    {
        uint domain = NewDomain();
        using var gw = new Gateway(domain, "r2");
        using var world = new DpsWorld(domain, "r2");
        gw.Session.Write(new D.Session { WorldEpoch = 1, Terrain = string.Empty, StationName = string.Empty });
        gw.Round.Write(new D.Round { EntityId = 21 });
        gw.Mover.Write(new D.Mover { MoverId = 101, Kind = D.MoverKind.MoverKindCar });
        gw.Status.Write(Rule(3001));
        Assert.True(Until(world, () => world.WorldEpoch == 1 && world.Rounds.Count == 1 && world.Movers.Count == 1 && world.Statuses.Count == 1));

        gw.Session.Write(new D.Session { WorldEpoch = 2, Terrain = string.Empty, StationName = string.Empty });

        Assert.True(Until(world, () => world.WorldEpoch == 2));
        Assert.Empty(world.Rounds);
        Assert.Empty(world.Statuses);
        Assert.Equal(0, world.Movers.Count);
        Assert.Empty(world.KillFeed);
    }

    // ---- events: kill feed and messages ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_kill_feed_keeps_the_last_five_lines_with_the_names_of_the_entities()
    {
        uint domain = NewDomain();
        using var gw = new Gateway(domain, "r3");
        using var world = new DpsWorld(domain, "r3");
        gw.Entities.Write(new D.Entity { EntityId = 1, Name = "Alpha" });
        gw.Entities.Write(new D.Entity { EntityId = 2, Name = "Bravo" });
        Assert.True(Until(world, () => world.Entities.Count == 2));
        Assert.True(await gw.Kills.WaitForReaderAsync(TimeSpan.FromSeconds(5)));

        for (int i = 0; i < 7; i++)
        {
            gw.Kills.Write(new D.ManDowned { VictimId = i == 6 ? 2 : 100 + i, SourceId = i == 6 ? 1 : 0, State = i == 6 ? D.Injury.InjuryDead : D.Injury.InjuryIncapacitated });
        }

        Assert.True(Until(world, () => world.Received["DPS_ManDowned"] == 7));
        Assert.Equal(5, world.KillFeed.Count);   // the last five only
        Assert.Equal("Alpha killed Bravo", world.KillFeed[^1]);
        Assert.Equal("#102 went down", world.KillFeed[0]);   // the oldest kept: the third of seven
    }

    [Fact]
    public async Task A_message_is_shown_only_to_who_it_is_addressed_to_once_and_for_its_time()
    {
        uint domain = NewDomain();
        double now = 100.0;
        using var gw = new Gateway(domain, "r4");
        using var world = new DpsWorld(domain, "r4", clock: () => now);
        gw.Slot.Write(new D.PlayerSlot { State = D.SlotState.SlotStatePlaying, UnitId = 1201, Side = 1, Reason = string.Empty, Offers = [] });
        Assert.True(Until(world, () => world.Slot is not null));
        Assert.True(await gw.Messages.WaitForReaderAsync(TimeSpan.FromSeconds(5)));

        D.PlayerMessage Message(int id, D.MessageAddress to, int value, string text, float seconds = 6f, int origin = 0) =>
            new() { MessageId = id, OriginNode = origin, To = to, ToValue = value, Text = text, Seconds = seconds };

        gw.Messages.Write(Message(1, D.MessageAddress.MessageAddressSide, 2, "for the other side"));
        Assert.True(Until(world, () => world.Received["DPS_PlayerMessage"] == 1));
        Assert.Null(world.CurrentMessage(now));

        gw.Messages.Write(Message(2, D.MessageAddress.MessageAddressSide, 1, "for my side"));
        Assert.True(Until(world, () => world.Received["DPS_PlayerMessage"] == 2));
        Assert.Equal("for my side", world.CurrentMessage(now)!.Value.Text);

        gw.Messages.Write(Message(3, D.MessageAddress.MessageAddressInstructor, 0, "for the instructor"));
        gw.Messages.Write(Message(4, D.MessageAddress.MessageAddressUnit, 1201, "for my unit", seconds: 3f));
        Assert.True(Until(world, () => world.Received["DPS_PlayerMessage"] == 4));
        Assert.Equal("for my unit", world.CurrentMessage(now)!.Value.Text);   // the newest addressed to the player

        gw.Messages.Write(Message(4, D.MessageAddress.MessageAddressUnit, 1201, "for my unit", seconds: 3f));   // a repeat of the same id and origin
        Assert.True(Until(world, () => world.Received["DPS_PlayerMessage"] == 5));
        now = 102.0;
        Assert.Equal("for my unit", world.CurrentMessage(now)!.Value.Text);
        now = 103.5;   // its 3 s are over: the earlier one, still within its 6 s
        Assert.Equal("for my side", world.CurrentMessage(now)!.Value.Text);
        now = 107.0;
        Assert.Null(world.CurrentMessage(now));
    }

    [Fact]
    public void The_message_filter_is_the_one_of_hrots_hud()
    {
        var viewer = new DpsViewer(1201, 1, StationId: 7);
        D.PlayerMessage To(D.MessageAddress a, int v) => new() { To = a, ToValue = v };

        Assert.True(DpsHud.Addressed(To(D.MessageAddress.MessageAddressAll, 0), viewer));
        Assert.True(DpsHud.Addressed(To(D.MessageAddress.MessageAddressSide, 1), viewer));
        Assert.False(DpsHud.Addressed(To(D.MessageAddress.MessageAddressSide, 2), viewer));
        Assert.True(DpsHud.Addressed(To(D.MessageAddress.MessageAddressUnit, 1201), viewer));
        Assert.False(DpsHud.Addressed(To(D.MessageAddress.MessageAddressUnit, 1202), viewer));
        Assert.True(DpsHud.Addressed(To(D.MessageAddress.MessageAddressStation, 7), viewer));
        Assert.False(DpsHud.Addressed(To(D.MessageAddress.MessageAddressStation, 8), viewer));
        Assert.False(DpsHud.Addressed(To(D.MessageAddress.MessageAddressStation, 7), viewer with { StationId = -1 }));   // a client that does not know its station never shows one
        Assert.False(DpsHud.Addressed(To(D.MessageAddress.MessageAddressInstructor, 0), viewer));
        Assert.True(DpsHud.Addressed(To(D.MessageAddress.MessageAddressInstructor, 0), viewer with { Instructor = true }));
        Assert.False(DpsHud.Addressed(To(D.MessageAddress.MessageAddressUnknown, 0), viewer));
        Assert.False(DpsHud.Addressed(To(D.MessageAddress.MessageAddressUnit, 0), new DpsViewer(0, 0)));   // no unit: a message "to unit 0" is for nobody
    }

    // ---- the HUD text ---------------------------------------------------------------------------------------------------------

    private static D.OwnUnit Own(byte slot = 1) => new()
    {
        UnitId = 1201, ActiveSlot = slot, FireMode = D.FireMode.FireModeLock, CanUse = true, UseLabel = "Open door", UseSeq = 9, UseOutcome = D.UseOutcome.UseOutcomeRefused,
        UseReason = D.UseReason.UseReasonLocked,
        Ammo =
        [
            new D.AmmoEntry { Slot = 0, Weapon = "RIFLE", AmmoType = "5.56 mm", Rounds = 30, Max = 210, Magazine = 3, LoadedRounds = 28, HeatPercent = 10 },
            new D.AmmoEntry { Slot = 1, Weapon = "MG", AmmoType = "7.62 mm", Rounds = 120, Max = 200, Magazine = -1, LoadedRounds = -1, Busy = true, Overheated = true, HeatPercent = 87 },
        ],
    };

    [Fact]
    public void The_weapon_line_names_the_weapon_round_rounds_state_and_fire_mode_of_the_selected_mount()
    {
        Assert.Equal("> 2 MG  7.62 mm  120/200  LOADING  OVERHEATED 87%  LOCK", DpsHud.WeaponLine(Own(slot: 1)));
        var rifle = Own(slot: 0);
        rifle.FireMode = D.FireMode.FireModeSight;
        Assert.Equal("> 1 RIFLE  5.56 mm 28  30/210  [3]  SIGHT", DpsHud.WeaponLine(rifle));
        var hot = Own(slot: 0);
        hot.Ammo[0] = hot.Ammo[0] with { HeatPercent = 40 };
        Assert.Contains("HEAT 40%", DpsHud.WeaponLine(hot));
        Assert.Equal("> 1 RIFLE  5.56 mm 28  30/210  [3]  LOCK", DpsHud.WeaponLine(Own(slot: 9)));   // an unknown slot falls back to the first weapon
        Assert.Null(DpsHud.WeaponLine(new D.OwnUnit { Ammo = [] }));
    }

    [Fact]
    public void The_use_prompt_and_the_verdict_which_is_shown_only_for_the_clients_own_last_press()
    {
        var own = Own();

        Assert.Equal("F  Open door", DpsHud.UsePrompt(own));
        Assert.Null(DpsHud.UsePrompt(own with { CanUse = false }));
        Assert.Equal("Open door: locked", DpsHud.UseVerdict(own, 9, "Open door"));
        Assert.Null(DpsHud.UseVerdict(own, 8, "Open door"));   // the answer is for another press
        Assert.Null(DpsHud.UseVerdict(own, null, "Open door"));   // the client never pressed
        Assert.Equal("Open door: done", DpsHud.UseVerdict(own with { UseOutcome = D.UseOutcome.UseOutcomeDone }, 9, "Open door"));
        Assert.Equal("Open door: refused", DpsHud.UseVerdict(own with { UseReason = D.UseReason.UseReasonOk }, 9, "Open door"));
        Assert.Null(DpsHud.UseVerdict(own with { UseOutcome = D.UseOutcome.UseOutcomeRunning }, 9, "Open door"));   // still running: nothing yet
        Assert.Equal("Open door: locked", DpsHud.UseVerdict(own with { UseSeq = 4 }, 256 + 4, "Open door"));   // the counter wraps as a byte
    }

    [Fact]
    public void A_use_press_changes_the_counter_and_remembers_the_label_for_the_verdict()
    {
        uint domain = NewDomain();
        using var gw = new Gateway(domain, "r5");
        using var world = new DpsWorld(domain, "r5");
        gw.Own.Write(Own() with { UseVerb = 3, UseTargetId = 55 });
        Assert.True(Until(world, () => world.Own is not null));
        Assert.Null(world.UseVerdict());   // no press yet

        world.PressUse();

        Assert.Equal("F  Open door", world.UsePrompt());
        Assert.Null(world.UseVerdict());   // the unit's UseSeq 9 is not the press (1)
        gw.Own.Write(Own() with { UseVerb = 3, UseTargetId = 55, UseSeq = 1, UseOutcome = D.UseOutcome.UseOutcomeDone });
        Assert.True(Until(world, () => world.Own is { UseSeq: 1 }));
        Assert.Equal("Open door: done", world.UseVerdict());
    }

    [Fact]
    public void An_objective_is_shown_by_its_visible_sides_or_its_owner_only_as_a_titled_leaf()
    {
        D.Status Of(byte owner, byte visible, string title = "T") => Rule(1, title, owner, visible);

        Assert.True(DpsHud.VisibleTo(Of(1, 0), 1, false));   // mine
        Assert.False(DpsHud.VisibleTo(Of(2, 0), 1, false));   // the enemy's
        Assert.True(DpsHud.VisibleTo(Of(3, 0), 1, false));   // neutral
        Assert.True(DpsHud.VisibleTo(Of(2, 0b010), 1, false));   // an enemy objective that names my side (bit 1)
        Assert.False(DpsHud.VisibleTo(Of(1, 0b100), 1, false));   // mine, but the mask says side 2 only: the mask wins
        Assert.False(DpsHud.VisibleTo(Of(1, 0), 1, true));   // a heading, not a task
        Assert.False(DpsHud.VisibleTo(Of(1, 0, title: ""), 1, false));   // no title
        Assert.False(DpsHud.VisibleTo(Rule(1, role: D.ObjectiveRole.ObjectiveRoleTrigger), 1, false));   // a trigger is not shown
        Assert.Equal("T - Active", DpsHud.StatusLine(Of(1, 0)));
    }

    [Fact]
    public void The_worlds_objectives_are_the_leaves_visible_to_the_side_of_the_slot()
    {
        uint domain = NewDomain();
        using var gw = new Gateway(domain, "r6");
        using var world = new DpsWorld(domain, "r6");
        gw.Slot.Write(new D.PlayerSlot { State = D.SlotState.SlotStatePlaying, UnitId = 1201, Side = 1, Reason = string.Empty, Offers = [] });
        gw.Status.Write(Rule(10, "Mission"));
        gw.Status.Write(Rule(11, "Hold the bridge", parent: 10));
        gw.Status.Write(Rule(12, "Enemy aim", owner: 2, parent: 10));
        gw.Status.Write(Rule(13, "Watch the road", owner: 3, parent: 10));

        Assert.True(Until(world, () => world.Statuses.Count == 4 && world.Slot is not null));

        Assert.Equal(["Hold the bridge", "Watch the road"], world.VisibleObjectives().Select(s => s.Title).ToArray());   // 10 is a parent, 12 is the enemy's
    }

    // ---- delayed mover playback -----------------------------------------------------------------------------------------------

    private static D.MoverTracks Batch(double simTime, params (int Id, double Time, float X, float Speed)[] knots) => new()
    {
        SimTime = simTime,
        Knots = [.. knots.Select(k => new D.MoverKnot { MoverId = k.Id, Dt = (float)(k.Time - simTime), X = k.X, Y = 20f, Z = 0f, Speed = k.Speed })],
    };

    [Fact]
    public void A_mover_is_played_behind_the_newest_knot_between_two_real_knots_and_never_past_the_newest()
    {
        var play = new MoverPlayback();
        play.SetMover(new D.Mover { MoverId = 101, Kind = D.MoverKind.MoverKindCar });
        Assert.Equal(1.2, MoverPlayback.PlaybackDelay);

        // a car at 10 m/s with a knot every second, the batches come as the simulation runs
        for (int s = 0; s <= 6; s++)
        {
            play.AddBatch(Batch(s, (101, s, 10f * s, 10f)), clockNow: 50.0 + s);
        }

        double clock = 56.0;   // the newest batch (sim 6) has just arrived
        Assert.Equal(6.0, play.NewestKnotTime);
        Assert.Equal(6.0 - MoverPlayback.PlaybackDelay, play.PlaybackTime(clock), 9);

        Assert.True(play.TryPose(101, clock, out var pose));
        Assert.Equal(10.0 * (6.0 - 1.2), pose.X, 6);   // interpolated: 1.2 s behind the newest, not at it
        Assert.True(pose.X < 60.0, "never as far as the newest knot");

        // the client's clock runs on between batches: the pose moves, and the sim clock is capped (a paused simulation stands still)
        Assert.True(play.TryPose(101, clock + 0.1, out var later));
        Assert.Equal(10.0 * (6.1 - 1.2), later.X, 6);
        Assert.True(play.TryPose(101, clock + 30.0, out var paused));
        Assert.Equal(10.0 * (6.0 + MoverPlayback.MaxExtrapolationS - 1.2), paused.X, 6);   // still behind the newest knot
        Assert.True(paused.X < 60.0);
    }

    [Fact]
    public void A_mover_shows_from_its_second_knot_and_only_with_its_state_and_goes_with_it()
    {
        var play = new MoverPlayback();
        play.AddBatch(Batch(10, (7, 10, 0f, 5f)), 1.0);   // the knots of a mover whose state has not arrived
        Assert.Empty(play.Visible(1.0));

        play.SetMover(new D.Mover { MoverId = 7, Kind = D.MoverKind.MoverKindPedestrian });
        Assert.Empty(play.Visible(1.0));   // one knot is no curve

        play.AddBatch(Batch(11, (7, 11, 5f, 5f)), 2.0);
        Assert.Empty(play.Visible(2.0));   // two knots, but playback (11 - 1.2 = 9.8 s) has not reached the first one (10 s): it is not there yet, never extrapolated backwards
        play.AddBatch(Batch(12, (7, 12, 10f, 5f)), 3.0);
        var shown = Assert.Single(play.Visible(3.0));
        Assert.Equal(D.MoverKind.MoverKindPedestrian, shown.Info.Kind);
        Assert.Equal(5.0 * (12 - 1.2 - 10), shown.Pose.X, 6);

        play.Remove(7);
        Assert.Empty(play.Visible(3.0));
        Assert.Equal(0, play.Count);
    }

    [Fact]
    public void A_late_or_repeated_knot_is_ignored_the_curve_turns_and_old_knots_are_trimmed()
    {
        var play = new MoverPlayback();
        play.SetMover(new D.Mover { MoverId = 1, Kind = D.MoverKind.MoverKindCar });
        play.AddBatch(Batch(2, (1, 1, 10f, 10f), (1, 2, 20f, 10f)), 1.0);
        play.AddBatch(Batch(2.5, (1, 2, 999f, 10f), (1, 1.5, 999f, 10f)), 1.5);   // not later than the newest knot: ignored
        play.AddBatch(Batch(4, (1, 3, 30f, 10f), (1, 4, 40f, 10f)), 3.0);
        Assert.True(play.TryPose(1, 3.0, out var pose));
        Assert.Equal(28.0, pose.X, 6);   // 2.8 s on the curve between the knots at 2 and 3 (an ignored 999 would show here)

        // many knots over a long time: the track keeps only a few seconds behind the playback
        for (int s = 5; s <= 200; s++)
        {
            play.AddBatch(Batch(s, (1, s, 10f * s, 10f)), 10.0 + s);
            Assert.True(play.TryPose(1, 10.0 + s, out var p));
            Assert.Equal(10.0 * (s - 1.2), p.X, 4);
        }
    }

    [Fact]
    public async Task Movers_and_their_batches_arrive_over_dds_and_are_drawn_delayed()
    {
        uint domain = NewDomain();
        double clock = 100.0;
        using var gw = new Gateway(domain, "r7");
        using var world = new DpsWorld(domain, "r7", clock: () => clock);
        gw.Mover.Write(new D.Mover { MoverId = 101, Kind = D.MoverKind.MoverKindCar, TypeId = 8851, ColourRgba = 0xB02A2AFF, Length = 4.5f, Width = 1.8f });
        Assert.True(Until(world, () => world.Movers.InfoOf(101) is not null));
        Assert.True(await gw.Tracks.WaitForReaderAsync(TimeSpan.FromSeconds(5)));

        for (int s = 0; s <= 5; s++)
        {
            gw.Tracks.Write(Batch(s, (101, s, 10f * s, 10f)));
        }

        Assert.True(Until(world, () => world.Received["DPS_MoverTracks"] == 6));
        var shown = Assert.Single(world.Movers.Visible(clock));
        Assert.Equal(0xB02A2AFFu, shown.Info.ColourRgba);
        Assert.Equal(10.0 * (5 - 1.2), shown.Pose.X, 4);

        gw.Mover.DisposeInstance(new D.Mover { MoverId = 101 });
        Assert.True(Until(world, () => world.Movers.InfoOf(101) is null));
        Assert.Empty(world.Movers.Visible(clock));
    }
}
