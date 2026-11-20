using System.Numerics;
using System.Text.Json.Nodes;
using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Animals;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Combat.Contracts;
using Fdp.Toolkit.Combat.Events;
using Fdp.Toolkit.Tkb.Domain;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>DPS round 2 (DD-018 3.2, 3.5a, 3.5b): the TKB type records, animals, and a two-wheeler's lean, on a staged HROT world.</summary>
public class TypeAnimalBankTests
{
    private const long ManType = 300, GunType = 0x6A13, BulletType = 0x6A14, CowType = 700, SparrowType = 708, BikeType = 115;

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
            RegisterTemplate(ManType, 3);
            RegisterTemplate(100, 1);
            var gun = new TkbTemplate("test gun carrier", GunType) { DisType = new DISEntityType { Kind = 1, Domain = 1, Country = 225, Category = 1 } };
            gun.AddDescriptor(new WeaponSuiteDto { Mounts = { new WeaponMountDto { Category = WeaponCategory.MachineGun, AmmoGuid = (ulong)BulletType, InitialAmmunition = 100 } } });
            gun.AddDescriptor(new TemperamentDto());   // an AI table: not a presentation descriptor
            TestTkb.Register(gun);
            TestTkb.Register(new TkbTemplate("test bullet", BulletType) { DisType = new DISEntityType { Kind = 2, Domain = 1, Country = 225, Category = 2 } });
            TestTkb.Register(new TkbTemplate("Cow", CowType) { DisType = new DISEntityType { Kind = 3, Domain = 1, Country = 225, Category = 200 } });
            TestTkb.Register(new TkbTemplate("Sparrows", SparrowType) { DisType = new DISEntityType { Kind = 3, Domain = 1, Country = 225, Category = 207 } });
            TestTkb.Register(new TkbTemplate("Motorcycle", BikeType) { DisType = new DISEntityType { Kind = 1, Domain = 1, Country = 225, Category = 80, Subcategory = 1 } });
            World.SetSingletonManaged<ITkbDatabase>(TestTkb);
            foreach (var register in new Action[] { () => World.RegisterComponent<AnimalState>(), () => World.RegisterComponent<FlockState>(), () => World.RegisterComponent<WeaponAmmo>() })
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
    }

    // ---- TKB type records -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_type_is_sent_once_with_its_presentation_descriptors_only()
    {
        using var h = new Stage();
        h.AddHrotEntity(2001, Vector3.Zero, tkb: GunType);
        h.AddHrotEntity(2002, new Vector3(5, 0, 0), tkb: GunType);   // the same type again

        var first = h.Gather(1.0);

        var record = Assert.Single(first.Of<HrotTypeRecord>(), r => r.TypeId == GunType);
        Assert.Equal(("test gun carrier", "1.1.225.1.0.0.0"), (record.Name, record.DisType));
        var json = JsonNode.Parse(record.Json)!.AsObject();
        Assert.Equal((long)GunType, (long)json["tkbType"]!);
        Assert.Equal("test gun carrier", (string)json["name"]!);
        var types = json["descriptors"]!.AsArray().Select(d => (string)d!["type"]!).ToArray();
        Assert.Contains(nameof(WeaponSuiteDto), types);
        Assert.DoesNotContain(nameof(TemperamentDto), types);   // an AI table stays out
        var suite = json["descriptors"]!.AsArray().Single(d => (string)d!["type"]! == nameof(WeaponSuiteDto))!;
        Assert.Equal(0, (int)suite["partId"]!);
        Assert.NotNull(suite["data"]);
        Assert.Equal(record.Json, TkbPresentation.JsonOf(h.TestTkb.GetByType(GunType)));   // deterministic

        // the second gather sends no type again, and the entity's own type is not the munition's
        Assert.Empty(h.Gather(1.1).Of<HrotTypeRecord>());
    }

    [Fact]
    public void The_munitions_of_fires_detonations_and_loaded_weapons_are_types_too_and_a_boundary_starts_over()
    {
        using var h = new Stage();
        var gun = h.AddHrotEntity(2001, Vector3.Zero, tkb: GunType);
        h.PublishForNextFrame(new WeaponFireNotification { Shooter = gun, Target = Entity.Null, WeaponIndex = 0, Quantity = 1, AmmoGuid = BulletType });
        Assert.Contains(h.GatherEvents().Of<HrotTypeRecord>(), r => r.TypeId == BulletType);   // a fire's munition

        h.World.Bus.SwapBuffers();
        h.PublishForNextFrame(new WeaponFireNotification { Shooter = gun, Target = Entity.Null, WeaponIndex = 0, Quantity = 1, AmmoGuid = BulletType });
        Assert.Empty(h.GatherEvents().Of<HrotTypeRecord>());   // once

        // a type the TKB does not know is asked once and not sent
        h.World.Bus.SwapBuffers();
        h.PublishForNextFrame(new DetonationNotification { Shooter = gun, Target = Entity.Null, Ammo = 0x999 });
        Assert.Empty(h.GatherEvents().Of<HrotTypeRecord>());

        // a unit with no loadout fires its mount's own round: that munition is a type of the unit (asked when the unit is first seen)
        var second = new TkbTemplate("second gun carrier", 0x6A15) { DisType = new DISEntityType { Kind = 1, Domain = 1, Country = 225, Category = 1 } };
        second.AddDescriptor(new WeaponSuiteDto { Mounts = { new WeaponMountDto { Category = WeaponCategory.MachineGun, AmmoGuid = 0x6A16, InitialAmmunition = 10 } } });
        h.TestTkb.Register(second);
        h.TestTkb.Register(new TkbTemplate("second bullet", 0x6A16));
        var carrier = h.AddHrotEntity(2003, new Vector3(9, 0, 0), tkb: 0x6A15);
        h.World.AddComponent(carrier, new WeaponAmmo { Count = 1, Load0 = 0 });
        Assert.Contains(h.Gather(2.0).Of<HrotTypeRecord>(), r => r.TypeId == 0x6A16);

        // a loaded round that is not the mount's own
        h.World.SetComponent(carrier, new WeaponAmmo { Count = 1, Load0 = BulletType });
        Assert.Empty(h.Gather(3.0).Of<HrotTypeRecord>());   // already sent

        h.Adapter.Reset(h.World);
        var after = h.Gather(4.0).Of<HrotTypeRecord>();
        Assert.Contains(after, r => r.TypeId == GunType);   // everything is new to the next runtime
        Assert.Contains(after, r => r.TypeId == 0x6A15);
        Assert.Contains(after, r => r.TypeId == BulletType);
    }

    [Fact]
    public void A_descriptor_that_fails_to_serialize_stays_in_the_list_with_its_error()
    {
        var template = new TkbTemplate("broken", 0x6A20);
        template.AddDescriptor(new MunitionVisualDto());
        template.AddDescriptor(new WarheadDto { Kind = WarheadKind.Incendiary, FlameRangeM = 60f });

        var descriptors = JsonNode.Parse(TkbPresentation.JsonOf(template, data => data is MunitionVisualDto ? throw new InvalidOperationException("no JSON for this one") : Fdp.Toolkit.Diagnostics.EventSerializationHelper.SerializeToJson(data)))!["descriptors"]!.AsArray();

        var broken = descriptors.Single(d => (string)d!["type"]! == nameof(MunitionVisualDto))!;
        Assert.Contains("serializationError", broken.AsObject().Select(p => p.Key));
        Assert.Equal("InvalidOperationException: no JSON for this one", (string)broken["serializationError"]!);
        Assert.DoesNotContain("data", broken.AsObject().Select(p => p.Key));
        Assert.NotNull(descriptors.Single(d => (string)d!["type"]! == nameof(WarheadDto))!["data"]);   // the others are not lost
    }

    [Fact]
    public void The_allow_list_is_the_presentation_descriptors_of_the_design()
    {
        string[] names = [.. TkbPresentation.Descriptors.Select(t => t.Name).Order()];

        foreach (var expected in new[]
        {
            "MunitionVisualDto", "WarheadDto", "WeaponSuiteDto", "WeaponSystemDto", "WeaponCapabilitiesDto", "TurretSuiteDto", "VehicleOpeningsDto", "VehicleLightsDto",
            "VehicleParametersDto", "CarrierDefDto", "BodyGeometryDto", "PersonDto", "OutfitDto", "VisualDefinitionDto", "EffectVisualDto", "SoundDto", "SoundSetDto", "LocomotionDto",
            "StrideRenderModelDefDto", "CharacterAnimationDefDto", "FlightParamsDto",
        })
        {
            Assert.Contains(expected, names);
        }

        foreach (var excluded in new[] { "TemperamentDto", "BehaviorProfileDto", "InjuryDefDto", "BodyZonesDto", "SensorCapabilitiesDto", "UnitCompositionDto", "TkbMasterDto" })
        {
            Assert.DoesNotContain(excluded, names);   // AI, damage, sensors and the scenario's own tables
        }
    }

    // ---- animals --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_animal_is_published_with_its_state_and_a_flock_with_its_birds_and_no_person_detail()
    {
        using var h = new Stage();
        var cow = h.AddHrotEntity(3001, new Vector3(1, 1, 0), tkb: CowType);
        h.World.AddComponent(cow, new AnimalState { Mode = AnimalMode.Alert, FearBand = 1, Flags = AnimalFlags.Calling });
        var birds = h.AddHrotEntity(3002, new Vector3(20, 1, 30), tkb: SparrowType);
        h.World.AddComponent(birds, new AnimalState { Mode = AnimalMode.Flee, FearBand = 3, Flags = AnimalFlags.Bolting | AnimalFlags.Dangerous });
        h.World.AddComponent(birds, new FlockState { Count = 12, InitialCount = 20, Seed = 777, Mode = FlockMode.Flying });
        h.AddHrotEntity(1201, new Vector3(5, 5, 0), tkb: ManType);

        var first = h.Gather(1.0);

        var a = Assert.Single(first.Of<HrotAnimal>(), x => x.Id == 3001);
        Assert.Equal(("Alert", (byte)1, false, false, true, 0, 0, string.Empty), (a.Mode, a.FearBand, a.Bolting, a.Dangerous, a.Calling, a.FlockCount, a.FlockInitialCount, a.FlockMode));
        var f = Assert.Single(first.Of<HrotAnimal>(), x => x.Id == 3002);
        Assert.Equal(("Flee", (byte)3, true, true, false, 12, 20, 777u, "Flying"), (f.Mode, f.FearBand, f.Bolting, f.Dangerous, f.Calling, f.FlockCount, f.FlockInitialCount, f.FlockSeed, f.FlockMode));
        Assert.True(f.Airborne);   // HROT's flock mode Flying is its flying state
        Assert.False(a.Airborne);   // a single body keeps no such state: false, never a height guess
        Assert.Equal(2, first.Of<HrotAnimal>().Count);   // the man is no animal
        Assert.Equal([1201], first.Of<HrotPersonDetail>().Select(d => d.Id).ToArray());   // and the animals are no persons
        Assert.Equal([1201], first.Of<HrotPersonAppearance>().Select(d => d.Id).ToArray());

        Assert.Empty(h.Gather(2.0).Of<HrotAnimal>());   // unchanged

        h.World.SetComponent(birds, new FlockState { Count = 11, InitialCount = 20, Seed = 777, Mode = FlockMode.Landed });   // a hit flock drops a bird and lands
        var changed = Assert.Single(h.Gather(3.0).Of<HrotAnimal>());
        Assert.Equal((3002, 11, "Landed"), (changed.Id, changed.FlockCount, changed.FlockMode));
        Assert.False(changed.Airborne);
        h.World.SetComponent(cow, new AnimalState { Mode = AnimalMode.Calm, FearBand = 1, Flags = AnimalFlags.Calling });   // the mode alone
        Assert.Equal("Calm", Assert.Single(h.Gather(4.0).Of<HrotAnimal>()).Mode);

        // the animal's entity is a DIS life form in the animal categories
        Assert.Equal(200, Fdp.Toolkit.Animals.Animals.IsAnimalDis(new DISEntityType { Kind = 3, Category = 200 }) ? 200 : 0);
        h.Adapter.Reset(h.World);
        Assert.Equal(2, h.Gather(5.0).Of<HrotAnimal>().Count);   // a boundary: new to the gateway
    }

    [Fact]
    public void An_animal_that_goes_is_removed_with_its_entity()
    {
        using var h = new Stage();
        var cow = h.AddHrotEntity(3001, new Vector3(1, 1, 0), tkb: CowType);
        h.World.AddComponent(cow, new AnimalState { Mode = AnimalMode.Calm });
        Assert.Single(h.Gather(1.0).Of<HrotAnimal>());

        h.RemoveHrotEntity(cow);

        Assert.Contains(h.Gather(2.0).All, m => m is HrotEntityRemoved { Id: 3001 });
        h.AddHrotEntity(3001, new Vector3(1, 1, 0), tkb: ManType);   // the id comes back as a man: no animal
        Assert.Empty(h.Gather(3.0).Of<HrotAnimal>());
    }

    // ---- the aim point --------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_aimed_person_sends_the_chest_of_the_target_or_the_aimed_point_as_hrots_own_reading()
    {
        using var h = new Stage();
        var target = h.AddHrotEntity(2002, new Vector3(40, 10, 0), tkb: 100);
        var man = h.AddHrotEntity(1201, Vector3.Zero, tkb: ManType);
        h.World.AddComponent(man, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Target = target, HeldItem = HeldItemKind.Weapon });

        var state = Assert.Single(h.Gather(1.0).Of<HrotEntityState>(), s => s.Id == 1201);

        Assert.Equal(HrotWeaponPosture.Aimed, state.Weapon);
        Assert.Equal(new HrotVec3(40, 10, 1), state.AimPoint);   // 1 m above the target's position, as WeaponPostureOf.TryAimPoint reads it (round 1 sent the feet)

        h.World.SetComponent(man, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Point = new WorldPoint(30, 5, 2), HeldItem = HeldItemKind.Weapon });
        Assert.Equal(new HrotVec3(30, 5, 2), Assert.Single(h.Gather(2.0).Of<HrotEntityState>(), s => s.Id == 1201).AimPoint);
    }

    // ---- bank -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_lean_rule_is_hrots_atan_of_speed_times_yaw_rate_over_g_clamped_and_eased()
    {
        // HROT's MotionTracker: target = clamp(-atan(v * rate / 9.81), +-0.61), eased with 0.15 s; a left turn (counter-clockwise, rate > 0) is negative
        Assert.Equal(-MathF.Atan(10f * 0.5f / 9.81f), BankRule.Target(10f, 0.5f), 6);
        Assert.Equal(-0.4710f, BankRule.Target(10f, 0.5f), 3);
        Assert.Equal(0.4710f, BankRule.Target(10f, -0.5f), 3);   // a right turn leans the top to the right: positive
        Assert.Equal(0f, BankRule.Target(0f, 1f));
        Assert.Equal(0f, BankRule.Target(10f, 0f));
        Assert.Equal(-0.61f, BankRule.Target(30f, 1f));   // the clamp
        Assert.Equal(0.61f, BankRule.Target(30f, -1f));
        Assert.Equal(0.4710f, BankRule.Target(-10f, 0.5f), 3);   // reversing flips the lean

        // one time constant covers 1 - 1/e of the way; no time keeps the value; a long time arrives
        Assert.Equal(-0.4710f * (1f - MathF.Exp(-1f)), BankRule.Ease(0f, -0.4710f, 0.15), 5);
        Assert.Equal(0.25f, BankRule.Ease(0.25f, -0.4f, 0.0));
        Assert.Equal(-0.4f, BankRule.Ease(0.25f, -0.4f, 100.0), 4);
        Assert.Equal(0.15f, BankRule.BankSeconds);
        Assert.Equal(0.61f, BankRule.MaxBank);
    }

    [Fact]
    public void A_motorcycle_leans_by_the_simulations_speed_and_yaw_rate_eased_on_sim_time_and_a_car_never_does()
    {
        using var h = new Stage();
        var bike = h.AddHrotEntity(4001, Vector3.Zero, tkb: BikeType);
        var car = h.AddHrotEntity(4002, new Vector3(0, 20, 0), tkb: 100);
        foreach (var e in new[] { bike, car })
        {
            h.World.SetComponent(e, new SimVelocity { Linear = new Vector3(10, 0, 0), Angular = new Vector3(0, 0, 0.5f) });   // 10 m/s forward, turning left at 0.5 rad/s
        }

        // the first sight starts upright
        var first = h.Gather(100.0).Of<HrotEntityState>();
        Assert.Equal(0.0, first.Single(s => s.Id == 4001).Bank);

        // one time constant of simulation time later: 1 - 1/e of the way to atan(10 * 0.5 / g), a left turn negative
        var second = h.Gather(100.15).Of<HrotEntityState>();
        double expected = -MathF.Atan(10f * 0.5f / 9.81f) * (1f - MathF.Exp(-1f));
        var leaning = second.Single(s => s.Id == 4001);
        Assert.Equal(expected, leaning.Bank, 5);
        Assert.True(leaning.Bank < 0);
        Assert.DoesNotContain(second, s => s.Id == 4002);   // the car sends nothing new (nothing changed)

        // wall-clock time passing with the simulation paused does not move the lean (the same sim time)
        Assert.Empty(h.Gather(100.15).Of<HrotEntityState>());

        // many time constants later it has arrived
        var arrived = h.Gather(105.0).Of<HrotEntityState>().Single(s => s.Id == 4001);
        Assert.Equal(-MathF.Atan(10f * 0.5f / 9.81f), arrived.Bank, 3);
        Assert.Empty(h.Gather(106.0).Of<HrotEntityState>());   // settled: nothing moved

        // a right turn leans the top to the right: positive
        h.World.SetComponent(bike, new SimVelocity { Linear = new Vector3(10, 0, 0), Angular = new Vector3(0, 0, -0.5f) });
        Assert.True(Assert.Single(h.Gather(106.6).Of<HrotEntityState>()).Bank > 0.3);

        // the car (same speed, same yaw rate) was never sent leaning
        Assert.All(first.Concat(second).Concat([arrived]).OfType<HrotEntityState>().Where(s => s.Id == 4002), s => Assert.Equal(0.0, s.Bank));
    }

    [Fact]
    public void A_change_of_the_lean_of_a_degree_or_more_sends_the_state_and_a_smaller_one_does_not()
    {
        using var h = new Stage();
        var bike = h.AddHrotEntity(4001, Vector3.Zero, tkb: BikeType);
        h.World.SetComponent(bike, new SimVelocity { Linear = new Vector3(10, 0, 0), Angular = new Vector3(0, 0, 0f) });
        Assert.Single(h.Gather(100.0).Of<HrotEntityState>());   // first sight

        // a very gentle turn: the lean moves by well under a degree in a step
        h.World.SetComponent(bike, new SimVelocity { Linear = new Vector3(10, 0, 0), Angular = new Vector3(0, 0, 0.002f) });
        Assert.Empty(h.Gather(100.05).Of<HrotEntityState>());

        // a hard one: more than a degree within the step
        h.World.SetComponent(bike, new SimVelocity { Linear = new Vector3(10, 0, 0), Angular = new Vector3(0, 0, 0.8f) });
        var sent = Assert.Single(h.Gather(100.3).Of<HrotEntityState>());
        Assert.True(Math.Abs(sent.Bank) * 180.0 / Math.PI > 1.0);
    }

    [Fact]
    public void The_motorcycle_class_is_found_from_the_dis_type_with_hrots_own_look_classification()
    {
        var bike = new DISEntityType { Kind = 1, Domain = 1, Country = 225, Category = 80, Subcategory = 1 };
        Assert.Equal(Hrot.UI.Common.Map3D.VisualFamily.Motorcycle, Hrot.UI.Common.Map3D.VisualFamilies.Of(bike));
        Assert.NotEqual(Hrot.UI.Common.Map3D.VisualFamily.Motorcycle, Hrot.UI.Common.Map3D.VisualFamilies.Of(new DISEntityType { Kind = 1, Domain = 1, Country = 225, Category = 1 }));
    }
}
