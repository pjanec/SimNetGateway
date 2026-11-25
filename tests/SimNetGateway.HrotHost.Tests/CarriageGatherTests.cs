using System.Numerics;
using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Terrain;
using Fdp.Toolkit.Tkb.Domain;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>DD-018 3.5c in the host: the seat of a man in a vehicle, how a man is carried and by whom (HROT's carriage rule), and a mine clearer's flail.</summary>
public class CarriageGatherTests
{
    private const long ManType = 300, CarType = 100, SeatedType = 400;

    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public List<T> Of<T>() => All.OfType<T>().ToList();
    }

    private static double _now = 1000;

    private static Capture Gather(PackHarness h, double simTime)
    {
        var inbound = new InMemoryInbound(new SourceId(99), "test");
        _now += 0.1;
        h.Adapter.GatherEntities(h.World, inbound, _now, simTime);
        var sink = new Capture();
        inbound.Drain(sink, int.MaxValue);
        return sink;
    }

    private static PackHarness Live()
    {
        var h = new PackHarness(dis: true);
        h.RegisterTemplate(CarType, 1);
        h.RegisterTemplate(ManType, 3);
        var seated = new TkbTemplate("seated", SeatedType) { DisType = new DISEntityType { Kind = 1, Domain = 1, Country = 225, Category = 1 } };
        seated.AddDescriptor(new CarrierDefDto
        {
            Seats =
            {
                new SeatDto { Role = SeatRole.Driver, Pose = SeatPose.Sit },
                new SeatDto { Role = SeatRole.Passenger, Pose = SeatPose.SitLegsOut },
                new SeatDto { Role = SeatRole.Passenger, Pose = SeatPose.Ride },
            },
        });
        h.TestTkb.Register(seated);
        h.Steps(3);
        return h;
    }

    private static Entity Man(PackHarness h, int id)
    {
        var e = h.AddHrotEntity(id, Vector3.Zero, tkb: ManType);
        h.World.AddComponent(e, new WeaponPosture { Kind = WeaponPostureKind.Ready, HeldItem = HeldItemKind.None });
        return e;
    }

    private static HrotPersonDetail Detail(Capture c, int id) => Assert.Single(c.Of<HrotPersonDetail>(), d => d.Id == id);

    // ---- the seat ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_man_in_an_enclosed_seat_has_the_seat_index_the_pose_and_is_not_exposed()
    {
        using var h = Live();
        var vehicle = h.AddHrotEntity(2100, Vector3.Zero, tkb: SeatedType);
        var man = Man(h, 2001);
        h.World.AddComponent(man, new IsEmbarkedTag { VehicleEntity = vehicle, Seat = 0, Exposed = 0, Mode = CarryMode.Seated });

        var d = Detail(Gather(h, 1.0), 2001);

        Assert.Equal(0, d.SeatIndex);
        Assert.False(d.SeatExposed);
        Assert.Equal("Sit", d.SeatPose);
        Assert.Equal(2100, d.CarryOther);
        Assert.Equal(HrotCarryRole.None, d.CarryRole);
        Assert.Equal(0, d.Partner);
    }

    [Fact]
    public void A_man_in_an_exposed_seat_is_exposed_and_has_that_seats_pose()
    {
        using var h = Live();
        var vehicle = h.AddHrotEntity(2100, Vector3.Zero, tkb: SeatedType);
        var man = Man(h, 2001);
        h.World.AddComponent(man, new IsEmbarkedTag { VehicleEntity = vehicle, Seat = 1, Exposed = 1, Mode = CarryMode.Seated });

        var d = Detail(Gather(h, 1.0), 2001);

        Assert.Equal(1, d.SeatIndex);
        Assert.True(d.SeatExposed);
        Assert.Equal("SitLegsOut", d.SeatPose);
    }

    [Fact]
    public void A_rider_astride_a_motorcycle_seat_has_the_ride_pose()
    {
        using var h = Live();
        var bike = h.AddHrotEntity(2100, Vector3.Zero, tkb: SeatedType);
        var man = Man(h, 2001);
        h.World.AddComponent(man, new IsEmbarkedTag { VehicleEntity = bike, Seat = 2, Exposed = 1, Mode = CarryMode.Seated });

        var d = Detail(Gather(h, 1.0), 2001);

        Assert.Equal(2, d.SeatIndex);
        Assert.True(d.SeatExposed);
        Assert.Equal("Ride", d.SeatPose);
    }

    [Fact]
    public void A_seat_the_type_does_not_describe_has_an_empty_pose_and_a_free_man_has_no_seat()
    {
        using var h = Live();
        var vehicle = h.AddHrotEntity(2100, Vector3.Zero);   // a type without a Gen.Carrier descriptor
        var seated = Man(h, 2001);
        h.World.AddComponent(seated, new IsEmbarkedTag { VehicleEntity = vehicle, Seat = 4, Exposed = 0, Mode = CarryMode.Seated });
        Man(h, 2002);

        var c = Gather(h, 1.0);

        Assert.Equal(4, Detail(c, 2001).SeatIndex);
        Assert.Equal(string.Empty, Detail(c, 2001).SeatPose);
        var free = Detail(c, 2002);
        Assert.Equal(HrotPersonDetail.NoSeat, free.SeatIndex);
        Assert.Equal(255, free.SeatIndex);
        Assert.False(free.SeatExposed);
        Assert.Equal(string.Empty, free.SeatPose);
        Assert.Equal(HrotCarryRole.None, free.CarryRole);
        Assert.Equal(0, free.CarryOther);
        Assert.Equal(0, free.Partner);
    }

    // ---- carried and dragged ---------------------------------------------------------------------------------------------------

    private static Entity Carrier(PackHarness h, int id, Entity casualty, CarryMode mode, Entity assistant = default)
    {
        var carrier = Man(h, id);
        h.World.AddComponent(carrier, new PersonCarrier());
        var buffer = default(PassengerBuffer);
        buffer.Passengers[0] = casualty;
        buffer.Count = 1;
        buffer.SetMode(0, mode);
        buffer.Assistant = assistant;
        h.World.AddComponent(carrier, buffer);
        h.World.AddComponent(casualty, new IsEmbarkedTag { VehicleEntity = carrier, Seat = 0, Exposed = 1, Mode = mode });
        return carrier;
    }

    [Theory]
    [InlineData(CarryMode.Carried, HrotCarryRole.Carried, HrotCarryRole.Carrying)]
    [InlineData(CarryMode.Dragged, HrotCarryRole.Dragged, HrotCarryRole.Dragging)]
    public void A_carried_or_dragged_casualty_and_his_carrier_name_each_other(CarryMode mode, HrotCarryRole casualtyRole, HrotCarryRole carrierRole)
    {
        using var h = Live();
        var casualty = Man(h, 2001);
        Carrier(h, 2002, casualty, mode);

        var c = Gather(h, 1.0);

        var held = Detail(c, 2001);
        Assert.Equal(casualtyRole, held.CarryRole);
        Assert.Equal(2002, held.CarryOther);   // his holder
        Assert.Equal(0, held.Partner);
        Assert.Equal(HrotPersonDetail.NoSeat, held.SeatIndex);
        var carrier = Detail(c, 2002);
        Assert.Equal(carrierRole, carrier.CarryRole);
        Assert.Equal(2001, carrier.CarryOther);   // what he carries
        Assert.Equal(0, carrier.Partner);
    }

    [Fact]
    public void A_stretcher_with_two_carriers_names_the_casualty_and_the_partners()
    {
        using var h = Live();
        var casualty = Man(h, 2001);
        var rear = Man(h, 2003);
        var front = Carrier(h, 2002, casualty, CarryMode.Stretcher, rear);
        h.World.AddComponent(rear, new IsAssistingTag { Carrier = front });

        var c = Gather(h, 1.0);

        var onStretcher = Detail(c, 2001);
        Assert.Equal(HrotCarryRole.OnStretcher, onStretcher.CarryRole);
        Assert.Equal(2002, onStretcher.CarryOther);
        Assert.Equal(2003, onStretcher.Partner);
        var frontMan = Detail(c, 2002);
        Assert.Equal(HrotCarryRole.StretcherFront, frontMan.CarryRole);
        Assert.Equal(2001, frontMan.CarryOther);
        Assert.Equal(2003, frontMan.Partner);
        var rearMan = Detail(c, 2003);
        Assert.Equal(HrotCarryRole.StretcherRear, rearMan.CarryRole);
        Assert.Equal(2001, rearMan.CarryOther);
        Assert.Equal(2002, rearMan.Partner);
    }

    [Fact]
    public void A_stale_holder_leaves_the_man_free()
    {
        using var h = Live();
        var vehicle = h.AddHrotEntity(2100, Vector3.Zero, tkb: SeatedType);
        var man = Man(h, 2001);
        h.World.AddComponent(man, new IsEmbarkedTag { VehicleEntity = vehicle, Seat = 0, Exposed = 1, Mode = CarryMode.Seated });
        h.RemoveHrotEntity(vehicle);

        var d = Detail(Gather(h, 1.0), 2001);

        Assert.Equal(HrotPersonDetail.NoSeat, d.SeatIndex);
        Assert.Equal(0, d.CarryOther);
    }

    // ---- the flail --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// T-079: <c>VehicleLook.Airborne</c> is HROT's one rule (<c>Hrot.Look.Airborne.Of</c>) for any flyer - a dragon or a helicopter with <c>FlightState</c> - and false for a ground vehicle; a change of it alone
    /// sends the look again, a steady gather sends nothing. Mutation: not copied from the rule (always false) fails the airborne row.
    /// </summary>
    [Fact]
    public void Airborne_on_the_vehicle_look_follows_the_flight_state_of_any_flyer_and_a_change_alone_is_sent()
    {
        using var h = Live();
        if (!h.World.IsComponentTypeRegistered<Fdp.Toolkit.Motion.FlightState>())
        {
            h.World.RegisterComponent<Fdp.Toolkit.Motion.FlightState>();
        }

        var flyer = h.AddHrotEntity(2100, Vector3.Zero);
        var ground = h.AddHrotEntity(2101, new Vector3(50, 0, 0));
        h.World.AddComponent(flyer, new Fdp.Toolkit.Motion.FlightState { Airborne = 1 });

        var first = Gather(h, 1.0).Of<HrotVehicleLook>();
        Assert.True(Assert.Single(first, l => l.Id == 2100).Airborne);
        Assert.False(Assert.Single(first, l => l.Id == 2101).Airborne);   // a ground vehicle
        Assert.Empty(Gather(h, 2.0).Of<HrotVehicleLook>());                // steady

        h.World.SetComponent(flyer, new Fdp.Toolkit.Motion.FlightState { Airborne = 0 });   // landed: the change alone is sent
        var landed = Assert.Single(Gather(h, 3.0).Of<HrotVehicleLook>());
        Assert.Equal((2100, false), (landed.Id, landed.Airborne));
        Assert.Empty(Gather(h, 4.0).Of<HrotVehicleLook>());

        h.World.SetComponent(flyer, new Fdp.Toolkit.Motion.FlightState { Airborne = 1 });
        Assert.True(Assert.Single(Gather(h, 5.0).Of<HrotVehicleLook>()).Airborne);
    }

    [Fact]
    public void The_flail_flag_follows_the_active_flail_of_the_mine_clearer_only()
    {
        using var h = Live();
        if (!h.World.IsComponentTypeRegistered<MineClearing>())
        {
            h.World.RegisterComponent<MineClearing>();
        }

        var tank = h.AddHrotEntity(2100, Vector3.Zero);
        Assert.False(Assert.Single(Gather(h, 1.0).Of<HrotVehicleLook>()).FlailActive);

        h.World.AddComponent(tank, new MineClearing { Active = 1, Tool = MineClearingTool.Flail, WidthM = 4.5f });
        Assert.True(Assert.Single(Gather(h, 2.0).Of<HrotVehicleLook>()).FlailActive);
        Assert.Empty(Gather(h, 3.0).Of<HrotVehicleLook>());   // steady: nothing again

        h.World.SetComponent(tank, new MineClearing { Active = 1, Tool = MineClearingTool.Plough, WidthM = 4.5f });
        Assert.False(Assert.Single(Gather(h, 4.0).Of<HrotVehicleLook>()).FlailActive);   // a plough never turns the drum

        h.World.SetComponent(tank, new MineClearing { Active = 1, Tool = MineClearingTool.Flail, WidthM = 4.5f });
        Assert.True(Assert.Single(Gather(h, 5.0).Of<HrotVehicleLook>()).FlailActive);

        h.World.SetComponent(tank, new MineClearing { Active = 0, Tool = MineClearingTool.Flail, WidthM = 4.5f });
        Assert.False(Assert.Single(Gather(h, 6.0).Of<HrotVehicleLook>()).FlailActive);   // the order ended
    }

    // ---- the guard --------------------------------------------------------------------------------------------------------------

    /// <summary>The seat pose crosses by name and is mapped by the plugin's <c>LookNames</c>: a value HROT appends or renames fails here until the plugin and the contract know it.</summary>
    [Fact]
    public void The_seat_pose_names_of_hrot_are_unchanged()
    {
        Assert.Equal(["Sit", "SitLegsOut", "Stand", "Kneel", "Ride"], Enum.GetNames<SeatPose>());
    }
}
