using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Behavior;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Behavior.Translators;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Replication.Components;
using Hrot.Core.Tkb;
using Hrot.Map.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;
using Fix = SimNetGateway.HrotHost.Tests.PersonHeldLookGatherTests;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// DD-032 (T-077): what carries a non-person entity, and the concealed rule. The properties: <c>HrotCarried</c> equals HROT's own <c>IsEmbarkedTag</c> / <c>CarryStations.IsStowed</c> on the same
/// world; a thing in a concealed seat is published on NO topic (every instance disposed as for an entity that left) and reappears when drawn; a person never gets one; change-only, 0 B steady.
/// </summary>
public class CarriedGatherTests
{
    private const int ManId = 4001, WeaponId = 4002, HeliId = 4003;

    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public IEnumerable<HrotCarried> Carried => All.OfType<HrotCarried>();

        /// <summary>Every message that names the entity <paramref name="id"/>, whatever the topic.</summary>
        public List<object> About(int id) => All.Where(m => m switch
        {
            HrotEntityState s => s.Id == id,
            HrotEntityRemoved r => r.Id == id,
            HrotCarried c => c.Id == id,
            HrotPersonDetail d => d.Id == id,
            HrotPersonAppearance a => a.Id == id,
            HrotVehicleLook v => v.Id == id,
            HrotTurretPose t => t.Id == id,
            HrotEntityStatus st => st.Id == id,
            _ => false,
        }).ToList();
    }

    private static Capture Gather(PackHarness h)
    {
        var inbound = new InMemoryInbound(new SourceId(99), "test");
        h.Adapter.GatherEntities(h.World, inbound, Fix._now += 0.1, 1.0);
        var sink = new Capture();
        inbound.Drain(sink, int.MaxValue);
        return sink;
    }

    /// <summary>A weapon system with a network id (as a spawned pick-up has) riding <paramref name="carrier"/>'s <paramref name="seat"/>, tagged as HROT tags it.</summary>
    private static Entity Ride(PackHarness h, Entity carrier, int seat, long weaponType, int id = WeaponId)
    {
        var w = h.AddHrotEntity(id, Vector3.Zero, tkb: weaponType);
        h.World.AddComponent(w, new WeaponSystemParams { NeedsOperator = 1, Portable = 1, FireWhileCarried = 1 });
        h.World.AddComponent(w, new WeaponSystemState { Deploy = WeaponDeploy.Stowed });
        var list = h.World.GetComponentRO<PassengerBuffer>(carrier);
        list.Passengers[seat] = w;
        list.Count++;
        h.World.SetComponent(carrier, list);
        h.World.AddComponent(w, new IsEmbarkedTag { VehicleEntity = carrier, Exposed = 1, Seat = (byte)seat });
        return w;
    }

    /// <summary>Moves the rider to another seat of the same carrier, as HROT's <c>CarryStations.Move</c> does (the tag follows).</summary>
    private static void Restation(PackHarness h, Entity carrier, Entity w, int seat)
    {
        var list = h.World.GetComponentRO<PassengerBuffer>(carrier);
        int from = h.World.GetComponentRO<IsEmbarkedTag>(w).Seat;
        list.Passengers[from] = Entity.Null;
        list.Passengers[seat] = w;
        h.World.SetComponent(carrier, list);
        var tag = h.World.GetComponentRO<IsEmbarkedTag>(w);
        tag.Seat = (byte)seat;
        h.World.SetComponent(w, tag);
    }

    private static Entity Civilian(PackHarness h) => Fix.Man(h, ManId, Fix.CivilianType);

    private static void AssertEqualsHrot(PackHarness h, Entity w, Entity carrier, HrotCarried c)
    {
        var tag = h.World.GetComponentRO<IsEmbarkedTag>(w);
        Assert.Equal((h.World.GetComponentRO<NetworkIdentity>(carrier).Value, tag.Seat, CarryStations.IsStowed(h.World, w)), (c.CarrierId, c.Seat, c.Stowed));
    }

    [Fact]
    public void A_pistol_in_the_hands_names_the_man_and_his_seat_and_is_not_stowed()
    {
        using var h = Fix.Live();
        var man = Civilian(h);
        int hands = CarryStations.Hands(h.World, man);
        Assert.True(hands >= 0, "the person carrier has a hands station");
        var w = Ride(h, man, hands, TkbEntityTypes.WeaponSystem_PickUpPistol);

        var c = Assert.Single(Gather(h).Carried);

        Assert.Equal((WeaponId, ManId, (byte)hands, false), (c.Id, c.CarrierId, c.Seat, c.Stowed));
        AssertEqualsHrot(h, w, man, c);
    }

    [Fact]
    public void A_rifle_on_the_back_is_stowed_and_equals_HROTs_answer()
    {
        using var h = Fix.Live();
        var man = Civilian(h);
        int back = CarryStations.Back(h.World, man);
        Assert.True(back >= 0, "the person carrier has a back station");
        var w = Ride(h, man, back, TkbEntityTypes.WeaponSystem_PickUpRifle);

        var c = Assert.Single(Gather(h).Carried);

        Assert.True(c.Stowed);
        Assert.Equal((byte)back, c.Seat);
        AssertEqualsHrot(h, w, man, c);
    }

    [Fact]
    public void The_door_gun_of_a_doors_off_UH60_names_the_helicopter_and_the_seat_HROT_tags()
    {
        using var h = Fix.Live();
        var heli = h.AddHrotEntity(HeliId, new Vector3(50, 0, 0), tkb: TkbEntityTypes.Heli_UH60_DoorsOff);
        new CarrierTkbTranslator().Inject(h.World, heli, h.TestTkb.GetByType(new TkbTypeId(TkbEntityTypes.Heli_UH60_DoorsOff))!);
        var stations = h.World.GetComponentRO<CarrierStations>(heli);
        int door = Enumerable.Range(0, PassengerBuffer.Capacity).First(i => stations.IsStation(i));
        var w = Ride(h, heli, door, TkbEntityTypes.WeaponSystem_M134Minigun);

        var c = Assert.Single(Gather(h).Carried, x => x.Id == WeaponId);

        Assert.Equal(HeliId, c.CarrierId);
        Assert.Equal(h.World.GetComponentRO<IsEmbarkedTag>(w).Seat, c.Seat);
        AssertEqualsHrot(h, w, heli, c);
    }

    [Fact]
    public void A_dropped_weapon_is_told_once_as_no_longer_carried_and_a_steady_one_sends_nothing()
    {
        using var h = Fix.Live();
        var man = Civilian(h);
        var w = Ride(h, man, CarryStations.Hands(h.World, man), TkbEntityTypes.WeaponSystem_PickUpPistol);
        Assert.Single(Gather(h).Carried);

        Assert.Empty(Gather(h).Carried);   // steady: nothing
        Assert.Empty(Gather(h).Carried);

        h.World.RemoveComponent<IsEmbarkedTag>(w);   // dropped: it lies on its own now
        var end = Assert.Single(Gather(h).Carried);
        Assert.Equal((WeaponId, 0), (end.Id, end.CarrierId));
        Assert.Empty(Gather(h).Carried);
    }

    [Fact]
    public void Moving_the_weapon_between_hands_and_back_sends_exactly_one_sample_each_way()
    {
        using var h = Fix.Live();
        var man = Civilian(h);
        int hands = CarryStations.Hands(h.World, man), back = CarryStations.Back(h.World, man);
        var w = Ride(h, man, hands, TkbEntityTypes.WeaponSystem_PickUpRifle);
        Assert.False(Assert.Single(Gather(h).Carried).Stowed);

        Restation(h, man, w, back);
        var c = Assert.Single(Gather(h).Carried);
        Assert.True(c.Stowed);
        Assert.Equal((byte)back, c.Seat);
        Assert.Empty(Gather(h).Carried);

        Restation(h, man, w, hands);
        Assert.False(Assert.Single(Gather(h).Carried).Stowed);
    }

    [Fact]
    public void A_concealed_weapon_is_published_on_no_topic_and_every_instance_of_it_goes_when_it_is_hidden_and_returns_when_drawn()
    {
        using var h = Fix.Live();
        var man = Civilian(h);
        int hands = CarryStations.Hands(h.World, man), hidden = CarryStations.Hidden(h.World, man);
        Assert.True(hidden >= 0, "the person carrier has a hidden station");

        // born hidden: nothing about it, on any topic
        var w = Ride(h, man, hidden, TkbEntityTypes.WeaponSystem_PickUpPistol);
        Assert.True(CarryStations.IsConcealed(h.World, w));
        var first = Gather(h);
        Assert.Empty(first.About(WeaponId));
        Assert.Contains(first.All.OfType<HrotEntityState>(), s => s.Id == ManId);   // the man himself is there

        // drawn into the hands: it appears, with its carriage
        Restation(h, man, w, hands);
        var drawn = Gather(h);
        Assert.Contains(drawn.About(WeaponId), m => m is HrotEntityState);
        Assert.Equal(new HrotCarried(WeaponId, ManId, (byte)hands, false), Assert.Single(drawn.Carried));

        // put back into the hidden seat: removed like an entity that left, and its carriage disposed
        Restation(h, man, w, hidden);
        var gone = Gather(h);
        Assert.Contains(gone.About(WeaponId), m => m is HrotEntityRemoved);
        Assert.Equal(0, Assert.Single(gone.Carried).CarrierId);
        Assert.Empty(Gather(h).About(WeaponId));   // and stays quiet
    }

    [Fact]
    public void A_person_never_gets_a_carriage_even_riding_a_seat()
    {
        using var h = Fix.Live();
        var heli = h.AddHrotEntity(HeliId, new Vector3(50, 0, 0), tkb: TkbEntityTypes.Heli_UH60_DoorsOff);
        new CarrierTkbTranslator().Inject(h.World, heli, h.TestTkb.GetByType(new TkbTypeId(TkbEntityTypes.Heli_UH60_DoorsOff))!);
        var soldier = Fix.Man(h, ManId, UrbanCombatTkbCatalog.TkbInfantrySoldier);
        h.World.AddComponent(soldier, new IsEmbarkedTag { VehicleEntity = heli, Exposed = 1, Seat = 1 });

        var all = Gather(h);

        Assert.Empty(all.Carried);
        Assert.Contains(all.All.OfType<HrotPersonDetail>(), d => d.Id == ManId);
    }

    [Fact]
    public void A_steady_carriage_gather_allocates_nothing()
    {
        using var h = Fix.Live();
        var man = Civilian(h);
        var a = Ride(h, man, CarryStations.Hands(h.World, man), TkbEntityTypes.WeaponSystem_PickUpPistol);
        var b = Ride(h, man, CarryStations.Back(h.World, man), TkbEntityTypes.WeaponSystem_PickUpRifle, 4005);
        var gather = new CarriedGather();
        var inbound = new InMemoryInbound(new SourceId(99), "test");
        var sink = new Capture();
        for (int i = 0; i < 50; i++)
        {
            gather.Gather(h.World, a, WeaponId, inbound);
            gather.Gather(h.World, b, 4005, inbound);
        }

        inbound.Drain(sink, int.MaxValue);
        Assert.Equal(2, sink.Carried.Count());   // first sight only
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            gather.Gather(h.World, a, WeaponId, inbound);
            gather.Gather(h.World, b, 4005, inbound);
        }

        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        sink.All.Clear();
        inbound.Drain(sink, int.MaxValue);
        Assert.Empty(sink.All);
        Assert.Equal(0L, bytes);
    }
}
