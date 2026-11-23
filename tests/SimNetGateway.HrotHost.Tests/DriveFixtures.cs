using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Behavior.Possession;
using Fdp.Toolkit.Tkb.Domain;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>What the Drive hand-over tests need in a harness world (T-070 / T-072): the components and templates of HROT's use rule, a person carrier and an empty two-seat vehicle one metre away. Shared by the possession-flow and the deploy-flow seats.</summary>
internal static class DriveFixtures
{
    public const int VehicleId = 2101;

    private static Fdp.Interfaces.TkbTemplate? _person, _truck;

    public static void Prepare(PackHarness h)
    {
        void Reg<T>()
            where T : unmanaged
        {
            if (!h.World.IsComponentTypeRegistered<T>())
            {
                h.World.RegisterComponent<T>();
            }
        }

        Reg<PassengerBuffer>();
        Reg<CarrierSeats>();
        Reg<CarrierStations>();
        Reg<OpenPassengerCabin>();
        Reg<PersonCarrier>();
        Reg<IsEmbarkedTag>();
        Reg<IsAssistingTag>();
        Reg<OperatorUseStatus>();
        _person = new Fdp.Interfaces.TkbTemplate("person", 310);
        _person.AddDescriptor(CarrierDefDto.PersonCarrier());
        _truck = new Fdp.Interfaces.TkbTemplate("HMMWV", 311) { DisType = new DISEntityType { Kind = 1, Domain = 1, Country = 225, Category = 1 } };
        _truck.AddDescriptor(new CarrierDefDto { Seats = { new SeatDto { Role = SeatRole.Driver }, new SeatDto { Role = SeatRole.Passenger } } });
        h.TestTkb.Register(_person);
        h.TestTkb.Register(_truck);
        h.World.SetSingletonManaged<Fdp.Interfaces.ITkbDatabase>(h.TestTkb);
        h.World.SetSingletonManaged(h.Map);   // a HROT node sets its id map as a world singleton; HROT's hand-over reads it there
    }

    public static void MakePerson(PackHarness h, Entity soldier) => new Fdp.Toolkit.Behavior.Translators.CarrierTkbTranslator().Inject(h.World, soldier, _person!);

    public static Entity AddVehicle(PackHarness h)
    {
        var vehicle = h.AddHrotEntity(VehicleId, new Vector3(2, 2, 0), "HMMWV", tkb: 311);
        new Fdp.Toolkit.Behavior.Translators.CarrierTkbTranslator().Inject(h.World, vehicle, _truck!);
        return vehicle;
    }
}
