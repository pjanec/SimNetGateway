using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>DPS round 2 in the plugin (DD-018 3.2 .. 3.5): the widened ids, the people and vehicle extensions, rounds, munition looks, flames, spots and ground marks.</summary>
public class DpsRoundFlameTests
{
    private static readonly long MunitionDis = new DisTypeCode(2, 1, 225, 2, 1, 0, 0).Pack();
    private static readonly long PersonDis = new DisTypeCode(3, 1, 225, 1, 1, 0, 0).Pack();
    private static readonly long TankDis = new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack();

    private static string Id(int id) => id.ToString();

    private static HrotEntityState State(int id, long tkb = 103, long? dis = null) =>
        new(id, tkb, dis ?? TankDis, "X", 2, 0, new HrotVec3(10, 20, 3), HrotQuat.Identity, HrotVec3.Zero, HrotVec3.Zero, 100, 100, 1.0);

    private static HrotPersonDetail Detail(int id, bool burning = false, byte fear = 0, byte anger = 0, HrotCivilianMode mode = HrotCivilianMode.Unknown) =>
        new(id, HrotStance.Standing, 1f, HrotHeldItem.None, 0, HrotVec3.Zero, false, 0f, HrotGesture.None, HrotAction.None, 0, HrotActionState.None, 0, 0, HrotSeatRole.None,
            HrotCarryMode.None, burning, fear, anger, mode);

    [Fact]
    public void Type_ids_are_64_bit_end_to_end()
    {
        uint domain = Watch.NewDomain();
        using var entities = new Watch.Watch<D.Entity>(domain, "station-1", e => Id(e.EntityId));
        using var fires = new Watch.Watch<D.Fire>(domain, "station-1", f => "fire");
        using var hits = new Watch.Watch<D.Detonation>(domain, "station-1", f => "hit");
        using var rig = new Rig(domain);
        const long Big = 5_000_000_123L;
        rig.Send(HrotOrigin.Utm(50, 14, 300, 1));   // the DIS side of the profile needs the geo reference to take a fire
        rig.Send(new HrotSession(ClusterPhase.Live, true, 1.0, 1.0));

        rig.Send(State(2001, tkb: Big));
        rig.Send(State(2002));

        Assert.True(entities.Until(w => w.Alive.ContainsKey("2001")));
        Assert.Equal(Big, entities.Alive["2001"].TypeId);
        Assert.True(fires.Until(w =>
        {
            rig.Send(new HrotFire(2001, 2002, Big + 1, MunitionDis, HrotVec3.Zero, HrotVec3.Zero, 1));
            return w.Events.Count > 0;
        }));
        Assert.Equal(Big + 1, fires.Events[0].MunitionTypeId);
        Assert.True(hits.Until(w =>
        {
            rig.Send(new HrotDetonation(2001, 0, Big + 2, MunitionDis, HrotVec3.Zero, HrotVec3.Zero));
            return w.Events.Count > 0;
        }));
        Assert.Equal(Big + 2, hits.Events[0].MunitionTypeId);
    }

    [Fact]
    public void A_fire_carries_the_weapon_category_and_an_unknown_name_is_unknown()
    {
        uint domain = Watch.NewDomain();
        using var fires = new Watch.Watch<D.Fire>(domain, "station-1", f => "fire");
        using var rig = new Rig(domain);
        rig.Send(HrotOrigin.Utm(50, 14, 300, 1));
        rig.Send(new HrotSession(ClusterPhase.Live, true, 1.0, 1.0));
        rig.Send(State(2001));

        Assert.True(fires.Until(w =>
        {
            rig.Send(new HrotFire(2001, 0, 501, MunitionDis, HrotVec3.Zero, HrotVec3.Zero, 1, HrotWeaponCategory.MachineGun));
            rig.Send(new HrotFire(2001, 0, 501, MunitionDis, HrotVec3.Zero, HrotVec3.Zero, 1, HrotWeaponCategory.Unknown));
            rig.Send(new HrotFire(2001, 0, 501, MunitionDis, HrotVec3.Zero, HrotVec3.Zero, 1));
            return w.Events.Count >= 3;
        }));

        Assert.Equal(D.WeaponCategory.WeaponCategoryMachineGun, fires.Events[0].WeaponCategory);
        Assert.Equal(D.WeaponCategory.WeaponCategoryUnknown, fires.Events[1].WeaponCategory);
        Assert.Equal(D.WeaponCategory.WeaponCategoryUnknown, fires.Events[2].WeaponCategory);
    }

