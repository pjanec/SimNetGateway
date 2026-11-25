using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>DPS round 4 (DD-020): PersonAim and the intent rules, stance and spool start times, the producer-neutral enumerations and the one control-frame rule.</summary>
public class DpsRoundFourTests
{
    private static readonly long PersonDis = new DisTypeCode(3, 1, 225, 1, 1, 0, 0).Pack();
    private static readonly long TankDis = new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack();

    private static HrotEntityState Man(int id, double t, HrotWeaponPosture weapon = HrotWeaponPosture.None, HrotVec3? aim = null, int aimTarget = 0, HrotVec3? at = null) =>
        new(id, 103, PersonDis, "Rifleman", 1, 0, at ?? new HrotVec3(10, 20, 0), HrotQuat.Identity, HrotVec3.Zero, HrotVec3.Zero, 100, 100, t, HrotStance.Standing, weapon,
            aim ?? HrotVec3.Zero, HrotInjury.Ok, 0.0, aimTarget);

    private static HrotPersonDetail Detail(int id, HrotVec3? look = null, bool hasLook = false, float headYaw = 0f, int lookTarget = 0, float progress = 1f, double stanceAt = 0.0,
        float stanceSeconds = 0f, HrotStance target = HrotStance.Standing, HrotStance stanceFrom = HrotStance.Unknown) =>
        new(id, target, progress, HrotHeldItem.None, 0, look ?? HrotVec3.Zero, hasLook, headYaw, HrotGesture.None, HrotAction.None, 0, HrotActionState.None, 0.0, 0, HrotSeatRole.None,
            HrotCarryMode.None, StanceChangedAt: stanceAt, StanceSeconds: stanceSeconds, LookTargetId: lookTarget, StanceFrom: stanceFrom);

    [Fact]
    public void An_aim_at_a_moving_unit_is_one_PersonAim_and_no_further_samples()
    {
        using var rig = new Rig(Watch.NewDomain());
        rig.Send(Man(1201, 1.0));
        rig.Send(Detail(1201));
        Assert.Equal(0, rig.Transport.PersonAim.Written);   // nothing aimed or looked: nothing sent
        int people = rig.Transport.PersonState.Written;

        // he starts aiming at unit 77 (the chest 1 m above its position)
        rig.Send(Man(1201, 1.1, HrotWeaponPosture.Aimed, new HrotVec3(50, 60, 1), 77));
        Assert.Equal(1, rig.Transport.PersonAim.Written);
        Assert.Equal(people + 1, rig.Transport.PersonState.Written);   // the posture changed: one discrete PersonState

        // the target walks on: even a state that arrives with a moved chest sends neither topic
        for (int i = 0; i < 30; i++)   // stays inside the 5 s heartbeat
        {
            rig.Send(Man(1201, 1.2 + i * 0.1, HrotWeaponPosture.Aimed, new HrotVec3(50 + i, 60, 1), 77));
        }

        Assert.Equal(1, rig.Transport.PersonAim.Written);
        Assert.Equal(people + 1, rig.Transport.PersonState.Written);
    }

    [Fact]
    public void A_point_aim_that_moves_sends_PersonAim_only()
    {
        uint domain = Watch.NewDomain();
        using var aims = new Watch.Watch<D.PersonAim>(domain, "station-1", a => a.EntityId.ToString());
        using var rig = new Rig(domain);
        rig.Send(Man(1201, 1.0, HrotWeaponPosture.Aimed, new HrotVec3(30, 40, 1)));
        rig.Send(Detail(1201));
        int people = rig.Transport.PersonState.Written;
        int sent = rig.Transport.PersonAim.Written;
        Assert.Equal(1, sent);

        rig.Send(Man(1201, 1.1, HrotWeaponPosture.Aimed, new HrotVec3(30.01, 40, 1)));   // 1 cm: noise
        Assert.Equal(sent, rig.Transport.PersonAim.Written);
        rig.Send(Man(1201, 1.2, HrotWeaponPosture.Aimed, new HrotVec3(32, 40, 1)));   // 2 m
        Assert.Equal(sent + 1, rig.Transport.PersonAim.Written);
        Assert.Equal(people, rig.Transport.PersonState.Written);

        Assert.True(aims.Until(w => w.Alive.TryGetValue("1201", out var a) && a.AimPoint.X == 32 && a.AimTargetId == 0));
    }

    [Fact]
    public void A_look_follows_its_entity_and_the_head_counts_only_when_it_turns_on_or_off()
    {
        uint domain = Watch.NewDomain();
        using var aims = new Watch.Watch<D.PersonAim>(domain, "station-1", a => a.EntityId.ToString());
        using var rig = new Rig(domain);
        rig.Send(Man(1201, 1.0));
        rig.Send(Detail(1201, new HrotVec3(5, 5, 0), true, 0.4f, lookTarget: 88));
        Assert.Equal(1, rig.Transport.PersonAim.Written);

        // the head keeps turning, the looked-at unit keeps moving: the same intent, no sample
        rig.Send(Detail(1201, new HrotVec3(9, 9, 0), true, 0.7f, lookTarget: 88));
        rig.Send(Detail(1201, new HrotVec3(12, 9, 0), true, 0.2f, lookTarget: 88));
        Assert.Equal(1, rig.Transport.PersonAim.Written);

        rig.Send(Detail(1201, new HrotVec3(12, 9, 0), true, 0.0f, lookTarget: 88));   // the body faces it: the head is straight again
        Assert.Equal(2, rig.Transport.PersonAim.Written);
        rig.Send(Detail(1201, null, false, 0f));   // the look ended
        Assert.Equal(3, rig.Transport.PersonAim.Written);
        Assert.True(aims.Until(w => w.Alive.TryGetValue("1201", out var a) && !a.HasLookTarget && a.LookTargetId == 0));
    }

