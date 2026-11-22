using System.Diagnostics;
using System.Reflection;
using CycloneDDS.Runtime;
using CycloneDDS.Schema;
using SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Protocol.DdsPlayer.Tests;

public class DpsTopicTests
{
    // DDS domains of the test assemblies are disjoint (Plugin 1..50, Protocol.DdsPlayer 55..94, DpsClient 100..166): the projects run in parallel and a shared domain lets their participants meet; all stay below domain 167 because DDS uses UDP port 7400 + 250 * domain and Windows excludes or hands out the ports above 49152, where participant creation then fails at random
    private static uint NewDomain() => (uint)(55 + (Environment.ProcessId % 20));

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
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

    /// <summary>Writes one sample and reads it back in the same participant (a new partition per call keeps the cases apart).</summary>
    private static T RoundTrip<T>(T value, string partition) where T : struct
    {
        using var participant = new DdsParticipant(NewDomain(), defaultPartition: partition);
        using var writer = new DdsWriter<T>(participant);
        using var reader = new DdsReader<T>(participant);
        Assert.True(writer.WaitForReaderAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        writer.Write(value);
        T? got = null;
        Assert.True(WaitFor(() =>
        {
            using var loan = reader.Take(8);
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    got = sample.Data;
                }
            }
            return got is not null;
        }));
        return got!.Value;
    }

    private static readonly DisType Dis = new() { Kind = 1, Domain = 1, Country = 225, Category = 1, Subcategory = 2, Specific = 3, Extra = 4 };

    [Fact]
    public void Every_topic_round_trips()
    {
        var session = RoundTrip(new Session
        {
            ProtocolMajor = DpsProtocol.Major, ProtocolMinor = DpsProtocol.Minor, Mode = SessionMode.SessionModeReplay, SimTime = 12.5, TimeScale = 2.0,
            WorldEpoch = 7, Terrain = "Terrain1", StationName = "Station1", UtmZone = 33, UtmNorthern = true, OriginEasting = 500000.5, OriginNorthing = 5500000.5,
        }, "t-session");
        Assert.Equal(SessionMode.SessionModeReplay, session.Mode);
        Assert.Equal("Station1", session.StationName);
        Assert.Equal(5500000.5, session.OriginNorthing);

        var entity = RoundTrip(new Entity
        {
            EntityId = 11, TypeId = 5_000_000_011L, Domain = Domain.DomainGround, Side = 2, Name = "Tank 1", Dis = Dis, Playable = true, ParentId = 3,
        }, "t-entity");
        Assert.Equal(11, entity.EntityId);
        Assert.Equal(5_000_000_011L, entity.TypeId);   // 64-bit (DD-018 3.2)
        Assert.Equal(Domain.DomainGround, entity.Domain);
        Assert.Equal("Tank 1", entity.Name);
        Assert.Equal((ushort)225, entity.Dis.Country);
        Assert.True(entity.Playable);

        var state = RoundTrip(new EntityState
        {
            EntityId = 11, SimTime = 3.25, Position = new Vec3d { X = 1, Y = 2, Z = 3 }, Orientation = new Quat { X = 0, Y = 0, Z = 0.5f, W = 0.5f },
            LinearVelocity = new Vec3f { X = 4, Y = 5, Z = 6 }, AngularVelocity = new Vec3f { Z = 0.1f },
            LinearAcceleration = new Vec3f { X = 0.5f }, AngularAcceleration = new Vec3f { Y = 0.25f }, Damage = PartCondition.PartConditionDamaged,
        }, "t-state");
        Assert.Equal(2.0, state.Position.Y);
        Assert.Equal(0.5f, state.Orientation.W);
        Assert.Equal(6f, state.LinearVelocity.Z);
        Assert.Equal(PartCondition.PartConditionDamaged, state.Damage);

        var articulation = RoundTrip(new Articulation { EntityId = 11, PartId = 2, Kind = "Turret", SimTime = 1, Azimuth = 0.5f, Elevation = 0.25f, Extension = 0.75f, Rate = 0.1f }, "t-art");
        Assert.Equal(2, articulation.PartId);
        Assert.Equal("Turret", articulation.Kind);
        Assert.Equal(0.25f, articulation.Elevation);

        var person = RoundTrip(new PersonState
        {
            EntityId = 12, SimTime = 9, Stance = Stance.StanceCrouched, TargetStance = Stance.StanceProne, StanceProgress = 0.5f, Posture = WeaponPosture.WeaponPostureAimed,
            AimPoint = new Vec3d { X = 10, Y = 20, Z = 1 }, ActiveSlot = 1, Held = HeldItem.HeldItemBinoculars, HasLookTarget = true,
            LookTarget = new Vec3d { X = 7 }, HeadYaw = 0.3f, Gesture = Gesture.GestureWaving, Injury = Injury.InjuryWounded, Action = ActionKind.ActionKindOpenDoor,
            ActionTargetId = 55, ActionState = ActionState.ActionStateRunning, ActionStartSimTime = 8.5, SeatVehicleId = 11, SeatRole = SeatRole.SeatRoleGunner,
            CarryMode = CarryMode.CarryModeDragged, CarryOtherId = 13,
            SeatIndex = 3, SeatExposed = true, SeatPose = SeatPose.SeatPoseRide, CarryRole = CarryRole.CarryRoleStretcherRear, PartnerId = 14,
        }, "t-person");
        Assert.Equal(Stance.StanceProne, person.TargetStance);
        Assert.Equal(Gesture.GestureWaving, person.Gesture);
        Assert.Equal(ActionKind.ActionKindOpenDoor, person.Action);
        Assert.Equal(8.5, person.ActionStartSimTime);
        Assert.Equal(SeatRole.SeatRoleGunner, person.SeatRole);
        Assert.Equal(13, person.CarryOtherId);
        Assert.Equal((byte)3, person.SeatIndex);
        Assert.True(person.SeatExposed);
        Assert.Equal(SeatPose.SeatPoseRide, person.SeatPose);
        Assert.Equal(CarryRole.CarryRoleStretcherRear, person.CarryRole);
        Assert.Equal(14, person.PartnerId);

        var appearance = RoundTrip(new Appearance
        {
            EntityId = 12, Seed = 99, Side = 1, Camouflage = "Woodland", Hair = "Short", FaceTraits = "round", Sex = Sex.SexFemale, AgeClass = AgeClass.AgeClassAdult,
            IsSoldier = true, IsWoman = true,
            Slots = [new AppearanceSlot { Slot = "Top", Kind = "Jacket", ColourRgba = 0x336699FF }, new AppearanceSlot { Slot = "Legs", Kind = "Trousers", ColourRgba = 0x112233FF }],
            Equipment = [new EquipmentItem { Slot = "Weapon0", Kind = "Rifle", TypeId = 321, Attachments = "scope,light" }],
        }, "t-look");
        Assert.Equal(2, appearance.Slots.Count);
        Assert.Equal(0x336699FFu, appearance.Slots[0].ColourRgba);
        Assert.Equal("Trousers", appearance.Slots[1].Kind);
        Assert.Equal("scope,light", appearance.Equipment[0].Attachments);
        Assert.Equal(Sex.SexFemale, appearance.Sex);

        var fire = RoundTrip(new Fire { SimTime = 5, ShooterId = 12, TargetId = 11, MunitionTypeId = 77, MunitionDis = Dis, WeaponSlot = 1, Origin = new Vec3d { X = 1 }, Velocity = new Vec3f { X = 800 } }, "t-fire");
        Assert.Equal(12, fire.ShooterId);
        Assert.Equal(800f, fire.Velocity.X);

        var detonation = RoundTrip(new Detonation { SimTime = 6, ShooterId = 12, TargetId = 11, MunitionTypeId = 77, MunitionDis = Dis, Position = new Vec3d { Z = 2 }, Velocity = new Vec3f { Z = -1 }, Result = 3 }, "t-det");
        Assert.Equal(3, detonation.Result);
        Assert.Equal(2.0, detonation.Position.Z);

        var effect = RoundTrip(new Effect
        {
            SimTime = 7, Kind = EffectKind.EffectKindExplosion, SizeClass = EffectSize.EffectSizeLarge, Surface = EffectSurface.EffectSurfaceBuilding, MunitionDis = Dis, EffectType = 40,
            Position = new Vec3d { X = 3 }, Rotation = new Quat { W = 1 }, AnchorId = 12, AnchorWeaponSlot = 1, LifetimeSeconds = 2.5f, Size = 4f,
        }, "t-effect");
        Assert.Equal(EffectKind.EffectKindExplosion, effect.Kind);
        Assert.Equal(EffectSurface.EffectSurfaceBuilding, effect.Surface);
        Assert.Equal(40, effect.EffectType);
        Assert.Equal(2.5f, effect.LifetimeSeconds);

        var look = RoundTrip(new VehicleLook
        {
            EntityId = 11, KillKind = KillKind.KillKindBurntOut, TurretThrown = true, Burning = true, Smoking = true, LightMask = 0x5, RotorSpeed = 0f, SmokeLaunchers = 0,
            Parts = [new PartLook { PartId = 1, State = PartCondition.PartConditionDestroyed }, new PartLook { PartId = 2, State = PartCondition.PartConditionDamaged }],
            Openings = [new OpeningLook { OpeningId = 9, Progress = 0.5f }],
        }, "t-vlook");
        Assert.Equal(KillKind.KillKindBurntOut, look.KillKind);
        Assert.True(look.TurretThrown);
        Assert.Equal(PartCondition.PartConditionDestroyed, look.Parts[0].State);
        Assert.Equal(9, look.Openings[0].OpeningId);
        Assert.Equal(5u, look.LightMask);

        var slot = RoundTrip(new PlayerSlot
        {
            State = SlotState.SlotStateChoosing, UnitId = 0, Side = 1, RespawnAtSim = 30.5, Reason = "WrongSide",
            Offers = [new Offer { Kind = OfferKind.OfferKindUnit, Id = 12, Name = "Rifleman 1", Side = 1, Free = true }, new Offer { Kind = OfferKind.OfferKindRole, Id = -5, Name = "Sniper", Side = 1, Free = true, Count = 1, Max = 2 }],
        }, "t-slot");
        Assert.Equal(SlotState.SlotStateChoosing, slot.State);
        Assert.Equal(2, slot.Offers.Count);
        Assert.Equal(OfferKind.OfferKindRole, slot.Offers[1].Kind);
        Assert.Equal("Sniper", slot.Offers[1].Name);

        var own = RoundTrip(new OwnUnit
        {
            UnitId = 12, Health = 80f, MaxHealth = 100f, ActiveSlot = 1, CanUse = true, UseTargetId = 55, UseVerb = 2, UseLabel = "Open door",
            Ammo = [new AmmoEntry { Slot = 1, Weapon = "Rifle", AmmoType = "5.56", Rounds = 20, Reserve = 90 }],
        }, "t-own");
        Assert.Equal(80f, own.Health);
        Assert.Equal(90, own.Ammo[0].Reserve);
        Assert.Equal("Open door", own.UseLabel);

        var join = RoundTrip(new JoinRequest { RequestSeq = 3, Kind = JoinKind.JoinKindRole, UnitId = 0, Role = "Rifleman", Side = 1, Kit = 2, AreaId = 4, ClientId = 0xFEDCBA9876543210UL }, "t-join");
        Assert.Equal(JoinKind.JoinKindRole, join.Kind);
        Assert.Equal("Rifleman", join.Role);
        Assert.Equal(2, join.Kit);
        Assert.Equal(0xFEDCBA9876543210UL, join.ClientId);

        var frame = RoundTrip(new ControlFrame
        {
            Seq = 100, Forward = 1f, Lateral = -1f, Steer = 0.5f, Yaw = 0.25f, Vertical = 0.1f, Gait = 2, Trigger = 1, Mount = 1,
            AimPoint = new Vec3d { X = 1, Y = 2, Z = 3 }, Stance = 3, ShotSeq = 4, CancelSeq = 5, AmmoSeq = 6, ShotTarget = 11, UseSeq = 7, UseVerb = 1,
            UseTarget = 55, Lean = 2, Binoculars = 1, LookFrom = new Vec3d { Z = 1.7 }, LookDir = new Vec3f { X = 1 }, FirePref = 2, ClientId = 42UL,
        }, "t-ctrl");
        Assert.Equal(100u, frame.Seq);
        Assert.Equal(3, frame.Stance);
        Assert.Equal(6, frame.AmmoSeq);
        Assert.Equal(1.7, frame.LookFrom.Z);
        Assert.Equal(2, frame.FirePref);
        Assert.Equal(42UL, frame.ClientId);

        var leave = RoundTrip(new Leave { Seq = 9, ClientId = ulong.MaxValue }, "t-leave");
        Assert.Equal(9u, leave.Seq);
        Assert.Equal(ulong.MaxValue, leave.ClientId);

        var view = RoundTrip(new GizmoView
        {
            Layers = [new NameItem { Name = "Paths" }, new NameItem { Name = "Contacts" }], SelectedIds = [11, 12], PinnedIds = [13],
            PinnedFamilies = [new NameItem { Name = "Perception" }], RateHz = 10f,
        }, "t-view");
        Assert.Equal(2, view.Layers.Count);
        Assert.Equal("Contacts", view.Layers[1].Name);
        Assert.Equal([11, 12], view.SelectedIds);
        Assert.Equal(10f, view.RateHz);

        var gizmo = RoundTrip(new GizmoFrame
        {
            Producer = "hrot", Layer = "Paths", SimTime = 4,
            Primitives =
            [
                new GizmoPrimitive { Shape = GizmoShape.GizmoShapeLineStrip, Points = [new Vec3d { X = 1 }, new Vec3d { X = 2, Y = 3 }], ColourRgba = 0xFF0000FF, Width = 2f },
                new GizmoPrimitive { Shape = GizmoShape.GizmoShapeText, Points = [new Vec3d { Z = 5 }], ColourRgba = 0xFFFFFFFF, Text = "W aiming" },
            ],
        }, "t-gizmo");
        Assert.Equal("hrot", gizmo.Producer);
        Assert.Equal(2, gizmo.Primitives.Count);
        Assert.Equal(2.0, gizmo.Primitives[0].Points[1].X);
        Assert.Equal("W aiming", gizmo.Primitives[1].Text);
        Assert.Equal(GizmoShape.GizmoShapeText, gizmo.Primitives[1].Shape);
    }

    /// <summary>Writes a sample, then joins a reader: a late reader still gets it only on a TRANSIENT_LOCAL state topic (DD-017 3.11).</summary>
    private static T LateJoin<T>(T value, string partition) where T : struct
    {
        using var writerSide = new DdsParticipant(NewDomain(), defaultPartition: partition);
        using var writer = new DdsWriter<T>(writerSide);
        writer.Write(value);
        using var readerSide = new DdsParticipant(NewDomain(), defaultPartition: partition);
        using var reader = new DdsReader<T>(readerSide);
        T? got = null;
        Assert.True(WaitFor(() =>
        {
            using var loan = reader.Take(8);
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    got = sample.Data;
                }
            }
            return got is not null;
        }), $"{typeof(T).Name}: a late reader got nothing (not a state topic?)");
        return got!.Value;
    }

    [Fact]
    public void Round_two_extensions_and_topics_round_trip()
    {
        var person = RoundTrip(new PersonState { EntityId = 12, Burning = true, Fear = 3, Anger = 4, CivilianMode = CivilianMode.CivilianModeArmedHostile }, "t2-person");
        Assert.True(person.Burning);
        Assert.Equal((byte)3, person.Fear);
        Assert.Equal((byte)4, person.Anger);
        Assert.Equal(CivilianMode.CivilianModeArmedHostile, person.CivilianMode);

        var look = RoundTrip(new VehicleLook { EntityId = 11, RotorSpeed = 0.55f, FlailActive = true, Parts = [], Openings = [new OpeningLook { OpeningId = 9, Progress = 1f, CrewExposed = true }] }, "t2-vlook");
        Assert.Equal(0.55f, look.RotorSpeed);
        Assert.True(look.Openings[0].CrewExposed);
        Assert.True(look.FlailActive);

        var fire = RoundTrip(new Fire { ShooterId = 12, MunitionTypeId = 7_000_000_077L, WeaponCategory = WeaponCategory.WeaponCategoryMachineGun }, "t2-fire");
        Assert.Equal(7_000_000_077L, fire.MunitionTypeId);
        Assert.Equal(WeaponCategory.WeaponCategoryMachineGun, fire.WeaponCategory);
        var detonation = RoundTrip(new Detonation { ShooterId = 12, MunitionTypeId = 7_000_000_077L }, "t2-det");
        Assert.Equal(7_000_000_077L, detonation.MunitionTypeId);

        var round = RoundTrip(new Round
        {
            EntityId = 21, MunitionTypeId = 6_000_000_001L, ShooterId = 12, Muzzle = new Vec3d { X = 1, Y = 2, Z = 3 }, Lying = true, FuzeLeftS = -1f, Stopped = true,
        }, "t2-round");
        Assert.Equal(21, round.EntityId);
        Assert.Equal(6_000_000_001L, round.MunitionTypeId);
        Assert.Equal(3.0, round.Muzzle.Z);
        Assert.True(round.Lying);
        Assert.Equal(-1f, round.FuzeLeftS);
        Assert.True(round.Stopped);

        var munition = RoundTrip(new MunitionLook
        {
            MunitionTypeId = 6_000_000_001L, Shape = MunitionShape.MunitionShapeArrow, Length = 0.7f, Diameter = 0.01f, ColourRgba = 0x4B5320FF, SmokeTrail = true, Tracer = true,
            TracerColourRgba = 0xFF9A30FF, Sticks = true, Bolt = true,
        }, "t2-mlook");
        Assert.Equal(MunitionShape.MunitionShapeArrow, munition.Shape);
        Assert.Equal(0xFF9A30FFu, munition.TracerColourRgba);
        Assert.True(munition.Bolt);

        var jet = RoundTrip(new FlameJet
        {
            EntityId = 12, Mount = 2, AimPoint = new Vec3d { X = 9 }, TargetId = 13, RangeM = 25f, HalfAngleRad = 0.1f, Intensity = 0.8f, BurstStartedAt = 4.5, BurnSeconds = 30f,
        }, "t2-jet");
        Assert.Equal(2, jet.Mount);
        Assert.Equal(25f, jet.RangeM);
        Assert.Equal(4.5, jet.BurstStartedAt);

        var spots = RoundTrip(new FireSpots
        {
            EntityId = 11,
            Spots = Enumerable.Range(0, 8).Select(i => new FireSpot { Position = new Vec3d { X = i }, Radius = 1.5f, Intensity = 1f, Since = 3, Seconds = 60 }).ToList(),
        }, "t2-spots");
        Assert.Equal(8, spots.Spots.Count);
        Assert.Equal(7.0, spots.Spots[7].Position.X);

        var marks = RoundTrip(new GroundMarks
        {
            EntityId = 31,
            Marks = Enumerable.Range(0, 16).Select(i => new GroundMark
            {
                Kind = HeatMarkKind.HeatMarkKindBurnedArea, Centre = new Vec3d { X = i, Y = 2 }, Heading = 0.5f, Length = 6f, Width = 3f, HotEnd = HeatMarkEnd.HeatMarkEndRear,
                StandSince = 5, LeftAt = 9,
            }).ToList(),
        }, "t2-marks");
        Assert.Equal(16, marks.Marks.Count);
        Assert.Equal(HeatMarkKind.HeatMarkKindBurnedArea, marks.Marks[15].Kind);
        Assert.Equal(HeatMarkEnd.HeatMarkEndRear, marks.Marks[0].HotEnd);
        Assert.Equal(9.0, marks.Marks[3].LeftAt);
    }

    [Fact]
    public async Task The_new_topics_are_state_topics_a_late_joiner_reads_and_a_jet_is_keyed_by_entity_and_mount()
    {
        Assert.Equal(21, LateJoin(new Round { EntityId = 21 }, "t2-late-round").EntityId);
        Assert.Equal(6L, LateJoin(new MunitionLook { MunitionTypeId = 6 }, "t2-late-mlook").MunitionTypeId);
        Assert.Equal(2, LateJoin(new FlameJet { EntityId = 12, Mount = 2 }, "t2-late-jet").Mount);
        Assert.Equal(11, LateJoin(new FireSpots { EntityId = 11, Spots = [] }, "t2-late-spots").EntityId);
        Assert.Equal(31, LateJoin(new GroundMarks { EntityId = 31, Marks = [] }, "t2-late-marks").EntityId);

        using var participant = new DdsParticipant(NewDomain(), defaultPartition: "t2-jet-keys");
        using var writer = new DdsWriter<FlameJet>(participant);
        using var reader = new DdsReader<FlameJet>(participant);
        Assert.True(await writer.WaitForReaderAsync(TimeSpan.FromSeconds(5)));
        writer.Write(new FlameJet { EntityId = 12, Mount = 0, Intensity = 0.25f });
        writer.Write(new FlameJet { EntityId = 12, Mount = 1, Intensity = 0.75f });
        writer.Write(new FlameJet { EntityId = 12, Mount = 1, Intensity = 1f });
        var seen = new Dictionary<int, float>();
        Assert.True(WaitFor(() =>
        {
            using var loan = reader.Take(8);
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    seen[sample.Data.Mount] = sample.Data.Intensity;
                }
            }
            return seen.Count == 2 && seen[1] == 1f;
        }));
        Assert.Equal(0.25f, seen[0]);
    }

    [Fact]
    public void The_hud_extensions_and_topics_round_trip()
    {
        var own = RoundTrip(new OwnUnit
        {
            UnitId = 12, Health = 80f, MaxHealth = 100f, ActiveSlot = 1, CanUse = true, UseTargetId = 55, UseVerb = 2, UseLabel = "Drive BMP-2", FireMode = FireMode.FireModeLock, UseSeq = 9,
            UseOutcome = UseOutcome.UseOutcomeRefused, UseReason = UseReason.UseReasonLocked, SpreadRad = 0.004f, RadarWarning = RadarWarningLevel.RadarWarningLevelTracked,
            RadarBearingDeg = 135f, MissileLaunch = true, MissileBearingDeg = 270f, MissileTimeToImpactS = 8.5f, MissileSeeker = MissileSeeker.MissileSeekerInfraRed,
            Ammo =
            [
                new AmmoEntry
                {
                    Slot = 1, Weapon = "MG", AmmoType = "7.62 mm", Rounds = 120, Reserve = 0, AmmoTypeId = 7_000_000_001L, Max = 200, Magazine = 3, LoadedRounds = 25, Busy = true, Overheated = true,
                    HeatPercent = 87,
                },
            ],
        }, "t2-own");
        Assert.Equal((FireMode.FireModeLock, (byte)9, UseOutcome.UseOutcomeRefused, UseReason.UseReasonLocked, 0.004f), (own.FireMode, own.UseSeq, own.UseOutcome, own.UseReason, own.SpreadRad));
        Assert.Equal((RadarWarningLevel.RadarWarningLevelTracked, 135f, true, 270f, 8.5f, MissileSeeker.MissileSeekerInfraRed),
            (own.RadarWarning, own.RadarBearingDeg, own.MissileLaunch, own.MissileBearingDeg, own.MissileTimeToImpactS, own.MissileSeeker));
        var a = own.Ammo[0];
        Assert.Equal((7_000_000_001L, 200, 3, 25, true, true, 87), (a.AmmoTypeId, a.Max, a.Magazine, a.LoadedRounds, a.Busy, a.Overheated, a.HeatPercent));

        var kill = RoundTrip(new ManDowned { SimTime = 5.5, VictimId = 1, SourceId = 2, OriginalSourceId = 3, State = Injury.InjuryDead, BodyPart = 4, VictimSide = 2 }, "t2-kill");
        Assert.Equal((5.5, 1, 2, 3, Injury.InjuryDead, (byte)4, (byte)2), (kill.SimTime, kill.VictimId, kill.SourceId, kill.OriginalSourceId, kill.State, kill.BodyPart, kill.VictimSide));

        var message = RoundTrip(new PlayerMessage
        {
            MessageId = 7, OriginNode = 3, To = MessageAddress.MessageAddressSide, ToValue = 1, Text = new string('m', 256), Seconds = 6f, Priority = 2, SentAt = 90.5, SourceId = 3001,
        }, "t2-message");
        Assert.Equal((7, 3, MessageAddress.MessageAddressSide, 1, 256, 6f, (byte)2, 90.5, 3001), (message.MessageId, message.OriginNode, message.To, message.ToValue, message.Text.Length,
            message.Seconds, message.Priority, message.SentAt, message.SourceId));

        var status = RoundTrip(new Status
        {
            StatusId = 3001, Role = ObjectiveRole.ObjectiveRoleTrigger, State = ObjectiveState.ObjectiveStateAchieved, TriggerState = TriggerState.TriggerStateFired, Closed = true, Owner = 2,
            ProgressCur = 3, ProgressNeed = 10, Value = 0.5f, Target = 1f, Score = 0.25f, Pass = 0.8f, Points = 5f, ChangedAt = 12.5, Reason = "held", Title = "Hold", Kind = "HoldArea",
            Group = "mission", Phase = 2, Required = true, Scored = true, Weight = 2f, VisibleSides = 6, StepCur = 1, StepCount = 3, HoldFor = 60f, Deadline = 900f, ExpectedAt = 130.0,
            Capture = true, Challenger = 1, AreaCentre = new Vec3d { X = 100, Y = 200 }, AreaRadius = 50f, EndsExercise = true, SubjectId = 77, ParentId = 5,
        }, "t2-status");
        Assert.Equal((3001, ObjectiveRole.ObjectiveRoleTrigger, ObjectiveState.ObjectiveStateAchieved, TriggerState.TriggerStateFired, true, (byte)2), (status.StatusId, status.Role, status.State,
            status.TriggerState, status.Closed, status.Owner));
        Assert.Equal(("held", "Hold", "HoldArea", "mission", (byte)6, 100.0, 77, 5), (status.Reason, status.Title, status.Kind, status.Group, status.VisibleSides, status.AreaCentre.X, status.SubjectId, status.ParentId));

        var exercise = RoundTrip(new Exercise
        {
            Phase = ExercisePhase.ExercisePhaseEnded, EndedAt = 1800.5, Reason = "Time is up", Friendly = new SideResult { Outcome = SideOutcome.SideOutcomeWin, Points = 40f, Score = 0.9f },
            Opposing = new SideResult { Outcome = SideOutcome.SideOutcomeLose }, Neutral = new SideResult(),
        }, "t2-exercise");
        Assert.Equal((ExercisePhase.ExercisePhaseEnded, 1800.5, "Time is up", SideOutcome.SideOutcomeWin, 0.9f, SideOutcome.SideOutcomeLose),
            (exercise.Phase, exercise.EndedAt, exercise.Reason, exercise.Friendly.Outcome, exercise.Friendly.Score, exercise.Opposing.Outcome));
    }

    [Fact]
    public void The_hud_state_topics_are_late_join_topics_and_the_events_are_not()
    {
        Assert.Equal(3001, LateJoin(new Status { StatusId = 3001, Reason = "", Title = "", Kind = "", Group = "" }, "t2-late-status").StatusId);
        Assert.Equal(ExercisePhase.ExercisePhaseClosing, LateJoin(new Exercise { Phase = ExercisePhase.ExercisePhaseClosing, Reason = "" }, "t2-late-exercise").Phase);

        // an event is volatile: written before the reader joined, it is not delivered
        using var writerSide = new DdsParticipant(NewDomain(), defaultPartition: "t2-late-kill");
        using var writer = new DdsWriter<ManDowned>(writerSide);
        writer.Write(new ManDowned { VictimId = 1 });
        using var readerSide = new DdsParticipant(NewDomain(), defaultPartition: "t2-late-kill");
        using var reader = new DdsReader<ManDowned>(readerSide);
        Thread.Sleep(500);
        using var loan = reader.Take(8);
        foreach (var sample in loan)
        {
            Assert.False(sample.IsValid);
        }
    }

    [Fact]
    public void The_type_animal_and_bank_additions_round_trip()
    {
        string json = "{\"descriptors\":[" + string.Join(",", Enumerable.Range(0, 30000).Select(i => "{\"type\":\"WeaponSuiteDto\",\"partId\":" + i + "}")) + "]}";
        var type = RoundTrip(new TkbType
        {
            TypeId = 5_000_000_001L, Name = "T-72", CategoryPath = "Vehicles/Tanks", DisType = "1.1.225.1.1.3.0", Schema = "hrot/1", ContentHash = 0xAF63DC4C8601EC8CUL, Json = json,
        }, "t2-type");
        Assert.Equal((5_000_000_001L, "T-72", "Vehicles/Tanks", "1.1.225.1.1.3.0", "hrot/1", 0xAF63DC4C8601EC8CUL), (type.TypeId, type.Name, type.CategoryPath, type.DisType, type.Schema, type.ContentHash));
        Assert.Equal(json, type.Json);   // an unbounded text of well over 1 MB

        var animal = RoundTrip(new Animal
        {
            EntityId = 3001, Mode = AnimalMode.AnimalModeFlee, FearBand = 3, Bolting = true, Dangerous = true, Calling = true, FlockCount = 12, FlockInitialCount = 20, FlockSeed = 4_000_000_000u,
            FlockMode = FlockMode.FlockModeLanded,
        }, "t2-animal");
        Assert.Equal((3001, AnimalMode.AnimalModeFlee, (byte)3, true, true, true, 12, 20, 4_000_000_000u, FlockMode.FlockModeLanded), (animal.EntityId, animal.Mode, animal.FearBand, animal.Bolting,
            animal.Dangerous, animal.Calling, animal.FlockCount, animal.FlockInitialCount, animal.FlockSeed, animal.FlockMode));

        var state = RoundTrip(new EntityState { EntityId = 4001, Bank = -0.4710f }, "t2-bank");
        Assert.Equal(-0.4710f, state.Bank);
        Assert.Equal(Domain.DomainAnimal, RoundTrip(new Entity { EntityId = 1, Domain = Domain.DomainAnimal, Name = "Cow" }, "t2-domain").Domain);
    }

    [Fact]
    public void The_type_and_animal_topics_are_late_join_state_topics()
    {
        Assert.Equal(7L, LateJoin(new TkbType { TypeId = 7, Name = "", CategoryPath = "", DisType = "", Schema = "hrot/1", Json = "{}" }, "t2-late-type").TypeId);
        Assert.Equal(9, LateJoin(new Animal { EntityId = 9 }, "t2-late-animal").EntityId);
    }

    [Fact]
    public void Building_damage_round_trips_and_is_a_late_join_state_keyed_by_the_building_label()
    {
        var damage = RoundTrip(new BuildingDamage
        {
            Building = "range/H2", Collapsed = true,
            Breaches = Enumerable.Range(0, 64).Select(i => new Breach { Panel = i, From = 1f, To = 2f, Bottom = 0f, Top = 2.2f, Kind = BreachKind.BreachKindPassage }).ToList(),
            Windows = Enumerable.Range(0, 256).Select(i => new WindowDamage { Panel = i, Opening = 3, Hits = 2, U0 = 10, V0 = 20, U1 = 30, V1 = 40 }).ToList(),
        }, "t2-building");
        Assert.Equal(("range/H2", true, 64, 256), (damage.Building, damage.Collapsed, damage.Breaches.Count, damage.Windows.Count));
        Assert.Equal((63, 2.2f, BreachKind.BreachKindPassage), (damage.Breaches[63].Panel, damage.Breaches[63].Top, damage.Breaches[63].Kind));
        Assert.Equal((255, 3, (byte)2, (byte)10, (byte)20, (byte)30, (byte)40), (damage.Windows[255].Panel, damage.Windows[255].Opening, damage.Windows[255].Hits, damage.Windows[255].U0, damage.Windows[255].V0, damage.Windows[255].U1, damage.Windows[255].V1));
        Assert.Equal("range/H1", LateJoin(new BuildingDamage { Building = "range/H1", Breaches = [], Windows = [] }, "t2-late-building").Building);
    }

    [Fact]
    public void The_traffic_topics_round_trip_with_the_compact_knot_units()
    {
        var mover = RoundTrip(new Mover { MoverId = 101, Kind = MoverKind.MoverKindCar, TypeId = 8851, ColourRgba = 0xB02A2AFF, Seed = 205, Length = 4.5f, Width = 1.8f, Source = MoverSource.MoverSourceSumo }, "t2-mover");
        Assert.Equal((101, MoverKind.MoverKindCar, 8851L, 0xB02A2AFFu, 205u, 4.5f, 1.8f, MoverSource.MoverSourceSumo), (mover.MoverId, mover.Kind, mover.TypeId, mover.ColourRgba, mover.Seed, mover.Length, mover.Width, mover.Source));

        var batch = RoundTrip(new MoverTracks
        {
            SimTime = 42.5,
            Knots = Enumerable.Range(0, 4096).Select(i => new MoverKnot { MoverId = i, Dt = -0.05f, X = 1f + i, Y = 2f, Z = 3f, HeadingCentiDeg = -18000, PitchCentiDeg = 9000, Speed = 13.9f }).ToList(),
        }, "t2-tracks");
        Assert.Equal((42.5, 4096), (batch.SimTime, batch.Knots.Count));
        Assert.Equal((4095, -0.05f, 4096f, (short)-18000, (short)9000, 13.9f), (batch.Knots[4095].MoverId, batch.Knots[4095].Dt, batch.Knots[4095].X, batch.Knots[4095].HeadingCentiDeg, batch.Knots[4095].PitchCentiDeg, batch.Knots[4095].Speed));

        var light = RoundTrip(new TrafficLights { SignalId = 4, Position = new Vec3d { X = 100, Y = -1.7, Z = 5 }, HeadingDeg = 90f, State = TrafficLightState.TrafficLightStateGreen }, "t2-light");
        Assert.Equal((4, 90f, TrafficLightState.TrafficLightStateGreen), (light.SignalId, light.HeadingDeg, light.State));
        Assert.Equal(7, LateJoin(new Mover { MoverId = 7 }, "t2-late-mover").MoverId);
        Assert.Equal(8, LateJoin(new TrafficLights { SignalId = 8 }, "t2-late-light").SignalId);
    }

    [Fact]
    public void Late_joiner_gets_every_live_instance()
    {
        using var writerSide = new DdsParticipant(NewDomain(), defaultPartition: "late");
        using var writer = new DdsWriter<EntityState>(writerSide);
        for (var i = 1; i <= 500; i++)
        {
            writer.Write(new EntityState { EntityId = i, SimTime = i, Position = new Vec3d { X = i } });
        }

        using var readerSide = new DdsParticipant(NewDomain(), defaultPartition: "late");
        using var reader = new DdsReader<EntityState>(readerSide);
        var seen = new HashSet<int>();
        WaitFor(() =>
        {
            using var loan = reader.Take(600);
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    seen.Add(sample.Data.EntityId);
                }
            }
            return seen.Count >= 500;
        }, 10000);

        Assert.Equal(500, seen.Count);
    }

    [Fact]
    public void Disposed_entity_is_gone_for_a_late_joiner()
    {
        using var writerSide = new DdsParticipant(NewDomain(), defaultPartition: "gone");
        using var writer = new DdsWriter<EntityState>(writerSide);
        writer.Write(new EntityState { EntityId = 1 });
        writer.Write(new EntityState { EntityId = 2 });
        writer.DisposeInstance(new EntityState { EntityId = 1 });

        using var readerSide = new DdsParticipant(NewDomain(), defaultPartition: "gone");
        using var reader = new DdsReader<EntityState>(readerSide);
        var valid = new List<int>();
        WaitFor(() =>
        {
            using var loan = reader.Take(16);
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    valid.Add(sample.Data.EntityId);
                }
            }
            return valid.Count > 0;
        });
        Thread.Sleep(300);
        using (var loan = reader.Take(16))
        {
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    valid.Add(sample.Data.EntityId);
                }
            }
        }

        Assert.Equal([2], valid);
    }

    [Fact]
    public void Generated_idl_contains_every_topic()
    {
        var idl = string.Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.idl").Select(File.ReadAllText));

        Assert.False(string.IsNullOrEmpty(idl), "no generated IDL next to the test assembly");
        foreach (var topic in DpsProtocol.AllTopics)
        {
            // the topic name is carried by the type name in the IDL (SimNetGateway::Protocol::DdsPlayer::<Type>) and by DdsTopicName
            var type = topic["DPS_".Length..];
            Assert.Contains($"struct {type} ", idl);
        }
        Assert.Equal(52, DpsProtocol.AllTopics.Count);   // + Utterance, Report, Order, Ambience, Weather (DD-026, DD-028)
        foreach (var name in new[] { "Mover", "MoverTracks", "TrafficLights" })
        {
            Assert.Contains("DPS_" + name, DpsProtocol.AllTopics);
        }

        foreach (var enumerator in new[] { "MoverKindPedestrian", "MoverSourceSumo", "TrafficLightStateYellow" })
        {
            Assert.Contains(enumerator, idl);
        }

        Assert.Contains("DPS_BuildingDamage", DpsProtocol.AllTopics);
        foreach (var enumerator in new[] { "BreachKindPassage" })
        {
            Assert.Contains(enumerator, idl);
        }

        Assert.Contains("DPS_Animal", DpsProtocol.AllTopics);
        Assert.Contains("DPS_TkbType", DpsProtocol.AllTopics);
        foreach (var enumerator in new[] { "DomainAnimal", "AnimalModeGuard", "FlockModePerched" })
        {
            Assert.Contains(enumerator, idl);
        }

        foreach (var name in new[] { "ManDowned", "PlayerMessage", "Status", "Exercise" })
        {
            Assert.Contains("DPS_" + name, DpsProtocol.AllTopics);
        }

        foreach (var enumerator in new[] { "FireModeLock", "UseOutcomeRefused", "UseReasonOtherSide", "RadarWarningLevelTracked", "MissileSeekerInfraRed", "MessageAddressInstructor", "ObjectiveRoleTrigger", "ObjectiveStateSkipped", "TriggerStateCooldown", "ExercisePhaseEnded", "SideOutcomeDraw" })
        {
            Assert.Contains(enumerator, idl);
        }


        // the round-2 topics and their enumerations (every enumerator carries its enum name; ids are 64-bit)
        foreach (var name in new[] { "Round", "MunitionLook", "FlameJet", "FireSpots", "GroundMarks" })
        {
            Assert.Contains("DPS_" + name, DpsProtocol.AllTopics);
        }

        foreach (var enumerator in new[] { "CivilianModePanic", "WeaponCategoryMachineGun", "WeaponCategoryFlamethrower", "MunitionShapeArrow", "HeatMarkKindBurnedArea", "HeatMarkEndRear" })
        {
            Assert.Contains(enumerator, idl);
        }

        Assert.Contains("int64 _TypeId;", idl);
        Assert.Contains("int64 MunitionTypeId;", idl);
        Assert.Equal("DPS_GizmoFrame", DdsTopicName.Of<GizmoFrame>());
        Assert.Equal("DPS_Session", DdsTopicName.Of<Session>());
    }

    // ---- round 7 (DD-022 3h): clock, steps, client world, rendering -----------------------------------------------------------------

    private static string Full(int length) => new string('x', length - 1) + "y";

    [Fact]
    public void The_round_7_topics_round_trip_every_field_at_full_bounds()
    {
        var clock = RoundTrip(new SimClock
        {
            Mode = ClockMode.ClockModeStepped, AnchorWallTicks = 639_000_000_000_000_001L, AnchorSimTime = 123.456789, TimeScale = 2.5f, FixedDelta = 0.05f, StepSeq = 9_000_000_001L,
            TimelineEpoch = 4_000_000_002L, Seq = 4_000_000_003u,
        }, "t7-clock");
        Assert.Equal((ClockMode.ClockModeStepped, 639_000_000_000_000_001L, 123.456789, 2.5f, 0.05f, 9_000_000_001L, 4_000_000_002L, 4_000_000_003u),
            (clock.Mode, clock.AnchorWallTicks, clock.AnchorSimTime, clock.TimeScale, clock.FixedDelta, clock.StepSeq, clock.TimelineEpoch, clock.Seq));

        var probe = RoundTrip(new ClockProbe { ClientId = 0xFEDCBA9876543210UL, ClientSendTicks = 639_000_000_000_000_002L }, "t7-probe");
        Assert.Equal((0xFEDCBA9876543210UL, 639_000_000_000_000_002L), (probe.ClientId, probe.ClientSendTicks));

        var echo = RoundTrip(new ClockEcho { ClientId = 7, ClientSendTicks = 11, StationReceiveTicks = 22, StationTransmitTicks = 33 }, "t7-echo");
        Assert.Equal((7UL, 11L, 22L, 33L), (echo.ClientId, echo.ClientSendTicks, echo.StationReceiveTicks, echo.StationTransmitTicks));

        var step = RoundTrip(new ClockStep { StepSeq = 77, FixedDelta = 0.1f, TargetSimTime = 8.25, TimeScale = 0.5f, TimelineEpoch = 3, AckRequired = true, Samples = 4_000_000_004u }, "t7-step");
        Assert.Equal((77L, 0.1f, 8.25, 0.5f, 3L, true, 4_000_000_004u), (step.StepSeq, step.FixedDelta, step.TargetSimTime, step.TimeScale, step.TimelineEpoch, step.AckRequired, step.Samples));

        var ack = RoundTrip(new StepAck { ClientId = 8, StepSeq = 78, Checksum = -123456 }, "t7-ack");
        Assert.Equal((8UL, 78L, -123456), (ack.ClientId, ack.StepSeq, ack.Checksum));

        var world = RoundTrip(new ClientWorld { ClientId = 9, WorldEpoch = 4_000_000_005u, State = ClientWorldState.ClientWorldStateFailed, Step = Full(64), Reason = Full(256), WaitForMe = true }, "t7-world");
        Assert.Equal((9UL, 4_000_000_005u, ClientWorldState.ClientWorldStateFailed, Full(64), Full(256), true), (world.ClientId, world.WorldEpoch, world.State, world.Step, world.Reason, world.WaitForMe));

        var client = RoundTrip(new RenderClient
        {
            ClientId = 10, Name = Full(64), Ready = true, MaxWidth = 7680, MaxHeight = 4320, MaxSupersample = 4, Formats = 1, AudioFormats = 3, MaxSampleRate = 96000, ReadyJobId = Full(32),
        }, "t7-client");
        Assert.Equal((10UL, Full(64), true, 7680, 4320, (byte)4, 1u, 3u, 96000, Full(32)),
            (client.ClientId, client.Name, client.Ready, client.MaxWidth, client.MaxHeight, client.MaxSupersample, client.Formats, client.AudioFormats, client.MaxSampleRate, client.ReadyJobId));

        var job = RoundTrip(new RenderJob
        {
            JobId = Full(32), ClientId = 11, Kind = RenderKind.RenderKindStills, What = Full(128), Fps = 60, Width = 3840, Height = 2160, Supersample = 2, Dir = Full(256), Audio = true,
            SampleRate = 48000, Channels = 2, State = RenderState.RenderStateCancelled, Reason = Full(256),
        }, "t7-job");
        Assert.Equal((Full(32), 11UL, RenderKind.RenderKindStills, Full(128), 60, 3840, 2160, (byte)2, Full(256), true, 48000, (byte)2, RenderState.RenderStateCancelled, Full(256)),
            (job.JobId, job.ClientId, job.Kind, job.What, job.Fps, job.Width, job.Height, job.Supersample, job.Dir, job.Audio, job.SampleRate, job.Channels, job.State, job.Reason));

        var request = new FrameRequest
        {
            JobId = Full(32), FrameIndex = 123456, Kind = FrameKind.FrameKindSilence, StepSeq = 5_000_000_006L, SimTime = 321.125, FilmRate = 0.25f,
            Position = new Vec3d { X = 1.5, Y = -2.5, Z = 3.5 }, Orientation = new Quat { X = 0.1f, Y = 0.2f, Z = 0.3f, W = 0.4f }, FovDeg = 47.5f, Width = 1920, Height = 1080, Supersample = 3,
            File = Full(256), Subtitle = Full(512), SubtitlePosition = SubtitlePosition.SubtitlePositionCentre, SubtitleAlpha = 0.75f,
            Graphics = Enumerable.Range(0, 64).Select(i => new FilmGraphic
            {
                Kind = FilmGraphicKind.FilmGraphicKindLabel, Points = Enumerable.Range(0, 64).Select(j => new Vec3d { X = i, Y = j, Z = i + j / 2.0 }).ToList(), Radius = 10f + i, Text = Full(64),
                ColourRgb = 0xFF8040u + (uint)i, Alpha = 0.5f,
            }).ToList(),
            LayersOn = Enumerable.Range(0, 32).Select(i => new LayerName { Name = i == 31 ? Full(64) : "on" + i }).ToList(),
            LayersOff = Enumerable.Range(0, 32).Select(i => new LayerName { Name = i == 31 ? Full(64) : "off" + i }).ToList(),
            Fade = 0.625f,
        };
        var frame = RoundTrip(request, "t7-frame");
        Assert.Equal(0.625f, frame.Fade);
        var repeat = RoundTrip(new FrameRequest { JobId = "r", FrameIndex = 7, Kind = FrameKind.FrameKindRepeat, Fade = 1f, Graphics = new(), LayersOn = new(), LayersOff = new() }, "t7-repeat");
        Assert.Equal((FrameKind.FrameKindRepeat, 1f), (repeat.Kind, repeat.Fade));
        Assert.Equal((Full(32), 123456, FrameKind.FrameKindSilence, 5_000_000_006L, 321.125, 0.25f, 47.5f, 1920, 1080, (byte)3, Full(256), Full(512), SubtitlePosition.SubtitlePositionCentre, 0.75f),
            (frame.JobId, frame.FrameIndex, frame.Kind, frame.StepSeq, frame.SimTime, frame.FilmRate, frame.FovDeg, frame.Width, frame.Height, frame.Supersample, frame.File, frame.Subtitle, frame.SubtitlePosition, frame.SubtitleAlpha));
        Assert.Equal((1.5, -2.5, 3.5, 0.1f, 0.2f, 0.3f, 0.4f), (frame.Position.X, frame.Position.Y, frame.Position.Z, frame.Orientation.X, frame.Orientation.Y, frame.Orientation.Z, frame.Orientation.W));
        Assert.Equal(64, frame.Graphics.Count);
        for (int i = 0; i < 64; i++)
        {
            var got = frame.Graphics[i];
            Assert.Equal((FilmGraphicKind.FilmGraphicKindLabel, 10f + i, Full(64), 0xFF8040u + (uint)i, 0.5f, 64), (got.Kind, got.Radius, got.Text, got.ColourRgb, got.Alpha, got.Points.Count));
            for (int j = 0; j < 64; j++)
            {
                Assert.Equal((double)i, got.Points[j].X);
                Assert.Equal((double)j, got.Points[j].Y);
                Assert.Equal(i + j / 2.0, got.Points[j].Z);
            }
        }

        Assert.Equal(request.LayersOn.Select(l => l.Name), frame.LayersOn.Select(l => l.Name));
        Assert.Equal(request.LayersOff.Select(l => l.Name), frame.LayersOff.Select(l => l.Name));
        Assert.Equal(32, frame.LayersOn.Count);
        Assert.Equal(Full(64), frame.LayersOff[31].Name);

        var done = RoundTrip(new FrameDone
        {
            ClientId = 12, JobId = Full(32), FrameIndex = 99, StepSeq = 5_000_000_007L, Status = FrameStatus.FrameStatusFailed, File = Full(256), AudioSamples = 1600, Reason = Full(256), RenderMs = 28000.5f,
        }, "t7-done");
        Assert.Equal((12UL, Full(32), 99, 5_000_000_007L, FrameStatus.FrameStatusFailed, Full(256), 1600, Full(256), 28000.5f),
            (done.ClientId, done.JobId, done.FrameIndex, done.StepSeq, done.Status, done.File, done.AudioSamples, done.Reason, done.RenderMs));

        var result = RoundTrip(new RenderJobResult { ClientId = 13, JobId = Full(32), Status = FrameStatus.FrameStatusWritten, TrackFile = Full(256), TrackSamples = 9_000_000_008L, Reason = Full(256) }, "t7-result");
        Assert.Equal((13UL, Full(32), FrameStatus.FrameStatusWritten, Full(256), 9_000_000_008L, Full(256)),
            (result.ClientId, result.JobId, result.Status, result.TrackFile, result.TrackSamples, result.Reason));

        var view = RoundTrip(new View
        {
            Shot = "s", Velocity = new Vec3f { X = 1, Y = 2, Z = 3 }, Acceleration = new Vec3f { X = 4, Y = 5, Z = 6 }, AngularVelocity = new Vec3f { X = 7, Y = 8, Z = 9 }, FovRate = -1.5f,
        }, "t7-view");
        Assert.Equal((1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, -1.5f),
            (view.Velocity.X, view.Velocity.Y, view.Velocity.Z, view.Acceleration.X, view.Acceleration.Y, view.Acceleration.Z, view.AngularVelocity.X, view.AngularVelocity.Y, view.AngularVelocity.Z, view.FovRate));
    }

    // topic type, reliable (all), transient local, history depth, liveliness (automatic with the default lease for the player topics)
    public static TheoryData<Type, DdsDurability, int, bool> Round7Qos => new()
    {
        { typeof(SimClock), DdsDurability.TransientLocal, 1, false },
        { typeof(ClockProbe), DdsDurability.Volatile, 16, false },
        { typeof(ClockEcho), DdsDurability.Volatile, 16, false },
        { typeof(ClockStep), DdsDurability.Volatile, 1024, false },
        { typeof(StepAck), DdsDurability.Volatile, 16, false },
        { typeof(ClientWorld), DdsDurability.Volatile, 1, true },
        { typeof(RenderClient), DdsDurability.Volatile, 1, true },
        { typeof(RenderJob), DdsDurability.TransientLocal, 1, false },
        { typeof(FrameRequest), DdsDurability.Volatile, 1024, false },
        { typeof(FrameDone), DdsDurability.Volatile, 1024, false },
        { typeof(RenderJobResult), DdsDurability.Volatile, 16, false },
    };

    [Theory]
    [MemberData(nameof(Round7Qos))]
    public void The_round_7_topics_carry_the_qos_of_the_frozen_table(Type type, DdsDurability durability, int depth, bool player)
    {
        var qos = type.GetCustomAttribute<DdsQosAttribute>()!;
        Assert.Equal(DdsReliability.Reliable, qos.Reliability);
        Assert.Equal(durability, qos.Durability);
        Assert.Equal(DdsHistoryKind.KeepLast, qos.HistoryKind);
        Assert.Equal(depth, qos.HistoryDepth);
        var unset = new DdsQosAttribute();   // no liveliness stated = the attribute's defaults
        Assert.Equal(player ? DdsLivelinessKind.Automatic : unset.LivelinessKind, qos.LivelinessKind);
        Assert.Equal(player ? DpsProtocol.DefaultLeaseMs : unset.LivelinessLeaseDurationMs, qos.LivelinessLeaseDurationMs);
        Assert.NotNull(type.GetCustomAttribute<DdsTopicAttribute>());
    }

    [Fact]
    public void The_round_7_topics_have_the_frozen_rate_classes_and_keys()
    {
        var rates = new Dictionary<Type, DpsRateClass>
        {
            [typeof(SimClock)] = DpsRateClass.Discrete, [typeof(ClockProbe)] = DpsRateClass.Event, [typeof(ClockEcho)] = DpsRateClass.Event, [typeof(ClockStep)] = DpsRateClass.Event,
            [typeof(StepAck)] = DpsRateClass.Event, [typeof(ClientWorld)] = DpsRateClass.Discrete, [typeof(RenderClient)] = DpsRateClass.Discrete, [typeof(RenderJob)] = DpsRateClass.Discrete,
            [typeof(FrameRequest)] = DpsRateClass.Event, [typeof(FrameDone)] = DpsRateClass.Event, [typeof(RenderJobResult)] = DpsRateClass.Event, [typeof(View)] = DpsRateClass.Continuous,
        };
        foreach (var (type, rate) in rates)
        {
            Assert.Equal(rate, type.GetCustomAttribute<DpsRateAttribute>()!.RateClass);
        }

        var keys = new Dictionary<Type, string?>
        {
            [typeof(SimClock)] = null, [typeof(ClockProbe)] = "ClientId", [typeof(ClockEcho)] = "ClientId", [typeof(ClockStep)] = null, [typeof(StepAck)] = "ClientId", [typeof(ClientWorld)] = "ClientId",
            [typeof(RenderClient)] = "ClientId", [typeof(RenderJob)] = "JobId", [typeof(FrameRequest)] = null, [typeof(FrameDone)] = null, [typeof(RenderJobResult)] = null,
        };
        foreach (var (type, key) in keys)
        {
            var keyed = type.GetFields().Where(f => f.GetCustomAttribute<DdsKeyAttribute>() is not null).Select(f => f.Name).ToList();
            Assert.Equal(key is null ? [] : [key], keyed);
        }
    }

    [Fact]
    public void Every_enum_of_the_contract_prefixes_its_enumerators_with_its_name_and_the_round_7_enums_start_at_unknown()
    {
        foreach (var type in typeof(DpsProtocol).Assembly.GetTypes().Where(t => t.IsEnum && t.Namespace == typeof(DpsProtocol).Namespace && t != typeof(DpsRateClass)))
        {
            foreach (var name in Enum.GetNames(type))
            {
                Assert.StartsWith(type.Name, name, StringComparison.Ordinal);
            }
        }

        foreach (var type in new[] { typeof(ClockMode), typeof(ClientWorldState), typeof(RenderKind), typeof(RenderState), typeof(FrameKind), typeof(FrameStatus), typeof(SubtitlePosition), typeof(FilmGraphicKind) })
        {
            Assert.Equal(type.Name + "Unknown", Enum.GetName(type, Enum.ToObject(type, 0)));
        }
    }

    // ---- T-048 / T-049: the appended fields (R-133, R-141) --------------------------------------------------------------------------

    [Fact]
    public void Session_carries_the_terrain_name_and_both_content_hashes_at_full_bounds()
    {
        string hash = new('a', 64);
        var session = RoundTrip(new Session { Terrain = new string('t', 64), StationName = "s", TerrainBaseHash = hash, TerrainOverlayHash = new string('b', 64) }, "t48-session");
        Assert.Equal((new string('t', 64), hash, new string('b', 64)), (session.Terrain, session.TerrainBaseHash, session.TerrainOverlayHash));

        var none = RoundTrip(new Session { Terrain = string.Empty, StationName = string.Empty, TerrainBaseHash = string.Empty, TerrainOverlayHash = string.Empty }, "t48-none");
        Assert.Equal((string.Empty, string.Empty, string.Empty), (none.Terrain, none.TerrainBaseHash, none.TerrainOverlayHash));
    }

    [Fact]
    public void Offer_carries_kits_and_an_area_outline_at_full_bounds_and_old_offers_stay_valid()
    {
        var kits = Enumerable.Range(0, 8).Select(i => new NameItem { Name = new string((char)('A' + i), 32) }).ToList();
        var outline = Enumerable.Range(0, 32).Select(i => new Vec3d { X = i, Y = -i * 2.5, Z = 100 + i }).ToList();
        var slot = RoundTrip(new PlayerSlot
        {
            State = SlotState.SlotStateChoosing, Reason = string.Empty,
            Offers =
            [
                new Offer { Kind = OfferKind.OfferKindRole, Id = 5, Name = "Rifleman", Side = 1, Free = true, Count = 1, Max = 3, Kits = kits, Outline = [] },
                new Offer { Kind = OfferKind.OfferKindArea, Id = 9, Name = "North field", Side = 1, Free = true, Kits = [], Outline = outline },
                new Offer { Kind = OfferKind.OfferKindUnit, Id = 12, Name = "Rifleman 1", Side = 1, Free = true, Kits = [], Outline = [] },
            ],
        }, "t49-slot");

        Assert.Equal(8, slot.Offers[0].Kits.Count);
        Assert.Equal(new string('H', 32), slot.Offers[0].Kits[7].Name);
        Assert.Empty(slot.Offers[0].Outline);
        Assert.Equal(OfferKind.OfferKindArea, slot.Offers[1].Kind);
        Assert.Equal(32, slot.Offers[1].Outline.Count);
        Assert.Equal(new Vec3d { X = 31, Y = -77.5, Z = 131 }, slot.Offers[1].Outline[31]);
        Assert.Empty(slot.Offers[1].Kits);
        Assert.Empty(slot.Offers[2].Kits);
        Assert.Equal((byte)2, (byte)OfferKind.OfferKindArea);   // appended after Unit = 0, Role = 1: the old values do not move
        Assert.Equal((byte)0, (byte)OfferKind.OfferKindUnit);
        Assert.Equal((byte)1, (byte)OfferKind.OfferKindRole);
    }

    [Fact]
    public void The_appended_fields_are_declared_last_so_nothing_before_them_moves()
    {
        static string Last<T>() => typeof(T).GetFields().Select(f => f.Name).Last();
        Assert.Equal("TerrainOverlayHash", Last<Session>());
        Assert.Equal(["Count", "Max", "Kits", "Outline"], typeof(Offer).GetFields().Select(f => f.Name).TakeLast(4).ToArray());
        Assert.Equal(["TerrainFormat", "TerrainBaseHash", "TerrainOverlayHash"], typeof(Session).GetFields().Select(f => f.Name).TakeLast(3).ToArray());
    }

    // ---- T-050: FrameKindRepeat and FrameRequest.Fade (DD-022 P-7, R-142) -----------------------------------------------------------

    [Fact]
    public void FrameKindRepeat_is_appended_after_the_old_values_and_Fade_is_the_last_field_of_FrameRequest()
    {
        Assert.Equal((byte)1, (byte)FrameKind.FrameKindScene);   // the old values do not move
        Assert.Equal((byte)2, (byte)FrameKind.FrameKindSilence);
        Assert.Equal((byte)3, (byte)FrameKind.FrameKindRepeat);
        Assert.Equal("Fade", typeof(FrameRequest).GetFields().Select(f => f.Name).Last());
        Assert.Equal(["LayersOn", "LayersOff", "Fade"], typeof(FrameRequest).GetFields().Select(f => f.Name).TakeLast(3).ToArray());
        Assert.Contains("FrameKindRepeat", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DpsTypes.idl")));
        Assert.Contains("float Fade", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DpsTopics.idl")));
    }

    // ---- T-052: entity cards in GizmoFrame (DD-024) and Entity.IsUnit ---------------------------------------------------------------

    [Fact]
    public void The_card_fields_and_IsUnit_are_appended_last_and_the_old_values_do_not_move()
    {
        Assert.Equal(["Space", "AnchorId", "Row", "CardX", "CardY", "CardW", "CardH", "FillRgba"], typeof(GizmoPrimitive).GetFields().Select(f => f.Name).TakeLast(8).ToArray());
        Assert.Equal(["Shape", "Points", "ColourRgba", "Width", "Text"], typeof(GizmoPrimitive).GetFields().Select(f => f.Name).Take(5).ToArray());
        Assert.Equal("IsUnit", typeof(Entity).GetFields().Select(f => f.Name).Last());
        Assert.Equal("ParentId", typeof(Entity).GetFields().Select(f => f.Name).SkipLast(1).Last());
        Assert.Equal((byte)0, (byte)GizmoSpace.GizmoSpaceWorld);   // a primitive that sets none of the new fields is a world primitive
        Assert.Equal((byte)1, (byte)GizmoSpace.GizmoSpaceEntityCard);
        Assert.Equal((byte)6, (byte)GizmoShape.GizmoShapeIcon);   // the shapes a card reuses did not move
        string topics = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DpsTopics.idl"));
        Assert.Contains("uint32 FillRgba", topics);
        Assert.Contains("boolean IsUnit", topics);
        Assert.Contains("GizmoSpaceEntityCard", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DpsTypes.idl")));
    }

    [Fact]
    public void A_card_primitive_and_a_world_primitive_round_trip_and_IsUnit_travels_on_Entity()
    {
        var frame = RoundTrip(new GizmoFrame
        {
            Producer = "p", Layer = "UnitCards", SimTime = 0,
            Primitives =
            [
                new GizmoPrimitive { Shape = GizmoShape.GizmoShapeBox, Points = [], ColourRgba = 0xAABBCCDD, Width = 1f, Text = string.Empty, Space = GizmoSpace.GizmoSpaceEntityCard, AnchorId = 1043, Row = 10, CardX = 0.35f, CardY = 2f, CardW = 0.35f, CardH = 2f, FillRgba = 0x00FF00FF },
                new GizmoPrimitive { Shape = GizmoShape.GizmoShapeText, Points = [], ColourRgba = 0xFFFFFFFF, Width = 13f, Text = new string('t', 31), Space = GizmoSpace.GizmoSpaceEntityCard, AnchorId = int.MaxValue, Row = 255, CardX = 3f },
                new GizmoPrimitive { Shape = GizmoShape.GizmoShapeIcon, Points = [], Text = "SFGPUCI-------|A", Space = GizmoSpace.GizmoSpaceEntityCard, AnchorId = 7, CardH = 22f },
                new GizmoPrimitive { Shape = GizmoShape.GizmoShapeText, Points = [new Vec3d { X = 1, Y = 2, Z = 3 }], ColourRgba = 1, Text = "world" },
            ],
        }, "t52-card");

        var box = frame.Primitives[0];
        Assert.Equal((GizmoSpace.GizmoSpaceEntityCard, 1043, (byte)10, 0.35f, 2f, 0.35f, 2f, 0x00FF00FFu), (box.Space, box.AnchorId, box.Row, box.CardX, box.CardY, box.CardW, box.CardH, box.FillRgba));
        Assert.Equal((int.MaxValue, (byte)255, new string('t', 31)), (frame.Primitives[1].AnchorId, frame.Primitives[1].Row, frame.Primitives[1].Text));
        Assert.Equal(("SFGPUCI-------|A", 22f), (frame.Primitives[2].Text, frame.Primitives[2].CardH));
        var world = frame.Primitives[3];
        Assert.Equal((GizmoSpace.GizmoSpaceWorld, 0, (byte)0, 0u), (world.Space, world.AnchorId, world.Row, world.FillRgba));
        Assert.Equal(new Vec3d { X = 1, Y = 2, Z = 3 }, Assert.Single(world.Points));

        var commander = RoundTrip(new Entity { EntityId = 5, Name = "Alpha squad", IsUnit = true }, "t52-unit");
        Assert.True(commander.IsUnit);
        Assert.False(RoundTrip(new Entity { EntityId = 6, Name = "Rifleman" }, "t52-man").IsUnit);
    }
}
