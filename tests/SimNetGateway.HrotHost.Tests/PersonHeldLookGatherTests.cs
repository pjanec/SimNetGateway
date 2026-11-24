using System.Numerics;
using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Behavior.Translators;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Tkb.Domain;
using Hrot.Map.Common;
using Hrot.Map.Definitions.Tkb;
using Hrot.Core.Tkb;
using Hrot.UI.Common.Map3D;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;
using WeaponPosture = Fdp.Toolkit.Combat.Components.WeaponPosture;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// DD-031 (T-076): the held-weapon look of a person as HROT draws it. The gateway calls <c>HeldWeapons.Of</c> / <c>MarkingOf</c> (Hrot.Look), so the property under test is
/// that <c>HrotPersonDetail.HeldLook</c> / <c>SlungLook</c> / <c>HeldMarking</c> EQUAL HROT's own answer on the same world, for a picked-up weapon riding a carry seat, for a
/// man's own mounts and for a toy; and that the look is change-only.
/// </summary>
public class PersonHeldLookGatherTests
{
    internal const long ToyType = 9301, CivilianType = 9302, BareType = 9303;

    private sealed class Capture : IIngressSink
    {
        public readonly List<HrotPersonDetail> Details = new();
        public readonly List<HrotEntityState> States = new();

        public void Deliver<T>(in T message, double? receivedAt = null)
        {
            if (message is HrotEntityState s)
            {
                States.Add(s);
            }

            if (message is HrotPersonDetail d)
            {
                Details.Add(d);
            }
        }
    }

    internal static double _now = 1000;

    private static List<HrotPersonDetail> Gather(PackHarness h, double simTime)
    {
        var inbound = new InMemoryInbound(new SourceId(99), "test");
        _now += 0.1;
        h.Adapter.GatherEntities(h.World, inbound, _now, simTime);
        var sink = new Capture();
        inbound.Drain(sink, int.MaxValue);
        return sink.Details;
    }

    /// <summary>A harness whose TKB is HROT's built-in catalogue plus a toy gunner and a civilian with no mount of his own; the components of a person's carry seats registered.</summary>
    internal static PackHarness Live()
    {
        var h = new PackHarness(dis: true);
        NedTkbCatalog.RegisterAll(h.TestTkb);
        UrbanCombatTkbCatalog.RegisterAll(h.TestTkb);
        CivilianTkbCatalog.RegisterAll(h.TestTkb);
        Person(h, ToyType, "Toy gunner", new WeaponSuiteDto
        {
            Mounts = { new WeaponMountDto { Category = WeaponCategory.SmallArms, Appearance = new ItemAppearanceDto { Look = ItemLook.Rifle } } },   // looks like a rifle, names no round
        });
        Person(h, CivilianType, "Civilian", null);
        Person(h, BareType, "Bare", null);
        void Reg<T>() where T : unmanaged { if (!h.World.IsComponentTypeRegistered<T>()) h.World.RegisterComponent<T>(); }
        Reg<PassengerBuffer>();
        Reg<CarrierSeats>();
        Reg<CarrierStations>();
        Reg<IsEmbarkedTag>();
        Reg<WeaponSystemParams>();
        Reg<WeaponSystemState>();
        h.World.SetSingletonManaged<ITkbDatabase>(h.TestTkb);
        h.Steps(3);
        return h;
    }

    private static void Person(PackHarness h, long type, string name, WeaponSuiteDto? suite)
    {
        var t = new TkbTemplate(name, type) { DisType = new DISEntityType { Kind = 3, Domain = 1, Country = 225, Category = 1 } };
        t.AddDescriptor(CarrierDefDto.PersonCarrier());
        if (suite != null)
        {
            t.AddDescriptor(suite);
        }

        h.TestTkb.Register(t);
    }

    internal static Entity Man(PackHarness h, int id, long type, int active = 0)
    {
        var e = h.AddHrotEntity(id, new Vector3(id % 100, 0, 0), tkb: type);
        h.World.AddComponent(e, new WeaponPosture { Kind = WeaponPostureKind.Ready, HeldItem = HeldItemKind.Weapon, ActiveMount = (byte)active });
        new CarrierTkbTranslator().Inject(h.World, e, h.TestTkb.GetByType(new TkbTypeId(type))!);   // his carry seats, as the sim gives them
        return e;
    }

