using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>DPS round 2, the HUD (DD-018 3.1): the own unit's extended data, the kill feed, player messages, rule statuses and the exercise result.</summary>
public class DpsHudTests
{
    private static string Id(int id) => id.ToString();

    private static HrotStatus Status(int id, string state = "Active", string title = "Hold the bridge") =>
        new(id, "Objective", state, "Disarmed", false, 1, 3, 10, 0.5f, 1f, 0.25f, 0.8f, 5f, 12.5, "held 3 s", title, "HoldArea", "mission", 2, true, true, 2f, 0b1010, 0, 0, 60f, 900f, 130.0,
            true, 2, new HrotVec3(100, 200, 0), 50f, false, 77, 5);

    [Fact]
    public void Own_unit_carries_the_extended_hud_data()
    {
        uint domain = Watch.NewDomain();
        var station = new DpsInboundTests.RecordingStation { Slot = new PlayerSlotSnapshot(PlayerSlotState.Playing, 1201, 0.0, 1) };
        station.Unit = new PlayerUnitStatus(80.0, 100.0,
            [new PlayerWeapon(0, "MG", "7.62 mm tracer", 6004, 120, 200, 3, 25, true, true, 87), new PlayerWeapon(1, "HANDS", "", 0, 0, 0, -1, -1, false, false, 0)], 3, 55,
            ActiveMount: 1, FireMode: "Lock", UseLabel: "Drive BMP-2", UseSeq: 9, UseOutcome: "Refused", UseReason: "Locked", SpreadRad: 0.004f, RadarWarning: "Tracked", RadarBearingDeg: 135f,
            MissileLaunch: true, MissileBearingDeg: 270f, MissileTimeToImpactS: 8.5f, MissileSeeker: "InfraRed");
        using var units = new Watch.Watch<D.OwnUnit>(domain, "station-1", u => "unit");
        using var rig = new Rig(domain, station: station);

        DpsInboundTests.Frames(rig);

        Assert.True(units.Until(w => w.Alive.ContainsKey("unit")));
        var u = units.Alive["unit"];
        Assert.Equal((byte)1, u.ActiveSlot);
        Assert.Equal("Drive BMP-2", u.UseLabel);
        Assert.Equal((D.FireMode.FireModeLock, (byte)9, D.UseOutcome.UseOutcomeRefused, D.UseReason.UseReasonLocked), (u.FireMode, u.UseSeq, u.UseOutcome, u.UseReason));
        Assert.Equal(0.004f, u.SpreadRad);
        Assert.Equal((D.RadarWarningLevel.RadarWarningLevelTracked, 135f), (u.RadarWarning, u.RadarBearingDeg));
        Assert.Equal((true, 270f, 8.5f, D.MissileSeeker.MissileSeekerInfraRed), (u.MissileLaunch, u.MissileBearingDeg, u.MissileTimeToImpactS, u.MissileSeeker));
        var mg = u.Ammo[0];
        Assert.Equal(("MG", "7.62 mm tracer", 6004L, 120, 200, 3, 25, true, true, 87), (mg.Weapon, mg.AmmoType, mg.AmmoTypeId, mg.Rounds, mg.Max, mg.Magazine, mg.LoadedRounds, mg.Busy, mg.Overheated, mg.HeatPercent));
        Assert.Equal(("HANDS", -1, -1), (u.Ammo[1].Weapon, u.Ammo[1].Magazine, u.Ammo[1].LoadedRounds));
        Assert.Equal(1, rig.Transport.OwnUnit.Written);

        // each new field alone is a change that is written (the use verdict, the fire mode, the heat)
        station.Unit = station.Unit with { UseOutcome = "Done" };
        DpsInboundTests.Frames(rig);
        Assert.Equal(2, rig.Transport.OwnUnit.Written);
        Assert.True(units.Until(w => w.Alive["unit"].UseOutcome == D.UseOutcome.UseOutcomeDone));
        station.Unit = station.Unit with { FireMode = "Sight" };
        DpsInboundTests.Frames(rig);
        Assert.Equal(3, rig.Transport.OwnUnit.Written);
        station.Unit = station.Unit with { Ammo = [station.Unit.Ammo[0] with { HeatPercent = 88 }, station.Unit.Ammo[1]] };
        DpsInboundTests.Frames(rig);
        Assert.Equal(4, rig.Transport.OwnUnit.Written);

        // the counting-down time to impact is not a change below a second, and is one beyond it
        station.Unit = station.Unit with { MissileTimeToImpactS = 8.1f };
        DpsInboundTests.Frames(rig);
        Assert.Equal(4, rig.Transport.OwnUnit.Written);
        station.Unit = station.Unit with { MissileTimeToImpactS = 7.4f };
        DpsInboundTests.Frames(rig);
        Assert.Equal(5, rig.Transport.OwnUnit.Written);

        // an unknown name is the unknown value, and a long name is cut to its bound
        station.Unit = station.Unit with { UseReason = "Haunted", MissileSeeker = "Laser", UseLabel = new string('x', 40) };
        DpsInboundTests.Frames(rig);
        Assert.True(units.Until(w => w.Alive["unit"].UseReason == D.UseReason.UseReasonUnknown));
        Assert.Equal(D.MissileSeeker.MissileSeekerUnknown, units.Alive["unit"].MissileSeeker);
        Assert.Equal(32, units.Alive["unit"].UseLabel.Length);
    }

