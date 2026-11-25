using System.Numerics;
using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Combat.Contracts;
using Fdp.Toolkit.Combat.Events;
using Fdp.Toolkit.Creatures;
using Fdp.Toolkit.Perception.Signatures;
using Fdp.Toolkit.Physics.BodyParts;
using Fdp.Toolkit.Physics.Components;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Terrain;
using Fdp.Toolkit.Tkb.Domain;
using Hrot.Core.Tkb;
using Hrot.Look.Vehicles;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;
using Civ = Fdp.Toolkit.Civilians;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// DPS round 2 in the host (DD-018 3.2 .. 3.5): a person's demeanour and burning, a vehicle's rotor and exposed crew, rounds with a look, the munition looks, the fire's weapon
/// category, flame jets, burning spots and ground heat marks, on a staged HROT world read through HROT's own readers.
/// </summary>
public class RoundFlameGatherTests
{
    private const long ManType = 300, FlameType = 0x6A11, FuelType = 0x6A12, GunType = 0x6A13, BulletType = 0x6A14;

    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public List<T> Of<T>() => All.OfType<T>().ToList();
    }

    private sealed class Stage : PackHarness
    {
        private double _now = 1000;

        public Stage(bool realTkb = false)
            : base(dis: true)
        {
            if (realTkb)
            {
                foreach (var template in Hrot.Map.Common.HrotEnvironment.CreateTkb().GetAll())
                {
                    TestTkb.Register(template);
                }
            }
            else
            {
                RegisterTemplate(ManType, 3);                                  // a life form
                RegisterTemplate(MunitionTkbCatalog.Tkb556Tracer, 2);           // munitions: DIS kind 2, the look is HROT's built-in one (no descriptor in this TKB)
                RegisterTemplate(MunitionTkbCatalog.TkbM67Grenade, 2);
                RegisterTemplate(MunitionTkbCatalog.Tkb25mmApdsT, 2);
                var flamer = new TkbTemplate("test flame unit", FlameType) { DisType = new DISEntityType { Kind = 1, Domain = 1, Country = 225, Category = 1 } };
                flamer.AddDescriptor(new WeaponSuiteDto { Mounts = { new WeaponMountDto { Category = WeaponCategory.Flamethrower, AmmoGuid = (ulong)FuelType, InitialAmmunition = 100 } } });
                TestTkb.Register(flamer);
                var fuel = new TkbTemplate("test flame fuel", FuelType);
                fuel.AddDescriptor(new WarheadDto { Kind = WarheadKind.Incendiary, FlameRangeM = 60f, FlameHalfAngleDeg = 10f, FlameIntensity = 0.8f, BurnSeconds = 30f });
                TestTkb.Register(fuel);
                var gun = new TkbTemplate("test gun carrier", GunType) { DisType = new DISEntityType { Kind = 1, Domain = 1, Country = 225, Category = 1 } };
                gun.AddDescriptor(new WeaponSuiteDto { Mounts = { new WeaponMountDto { Category = WeaponCategory.MachineGun, AmmoGuid = (ulong)BulletType, InitialAmmunition = 100 } } });
                TestTkb.Register(gun);
            }

            World.SetSingletonManaged<ITkbDatabase>(TestTkb);
            foreach (var register in new Action[]
            {
                () => World.RegisterComponent<PartDamage>(), () => World.RegisterComponent<FireState>(), () => World.RegisterComponent<VehicleOpenings>(),
                () => World.RegisterComponent<VehicleLights>(), () => World.RegisterComponent<NetworkVelocity>(),
                () => World.RegisterComponent<WeaponAmmo>(), () => World.RegisterComponent<GroundHeatMarks>(), () => World.RegisterComponent<LightweightRound>(),
                () => World.RegisterComponent<BallisticProjectile>(), () => World.RegisterComponent<HeatSource>(),
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

        /// <summary>A lightweight round of munition <paramref name="munition"/> with its combat record, as HROT's fire processing leaves it.</summary>
        public Entity AddRound(int id, long munition, Entity shooter, Vector3 muzzle, Vector3 velocity, byte warhead = 0, float fuze = 0f, bool combat = true)
        {
            var e = AddHrotEntity(id, muzzle, name: "round", tkb: munition);
            World.SetComponent(e, new SimVelocity { Linear = velocity });
            World.AddComponent(e, default(LightweightRound));
            if (combat)
            {
                World.AddComponent(e, new BallisticProjectile
                {
                    Shooter = shooter, Muzzle = new WorldPoint(muzzle.X, muzzle.Y, muzzle.Z), Ammo = munition, Warhead = warhead, FuzeRemaining = fuze,
                });
            }

            return e;
        }

        public Entity AddFlameUnit(int id, double lastShotAt, Vector3 aim, double burstStartedAt = 99.0)
        {
            var e = AddHrotEntity(id, new Vector3(0, 0, 0), tkb: FlameType);
            World.AddComponent(e, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Point = new WorldPoint(aim.X, aim.Y, aim.Z), ActiveMount = 0 });
            World.AddComponent(e, new WeaponAmmo { Count = 1, Load0 = FuelType, LastShotAt0 = lastShotAt, BurstStartedAt0 = burstStartedAt });
            return e;
        }
    }

    // ---- people ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_person_detail_carries_demeanour_and_burning_and_changes_alone_are_sent()
    {
        using var h = new Stage();
        var man = h.AddHrotEntity(1201, new Vector3(1, 1, 0), tkb: ManType);
        h.World.AddComponent(man, new Demeanour { Kind = GestureKind.None, Mode = Civ.CivilianMode.Panic, Fear = 3, Anger = 1 });
        h.World.AddComponent(man, new EffectState { BurningUntil = new SimInstant(20.0) });

        var first = Assert.Single(h.Gather(10.0).Of<HrotPersonDetail>());
        Assert.Equal((true, (byte)3, (byte)1, HrotCivilianMode.Panic), (first.Burning, first.Fear, first.Anger, first.CivilianMode));
        Assert.Empty(h.Gather(11.0).Of<HrotPersonDetail>());   // nothing changed: nothing sent

        // the fire goes out with time alone
        var out_ = Assert.Single(h.Gather(25.0).Of<HrotPersonDetail>());
        Assert.False(out_.Burning);
        Assert.Equal(HrotCivilianMode.Panic, out_.CivilianMode);

        // a mood change alone (the mode, a quarter of fear, a quarter of anger)
        h.World.SetComponent(man, new Demeanour { Kind = GestureKind.None, Mode = Civ.CivilianMode.Riot, Fear = 3, Anger = 1 });
        Assert.Equal(HrotCivilianMode.Riot, Assert.Single(h.Gather(26.0).Of<HrotPersonDetail>()).CivilianMode);
        h.World.SetComponent(man, new Demeanour { Kind = GestureKind.None, Mode = Civ.CivilianMode.Riot, Fear = 4, Anger = 1 });
        Assert.Equal((byte)4, Assert.Single(h.Gather(27.0).Of<HrotPersonDetail>()).Fear);
        h.World.SetComponent(man, new Demeanour { Kind = GestureKind.None, Mode = Civ.CivilianMode.Riot, Fear = 4, Anger = 2 });
        Assert.Equal((byte)2, Assert.Single(h.Gather(28.0).Of<HrotPersonDetail>()).Anger);

        // the civilian mode "None" and a person with no demeanour at all send the empty name
        h.World.SetComponent(man, new Demeanour { Kind = GestureKind.None, Mode = Civ.CivilianMode.None, Fear = 0, Anger = 0 });
        Assert.Equal(HrotCivilianMode.Unknown, Assert.Single(h.Gather(29.0).Of<HrotPersonDetail>()).CivilianMode);
        var plain = h.AddHrotEntity(1202, new Vector3(2, 2, 0), tkb: ManType);
        var detail = Assert.Single(h.Gather(30.0).Of<HrotPersonDetail>(), d => d.Id == 1202);
        Assert.Equal((false, (byte)0, (byte)0, HrotCivilianMode.Unknown), (detail.Burning, detail.Fear, detail.Anger, detail.CivilianMode));
        Assert.True(h.World.IsAlive(plain));
    }

    // ---- vehicles -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_rotor_spool_is_one_message_per_change_with_the_value_at_the_time_of_sending()
    {
        using var h = new Stage(realTkb: true);
        var tank = h.AddHrotEntity(2001, Vector3.Zero, tkb: Hrot.Map.Common.TkbEntityTypes.Tank_T72);
        h.World.AddComponent(tank, new HeatSource { State = HeatSourceState.Idle, Since = new SimInstant(0.0), MotionSince = new SimInstant(0.0) });

        // held from the start: at its target at once (idle speed)
        var first = Assert.Single(h.Gather(1.0).Of<HrotVehicleLook>());
        Assert.Equal(VehicleLook.IdleSpeed, first.RotorSpeed);
        Assert.Equal((VehicleLook.IdleSpeed, VehicleLook.IdleSpeed, 0.0, 0f), (first.RotorTarget, first.RotorFrom, first.RotorChangedAt, first.RotorSpoolSeconds));
        Assert.Empty(h.Gather(1.5).Of<HrotVehicleLook>());

        // running from 100 s: the rotor winds up from idle to full over the type's spin-up, announced once (DD-020 3.2)
        h.World.SetComponent(tank, new HeatSource { State = HeatSourceState.Running, Since = new SimInstant(100.0), MotionSince = new SimInstant(100.0) });
        var run = Assert.Single(h.Gather(100.0).Of<HrotVehicleLook>());
        Assert.Equal((1f, VehicleLook.IdleSpeed, 100.0), (run.RotorTarget, run.RotorFrom, run.RotorChangedAt));
        Assert.True(run.RotorSpoolSeconds > 0f);
        for (double t = 100.05; t < 400.0; t += 0.05)
        {
            Assert.Empty(h.Gather(t).Of<HrotVehicleLook>());   // the speed moves, nothing is sent
        }

        // the client's own reading of the spool equals HROT's at any time
        for (double t = 100.0; t < 110.0; t += 0.25)
        {
            double step = (t - run.RotorChangedAt) / run.RotorSpoolSeconds;   // a full scale per spool seconds, toward the target, not past it
            double expected = Math.Min(run.RotorTarget, run.RotorFrom + step);
            Assert.Equal(VehicleLook.RotorSpeed(h.World, tank, t), (float)expected, 3);
        }

        // the engine dies from full speed: one message, from full to 0
        h.World.SetComponent(tank, new HeatSource { State = HeatSourceState.Dead, Since = new SimInstant(600.0), MotionSince = new SimInstant(100.0) });
        var dead = Assert.Single(h.Gather(600.0).Of<HrotVehicleLook>());
        Assert.Equal((0f, 1f, 600.0), (dead.RotorTarget, dead.RotorFrom, dead.RotorChangedAt));
        Assert.True(dead.RotorSpoolSeconds > 0f);
    }

    [Fact]
    public void An_opening_with_the_crew_head_out_is_listed()
    {
        using var h = new Stage(realTkb: true);
        var tank = h.AddHrotEntity(2001, Vector3.Zero, tkb: Hrot.Map.Common.TkbEntityTypes.Tank_T72);
        var openings = new VehicleOpenings { Count = 3 };
        openings.Slots[1] = new OpeningSlot { State = OpeningState.Open, Since = 10.0, CrewExposed = true };
        h.World.AddComponent(tank, openings);

        var look = Assert.Single(h.Gather(20.0).Of<HrotVehicleLook>());
        Assert.Equal([(byte)2], look.CrewExposed);   // slot 1 is opening id 2 in the tank's catalogue (as the opening test of the look gather)
        Assert.Empty(h.Gather(20.0).Of<HrotVehicleLook>());

        var calm = openings;
        calm.Slots[1] = new OpeningSlot { State = OpeningState.Open, Since = 10.0, CrewExposed = false };
        h.World.SetComponent(tank, calm);
        var later = Assert.Single(h.Gather(21.0).Of<HrotVehicleLook>());
        Assert.True(later.CrewExposed is null or { Count: 0 });
    }

    // ---- rounds ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_round_with_a_look_is_an_entity_with_a_round_and_its_munition_look_once()
    {
        using var h = new Stage();
        var shooter = h.AddHrotEntity(2001, Vector3.Zero, tkb: GunType);
        h.AddRound(3001, MunitionTkbCatalog.Tkb25mmApdsT, shooter, new Vector3(5, 6, 2), new Vector3(800, 0, 0), WarheadRound.TimeFuze, fuze: 3.5f);

        var first = h.Gather(1.0);

        var state = Assert.Single(first.Of<HrotEntityState>(), s => s.Id == 3001);   // already published as an entity by the ordinary query (Domain Munition)
        Assert.Equal(MunitionTkbCatalog.Tkb25mmApdsT, state.TkbType);
        Assert.Equal(2, Hrot_DisKind(state.DisType));
        var look = Assert.Single(first.Of<HrotMunitionLook>());
        Assert.Equal((long)MunitionTkbCatalog.Tkb25mmApdsT, look.MunitionTkb);
        Assert.Equal(HrotMunitionShape.Shell, look.Shape);   // HROT's built-in look (this TKB carries no descriptor): by name
        Assert.True(look.Tracer);
        Assert.False(look.SmokeTrail);
        Assert.Equal(0.13f, look.Length);
        Assert.Equal(0x2F2F2FFFu, look.Colour);
        Assert.Equal(0xFF8A30FFu, look.TracerColour);
        var round = Assert.Single(first.Of<HrotRound>());
        Assert.Equal((3001, (long)MunitionTkbCatalog.Tkb25mmApdsT, 2001), (round.Id, round.MunitionTkb, round.ShooterId));
        Assert.Equal(new HrotVec3(5, 6, 2), round.Muzzle);
        Assert.Equal((false, false, 3.5f), (round.Lying, round.Stopped, round.FuzeLeftS));
        // the order the plugin relies on: the entity first, then the look, then the round
        Assert.True(first.All.IndexOf(state) < first.All.IndexOf(look) && first.All.IndexOf(look) < first.All.IndexOf(round));

        // unchanged: no round, no look
        var second = h.Gather(1.1);
        Assert.Empty(second.Of<HrotRound>());
        Assert.Empty(second.Of<HrotMunitionLook>());

        // a second round of the same munition: its round, not its look again
        h.AddRound(3002, MunitionTkbCatalog.Tkb25mmApdsT, shooter, new Vector3(5, 6, 2), new Vector3(800, 0, 0));
        var third = h.Gather(1.2);
        Assert.Equal(3002, Assert.Single(third.Of<HrotRound>()).Id);
        Assert.Equal(-1f, third.Of<HrotRound>()[0].FuzeLeftS);   // no time fuze
        Assert.Empty(third.Of<HrotMunitionLook>());
    }

    [Fact]
    public void A_round_is_sent_again_when_it_lands_or_stops_and_never_for_a_bullet_without_a_look()
    {
        using var h = new Stage();
        var shooter = h.AddHrotEntity(2001, Vector3.Zero, tkb: GunType);
        var grenade = h.AddRound(3001, MunitionTkbCatalog.TkbM67Grenade, shooter, new Vector3(1, 1, 1), new Vector3(10, 0, 5), WarheadRound.TimeFuze, fuze: 4f);
        h.AddHrotEntity(3002, new Vector3(9, 9, 1), tkb: MunitionTkbCatalog.TkbM67Grenade);   // no LightweightRound: not a round
        Assert.Equal(3001, Assert.Single(h.Gather(1.0).Of<HrotRound>()).Id);
        Assert.Empty(h.Gather(1.1).Of<HrotRound>());

        // it lands: lying
        ref var projectile = ref h.World.GetComponentRW<BallisticProjectile>(grenade);
        projectile.Warhead = (byte)(WarheadRound.TimeFuze | WarheadRound.Landed);
        var landed = Assert.Single(h.Gather(2.0).Of<HrotRound>());
        Assert.True(landed.Lying);
        Assert.False(landed.Stopped);
        Assert.Empty(h.Gather(2.1).Of<HrotRound>());

        // it sticks in a wall: stopped
        projectile = ref h.World.GetComponentRW<BallisticProjectile>(grenade);
        projectile.StoppedTick = 7;
        var stopped = Assert.Single(h.Gather(3.0).Of<HrotRound>());
        Assert.True(stopped.Lying && stopped.Stopped);
        Assert.Empty(h.Gather(3.1).Of<HrotRound>());
    }

    [Fact]
    public void A_replicated_round_has_no_combat_record_and_lies_when_it_does_not_move()
    {
        using var h = new Stage();
        h.AddRound(3001, MunitionTkbCatalog.Tkb556Tracer, Entity.Null, new Vector3(4, 5, 6), Vector3.Zero, combat: false);

        var round = Assert.Single(h.Gather(1.0).Of<HrotRound>());

        Assert.True(round.Lying);
        Assert.Equal(0, round.ShooterId);
        Assert.Equal(new HrotVec3(4, 5, 6), round.Muzzle);   // the muzzle is unknown here: the round's own place
        Assert.Equal(-1f, round.FuzeLeftS);
    }

    [Fact]
    public void The_munition_look_is_resent_after_a_world_boundary_and_a_round_is_forgotten_with_its_entity()
    {
        using var h = new Stage();
        var shooter = h.AddHrotEntity(2001, Vector3.Zero, tkb: GunType);
        var round = h.AddRound(3001, MunitionTkbCatalog.Tkb556Tracer, shooter, Vector3.Zero, new Vector3(900, 0, 0));
        Assert.Single(h.Gather(1.0).Of<HrotMunitionLook>());

        h.RemoveHrotEntity(round);
        var removal = h.Gather(2.0);
        Assert.Contains(removal.All, m => m is HrotEntityRemoved { Id: 3001 });

        // the same id and type again (a new round after the id was reused): the round is new to the gateway, the look is not
        h.AddRound(3001, MunitionTkbCatalog.Tkb556Tracer, shooter, Vector3.Zero, new Vector3(900, 0, 0));
        var again = h.Gather(3.0);
        Assert.Single(again.Of<HrotRound>());
        Assert.Empty(again.Of<HrotMunitionLook>());

        h.Adapter.Reset(h.World);   // a world boundary: new runtime, nothing known
        var afterBoundary = h.Gather(4.0);
        Assert.Single(afterBoundary.Of<HrotMunitionLook>());
        Assert.Single(afterBoundary.Of<HrotRound>());
    }

    [Fact]
    public void A_fire_sends_its_munition_look_before_the_fire_once_and_the_mount_category_by_name()
    {
        using var h = new Stage();
        var gun = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: GunType);
        var man = h.AddHrotEntity(1201, new Vector3(5, 0, 0), tkb: ManType);
        h.PublishForNextFrame(new WeaponFireNotification { Shooter = gun, Target = Entity.Null, WeaponIndex = 0, Quantity = 1, AmmoGuid = MunitionTkbCatalog.TkbM67Grenade });

        var fired = h.GatherEvents();

        var fire = Assert.Single(fired.Of<HrotFire>());
        Assert.Equal(HrotWeaponCategory.MachineGun, fire.WeaponCategory);   // the mount's own category, by name
        var look = Assert.Single(fired.Of<HrotMunitionLook>());
        Assert.Equal(HrotMunitionShape.Grenade, look.Shape);   // HROT's value 0: still named, not "none"
        Assert.True(fired.All.IndexOf(look) < fired.All.IndexOf(fire));

        h.PublishForNextFrame(new WeaponFireNotification { Shooter = gun, Target = Entity.Null, WeaponIndex = 0, Quantity = 1, AmmoGuid = MunitionTkbCatalog.TkbM67Grenade });
        Assert.Empty(h.GatherEvents().Of<HrotMunitionLook>());

        // a shooter with no weapon suite and a ball round (no look): an empty category, no look
        h.PublishForNextFrame(new WeaponFireNotification { Shooter = man, Target = Entity.Null, WeaponIndex = 0, Quantity = 1, AmmoGuid = 0x777 });
        var ball = h.GatherEvents();
        Assert.Equal(HrotWeaponCategory.Unknown, Assert.Single(ball.Of<HrotFire>()).WeaponCategory);
        Assert.Empty(ball.Of<HrotMunitionLook>());
    }

    // ---- flames ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_lit_flame_jet_is_sent_once_while_lit_and_removed_when_it_goes_out()
    {
        using var h = new Stage();
        var flamer = h.AddFlameUnit(2001, lastShotAt: 100.0, aim: new Vector3(40, 5, 0));
        h.AddHrotEntity(2002, new Vector3(50, 0, 0), tkb: ManType);   // an ordinary unit: no jet

        var lit = h.Gather(100.1);

        var jet = Assert.Single(lit.Of<HrotFlameJet>());
        Assert.Equal((2001, 0), (jet.Id, jet.Mount));
        Assert.Equal(new HrotVec3(40, 5, 0), jet.AimPoint);
        Assert.Equal(0, jet.TargetId);
        Assert.Equal(60f, jet.RangeM);
        Assert.Equal(10f * MathF.PI / 180f, jet.HalfAngleRad, 5);
        Assert.Equal(0.8f, jet.Intensity, 5);
        Assert.Equal(99.0, jet.BurstStartedAt);
        Assert.Equal(30f, jet.BurnSeconds);
        Assert.Empty(lit.Of<HrotFlameJetRemoved>());

        // still lit, nothing changed: not sent again
        Assert.Empty(h.Gather(100.2).Of<HrotFlameJet>());

        // the target moves on: a change is sent
        h.World.SetComponent(flamer, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Point = new WorldPoint(40, 9, 0), ActiveMount = 0 });
        Assert.Equal(new HrotVec3(40, 9, 0), Assert.Single(h.Gather(100.22).Of<HrotFlameJet>()).AimPoint);

        // it goes out (last shot older than 0.25 s): removed once
        var gone = h.Gather(100.5);
        Assert.Empty(gone.Of<HrotFlameJet>());
        Assert.Equal((2001, 0), (Assert.Single(gone.Of<HrotFlameJetRemoved>()).Id, 0));
        Assert.Empty(h.Gather(100.6).Of<HrotFlameJetRemoved>());

        // a new burst lights it again
        h.World.SetComponent(flamer, new WeaponAmmo { Count = 1, Load0 = FuelType, LastShotAt0 = 200.0, BurstStartedAt0 = 199.0 });
        Assert.Single(h.Gather(200.1).Of<HrotFlameJet>());
    }

    [Fact]
    public void A_jet_is_removed_with_its_unit_and_forgotten_at_a_world_boundary()
    {
        using var h = new Stage();
        var flamer = h.AddFlameUnit(2001, lastShotAt: 100.0, aim: new Vector3(40, 5, 0));
        Assert.Single(h.Gather(100.1).Of<HrotFlameJet>());

        h.RemoveHrotEntity(flamer);
        var gone = h.Gather(100.15);
        Assert.Single(gone.Of<HrotFlameJetRemoved>());
        Assert.Contains(gone.All, m => m is HrotEntityRemoved { Id: 2001 });

        var again = h.AddFlameUnit(2001, lastShotAt: 100.0, aim: new Vector3(40, 5, 0));
        Assert.Single(h.Gather(100.2).Of<HrotFlameJet>());
        h.Adapter.Reset(h.World);   // a world boundary: the gateway knows nothing, the lit jet is sent as new
        Assert.Single(h.Gather(100.22).Of<HrotFlameJet>());
        Assert.True(h.World.IsAlive(again));
    }

    [Fact]
    public void Burning_spots_are_world_points_and_only_the_burning_ones_and_removed_when_none_burns()
    {
        using var h = new Stage();
        var truck = h.AddHrotEntity(2001, new Vector3(10, 0, 0), tkb: GunType);
        h.World.SetComponent(truck, new SimTransform { Position = new Vector3(10, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f) });
        var fire = default(FireState);
        fire.TryAdd(new FireSpot(1f, 0f, 0.5f, 2f, new SimInstant(100.0), 60f, 0.9f), 100.0);      // burns 100 .. 160
        fire.TryAdd(new FireSpot(-1f, 0f, 0.5f, 1f, new SimInstant(10.0), 5f, 1f), 10.0);          // went out at 15
        h.World.AddComponent(truck, fire);

        var burning = Assert.Single(h.Gather(110.0).Of<HrotFireSpots>());

        Assert.Equal(2001, burning.Id);
        var spot = Assert.Single(burning.Spots);
        Assert.Equal(10.0, spot.Position.X, 3);   // the holder's frame turned a quarter and moved: local (1, 0, 0.5) is world (10, 1, 0.5)
        Assert.Equal(1.0, spot.Position.Y, 3);
        Assert.Equal(0.5, spot.Position.Z, 3);
        Assert.Equal((2f, 0.9f, 100.0, 60f), (spot.Radius, spot.Intensity, spot.Since, spot.Seconds));
        Assert.Empty(h.Gather(111.0).Of<HrotFireSpots>());   // unchanged

        // the vehicle drives on: the world point moved, so it is sent again (a hair of movement is not)
        h.World.SetComponent(truck, new SimTransform { Position = new Vector3(10.02f, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f) });
        Assert.Empty(h.Gather(112.0).Of<HrotFireSpots>());
        h.World.SetComponent(truck, new SimTransform { Position = new Vector3(14, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f) });
        Assert.Equal(14.0, Assert.Single(Assert.Single(h.Gather(113.0).Of<HrotFireSpots>()).Spots).Position.X, 3);

        // the fire burns out: one empty set, then nothing
        var out_ = Assert.Single(h.Gather(170.0).Of<HrotFireSpots>());
        Assert.Equal((2001, 0), (out_.Id, out_.Spots.Count));
        Assert.Empty(h.Gather(171.0).Of<HrotFireSpots>());
    }

    [Fact]
    public void A_record_entity_without_a_transform_keeps_its_spots_as_they_are_and_a_removed_holder_clears_them()
    {
        using var h = new Stage();
        var record = h.World.CreateEntity();   // a building's damage record: a network id and a fire state, no pose
        h.World.AddComponent(record, new NetworkIdentity(4001));
        var fire = default(FireState);
        fire.TryAdd(new FireSpot(30f, 40f, 2f, 3f, new SimInstant(1.0), 600f, 1f), 1.0);
        h.World.AddComponent(record, fire);

        var spots = Assert.Single(h.Gather(10.0).Of<HrotFireSpots>());
        Assert.Equal(4001, spots.Id);
        Assert.Equal(new HrotVec3(30, 40, 2), Assert.Single(spots.Spots).Position);

        h.World.DestroyEntity(record);
        var gone = Assert.Single(h.Gather(11.0).Of<HrotFireSpots>());
        Assert.Equal((4001, 0), (gone.Id, gone.Spots.Count));
    }

    [Fact]
    public void Fire_spots_follow_a_world_boundary()
    {
        using var h = new Stage();
        var truck = h.AddHrotEntity(2001, new Vector3(10, 0, 0), tkb: GunType);
        var fire = default(FireState);
        fire.TryAdd(new FireSpot(0f, 0f, 0f, 2f, new SimInstant(100.0), 600f, 1f), 100.0);
        h.World.AddComponent(truck, fire);
        Assert.Single(h.Gather(110.0).Of<HrotFireSpots>());
        Assert.Empty(h.Gather(111.0).Of<HrotFireSpots>());

        h.Adapter.Reset(h.World);

        Assert.Single(h.Gather(112.0).Of<HrotFireSpots>());
    }

    // ---- ground marks ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Ground_heat_marks_are_sent_by_name_when_they_change_and_cleared_when_the_cell_is_empty()
    {
        using var h = new Stage();
        var cell = h.World.CreateEntity();
        h.World.AddComponent(cell, new NetworkIdentity(5001));
        var marks = default(GroundHeatMarks);
        marks.Set(0, new HeatMark(HeatMarkKind.BurnedArea, new Vector2(5, 6), 0.5f, 6f, 3f, HeatMarkEnd.Rear, new SimInstant(10.0), new SimInstant(20.0), 0f, ThermalPowerClass.NoPowerClass));
        marks.Set(1, new HeatMark(HeatMarkKind.VehicleStand, new Vector2(7, 8), 1.5f, 8f, 3.5f, HeatMarkEnd.Front, new SimInstant(30.0), new SimInstant(40.0), 12f, ThermalPowerClass.Tank));
        marks.Count = 2;
        h.World.AddComponent(cell, marks);

        var sent = Assert.Single(h.Gather(50.0).Of<HrotGroundMarks>());

        Assert.Equal(5001, sent.Id);
        Assert.Equal(2, sent.Marks.Count);
        var scorch = sent.Marks[0];
        Assert.Equal(HrotHeatMarkKind.BurnedArea, scorch.Kind);
        Assert.Equal(HrotHeatMarkEnd.Rear, scorch.HotEnd);
        Assert.Equal(new HrotVec3(5, 6, 0), scorch.Centre);   // z is 0: the host has no ground-height reader
        Assert.Equal((0.5f, 6f, 3f, 10.0, 20.0), (scorch.Heading, scorch.Length, scorch.Width, scorch.StandSince, scorch.LeftAt));
        Assert.Equal(HrotHeatMarkKind.VehicleStand, sent.Marks[1].Kind);
        Assert.Empty(h.Gather(51.0).Of<HrotGroundMarks>());

        // another mark joins the cell: the set changes
        marks.Set(2, new HeatMark(HeatMarkKind.BodyImprint, new Vector2(1, 1), 0f, 1.8f, 0.6f, HeatMarkEnd.NoHotEnd, new SimInstant(60.0), new SimInstant(70.0), 0f, ThermalPowerClass.NoPowerClass));
        marks.Count = 3;
        h.World.SetComponent(cell, marks);
        Assert.Equal(3, Assert.Single(h.Gather(52.0).Of<HrotGroundMarks>()).Marks.Count);

        // a cell whose marks went cold and were dropped: an empty set, once
        marks.Count = 0;
        h.World.SetComponent(cell, marks);
        var cleared = Assert.Single(h.Gather(53.0).Of<HrotGroundMarks>());
        Assert.Equal((5001, 0), (cleared.Id, cleared.Marks.Count));
        Assert.Empty(h.Gather(54.0).Of<HrotGroundMarks>());
    }

    [Fact]
    public void Ground_marks_are_resent_after_a_world_boundary()
    {
        using var h = new Stage();
        var cell = h.World.CreateEntity();
        h.World.AddComponent(cell, new NetworkIdentity(5001));
        var marks = default(GroundHeatMarks);
        marks.Set(0, new HeatMark(HeatMarkKind.BurnedArea, new Vector2(5, 6), 0.5f, 6f, 3f, HeatMarkEnd.NoHotEnd, new SimInstant(10.0), new SimInstant(20.0), 0f, ThermalPowerClass.NoPowerClass));
        marks.Count = 1;
        h.World.AddComponent(cell, marks);
        Assert.Single(h.Gather(50.0).Of<HrotGroundMarks>());

        h.Adapter.Reset(h.World);

        Assert.Single(h.Gather(51.0).Of<HrotGroundMarks>());
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------

    private static int Hrot_DisKind(long packed) => (int)((ulong)packed >> 56);
}
