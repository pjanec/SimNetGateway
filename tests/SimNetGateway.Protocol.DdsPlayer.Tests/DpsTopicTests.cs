using System.Diagnostics;
using CycloneDDS.Runtime;
using CycloneDDS.Schema;
using SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Protocol.DdsPlayer.Tests;

public class DpsTopicTests
{
    private static uint NewDomain() => (uint)(100 + (Environment.ProcessId % 40));

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
        }, "t-person");
        Assert.Equal(Stance.StanceProne, person.TargetStance);
        Assert.Equal(Gesture.GestureWaving, person.Gesture);
        Assert.Equal(ActionKind.ActionKindOpenDoor, person.Action);
        Assert.Equal(8.5, person.ActionStartSimTime);
        Assert.Equal(SeatRole.SeatRoleGunner, person.SeatRole);
        Assert.Equal(13, person.CarryOtherId);

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

        var look = RoundTrip(new VehicleLook { EntityId = 11, RotorSpeed = 0.55f, Parts = [], Openings = [new OpeningLook { OpeningId = 9, Progress = 1f, CrewExposed = true }] }, "t2-vlook");
        Assert.Equal(0.55f, look.RotorSpeed);
        Assert.True(look.Openings[0].CrewExposed);

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
        Assert.Equal(22, DpsProtocol.AllTopics.Count);

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
}