    private static PlayerUseAction Verb(string verb, string label, bool available = true, string reason = "Ok", bool holdOnly = false) => new(verb, label, available, reason, holdOnly);

    /// <summary>DD-029 D-3: <c>OwnUnit.Actions</c> is the producer's whole offered list, in its order, bounded 8 / 32, a refused verb carrying its reason; written on change only, also when the
    /// station reuses and refills one list; 0 B when nothing changed.</summary>
    [Fact]
    public void Own_unit_carries_the_whole_verb_list_in_order_bounded_on_change_only_and_free_when_steady()
    {
        uint domain = Watch.NewDomain();
        var station = new DpsInboundTests.RecordingStation { Slot = new PlayerSlotSnapshot(PlayerSlotState.Playing, 1201, 0.0, 1) };
        var list = new List<PlayerUseAction>
        {
            Verb("Drive", "Drive BMP-2", available: false, reason: "SeatTaken"),
            Verb("Board", "Board BMP-2"),
            Verb("Drop", "Drop", holdOnly: true),
        };
        station.Unit = new PlayerUnitStatus(80.0, 100.0, [], 0, 55, Actions: list);
        using var units = new Watch.Watch<D.OwnUnit>(domain, "station-1", u => "unit");
        using var rig = new Rig(domain, station: station);

        DpsInboundTests.Frames(rig);

        Assert.True(units.Until(w => w.Alive.ContainsKey("unit") && w.Alive["unit"].Actions.Count == 3));
        var actions = units.Alive["unit"].Actions;
        Assert.Equal([D.UseVerb.UseVerbDrive, D.UseVerb.UseVerbBoard, D.UseVerb.UseVerbDrop], actions.Select(a => a.Verb));   // HROT's order
        Assert.Equal(["Drive BMP-2", "Board BMP-2", "Drop"], actions.Select(a => a.Label));
        Assert.Equal((false, D.UseReason.UseReasonSeatTaken, false), (actions[0].Available, actions[0].Reason, actions[0].HoldOnly));   // a refused verb carries its reason
        Assert.Equal((true, D.UseReason.UseReasonOk, false), (actions[1].Available, actions[1].Reason, actions[1].HoldOnly));
        Assert.True(actions[2].HoldOnly);
        Assert.Equal(1, rig.Transport.OwnUnit.Written);

        // the station reuses ONE list and refills it with the same verbs: nothing the client sees changed, nothing is written
        list.Clear();
        list.AddRange([Verb("Drive", "Drive BMP-2", available: false, reason: "SeatTaken"), Verb("Board", "Board BMP-2"), Verb("Drop", "Drop", holdOnly: true)]);
        DpsInboundTests.Frames(rig);
        Assert.Equal(1, rig.Transport.OwnUnit.Written);

        // a verb alone changing (the seat is free now), a reorder, and a shorter list are each a change
        list[0] = Verb("Drive", "Drive BMP-2");
        DpsInboundTests.Frames(rig);
        Assert.Equal(2, rig.Transport.OwnUnit.Written);
        (list[0], list[1]) = (list[1], list[0]);
        DpsInboundTests.Frames(rig);
        Assert.Equal(3, rig.Transport.OwnUnit.Written);
        list.RemoveAt(2);
        DpsInboundTests.Frames(rig);
        Assert.Equal(4, rig.Transport.OwnUnit.Written);
        Assert.True(units.Until(w => w.Alive["unit"].Actions.Count == 2 && w.Alive["unit"].Actions[0].Verb == D.UseVerb.UseVerbBoard));

        // bounds: more than 8 entries are cut to 8, a long label to 32, an unknown verb or reason name is the unknown value
        list.Clear();
        for (int i = 0; i < 10; i++)
        {
            list.Add(Verb(i == 0 ? "Teleport" : "Drive", new string('l', 40), reason: i == 0 ? "Haunted" : "Ok"));
        }

        DpsInboundTests.Frames(rig);
        Assert.True(units.Until(w => w.Alive["unit"].Actions.Count == 8));
        var cut = units.Alive["unit"].Actions;
        Assert.Equal((D.UseVerb.UseVerbNone, D.UseReason.UseReasonUnknown, 32), (cut[0].Verb, cut[0].Reason, cut[0].Label.Length));

        // an empty list is a change from a list, and is empty on the wire
        list.Clear();
        DpsInboundTests.Frames(rig);
        Assert.True(units.Until(w => w.Alive["unit"].Actions.Count == 0));

        // steady: nothing changes, no wire value is built and nothing is allocated
        for (int i = 0; i < 50; i++)
        {
            rig.Transport.Pump();
        }

        int written = rig.Transport.OwnUnit.Written;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            rig.Transport.Pump();
        }

