using System.Numerics;
using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Combat.Contracts;
using Fdp.Toolkit.Combat.Events;
using Fdp.Toolkit.Perception.Signatures;
using Fdp.Toolkit.Physics.BodyParts;
using Fdp.Toolkit.Physics.Components;
using Fdp.Toolkit.Replication.Components;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>HROT's look library (Hrot.Look) in the pivot: effects, a person's outfit and a vehicle's look, called the way the gateway does, on a staged HROT world with HROT's own TKB.</summary>
public class LookGatherTests
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
            foreach (var template in Hrot.Map.Common.HrotEnvironment.CreateTkb().GetAll())
            {
                TestTkb.Register(template);
            }

            World.SetSingletonManaged<ITkbDatabase>(TestTkb);
            foreach (var register in new Action[]
            {
                () => World.RegisterComponent<PartDamage>(), () => World.RegisterComponent<FireState>(), () => World.RegisterComponent<VehicleOpenings>(),
                () => World.RegisterComponent<VehicleLights>(), () => World.RegisterComponent<NetworkVelocity>(),
            })
            {
                register();
            }

            Steps(3);
        }

        /// <summary>The entity gather, run directly (the harness's own gather is not stepped in between).</summary>
        public Capture Gather(double simTime)
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            _now += 0.1;
            Adapter.GatherEntities(World, inbound, _now, simTime);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }

        /// <summary>The event gather for events published in the previous frame.</summary>
        public Capture GatherEvents()
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            Adapter.GatherEvents(World, inbound);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }
    }

    private const long TankType = Hrot.Map.Common.TkbEntityTypes.Tank_T72;
    private const long RiflemanType = Hrot.Map.Common.TkbEntityTypes.Infantry_Rifleman;

    [Fact]
    public void Detonation_gives_effect_spec_with_category_and_munition_dis()
    {
        using var h = new Stage();
        var munition = h.TestTkb.GetAll().First(t => t.DisType.Kind == 2);
        h.GatherEffects(10.0);   // the baseline
        var burst = new DetonationNotification { Target = Entity.Null, Ammo = (long)munition.TkbType, HitX = 50, HitY = 20, HitZ = 0.4f, Damage = 1100f };
        h.SpawnEffects(10.1, specs => Hrot.Look.Effects.EffectRules.ForDetonation(in burst, h.World, h.TestTkb, specs));   // what HROT's spawner makes of the event

        var effects = h.GatherEffects(10.2);

        Assert.NotEmpty(effects);
        var boom = effects[0];
        Assert.Equal(nameof(Hrot.Look.Effects.EffectSurface.Ground), boom.Surface);   // HROT's enumerations go by name
        Assert.Equal(nameof(Hrot.Core.Tkb.EffectTkbCatalog.CalibreClass.Large), boom.SizeClass);
        Assert.Equal((ulong)munition.DisType.Value, boom.MunitionDis);
        Assert.Equal(new HrotVec3(50, 20, 0.4f), boom.At);
        Assert.Equal(0, boom.AnchorEntity);
        Assert.True(boom.Lifetime > 0f);
        Assert.Equal(10.1, boom.StartedAt);   // EffectSpec.StartedAt: the effect entity's recorded birth
        Assert.All(effects, e => Assert.NotEqual(0L, e.EffectType));
    }

    [Fact]
    public void Shot_gives_muzzle_flash_anchored_to_shooter()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: TankType);
        h.GatherEffects(10.0);
        var fire = new WeaponFireNotification { Shooter = tank, Target = Entity.Null, WeaponIndex = 0 };
        h.SpawnEffects(10.1, specs => Hrot.Look.Effects.EffectRules.ForShot(in fire, h.World, h.TestTkb, specs));

        var effects = h.GatherEffects(10.2);

        var flash = Assert.Single(effects, e => e.Kind == nameof(Fdp.Toolkit.Tkb.Domain.EffectKind.MuzzleFlash));
        Assert.Equal(2001, flash.AnchorEntity);
        Assert.Equal((byte)0, flash.AnchorMount);
        Assert.Equal(nameof(Hrot.Look.Effects.EffectSurface.Air), flash.Surface);
        Assert.True(flash.Lifetime > 0f);
    }

    [Fact]
    public void An_effect_entity_is_sent_once_whatever_the_later_frames_and_a_frame_without_effects_sends_none()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: TankType);
        Assert.Empty(h.GatherEffects(10.0));
        var fire = new WeaponFireNotification { Shooter = tank, Target = Entity.Null, WeaponIndex = 0 };
        h.SpawnEffects(10.1, specs => Hrot.Look.Effects.EffectRules.ForShot(in fire, h.World, h.TestTkb, specs));

        Assert.NotEmpty(h.GatherEffects(10.1));
        for (double t = 10.2; t < 11.0; t += 0.1)
        {
            Assert.Empty(h.GatherEffects(t));   // the effect lives on: never sent again (no heartbeat)
        }

        h.SpawnEffects(11.0, specs => Hrot.Look.Effects.EffectRules.ForShot(in fire, h.World, h.TestTkb, specs));   // the next shot is a new effect
        Assert.Single(h.GatherEffects(11.0), e => e.Kind == nameof(Fdp.Toolkit.Tkb.Domain.EffectKind.MuzzleFlash));
    }

    [Fact]
    public void A_fuel_shot_sends_no_fire_while_an_ordinary_shot_does()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: TankType);
        var flamer = h.AddHrotEntity(2002, new Vector3(5, 0, 3), tkb: Hrot.Core.Tkb.UrbanCombatTkbCatalog.TkbFlamethrowerSoldier);
        h.PublishForNextFrame(new WeaponFireNotification { Shooter = flamer, Target = Entity.Null, WeaponIndex = 0, Quantity = 1 });
        Assert.Empty(h.GatherEvents().Of<HrotFire>());

        h.PublishForNextFrame(new WeaponFireNotification { Shooter = tank, Target = Entity.Null, WeaponIndex = 0, Quantity = 1 });
        var fire = Assert.Single(h.GatherEvents().Of<HrotFire>());
        Assert.Equal(2001, fire.ShooterId);
    }

    [Fact]
    public void A_planned_burst_stamp_is_copied_to_the_fire_and_the_index_is_shifted_to_zero_based()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: TankType);
        for (byte hrotIndex = 1; hrotIndex <= 3; hrotIndex++)   // HROT counts 1..3, one WeaponFireNotification per round
        {
            h.PublishForNextFrame(new WeaponFireNotification { Shooter = tank, Target = Entity.Null, WeaponIndex = 0, Quantity = 1, BurstId = 7, BurstRounds = 3, BurstIndex = hrotIndex });
            var fire = Assert.Single(h.GatherEvents().Of<HrotFire>());
            Assert.Equal((7u, (byte)3, (byte)(hrotIndex - 1)), (fire.BurstId, fire.BurstRounds, fire.BurstIndex));   // one id, indices 0, 1, 2
        }
    }

    [Fact]
    public void A_round_outside_a_planned_burst_passes_as_zero_zero_zero()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: TankType);
        h.PublishForNextFrame(new WeaponFireNotification { Shooter = tank, Target = Entity.Null, WeaponIndex = 0, Quantity = 1 });
        var fire = Assert.Single(h.GatherEvents().Of<HrotFire>());
        Assert.Equal((0u, (byte)0, (byte)0), (fire.BurstId, fire.BurstRounds, fire.BurstIndex));
    }

    [Fact]
    public void The_burst_index_shift_holds_for_every_value_and_never_wraps()
    {
        Assert.Equal((byte)0, EcsHrotAdapter.BurstIndexOf(0, 0));   // not a burst
        Assert.Equal((byte)0, EcsHrotAdapter.BurstIndexOf(0, 5));   // a stray index without an id is dropped
        Assert.Equal((byte)0, EcsHrotAdapter.BurstIndexOf(9, 0));   // an unstamped index never wraps to 255
        for (int i = 1; i <= 255; i++)
        {
            Assert.Equal((byte)(i - 1), EcsHrotAdapter.BurstIndexOf(9, (byte)i));
        }
    }

    [Fact]
    public void Person_appearance_is_published_once_and_on_change()
    {
        using var h = new Stage();
        var e = h.AddHrotEntity(1201, new Vector3(1, 1, 0), tkb: RiflemanType, force: ForceId.Hostile);

        var first = Assert.Single(h.Gather(1.0).Of<HrotPersonAppearance>());
        Assert.Equal(1201, first.Id);
        Assert.Equal(1201L, first.Seed);
        Assert.Equal((byte)2, first.Side);
        Assert.True(first.Soldier);
        Assert.Equal(0xFFu, first.Skin & 0xFFu);   // an opaque colour

        // the kind names are the names of HROT's own outfit enumerations for this person, never numbers
        var outfit = Hrot.Look.People.PersonLook.Of(h.World, e, h.TestTkb)!.Value.Outfit;
        Assert.Equal((outfit.HairStyle.ToString(), outfit.Legwear.ToString(), outfit.Camo.ToString(), outfit.TopKind.ToString(), outfit.HeadKind.ToString(),
            outfit.OverKind.ToString(), outfit.ExtraKind.ToString(), outfit.Face.Beard.ToString()),
            (first.HairStyle, first.Legwear, first.Camo, first.TopKind, first.HeadKind, first.OverKind, first.ExtraKind, first.Beard));
        Assert.Equal(outfit.Camo.ToString(), first.Camo);
        Assert.True(Enum.IsDefined(typeof(Hrot.UI.Common.Map3D.CamoPattern), first.Camo));
        Assert.True(Enum.IsDefined(typeof(Hrot.UI.Common.Map3D.HairStyle), first.HairStyle));
        Assert.True(Enum.IsDefined(typeof(Hrot.UI.Common.Map3D.Legwear), first.Legwear));
        Assert.True(Enum.IsDefined(typeof(Hrot.Core.Tkb.OutfitTop), first.TopKind));
        Assert.True(Enum.IsDefined(typeof(Hrot.Core.Tkb.OutfitHead), first.HeadKind));
        Assert.True(Enum.IsDefined(typeof(Hrot.Core.Tkb.OutfitOver), first.OverKind));
        Assert.True(Enum.IsDefined(typeof(Hrot.Core.Tkb.OutfitExtra), first.ExtraKind));
        Assert.True(Enum.IsDefined(typeof(Hrot.UI.Common.Map3D.BeardKind), first.Beard));
        Assert.All(new[] { first.HairStyle, first.Legwear, first.Camo, first.TopKind, first.HeadKind, first.OverKind, first.ExtraKind, first.Beard },
            name => Assert.False(int.TryParse(name, out _)));

        Assert.Empty(h.Gather(2.0).Of<HrotPersonAppearance>());
        Assert.Empty(h.Gather(3.0).Of<HrotPersonAppearance>());

        h.World.SetComponent(e, new EntityInfo { Name = new FixedString64("HROT-1"), ForceId = ForceId.Friend });
        var changed = Assert.Single(h.Gather(4.0).Of<HrotPersonAppearance>());
        Assert.Equal((byte)1, changed.Side);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void A_vehicle_has_no_person_appearance_and_a_person_no_vehicle_look()
    {
        using var h = new Stage();
        h.AddHrotEntity(2001, new Vector3(0, 0, 0), tkb: TankType);
        h.AddHrotEntity(1201, new Vector3(5, 0, 0), tkb: RiflemanType);

        var sent = h.Gather(1.0);

        Assert.Equal([1201], sent.Of<HrotPersonAppearance>().Select(a => a.Id).ToArray());
        Assert.Equal([2001], sent.Of<HrotVehicleLook>().Select(v => v.Id).ToArray());
    }

    [Fact]
    public void Vehicle_look_parts_and_openings_are_published()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 0), tkb: TankType);
        var damage = new PartDamage { Plan = BodyPlan.TrackedVehicle };
        damage.Set(VehiclePart.RunningGearL, 255);
        h.World.AddComponent(tank, damage);
        var openings = new VehicleOpenings { Count = 3 };
        openings.Slots[1] = new OpeningSlot { State = OpeningState.Opening, Since = 10.0 };
        h.World.AddComponent(tank, openings);

        var first = Assert.Single(h.Gather(10.75).Of<HrotVehicleLook>());
        Assert.Contains(((byte)VehiclePart.RunningGearL, (byte)2), first.Parts);
        Assert.Equal(nameof(DestructionKind.None), first.Destruction);
        Assert.False(first.Burning);
        Assert.Contains(((byte)2, 0.5f), first.Openings);

        // the same look again: nothing
        Assert.Empty(h.Gather(10.75).Of<HrotVehicleLook>());

        // the motion is data (target, start, seconds): the hatch moving on sends nothing (DD-020 3.2)
        var motion = Assert.Single(first.OpeningMotions!, m => m.Opening == 2);
        Assert.Equal((1f, 10.0), (motion.Target, motion.ChangedAt));
        Assert.True(motion.Seconds > 0f);
        Assert.Empty(h.Gather(20.0).Of<HrotVehicleLook>());

        // a new decision (closing from 25 s) is one message
        var closing = new VehicleOpenings { Count = 3 };
        closing.Slots[1] = new OpeningSlot { State = OpeningState.Closing, Since = 25.0 };
        h.World.SetComponent(tank, closing);
        var closed = Assert.Single(h.Gather(25.2).Of<HrotVehicleLook>());
        Assert.Equal((0f, 25.0), (closed.OpeningMotions!.Single(m => m.Opening == 2).Target, closed.OpeningMotions!.Single(m => m.Opening == 2).ChangedAt));
    }


    [Fact]
    public void The_rotor_spool_is_sent_as_target_start_and_seconds_and_not_as_it_moves()
    {
        using var h = new Stage();
        if (!h.World.IsComponentTypeRegistered<HeatSource>())
        {
            h.World.RegisterComponent<HeatSource>();
        }

        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 0), tkb: TankType);
        h.World.AddComponent(tank, new HeatSource { State = HeatSourceState.Idle, Since = new SimInstant(100.0), MotionSince = new SimInstant(0.0) });
        var idle = Assert.Single(h.Gather(100.0).Of<HrotVehicleLook>());
        var plant = Fdp.Toolkit.Perception.Signatures.ThermalDefaults.Of(h.TestTkb.GetAll().First(t => t.TkbType == TankType));
        Assert.True(plant is { SpinUpSeconds: > 0f });
        Assert.Equal(0.6f, idle.RotorTarget);   // idle: a started engine spools from 0 to the idle speed
        Assert.Equal((0f, 100.0, plant!.SpinUpSeconds), (idle.RotorFrom, idle.RotorChangedAt, idle.RotorSpoolSeconds));

        // it runs on: the speed moves with time, the message does not
        Assert.Empty(h.Gather(100.5).Of<HrotVehicleLook>());
        Assert.Empty(h.Gather(101.0).Of<HrotVehicleLook>());
        Assert.Empty(h.Gather(110.0).Of<HrotVehicleLook>());

        // it drives off: one message, from the idle speed up to full
        h.World.SetComponent(tank, new HeatSource { State = HeatSourceState.Running, Since = new SimInstant(120.0), MotionSince = new SimInstant(120.0) });
        var running = Assert.Single(h.Gather(120.5).Of<HrotVehicleLook>());
        Assert.Equal((1f, 0.6f, 120.0, plant.SpinUpSeconds), (running.RotorTarget, running.RotorFrom, running.RotorChangedAt, running.RotorSpoolSeconds));
        Assert.Empty(h.Gather(121.0).Of<HrotVehicleLook>());
    }
    [Fact]
    public void A_wreck_burns_and_is_sent_as_changed()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 0), tkb: TankType, health: 0f);
        h.World.SetComponent(tank, new Health { Current = 0f, Max = 100f, Destruction = DestructionKind.Catastrophic, StateSince = 100.0 });
        var fire = default(FireState);
        fire.TryAdd(new FireSpot(0f, -1f, 1f, 2f, new SimInstant(100.0), 60f, 1f), 100.0);
        h.World.AddComponent(tank, fire);

        var look = Assert.Single(h.Gather(130.0).Of<HrotVehicleLook>());

        Assert.Equal(nameof(DestructionKind.Catastrophic), look.Destruction);
        Assert.True(look.TurretThrown);
        Assert.True(look.Burning);
    }

    [Fact]
    public void Brake_lights_follow_deceleration()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 0), tkb: TankType);
        h.World.AddComponent(tank, new VehicleLights { OnMask = 0b11, Discipline = LightDiscipline.Normal });
        void Speed(float mps) => h.World.SetComponent(tank, new SimVelocity { Linear = new Vector3(mps, 0, 0) });

        Speed(8f);
        Assert.Equal(0b11u, Assert.Single(h.Gather(0.0).Of<HrotVehicleLook>()).Lights);   // first sight: no history
        Assert.Empty(h.Gather(0.5).Of<HrotVehicleLook>());                                  // steady: nothing changed

        Speed(4f);
        Assert.Equal(0b111u, Assert.Single(h.Gather(1.0).Of<HrotVehicleLook>()).Lights);  // 8 m/s^2: the stop lights come on

        Speed(4f);
        Assert.Equal(0b11u, Assert.Single(h.Gather(1.7).Of<HrotVehicleLook>()).Lights);   // steady again, the hold is over
    }

    [Fact]
    public void Removed_entity_forgets_its_look()
    {
        using var h = new Stage();
        var man = h.AddHrotEntity(1201, new Vector3(1, 1, 0), tkb: RiflemanType);
        var tank = h.AddHrotEntity(2001, new Vector3(9, 9, 0), tkb: TankType);
        Assert.Single(h.Gather(1.0).Of<HrotPersonAppearance>());
        Assert.Empty(h.Gather(2.0).Of<HrotPersonAppearance>());

        h.RemoveHrotEntity(man);
        h.RemoveHrotEntity(tank);
        Assert.Empty(h.Gather(3.0).Of<HrotPersonAppearance>());   // the removal pass forgets them

        h.AddHrotEntity(1201, new Vector3(1, 1, 0), tkb: RiflemanType);
        h.AddHrotEntity(2001, new Vector3(9, 9, 0), tkb: TankType);
        var again = h.Gather(4.0);
        Assert.Single(again.Of<HrotPersonAppearance>());
        Assert.Single(again.Of<HrotVehicleLook>());
    }

    /// <summary>
    /// The look enumerations go to the plugins by name, and the dps plugin maps the names it knows (its <c>LookNames</c>); a name it does not know is sent as the
    /// contract's unknown value. This list is HROT's today: when HROT appends or renames a value this test fails, as the reminder to map the new name in the plugin.
    /// </summary>
    [Fact]
    public void The_look_enumerations_the_plugins_map_by_name_are_unchanged()
    {
        Assert.Equal(["MuzzleFlash", "Explosion", "Decal", "Tracer", "Fire", "Smoke", "Flare", "Chaff", "FlameJet"], Enum.GetNames<Fdp.Toolkit.Tkb.Domain.EffectKind>());
        Assert.Equal(["Small", "Medium", "Large", "Huge"], Enum.GetNames<Hrot.Core.Tkb.EffectTkbCatalog.CalibreClass>());
        Assert.Equal(["Ground", "Water", "Building", "Vehicle", "Person", "Air"], Enum.GetNames<Hrot.Look.Effects.EffectSurface>());
        Assert.Equal(["None", "KnockedOut", "BurntOut", "Catastrophic"], Enum.GetNames<DestructionKind>());
    }

    /// <summary>
    /// DPS round 2 (DD-018): the enumerations of a person's mood, a weapon mount, a round's form and a ground heat mark also cross by name and are mapped by the plugin's
    /// <c>LookNames</c>. A value HROT appends or renames fails this test until the plugin maps it (and the contract gains the value).
    /// </summary>
    [Fact]
    public void The_round_two_enumerations_the_plugins_map_by_name_are_unchanged()
    {
        Assert.Equal(["None", "Routine", "Alarmed", "Panic", "Shelter", "Inside", "Cower", "Casualty", "Protest", "Riot", "Dispersing", "ArmedHostile", "Gather", "Surrender", "Watch"],
            Enum.GetNames<Fdp.Toolkit.Civilians.CivilianMode>());
        Assert.Equal(
            ["Unknown", "SmallArms", "MachineGun", "Autocannon", "TankGun", "Atgm", "Mortar", "Grenade", "Bomb", "Rocket", "Howitzer", "Sam", "Countermeasure", "AirToAir", "Bow",
                "SmokeDischarger", "Flamethrower"],
            Enum.GetNames<Fdp.Toolkit.Tkb.Domain.WeaponCategory>());
        Assert.Equal(["NoMark", "VehicleStand", "BurnedArea", "BodyImprint"], Enum.GetNames<Fdp.Toolkit.Terrain.HeatMarkKind>());
        Assert.Equal(["NoHotEnd", "Front", "Rear"], Enum.GetNames<Fdp.Toolkit.Terrain.HeatMarkEnd>());
        Assert.Equal(["Grenade", "Bomb", "Shell", "Rocket", "Missile", "Bullet", "Axe", "Spear", "Star", "Arrow"], Enum.GetNames<Fdp.Toolkit.Tkb.Domain.MunitionShape>());
    }
}