    [Fact]
    public void Person_detail_maps_burning_fear_anger_and_civilian_mode()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.PersonState>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(State(1201, dis: PersonDis));

        rig.Send(Detail(1201, burning: true, fear: 3, anger: 2, mode: HrotCivilianMode.Panic));
        Assert.True(watch.Until(w => w.Alive.TryGetValue("1201", out var p) && p.Burning));
        var p = watch.Alive["1201"];
        Assert.Equal((byte)3, p.Fear);
        Assert.Equal((byte)2, p.Anger);
        Assert.Equal(D.CivilianMode.CivilianModePanic, p.CivilianMode);

        // each of the new fields alone is a change that is sent (not held back to the heartbeat)
        rig.Send(Detail(1201, burning: true, fear: 3, anger: 2, mode: HrotCivilianMode.Riot));
        Assert.True(watch.Until(w => w.Alive["1201"].CivilianMode == D.CivilianMode.CivilianModeRiot));
        rig.Send(Detail(1201, burning: false, fear: 3, anger: 2, mode: HrotCivilianMode.Riot));
        Assert.True(watch.Until(w => !w.Alive["1201"].Burning));
        rig.Send(Detail(1201, burning: false, fear: 4, anger: 2, mode: HrotCivilianMode.Riot));
        Assert.True(watch.Until(w => w.Alive["1201"].Fear == 4));
        rig.Send(Detail(1201, burning: false, fear: 4, anger: 1, mode: HrotCivilianMode.Unknown));
        Assert.True(watch.Until(w => w.Alive["1201"].Anger == 1));
        Assert.Equal(D.CivilianMode.CivilianModeUnknown, watch.Alive["1201"].CivilianMode);
    }

    [Fact]
    public void The_pivot_look_enums_mirror_the_contracts_by_name_and_order()
    {
        // the outbound copies by number: a member added or reordered on one side only would mislabel every weapon
        Assert.Equal(Enum.GetNames<HrotHeldLook>().Select(n => "HeldLook" + n), Enum.GetNames<D.HeldLook>());
        Assert.Equal(Enum.GetNames<HrotHeldMarking>().Select(n => "HeldMarking" + n), Enum.GetNames<D.HeldMarking>());
    }

    [Fact]
    public void A_held_look_slung_look_and_marking_are_copied_and_each_alone_is_a_change_that_is_sent()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.PersonState>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(State(1201, dis: PersonDis));

        // every look maps to its contract name (property over the whole enumeration)
        foreach (var look in Enum.GetValues<HrotHeldLook>())
        {
            var want = Enum.Parse<D.HeldLook>("HeldLook" + look);
            rig.Send(Detail(1201) with { HeldLook = look, SlungLook = look == HrotHeldLook.Rifle ? HrotHeldLook.Mortar : HrotHeldLook.Rifle, HeldMarking = HrotHeldMarking.Toy });
            Assert.True(watch.Until(w => w.Alive.TryGetValue("1201", out var p) && p.HeldLook == want), $"held look {look}");
            var p = watch.Alive["1201"];
            Assert.Equal(look == HrotHeldLook.Rifle ? D.HeldLook.HeldLookMortar : D.HeldLook.HeldLookRifle, p.SlungLook);
            Assert.Equal(D.HeldMarking.HeldMarkingToy, p.HeldMarking);
        }

        // each field alone is sent, not held back to the heartbeat (one field differs per step, the rest as the last sample)
        rig.Send(Detail(1201) with { HeldLook = HrotHeldLook.Crossbow, SlungLook = HrotHeldLook.Rifle, HeldMarking = HrotHeldMarking.None });
        Assert.True(watch.Until(w => w.Alive["1201"].HeldLook == D.HeldLook.HeldLookCrossbow && w.Alive["1201"].HeldMarking == D.HeldMarking.HeldMarkingNone));
        rig.Send(Detail(1201) with { HeldLook = HrotHeldLook.Crossbow, SlungLook = HrotHeldLook.Manpads, HeldMarking = HrotHeldMarking.None });
        Assert.True(watch.Until(w => w.Alive["1201"].SlungLook == D.HeldLook.HeldLookManpads));
        rig.Send(Detail(1201) with { HeldLook = HrotHeldLook.Crossbow, SlungLook = HrotHeldLook.Manpads, HeldMarking = HrotHeldMarking.LessLethal });
        Assert.True(watch.Until(w => w.Alive["1201"].HeldMarking == D.HeldMarking.HeldMarkingLessLethal));
        rig.Send(Detail(1201) with { HeldLook = HrotHeldLook.Bow, SlungLook = HrotHeldLook.Manpads, HeldMarking = HrotHeldMarking.LessLethal });
        Assert.True(watch.Until(w => w.Alive["1201"].HeldLook == D.HeldLook.HeldLookBow));
    }

    [Fact]
    public void Vehicle_look_carries_rotor_speed_and_crew_exposed_openings()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.VehicleLook>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(State(2001));

        rig.Send(new HrotVehicleLook(2001, HrotKillKind.None, false, false, false, 0, [], [(2, 1f), (3, 0f)], 0.75f, [2]));

        Assert.True(watch.Until(w => w.Alive.ContainsKey("2001")));
        var v = watch.Alive["2001"];
        Assert.Equal(0.75f, v.RotorSpeed);
        Assert.Equal([(2, true), (3, false)], v.Openings.Select(o => (o.OpeningId, o.CrewExposed)).ToArray());

        // a vehicle look from before round 2 (no crew list, no rotor) maps to nothing exposed and a stopped rotor
        rig.Send(new HrotVehicleLook(2001, HrotKillKind.None, false, false, false, 0, [], [(2, 1f)]));
        Assert.True(watch.Until(w => w.Alive["2001"].RotorSpeed == 0f));
        Assert.False(watch.Alive["2001"].Openings[0].CrewExposed);
    }

    [Fact]
    public void Person_state_carries_the_seat_and_the_carriage()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.PersonState>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(State(1201, dis: PersonDis));

        // a rider astride a seat of a vehicle
        rig.Send(Detail(1201) with { SeatVehicle = 2100, SeatIndex = 2, SeatExposed = true, SeatPose = HrotSeatPose.Ride, CarryOther = 2100 });
        Assert.True(watch.Until(w => w.Alive.TryGetValue("1201", out var s) && s.SeatPose == D.SeatPose.SeatPoseRide));
        var rider = watch.Alive["1201"];
        Assert.Equal(2, rider.SeatIndex);
        Assert.True(rider.SeatExposed);
        Assert.Equal(2100, rider.CarryOtherId);
        Assert.Equal(D.CarryRole.CarryRoleNone, rider.CarryRole);

        // a stretcher's rear man: the load and his partner
        rig.Send(Detail(1201) with { CarryRole = HrotCarryRole.StretcherRear, CarryOther = 1202, Partner = 1203 });
        Assert.True(watch.Until(w => w.Alive["1201"].CarryRole == D.CarryRole.CarryRoleStretcherRear));
        var rear = watch.Alive["1201"];
        Assert.Equal(1202, rear.CarryOtherId);
        Assert.Equal(1203, rear.PartnerId);
        Assert.Equal((byte)255, rear.SeatIndex);
        Assert.False(rear.SeatExposed);
        Assert.Equal(D.SeatPose.SeatPoseUnknown, rear.SeatPose);

        // only a change of the partner is a change worth sending
        rig.Send(Detail(1201) with { CarryRole = HrotCarryRole.StretcherRear, CarryOther = 1202, Partner = 1204 });
        Assert.True(watch.Until(w => w.Alive["1201"].PartnerId == 1204));
    }

    [Fact]
    public void The_roles_and_poses_map_by_name_and_value()
    {
        // the contract's roles are the message's roles, by name and value
        Assert.Equal(Enum.GetNames<HrotCarryRole>().Select(n => "CarryRole" + n), Enum.GetNames<D.CarryRole>());
        Assert.Equal(Enum.GetValues<HrotCarryRole>().Select(v => (byte)v), Enum.GetValues<D.CarryRole>().Select(v => (byte)v));
    }

    [Fact]
    public void Vehicle_look_carries_the_flail_flag()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.VehicleLook>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(State(2001));

        rig.Send(new HrotVehicleLook(2001, HrotKillKind.None, false, false, false, 0, [], [], FlailActive: true));
        Assert.True(watch.Until(w => w.Alive.TryGetValue("2001", out var v) && v.FlailActive));

        rig.Send(new HrotVehicleLook(2001, HrotKillKind.None, false, false, false, 0, [], [], FlailActive: false));
        Assert.True(watch.Until(w => !w.Alive["2001"].FlailActive));

        // T-079: the flight state travels with the look; a change alone replaces the instance
        rig.Send(new HrotVehicleLook(2001, HrotKillKind.None, false, false, false, 0, [], [], Airborne: true));
        Assert.True(watch.Until(w => w.Alive["2001"].Airborne));
        rig.Send(new HrotVehicleLook(2001, HrotKillKind.None, false, false, false, 0, [], [], Airborne: false));
        Assert.True(watch.Until(w => !w.Alive["2001"].Airborne));
    }

    [Fact]
    public void A_round_munition_look_and_its_disposal_with_the_entity()
    {
        uint domain = Watch.NewDomain();
        using var rounds = new Watch.Watch<D.Round>(domain, "station-1", r => Id(r.EntityId));
        using var looks = new Watch.Watch<D.MunitionLook>(domain, "station-1", l => l.MunitionTypeId.ToString());
        using var rig = new Rig(domain);
        rig.Send(State(3001, tkb: 6_000_000_001L, dis: MunitionDis));

        rig.Send(new HrotMunitionLook(6_000_000_001L, HrotMunitionShape.Shell, 0.13f, 0.025f, 0x2F2F2FFF, false, true, 0xFF8A30FF, false, false));
        rig.Send(new HrotRound(3001, 6_000_000_001L, 2001, new HrotVec3(1, 2, 3), true, -1f, true));

        Assert.True(rounds.Until(w => w.Alive.ContainsKey("3001")));
        var r = rounds.Alive["3001"];
        Assert.Equal(6_000_000_001L, r.MunitionTypeId);
        Assert.Equal(2001, r.ShooterId);
        Assert.Equal(new D.Vec3d { X = 1, Y = 2, Z = 3 }, r.Muzzle);
        Assert.True(r.Lying);
        Assert.True(r.Stopped);
        Assert.Equal(-1f, r.FuzeLeftS);
        Assert.True(looks.Until(w => w.Alive.ContainsKey("6000000001")));
        var look = looks.Alive["6000000001"];
        Assert.Equal(D.MunitionShape.MunitionShapeShell, look.Shape);
        Assert.Equal(0.13f, look.Length);
        Assert.Equal(0.025f, look.Diameter);
        Assert.Equal(0x2F2F2FFFu, look.ColourRgba);
        Assert.True(look.Tracer);
        Assert.False(look.SmokeTrail);
        Assert.Equal(0xFF8A30FFu, look.TracerColourRgba);

        // a shape HROT appends later is sent as unknown, never as another shape
        rig.Send(new HrotMunitionLook(6_000_000_002L, HrotMunitionShape.Unknown, 1f, 1f, 0, false, false, 0, false, false));
        Assert.True(looks.Until(w => w.Alive.ContainsKey("6000000002")));
        Assert.Equal(D.MunitionShape.MunitionShapeUnknown, looks.Alive["6000000002"].Shape);

        // the round is gone with its entity
        rig.Send(new HrotEntityRemoved(3001));
        using var late = new Watch.Watch<D.Round>(domain, "station-1", x => Id(x.EntityId));
        Thread.Sleep(300);
        Assert.True(rounds.Until(w => !w.Alive.ContainsKey("3001")));
        late.Pump();
        Assert.Empty(late.Alive);
    }

    [Fact]
    public void A_flame_jet_is_written_while_lit_and_disposed_when_it_goes_out_or_its_unit_goes()
    {
        uint domain = Watch.NewDomain();
        using var jets = new Watch.Watch<D.FlameJet>(domain, "station-1", j => $"{j.EntityId}/{j.Mount}");
        using var rig = new Rig(domain);
        rig.Send(State(1201, dis: PersonDis));
        rig.Send(State(1202, dis: PersonDis));

        rig.Send(new HrotFlameJet(1201, 0, new HrotVec3(40, 5, 1), 1202, 60f, 0.17f, 0.8f, 99.0, 30f));
        rig.Send(new HrotFlameJet(1201, 1, new HrotVec3(41, 5, 1), 0, 20f, 0.1f, 1f, 99.5, 0f));
        Assert.True(jets.Until(w => w.Alive.Count == 2));
        var jet = jets.Alive["1201/0"];
        Assert.Equal(new D.Vec3d { X = 40, Y = 5, Z = 1 }, jet.AimPoint);
        Assert.Equal(1202, jet.TargetId);
        Assert.Equal(60f, jet.RangeM);
        Assert.Equal(0.17f, jet.HalfAngleRad);
        Assert.Equal(0.8f, jet.Intensity);
        Assert.Equal(99.0, jet.BurstStartedAt);
        Assert.Equal(30f, jet.BurnSeconds);

        rig.Send(new HrotFlameJetRemoved(1201, 0));
        Assert.True(jets.Until(w => w.Alive.Count == 1 && w.Alive.ContainsKey("1201/1")));

        rig.Send(new HrotEntityRemoved(1201));   // the unit goes while its second jet is lit
        Assert.True(jets.Until(w => w.Alive.Count == 0));
    }

    [Fact]
    public void Fire_spots_and_ground_marks_are_bounded_mapped_and_disposed_when_empty()
    {
        uint domain = Watch.NewDomain();
        using var spots = new Watch.Watch<D.FireSpots>(domain, "station-1", s => Id(s.EntityId));
        using var marks = new Watch.Watch<D.GroundMarks>(domain, "station-1", s => Id(s.EntityId));
        using var rig = new Rig(domain);

        rig.Send(new HrotFireSpots(2001, [.. Enumerable.Range(0, 12).Select(i => new HrotFireSpot(new HrotVec3(i, 2, 3), 1.5f, 0.5f, 10.0 + i, 60f))]));
        rig.Send(new HrotGroundMarks(5001,
        [
            .. Enumerable.Range(0, 20).Select(i => new HrotGroundMark(i == 0 ? HrotHeatMarkKind.BurnedArea : HrotHeatMarkKind.VehicleStand, new HrotVec3(i, 2, 0), 0.5f, 6f, 3f, i == 0 ? HrotHeatMarkEnd.Rear : HrotHeatMarkEnd.Front, 5.0, 9.0)),
        ]));

        Assert.True(spots.Until(w => w.Alive.ContainsKey("2001")));
        var s = spots.Alive["2001"];
        Assert.Equal(8, s.Spots.Count);   // bounded
        Assert.Equal(new D.Vec3d { X = 7, Y = 2, Z = 3 }, s.Spots[7].Position);
        Assert.Equal(1.5f, s.Spots[0].Radius);
        Assert.Equal(0.5f, s.Spots[0].Intensity);
        Assert.Equal(10.0, s.Spots[0].Since);
        Assert.Equal(60f, s.Spots[0].Seconds);
        Assert.True(marks.Until(w => w.Alive.ContainsKey("5001")));
        var m = marks.Alive["5001"];
        Assert.Equal(16, m.Marks.Count);   // bounded
        Assert.Equal(D.HeatMarkKind.HeatMarkKindBurnedArea, m.Marks[0].Kind);
        Assert.Equal(D.HeatMarkEnd.HeatMarkEndRear, m.Marks[0].HotEnd);
        Assert.Equal(D.HeatMarkKind.HeatMarkKindVehicleStand, m.Marks[1].Kind);
        Assert.Equal(D.HeatMarkEnd.HeatMarkEndFront, m.Marks[1].HotEnd);
        Assert.Equal(new D.Vec3d { X = 0, Y = 2, Z = 0 }, m.Marks[0].Centre);
        Assert.Equal((0.5f, 6f, 3f, 5.0, 9.0), (m.Marks[0].Heading, m.Marks[0].Length, m.Marks[0].Width, m.Marks[0].StandSince, m.Marks[0].LeftAt));

        // an empty set disposes the instance
        rig.Send(new HrotFireSpots(2001, []));
        rig.Send(new HrotGroundMarks(5001, []));
        Assert.True(spots.Until(w => w.Alive.Count == 0));
        Assert.True(marks.Until(w => w.Alive.Count == 0));

        // and a set for a holder that was never written disposes nothing and writes nothing
        rig.Send(new HrotFireSpots(2002, []));
        spots.Pump();
        Assert.Empty(spots.Alive);
    }

    [Fact]
    public void A_burning_vehicle_that_is_removed_loses_its_fire_spots()
    {
        uint domain = Watch.NewDomain();
        using var spots = new Watch.Watch<D.FireSpots>(domain, "station-1", s => Id(s.EntityId));
        using var rig = new Rig(domain);
        rig.Send(State(2001));
        rig.Send(new HrotFireSpots(2001, [new HrotFireSpot(new HrotVec3(1, 2, 3), 1f, 1f, 1.0, 60f)]));
        Assert.True(spots.Until(w => w.Alive.ContainsKey("2001")));

        rig.Send(new HrotEntityRemoved(2001));

        Assert.True(spots.Until(w => w.Alive.Count == 0));
    }

    [Fact]
    public void World_boundary_disposes_rounds_jets_spots_and_marks()
    {
        uint domain = Watch.NewDomain();
        using var rounds = new Watch.Watch<D.Round>(domain, "station-1", r => Id(r.EntityId));
        using var jets = new Watch.Watch<D.FlameJet>(domain, "station-1", j => $"{j.EntityId}/{j.Mount}");
        using var spots = new Watch.Watch<D.FireSpots>(domain, "station-1", s => Id(s.EntityId));
        using var marks = new Watch.Watch<D.GroundMarks>(domain, "station-1", s => Id(s.EntityId));
        using var looks = new Watch.Watch<D.MunitionLook>(domain, "station-1", l => l.MunitionTypeId.ToString());
        var rig = new Rig(domain);
        rig.Send(State(3001, tkb: 6, dis: MunitionDis));
        rig.Send(new HrotMunitionLook(6, HrotMunitionShape.Bullet, 0.02f, 0.006f, 0xFFFFFFFF, false, true, 0xFF6A30FF, false, false));
        rig.Send(new HrotRound(3001, 6, 0, HrotVec3.Zero, false, -1f, false));
        rig.Send(new HrotFlameJet(1201, 0, HrotVec3.Zero, 0, 10f, 0.1f, 1f, 0.0, 0f));
        rig.Send(new HrotFireSpots(2001, [new HrotFireSpot(HrotVec3.Zero, 1f, 1f, 1.0, 60f)]));
        rig.Send(new HrotGroundMarks(5001, [new HrotGroundMark(HrotHeatMarkKind.BurnedArea, HrotVec3.Zero, 0f, 1f, 1f, HrotHeatMarkEnd.Unknown, 0.0, 0.0)]));
        Assert.True(rounds.Until(w => w.Alive.Count == 1));
        Assert.True(jets.Until(w => w.Alive.Count == 1));
        Assert.True(spots.Until(w => w.Alive.Count == 1));
        Assert.True(marks.Until(w => w.Alive.Count == 1));
        Assert.True(looks.Until(w => w.Alive.Count == 1));

        rig.Dispose();   // the runtime ends at the world boundary

        Assert.True(rounds.Until(w => w.Alive.Count == 0));
        Assert.True(jets.Until(w => w.Alive.Count == 0));
        Assert.True(spots.Until(w => w.Alive.Count == 0));
        Assert.True(marks.Until(w => w.Alive.Count == 0));
        Assert.True(looks.Until(w => w.Alive.Count == 0));
    }


    /// <summary>
    /// T-080: every pivot enum mirrors the DPS enum it ends in - the same members in the same ORDER (the DPS enumerator is the enum's name + the member), so the outbound is a cast. <c>HrotEffectKind</c> has ONE more member after the
    /// mirrored ones, <c>Omitted</c> (a kind DPS does not carry: the effect is not sent). Mutation: reorder or rename a pivot member and this fails.
    /// </summary>
    [Fact]
    public void Every_pivot_enum_mirrors_its_dps_enum_in_names_and_order()
    {
        (Type Pivot, Type Dps, string Prefix)[] pairs =
        [
        (typeof(HrotBodyPart), typeof(D.BodyPart), "BodyPart"),
        (typeof(HrotMessageAddress), typeof(D.MessageAddress), "MessageAddress"),
        (typeof(HrotObjectiveRole), typeof(D.ObjectiveRole), "ObjectiveRole"),
        (typeof(HrotObjectiveState), typeof(D.ObjectiveState), "ObjectiveState"),
        (typeof(HrotTriggerState), typeof(D.TriggerState), "TriggerState"),
        (typeof(HrotExercisePhase), typeof(D.ExercisePhase), "ExercisePhase"),
        (typeof(HrotSideOutcome), typeof(D.SideOutcome), "SideOutcome"),
        (typeof(HrotAnimalMode), typeof(D.AnimalMode), "AnimalMode"),
        (typeof(HrotFlockMode), typeof(D.FlockMode), "FlockMode"),
        (typeof(HrotBreachKind), typeof(D.BreachKind), "BreachKind"),
        (typeof(HrotBreachCause), typeof(D.BreachCause), "BreachCause"),
        (typeof(HrotCraterKind), typeof(D.CraterKind), "CraterKind"),
        (typeof(HrotMoverKind), typeof(D.MoverKind), "MoverKind"),
        (typeof(HrotMoverSource), typeof(D.MoverSource), "MoverSource"),
        (typeof(HrotTrafficLightState), typeof(D.TrafficLightState), "TrafficLightState"),
        (typeof(HrotViewSource), typeof(D.ViewSource), "ViewSource"),
        (typeof(HrotViewMode), typeof(D.ViewMode), "ViewMode"),
        (typeof(HrotCivilianMode), typeof(D.CivilianMode), "CivilianMode"),
        (typeof(HrotWeaponCategory), typeof(D.WeaponCategory), "WeaponCategory"),
        (typeof(HrotMunitionShape), typeof(D.MunitionShape), "MunitionShape"),
        (typeof(HrotSeatPose), typeof(D.SeatPose), "SeatPose"),
        (typeof(HrotHeatMarkKind), typeof(D.HeatMarkKind), "HeatMarkKind"),
        (typeof(HrotHeatMarkEnd), typeof(D.HeatMarkEnd), "HeatMarkEnd"),
        (typeof(HrotEffectSize), typeof(D.EffectSize), "EffectSize"),
        (typeof(HrotEffectSurface), typeof(D.EffectSurface), "EffectSurface"),
        (typeof(HrotImpactKind), typeof(D.ImpactKind), "ImpactKind"),
        (typeof(HrotKillKind), typeof(D.KillKind), "KillKind"),
        (typeof(HrotEffectKind), typeof(D.EffectKind), "EffectKind"),
        (typeof(HrotUseVerb), typeof(D.UseVerb), "UseVerb"),
        (typeof(HrotFireMode), typeof(D.FireMode), "FireMode"),
        (typeof(HrotUseOutcome), typeof(D.UseOutcome), "UseOutcome"),
        (typeof(HrotUseReason), typeof(D.UseReason), "UseReason"),
        (typeof(HrotRadarWarningLevel), typeof(D.RadarWarningLevel), "RadarWarningLevel"),
        (typeof(HrotMissileSeeker), typeof(D.MissileSeeker), "MissileSeeker"),
        (typeof(HrotInjury), typeof(D.Injury), "Injury"),
        ];
        foreach (var (pivot, dps, prefix) in pairs)
        {
            var want = Enum.GetNames(dps).Select(n => n[prefix.Length..]).ToArray();
            var have = Enum.GetNames(pivot).Take(want.Length).ToArray();
            Assert.True(want.SequenceEqual(have), $"{pivot.Name} does not mirror {dps.Name}");
            Assert.True(Enum.GetNames(dps).All(n => n.StartsWith(prefix, StringComparison.Ordinal)), dps.Name);
            Assert.True(pivot == typeof(HrotEffectKind) ? Enum.GetNames(pivot).Length == want.Length + 1 : Enum.GetNames(pivot).Length == want.Length, $"{pivot.Name} has members the DPS enum has not");
            // and the values are the same numbers, so a cast is the mapping
            var dpsValues = Enum.GetValues(dps).Cast<object>().Select(v => Convert.ToInt32(v)).ToArray();
            Assert.True(dpsValues.SequenceEqual(Enum.GetValues(pivot).Cast<object>().Take(dpsValues.Length).Select(v => Convert.ToInt32(v))), $"{pivot.Name} values");
        }
    }
}