        Assert.Equal(0L, (GC.GetAllocatedBytesForCurrentThread() - before) / 300);
        Assert.Equal(written, rig.Transport.OwnUnit.Written);
        list.Add(Verb("Drive", "Drive BMP-2"));   // and a steady list with entries is free too
        DpsInboundTests.Frames(rig);
        Assert.True(units.Until(w => w.Alive["unit"].Actions.Count == 1));
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            rig.Transport.Pump();
        }

        Assert.Equal(0L, (GC.GetAllocatedBytesForCurrentThread() - before) / 300);
    }

    [Fact]
    public void A_kill_is_an_event_with_ids_state_body_part_and_side()
    {
        uint domain = Watch.NewDomain();
        using var kills = new Watch.Watch<D.ManDowned>(domain, "station-1", k => "kill");
        using var rig = new Rig(domain);

        Assert.True(kills.Until(w =>
        {
            rig.Send(new HrotManDowned(42.5, 1201, 1202, 1203, "Dead", 4, 2));
            rig.Send(new HrotManDowned(43.0, 1204, 0, 0, "Incapacitated", 0, 3));
            rig.Send(new HrotManDowned(44.0, 1205, 0, 0, "Vaporised", 0, 1));
            return w.Events.Count >= 3;
        }));

        var dead = kills.Events.First(k => k.VictimId == 1201);
        Assert.Equal((42.5, 1201, 1202, 1203, D.Injury.InjuryDead, (byte)4, (byte)2), (dead.SimTime, dead.VictimId, dead.SourceId, dead.OriginalSourceId, dead.State, dead.BodyPart, dead.VictimSide));
        var down = kills.Events.First(k => k.VictimId == 1204);
        Assert.Equal((D.Injury.InjuryIncapacitated, 0, (byte)0, (byte)3), (down.State, down.SourceId, down.BodyPart, down.VictimSide));
        Assert.Equal(D.Injury.InjuryOk, kills.Events.First(k => k.VictimId == 1205).State);   // a state HROT appends later: not a different state
    }

    [Fact]
    public void A_player_message_keeps_its_addressing_and_its_text_is_bounded()
    {
        uint domain = Watch.NewDomain();
        using var messages = new Watch.Watch<D.PlayerMessage>(domain, "station-1", k => "msg");
        using var rig = new Rig(domain);

        Assert.True(messages.Until(w =>
        {
            rig.Send(new HrotPlayerMessage(7, 0, "Side", 1, "Hold the line", 6f, 2, 90.5, 3001));
            rig.Send(new HrotPlayerMessage(8, 12, "Instructor", 0, new string('t', 300), 4f, 0, 91.0, 0));
            rig.Send(new HrotPlayerMessage(9, 0, "Squad", 5, "?", 1f, 0, 92.0, 0));
            rig.Send(new HrotPlayerMessage(10, 0, "All", 0, string.Empty, 1f, 0, 93.0, 0));
            return w.Events.Count >= 4;
        }));

        var side = messages.Events.First(m => m.MessageId == 7);
        Assert.Equal((7, 0, D.MessageAddress.MessageAddressSide, 1, "Hold the line", 6f, (byte)2, 90.5, 3001),
            (side.MessageId, side.OriginNode, side.To, side.ToValue, side.Text, side.Seconds, side.Priority, side.SentAt, side.SourceId));
        var instructor = messages.Events.First(m => m.MessageId == 8);
        Assert.Equal((D.MessageAddress.MessageAddressInstructor, 12, 256), (instructor.To, instructor.OriginNode, instructor.Text.Length));
        Assert.Equal(D.MessageAddress.MessageAddressUnknown, messages.Events.First(m => m.MessageId == 9).To);
        Assert.Equal((D.MessageAddress.MessageAddressAll, string.Empty), (messages.Events.First(m => m.MessageId == 10).To, messages.Events.First(m => m.MessageId == 10).Text));
    }

    [Fact]
    public void A_status_maps_every_field_changes_in_place_and_is_disposed_when_it_goes()
    {
        uint domain = Watch.NewDomain();
        using var statuses = new Watch.Watch<D.Status>(domain, "station-1", s => Id(s.StatusId));
        using var rig = new Rig(domain);

        rig.Send(Status(3001));
        rig.Send(Status(3002, "Failed", title: new string('T', 80)));
        Assert.True(statuses.Until(w => w.Alive.Count == 2));
        var s = statuses.Alive["3001"];
        Assert.Equal((D.ObjectiveRole.ObjectiveRoleObjective, D.ObjectiveState.ObjectiveStateActive, D.TriggerState.TriggerStateDisarmed, false, (byte)1),
            (s.Role, s.State, s.TriggerState, s.Closed, s.Owner));
        Assert.Equal((3, 10, 0.5f, 1f, 0.25f, 0.8f, 5f, 12.5), (s.ProgressCur, s.ProgressNeed, s.Value, s.Target, s.Score, s.Pass, s.Points, s.ChangedAt));
        Assert.Equal(("held 3 s", "Hold the bridge", "HoldArea", "mission", (byte)2, true, true, 2f, (byte)0b1010),
            (s.Reason, s.Title, s.Kind, s.Group, s.Phase, s.Required, s.Scored, s.Weight, s.VisibleSides));
        Assert.Equal((60f, 900f, 130.0, true, (byte)2, 50f, false, 77, 5), (s.HoldFor, s.Deadline, s.ExpectedAt, s.Capture, s.Challenger, s.AreaRadius, s.EndsExercise, s.SubjectId, s.ParentId));
        Assert.Equal(new D.Vec3d { X = 100, Y = 200, Z = 0 }, s.AreaCentre);
        Assert.Equal(D.ObjectiveState.ObjectiveStateFailed, statuses.Alive["3002"].State);
        Assert.Equal(64, statuses.Alive["3002"].Title.Length);   // bounded

        rig.Send(Status(3001, "Achieved"));
        Assert.True(statuses.Until(w => w.Alive["3001"].State == D.ObjectiveState.ObjectiveStateAchieved));
        Assert.Equal(2, statuses.Alive.Count);

        rig.Send(new HrotStatusRemoved(3001));
        Assert.True(statuses.Until(w => w.Alive.Count == 1 && w.Alive.ContainsKey("3002")));
        rig.Send(new HrotStatusRemoved(9999));   // never written: nothing to dispose
    }

    [Fact]
    public void The_exercise_result_is_one_instance_that_updates()
    {
        uint domain = Watch.NewDomain();
        using var exercise = new Watch.Watch<D.Exercise>(domain, "station-1", e => "exercise");
        using var rig = new Rig(domain);

        rig.Send(new HrotExercise("Running", 0.0, string.Empty, new HrotSideResult("None", 0f, 0f), new HrotSideResult("None", 0f, 0f), new HrotSideResult("None", 0f, 0f)));
        Assert.True(exercise.Until(w => w.Alive.ContainsKey("exercise")));
        Assert.Equal(D.ExercisePhase.ExercisePhaseRunning, exercise.Alive["exercise"].Phase);

        rig.Send(new HrotExercise("Ended", 1800.5, "Time is up", new HrotSideResult("Win", 40f, 0.9f), new HrotSideResult("Lose", 10f, 0.2f), new HrotSideResult("Mystery", 0f, 0f)));
        Assert.True(exercise.Until(w => w.Alive["exercise"].Phase == D.ExercisePhase.ExercisePhaseEnded));
        var e = exercise.Alive["exercise"];
        Assert.Equal((1800.5, "Time is up"), (e.EndedAt, e.Reason));
        Assert.Equal((D.SideOutcome.SideOutcomeWin, 40f, 0.9f), (e.Friendly.Outcome, e.Friendly.Points, e.Friendly.Score));
        Assert.Equal((D.SideOutcome.SideOutcomeLose, 10f, 0.2f), (e.Opposing.Outcome, e.Opposing.Points, e.Opposing.Score));
        Assert.Equal(D.SideOutcome.SideOutcomeNone, e.Neutral.Outcome);   // an outcome HROT appends later: not a different one
    }

    [Fact]
    public void Hud_names_map_to_the_contract_and_an_unknown_name_to_its_unknown_value()
    {
        Assert.Equal(D.FireMode.FireModeLock, LookNames.ByName<D.FireMode>("FireMode", "Lock"));
        Assert.Equal(D.FireMode.FireModeSight, LookNames.ByName<D.FireMode>("FireMode", "Sight"));
        Assert.Equal(D.UseOutcome.UseOutcomeNone, LookNames.ByName<D.UseOutcome>("UseOutcome", "NoOutcome"));
        Assert.Equal(D.UseOutcome.UseOutcomeRunning, LookNames.ByName<D.UseOutcome>("UseOutcome", "Running"));
        Assert.Equal(D.UseReason.UseReasonOk, LookNames.ByName<D.UseReason>("UseReason", "Ok"));
        Assert.Equal(D.UseReason.UseReasonOtherSide, LookNames.ByName<D.UseReason>("UseReason", "OtherSide"));
        Assert.Equal(D.UseReason.UseReasonUnknown, LookNames.ByName<D.UseReason>("UseReason", "Haunted"));
        Assert.Equal(D.UseReason.UseReasonUnknown, LookNames.ByName<D.UseReason>("UseReason", null));
        Assert.Equal(D.UseReason.UseReasonUnknown, LookNames.ByName<D.UseReason>("UseReason", "3"));   // a number is never a name
        Assert.Equal(D.RadarWarningLevel.RadarWarningLevelTracked, LookNames.ByName<D.RadarWarningLevel>("RadarWarningLevel", "Tracked"));
        Assert.Equal(D.MissileSeeker.MissileSeekerUnguided, LookNames.ByName<D.MissileSeeker>("MissileSeeker", "Unguided"));
        Assert.Equal(D.MissileSeeker.MissileSeekerRadar, LookNames.ByName<D.MissileSeeker>("MissileSeeker", "Radar"));
        Assert.Equal(D.MessageAddress.MessageAddressStation, LookNames.ByName<D.MessageAddress>("MessageAddress", "Station"));
        Assert.Equal(D.ObjectiveRole.ObjectiveRoleTrigger, LookNames.ByName<D.ObjectiveRole>("ObjectiveRole", "Trigger"));
        Assert.Equal(D.ObjectiveState.ObjectiveStateSkipped, LookNames.ByName<D.ObjectiveState>("ObjectiveState", "Skipped"));
        Assert.Equal(D.TriggerState.TriggerStateCooldown, LookNames.ByName<D.TriggerState>("TriggerState", "Cooldown"));
        Assert.Equal(D.ExercisePhase.ExercisePhaseClosing, LookNames.ByName<D.ExercisePhase>("ExercisePhase", "Closing"));
        Assert.Equal(D.SideOutcome.SideOutcomeDraw, LookNames.ByName<D.SideOutcome>("SideOutcome", "Draw"));

        // every name HROT has today (the HrotHost guard lists them) is a contract enumerator of the same name
        foreach (var name in new[] { "Running", "Done", "Refused" })
        {
            Assert.NotEqual(D.UseOutcome.UseOutcomeNone, LookNames.ByName<D.UseOutcome>("UseOutcome", name));
        }

        foreach (var name in new[] { "Ok", "OutOfReach", "Locked", "DoorRefuses", "NotDown", "SeatTaken", "Unfit", "Carrying", "NothingToDo", "Failed", "OtherSide" })
        {
            Assert.NotEqual(D.UseReason.UseReasonUnknown, LookNames.ByName<D.UseReason>("UseReason", name));
        }

        foreach (var name in new[] { "Unguided", "Command", "FireAndForget", "InfraRed", "Radar" })
        {
            Assert.NotEqual(D.MissileSeeker.MissileSeekerUnknown, LookNames.ByName<D.MissileSeeker>("MissileSeeker", name));
        }

        foreach (var name in new[] { "Disarmed", "Armed", "Holding", "Waiting", "Acting", "Fired", "Cooldown", "Done" })
        {
            Assert.NotEqual(D.TriggerState.TriggerStateUnknown, LookNames.ByName<D.TriggerState>("TriggerState", name));
        }
    }
}
