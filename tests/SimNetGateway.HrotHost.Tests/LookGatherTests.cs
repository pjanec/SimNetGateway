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
        h.PublishForNextFrame(new DetonationNotification { Target = Entity.Null, Ammo = (long)munition.TkbType, HitX = 50, HitY = 20, HitZ = 0.4f, Damage = 1100f });

        var effects = h.GatherEvents().Of<HrotEffect>();

        Assert.NotEmpty(effects);
        var boom = effects[0];
        Assert.Equal(nameof(Hrot.Look.Effects.EffectSurface.Ground), boom.Surface);   // HROT's enumerations go by name
        Assert.Equal(nameof(Hrot.Core.Tkb.EffectTkbCatalog.CalibreClass.Large), boom.SizeClass);
        Assert.Equal((ulong)munition.DisType.Value, boom.MunitionDis);
        Assert.Equal(new HrotVec3(50, 20, 0.4f), boom.At);
        Assert.Equal(0, boom.AnchorEntity);
        Assert.True(boom.Lifetime > 0f);
        Assert.All(effects, e => Assert.NotEqual(0L, e.EffectType));
    }

    [Fact]
    public void Shot_gives_muzzle_flash_anchored_to_shooter()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: TankType);
        h.PublishForNextFrame(new WeaponFireNotification { Shooter = tank, Target = Entity.Null, WeaponIndex = 0 });

        var effects = h.GatherEvents().Of<HrotEffect>();

        var flash = Assert.Single(effects, e => e.Kind == nameof(Fdp.Toolkit.Tkb.Domain.EffectKind.MuzzleFlash));
        Assert.Equal(2001, flash.AnchorEntity);
        Assert.Equal((byte)0, flash.AnchorMount);
        Assert.Equal(nameof(Hrot.Look.Effects.EffectSurface.Air), flash.Surface);
        Assert.True(flash.Lifetime > 0f);
    }

    [Fact]
    public void A_frame_without_events_sends_no_effect()
    {
        using var h = new Stage();
        Assert.Empty(h.GatherEvents().Of<HrotEffect>());
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

        // the hatch moves on: progress is sent in steps of 1/50
        var later = Assert.Single(h.Gather(20.0).Of<HrotVehicleLook>());
        Assert.Contains(((byte)2, 1f), later.Openings);
        Assert.All(later.Openings, o => Assert.Equal(o.Progress, MathF.Round(o.Progress * 50f) / 50f));
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
        Assert.Equal(["MuzzleFlash", "Explosion", "Decal", "Tracer", "Fire", "Smoke", "Flare", "Chaff"], Enum.GetNames<Fdp.Toolkit.Tkb.Domain.EffectKind>());
        Assert.Equal(["Small", "Medium", "Large", "Huge"], Enum.GetNames<Hrot.Core.Tkb.EffectTkbCatalog.CalibreClass>());
        Assert.Equal(["Ground", "Water", "Building", "Vehicle", "Person", "Air"], Enum.GetNames<Hrot.Look.Effects.EffectSurface>());
        Assert.Equal(["None", "KnockedOut", "BurntOut", "Catastrophic"], Enum.GetNames<DestructionKind>());
    }
}