    [Fact]
    public void A_stance_change_sends_one_PersonState_with_start_and_duration_and_the_progress_alone_sends_nothing()
    {
        uint domain = Watch.NewDomain();
        using var people = new Watch.Watch<D.PersonState>(domain, "station-1", p => p.EntityId.ToString());
        using var rig = new Rig(domain);
        rig.Send(Man(1201, 12.0));
        rig.Send(Detail(1201));
        int sent = rig.Transport.PersonState.Written;

        rig.Send(Detail(1201, target: HrotStance.Prone, progress: 0f, stanceAt: 12.4, stanceSeconds: 0.9f, stanceFrom: HrotStance.Crouched));
        Assert.Equal(sent + 1, rig.Transport.PersonState.Written);
        rig.Send(Detail(1201, target: HrotStance.Prone, progress: 0.5f, stanceAt: 12.4, stanceSeconds: 0.9f, stanceFrom: HrotStance.Crouched));
        rig.Send(Detail(1201, target: HrotStance.Prone, progress: 1f, stanceAt: 12.4, stanceSeconds: 0.9f, stanceFrom: HrotStance.Crouched));
        Assert.Equal(sent + 1, rig.Transport.PersonState.Written);

        Assert.True(people.Until(w => w.Alive.TryGetValue("1201", out var p) && p.StanceChangedAt == 12.4 && p.TargetStance == D.Stance.StanceProne));
        Assert.Equal((0.9f, D.Stance.StanceCrouched), (people.Alive["1201"].StanceSeconds, people.Alive["1201"].StanceFrom));   // a client that joins mid-blend knows where it started
    }

    [Fact]
    public void The_spool_and_the_opening_motion_cross_as_start_target_and_duration()
    {
        uint domain = Watch.NewDomain();
        using var looks = new Watch.Watch<D.VehicleLook>(domain, "station-1", l => l.EntityId.ToString());
        using var rig = new Rig(domain);
        rig.Send(new HrotEntityState(2001, 103, TankDis, "Heli", 1, 0, new HrotVec3(1, 2, 3), HrotQuat.Identity, HrotVec3.Zero, HrotVec3.Zero, 100, 100, 5.0));
        rig.Send(new HrotVehicleLook(2001, HrotKillKind.None, false, false, false, 0, [], [(3, 0.5f)], 0.25f, null, false, 1f, 0.6f, 4.0, 20f, [new HrotOpeningMotion(3, 1f, 4.5, 1.5f)]));

        Assert.True(looks.Until(w => w.Alive.ContainsKey("2001")));
        var l = looks.Alive["2001"];
        Assert.Equal((1f, 0.6f, 4.0, 20f), (l.RotorTarget, l.RotorFrom, l.RotorChangedAt, l.RotorSpoolSeconds));
        Assert.Equal((1f, 4.5, 1.5f, 0.5f), (l.Openings[0].TargetProgress, l.Openings[0].ChangedAt, l.Openings[0].Seconds, l.Openings[0].Progress));
        Assert.Equal(0.25f, l.RotorSpeed);
    }

    [Fact]
    public void The_session_names_the_vocabulary_and_the_terrain_format()
    {
        uint domain = Watch.NewDomain();
        using var sessions = new Watch.Watch<D.Session>(domain, "station-1", s => "session");
        using var rig = new Rig(domain);
        rig.Send(new HrotSession(ClusterPhase.Live, true, 1.0, 3.0));
        Assert.True(sessions.Until(w => w.Alive.ContainsKey("session")));
        Assert.Equal(("hrot/1", "hrot-terrain/1"), (sessions.Alive["session"].Vocabulary, sessions.Alive["session"].TerrainFormat));
    }