    /// <summary>What HROT does when a man picks a weapon up (CE-3233): a weapon-system ENTITY of <paramref name="weaponType"/> rides one of his carry stations.</summary>
    private static Entity PickUp(PackHarness h, Entity man, long weaponType)
    {
        var w = h.World.CreateEntity();
        h.World.AddComponent(w, new SimTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity });
        h.World.AddComponent(w, new TkbIdentity { TkbType = new TkbTypeId(weaponType) });
        h.World.AddComponent(w, new WeaponSystemParams { NeedsOperator = 1, Portable = 1, FireWhileCarried = 1 });
        h.World.AddComponent(w, new WeaponSystemState { Deploy = WeaponDeploy.Stowed });
        var stations = h.World.GetComponentRO<CarrierStations>(man);
        int seat = Enumerable.Range(0, PassengerBuffer.Capacity).First(i => stations.IsStation(i));
        var list = h.World.GetComponentRO<PassengerBuffer>(man);
        list.Passengers[seat] = w;
        list.Count = 1;
        h.World.SetComponent(man, list);
        h.World.AddComponent(w, new IsEmbarkedTag { VehicleEntity = man, Exposed = 1, Seat = (byte)seat });
        return w;
    }

    internal static void Hold(PackHarness h, Entity man, int mount)
        => h.World.SetComponent(man, new WeaponPosture { Kind = WeaponPostureKind.Ready, HeldItem = HeldItemKind.Weapon, ActiveMount = (byte)mount });

    private static HrotPersonDetail Detail(PackHarness h, int id)
        => Assert.Single(Gather(h, 1.0), d => d.Id == id);

    /// <summary>HROT's own answer on the same world, by name (the pivot enums mirror HROT's).</summary>
    private static (string InHands, string Slung, string Marking) Expected(PackHarness h, Entity man)
    {
        var (inHands, slung) = HeldWeapons.Of(h.World, man, h.TestTkb, out _);
        return (inHands.ToString(), slung.ToString(), HeldWeapons.MarkingOf(h.World, HeldWeapons.OwnMountInHands(h.World, man)).ToString());
    }

    private static void AssertEqualsHrot(PackHarness h, Entity man, HrotPersonDetail d)
        => Assert.Equal(Expected(h, man), (d.HeldLook.ToString(), d.SlungLook.ToString(), d.HeldMarking.ToString()));

    [Fact]
    public void A_civilian_with_a_picked_up_pistol_holds_a_pistol_as_HROT_draws_it()
    {
        using var h = Live();
        var man = Man(h, 3001, CivilianType);
        PickUp(h, man, TkbEntityTypes.WeaponSystem_PickUpPistol);
        Hold(h, man, WeaponSystems.ControlledMountBase);

        var d = Detail(h, 3001);

        Assert.Equal(HrotHeldLook.Pistol, d.HeldLook);
        AssertEqualsHrot(h, man, d);
    }

    [Fact]
    public void A_civilian_with_a_picked_up_rifle_holds_a_rifle_and_a_bare_man_the_defaults()
    {
        using var h = Live();
        var man = Man(h, 3001, CivilianType);
        PickUp(h, man, TkbEntityTypes.WeaponSystem_PickUpRifle);
        Hold(h, man, WeaponSystems.ControlledMountBase);
        var bare = Man(h, 3002, BareType);

        var all = Gather(h, 1.0);

        var d = Assert.Single(all, x => x.Id == 3001);
        Assert.Equal((HrotHeldLook.Rifle, HrotHeldLook.Rifle, HrotHeldMarking.None), (d.HeldLook, d.SlungLook, d.HeldMarking));
        AssertEqualsHrot(h, man, d);
        AssertEqualsHrot(h, bare, Assert.Single(all, x => x.Id == 3002));
    }

    [Theory]
    [InlineData(TkbEntityTypes.Infantry_MachineGunnerPkm, 0, HrotHeldLook.MachineGun, HrotHeldLook.Rifle)]
    [InlineData(TkbEntityTypes.Infantry_Sniper, 0, HrotHeldLook.ScopedRifle, HrotHeldLook.Rifle)]
    [InlineData(UrbanCombatTkbCatalog.TkbInfantrySoldier, 1, HrotHeldLook.Pistol, HrotHeldLook.Rifle)]
    [InlineData(UrbanCombatTkbCatalog.TkbInsurgent, 0, HrotHeldLook.Rpg, HrotHeldLook.Rifle)]
    [InlineData(UrbanCombatTkbCatalog.TkbInsurgent, 1, HrotHeldLook.Rifle, HrotHeldLook.Rpg)]   // the launcher he is not using is slung
    [InlineData(UrbanCombatTkbCatalog.TkbJavelinTeam, 0, HrotHeldLook.MissileTube, HrotHeldLook.Rifle)]
    public void A_soldiers_own_mount_is_the_look_HROT_draws_and_an_unused_launcher_is_slung(long type, int active, HrotHeldLook inHands, HrotHeldLook slung)
    {
        using var h = Live();
        var man = Man(h, 3001, type, active);

        var d = Detail(h, 3001);

        Assert.Equal((inHands, slung), (d.HeldLook, d.SlungLook));
        AssertEqualsHrot(h, man, d);
    }

    [Fact]
    public void A_toy_gun_is_marked_a_toy_and_a_real_weapon_is_not()
    {
        using var h = Live();
        var toy = Man(h, 3001, ToyType);
        var real = Man(h, 3002, UrbanCombatTkbCatalog.TkbInfantrySoldier);

        var all = Gather(h, 1.0);

        var t = Assert.Single(all, x => x.Id == 3001);
        Assert.Equal(HrotHeldMarking.Toy, t.HeldMarking);
        Assert.Equal(HrotHeldMarking.None, Assert.Single(all, x => x.Id == 3002).HeldMarking);
        AssertEqualsHrot(h, toy, t);
        AssertEqualsHrot(h, real, Assert.Single(all, x => x.Id == 3002));
    }

    [Fact]
    public void A_man_whose_weapon_does_not_change_sends_nothing_and_switching_it_sends_exactly_one_sample()
    {
        using var h = Live();
        var man = Man(h, 3001, UrbanCombatTkbCatalog.TkbInsurgent, 0);
        Assert.Single(Gather(h, 1.0));   // first sight

        Assert.Empty(Gather(h, 1.1));   // steady: nothing
        Assert.Empty(Gather(h, 1.2));

        Hold(h, man, 1);   // rifle in hands, the RPG slung
        var d = Assert.Single(Gather(h, 1.3));
        Assert.Equal((HrotHeldLook.Rifle, HrotHeldLook.Rpg), (d.HeldLook, d.SlungLook));
        Assert.Empty(Gather(h, 1.4));

        // picking a pistol up and taking it in hand is one more
        PickUp(h, man, TkbEntityTypes.WeaponSystem_PickUpPistol);
        Hold(h, man, WeaponSystems.ControlledMountBase);
        d = Assert.Single(Gather(h, 1.5));
        Assert.Equal(HrotHeldLook.Pistol, d.HeldLook);
        AssertEqualsHrot(h, man, d);
    }

    /// <summary>
    /// D-4 MEASUREMENT (DD-031): a picked-up weapon riding a man's carry seat that has a network id (as a spawned pick-up has) is gathered by <c>EcsHrotAdapter</c> like any other entity -
    /// its query excludes records and doors but not an embarked weapon system. Pins what IS, not what should be: whether the gateway should hold it back is the coordinator's call.
    /// </summary>
    [Fact]
    public void D4_a_carried_pick_up_weapon_with_a_network_id_is_published_as_an_entity_of_its_own()
    {
        using var h = Live();
        var man = Man(h, 3001, CivilianType);
        var w = h.AddHrotEntity(3002, Vector3.Zero, tkb: TkbEntityTypes.WeaponSystem_PickUpPistol);
        h.World.AddComponent(w, new WeaponSystemParams { NeedsOperator = 1, Portable = 1, FireWhileCarried = 1 });
        h.World.AddComponent(w, new WeaponSystemState { Deploy = WeaponDeploy.Stowed });
        var stations = h.World.GetComponentRO<CarrierStations>(man);
        int seat = Enumerable.Range(0, PassengerBuffer.Capacity).First(i => stations.IsStation(i));
        var list = h.World.GetComponentRO<PassengerBuffer>(man);
        list.Passengers[seat] = w;
        list.Count = 1;
        h.World.SetComponent(man, list);
        h.World.AddComponent(w, new IsEmbarkedTag { VehicleEntity = man, Exposed = 1, Seat = (byte)seat });
        Hold(h, man, WeaponSystems.ControlledMountBase);

        var inbound = new InMemoryInbound(new SourceId(99), "test");
        h.Adapter.GatherEntities(h.World, inbound, _now += 0.1, 1.0);
        var sink = new Capture();
        inbound.Drain(sink, int.MaxValue);

        Assert.Contains(sink.States, s => s.Id == 3002);   // the weapon is an entity of its own while it rides the man
        Assert.Contains(sink.States, s => s.Id == 3001);
        Assert.Equal(HrotHeldLook.Pistol, Assert.Single(sink.Details, d => d.Id == 3001).HeldLook);
    }

    [Fact]
    public void A_steady_person_gather_with_a_held_look_allocates_nothing_and_sends_nothing()
    {
        using var h = Live();
        var men = new[]
        {
            (Id: 3001, E: Man(h, 3001, UrbanCombatTkbCatalog.TkbInsurgent, 1)),
            (Id: 3002, E: Man(h, 3002, ToyType)),
            (Id: 3003, E: Man(h, 3003, CivilianType)),
            (Id: 3004, E: Man(h, 3004, UrbanCombatTkbCatalog.TkbInfantrySoldier, 1)),
        };
        PickUp(h, men[2].E, TkbEntityTypes.WeaponSystem_PickUpPistol);
        Hold(h, men[2].E, WeaponSystems.ControlledMountBase);
        PersonDetailGather.EnsureRegistered(h.World);
        var gather = new PersonDetailGather();
        int sent = 0;
        for (int i = 0; i < 50; i++)   // first sight, then warm
        {
            foreach (var (id, e) in men)
            {
                sent += gather.TryGather(h.World, h.TestTkb, e, id, 1.0 + i, out _) ? 1 : 0;
            }
        }

        Assert.Equal(men.Length, sent);   // each once: first sight only
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            foreach (var (id, e) in men)
            {
                sent += gather.TryGather(h.World, h.TestTkb, e, id, 100.0 + i, out _) ? 1 : 0;
            }
        }

        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(men.Length, sent);   // nothing changed, nothing sent
        Assert.Equal(0L, bytes);
    }
}