    [Fact]
    public void A_turret_is_an_articulation_of_kind_turret_with_its_index_and_an_unknown_part_keeps_its_id()
    {
        uint domain = Watch.NewDomain();
        using var arts = new Watch.Watch<D.Articulation>(domain, "station-1", a => $"{a.EntityId}/{a.PartId}");
        using var rig = new Rig(domain);
        rig.Send(new HrotEntityState(2001, 103, TankDis, "T-72", 2, 0, new HrotVec3(1, 2, 3), HrotQuat.Identity, HrotVec3.Zero, HrotVec3.Zero, 100, 100, 1.0));
        rig.Send(new HrotTurretPose(2001, 2000, 0.5, 0.1));
        rig.Send(new HrotTurretPose(2001, 2001, 0.25, 0.0));
        rig.Send(new HrotTurretPose(2001, 7, 0.25, 0.0));

        Assert.True(arts.Until(w => w.Alive.Count == 3));
        Assert.Equal((D.ArticulationKind.ArticulationKindTurret, 0), (arts.Alive["2001/2000"].PartKind, arts.Alive["2001/2000"].Index));
        Assert.Equal((D.ArticulationKind.ArticulationKindTurret, 1), (arts.Alive["2001/2001"].PartKind, arts.Alive["2001/2001"].Index));
        Assert.Equal((D.ArticulationKind.ArticulationKindUnknown, 7), (arts.Alive["2001/7"].PartKind, arts.Alive["2001/7"].Index));
        Assert.Equal("Turret", arts.Alive["2001/2000"].Kind);
    }

    [Fact]
    public void Body_parts_and_use_verbs_map_by_name_with_an_unknown_fallback()
    {
        uint domain = Watch.NewDomain();
        using var downed = new Watch.Watch<D.ManDowned>(domain, "station-1", m => m.VictimId.ToString());
        using var rig = new Rig(domain);
        // events are volatile: send until the reader has matched
        Assert.True(downed.Until(w =>
        {
            rig.Send(new HrotManDowned(1.0, 3, 4, 4, HrotInjury.Dead, 6, 1, HrotBodyPart.Head));
            rig.Send(new HrotManDowned(1.0, 5, 4, 4, HrotInjury.Dead, 99, 1, HrotBodyPart.Unknown));
            rig.Send(new HrotManDowned(1.0, 6, 4, 4, HrotInjury.Dead, 0, 1));
            return w.Events.Any(e => e.VictimId == 3) && w.Events.Any(e => e.VictimId == 5) && w.Events.Any(e => e.VictimId == 6);
        }));
        Assert.Equal(D.BodyPart.BodyPartHead, downed.Events.First(e => e.VictimId == 3).Part);
        Assert.Equal((byte)6, downed.Events.First(e => e.VictimId == 3).BodyPart);
        Assert.Equal(D.BodyPart.BodyPartUnknown, downed.Events.First(e => e.VictimId == 5).Part);
        Assert.Equal(D.BodyPart.BodyPartUnknown, downed.Events.First(e => e.VictimId == 6).Part);
    }

    [Fact]
    public void The_own_unit_names_its_use_verb()
    {
        uint domain = Watch.NewDomain();
        var station = new DpsInboundTests.RecordingStation { Slot = new PlayerSlotSnapshot(PlayerSlotState.Playing, 1201, 0.0, 1) };
        station.Unit = new PlayerUnitStatus(80.0, 100.0, [], 3, 55, UseVerbKind: HrotUseVerb.Drive);
        using var units = new Watch.Watch<D.OwnUnit>(domain, "station-1", u => "unit");
        using var rig = new Rig(domain, station: station);
        DpsInboundTests.Frames(rig);
        Assert.True(units.Until(w => w.Alive.ContainsKey("unit")));
        Assert.Equal((D.UseVerb.UseVerbDrive, (byte)3), (units.Alive["unit"].Verb, units.Alive["unit"].UseVerb));
    }

    // ---- the one rule of the control frame: a set new enumeration wins, 0 falls back to the old byte -------------------------------------------

    [Theory]
    [InlineData(D.Gait.GaitWalk, 0, 1)]   // the new says walk, the old byte says run: the new wins
    [InlineData(D.Gait.GaitRun, 1, 0)]
    [InlineData(D.Gait.GaitUnknown, 1, 1)]   // unknown: the old byte
    [InlineData(D.Gait.GaitUnknown, 0, 0)]
    public void The_gait_rule(D.Gait mode, byte old, int expected) =>
        Assert.Equal((byte)expected, DpsInbound.ToControl(new D.ControlFrame { GaitMode = mode, Gait = old }).Gait);

    [Theory]
    [InlineData(D.StanceRequest.StanceRequestStanding, 3, 1)]
    [InlineData(D.StanceRequest.StanceRequestCrouched, 0, 2)]
    [InlineData(D.StanceRequest.StanceRequestProne, 1, 3)]
    [InlineData(D.StanceRequest.StanceRequestNone, 2, 2)]   // none: the old byte
    [InlineData(D.StanceRequest.StanceRequestNone, 0, 0)]
    public void The_stance_rule(D.StanceRequest wanted, byte old, int expected) =>
        Assert.Equal((byte)expected, DpsInbound.ToControl(new D.ControlFrame { StanceWanted = wanted, Stance = old }).Stance);

    [Fact]
    public void The_use_verb_rule_names_the_new_verb_and_keeps_the_old_byte_for_the_fallback()
    {
        var named = DpsInbound.ToControl(new D.ControlFrame { Verb = D.UseVerb.UseVerbBreachDoor, UseVerb = 2 });
        Assert.Equal(("BreachDoor", (byte)2), (named.UseVerbName, named.UseVerb));
        var old = DpsInbound.ToControl(new D.ControlFrame { UseVerb = 4 });
        Assert.Equal(("", (byte)4), (old.UseVerbName, old.UseVerb));
    }
}
