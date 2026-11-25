using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>DPS round 2 in the plugin (DD-018 3.2, 3.5a, 3.5b): the TKB type records, animals and the domain they are in, and the two-wheeler's lean.</summary>
public class DpsTypeAnimalBankTests
{
    private static string Id(int id) => id.ToString();

    private static long Dis(byte kind, byte category) => new DisTypeCode(kind, 1, 225, category, 1, 0, 0).Pack();

    private static HrotEntityState State(int id, long dis, double bank = 0.0, double simTime = 1.0) =>
        new(id, 103, dis, "X", 2, 0, new HrotVec3(10, 20, 3), HrotQuat.Identity, HrotVec3.Zero, HrotVec3.Zero, 100, 100, simTime, Bank: bank);

    [Fact]
    public void A_type_record_is_written_with_its_hash_schema_and_an_unbounded_json()
    {
        uint domain = Watch.NewDomain();
        using var types = new Watch.Watch<D.TkbType>(domain, "station-1", t => t.TypeId.ToString());
        using var rig = new Rig(domain);
        string big = "{\"descriptors\":[" + string.Join(",", Enumerable.Range(0, 20000).Select(i => "{\"type\":\"X\",\"partId\":" + i + "}")) + "]}";   // far beyond any practical bound

        rig.Send(new HrotTypeRecord(5_000_000_001L, "T-72", "Vehicles/Tanks", "1.1.225.1.1.3.0", "{\"tkbType\":5000000001}"));
        rig.Send(new HrotTypeRecord(6, new string('n', 100), new string('c', 200), new string('d', 100), big));

        Assert.True(types.Until(w => w.Alive.Count == 2));
        var t72 = types.Alive["5000000001"];
        Assert.Equal(("T-72", "Vehicles/Tanks", "1.1.225.1.1.3.0", "hrot/1"), (t72.Name, t72.CategoryPath, t72.DisType, t72.Schema));
        Assert.Equal("{\"tkbType\":5000000001}", t72.Json);
        Assert.Equal(DpsTypeCatalog.HashOf(t72.Json), t72.ContentHash);
        var large = types.Alive["6"];
        Assert.Equal(big, large.Json);   // the whole text arrives
        Assert.True(large.Json.Length > 500_000);
        Assert.Equal((64, 128, 64), (large.Name.Length, large.CategoryPath.Length, large.DisType.Length));   // the short fields are bounded
        Assert.Equal(DpsTypeCatalog.HashOf(big), large.ContentHash);
    }

    [Fact]
    public void The_content_hash_is_fnv_1a_64_of_the_utf8_json()
    {
        Assert.Equal(14695981039346656037UL, DpsTypeCatalog.HashOf(string.Empty));   // the offset basis
        Assert.Equal(0xAF63DC4C8601EC8CUL, DpsTypeCatalog.HashOf("a"));              // the published test vectors of FNV-1a 64
        Assert.Equal(0x85944171F73967E8UL, DpsTypeCatalog.HashOf("foobar"));
        Assert.Equal(DpsTypeCatalog.HashOf("{\"a\":1}"), DpsTypeCatalog.HashOf("{\"a\":1}"));
        Assert.NotEqual(DpsTypeCatalog.HashOf("{\"a\":1}"), DpsTypeCatalog.HashOf("{\"a\":2}"));
        Assert.Equal(DpsTypeCatalog.HashOf("é"), DpsTypeCatalog.HashOf("é"));   // non-ASCII text hashes its UTF-8 bytes
        Assert.NotEqual(DpsTypeCatalog.HashOf("e"), DpsTypeCatalog.HashOf("é"));
    }

    [Fact]
    public void An_animal_entity_is_domain_animal_with_an_animal_state_and_no_person_state()
    {
        uint domain = Watch.NewDomain();
        using var entities = new Watch.Watch<D.Entity>(domain, "station-1", e => Id(e.EntityId));
        using var animals = new Watch.Watch<D.Animal>(domain, "station-1", a => Id(a.EntityId));
        using var persons = new Watch.Watch<D.PersonState>(domain, "station-1", p => Id(p.EntityId));
        using var rig = new Rig(domain);

        rig.Send(State(3001, Dis(3, 200)));   // a cow
        rig.Send(State(3002, Dis(3, 209)));   // the last animal category
        rig.Send(State(3003, Dis(3, 199)));   // a person
        rig.Send(State(3004, Dis(3, 210)));   // a person
        rig.Send(State(3005, Dis(1, 200)));   // a platform whose category happens to be 200
        rig.Send(new HrotAnimal(3001, HrotAnimalMode.Alert, 2, false, true, true, 0, 0, 0, HrotFlockMode.Unknown));
        rig.Send(new HrotAnimal(3002, HrotAnimalMode.Flee, 3, true, false, false, 12, 20, 777, HrotFlockMode.Flying));
        rig.Send(Detail(3003));
        rig.Send(Detail(3001));   // a detail of an animal is not a person state

        Assert.True(entities.Until(w => w.Alive.Count == 5));
        Assert.Equal((D.Domain.DomainAnimal, D.Domain.DomainAnimal, D.Domain.DomainPerson, D.Domain.DomainPerson, D.Domain.DomainGround),
            (entities.Alive["3001"].Domain, entities.Alive["3002"].Domain, entities.Alive["3003"].Domain, entities.Alive["3004"].Domain, entities.Alive["3005"].Domain));
        Assert.True(animals.Until(w => w.Alive.Count == 2));
        var cow = animals.Alive["3001"];
        Assert.Equal((D.AnimalMode.AnimalModeAlert, (byte)2, false, true, true, 0, D.FlockMode.FlockModeUnknown), (cow.Mode, cow.FearBand, cow.Bolting, cow.Dangerous, cow.Calling, cow.FlockCount, cow.FlockMode));
        var flock = animals.Alive["3002"];
        Assert.Equal((D.AnimalMode.AnimalModeFlee, true, 12, 20, 777u, D.FlockMode.FlockModeFlying), (flock.Mode, flock.Bolting, flock.FlockCount, flock.FlockInitialCount, flock.FlockSeed, flock.FlockMode));
        Assert.True(persons.Until(w => w.Alive.ContainsKey("3003")));
        Thread.Sleep(200);
        persons.Pump();
        Assert.DoesNotContain("3001", persons.Alive.Keys);

        // an animal that goes loses its state with its entity
        rig.Send(new HrotEntityRemoved(3001));
        Assert.True(animals.Until(w => w.Alive.Count == 1 && w.Alive.ContainsKey("3002")));
    }

    private static HrotPersonDetail Detail(int id) =>
        new(id, HrotStance.Standing, 1f, HrotHeldItem.None, 0, HrotVec3.Zero, false, 0f, HrotGesture.None, HrotAction.None, 0, HrotActionState.None, 0, 0, HrotSeatRole.None, HrotCarryMode.None);

    [Fact]
    public void The_bank_goes_to_the_entity_state_and_a_change_of_a_degree_sends_one()
    {
        uint domain = Watch.NewDomain();
        using var states = new Watch.Watch<D.EntityState>(domain, "station-1", s => Id(s.EntityId));
        using var rig = new Rig(domain);
        long bike = Dis(1, 80);
        const double Degree = Math.PI / 180.0;

        rig.Send(State(4001, bike, bank: 0.0, simTime: 1.0));
        Assert.True(states.Until(w => w.Alive.ContainsKey("4001")));
        Assert.Equal(0f, states.Alive["4001"].Bank);

        // under a degree: nothing (the heartbeat is longer than this)
        rig.Send(State(4001, bike, bank: 0.5 * Degree, simTime: 1.1));
        Thread.Sleep(150);
        states.Pump();
        Assert.Equal(0f, states.Alive["4001"].Bank);

        // a degree and a half: sent, in radians, a left turn negative
        rig.Send(State(4001, bike, bank: -1.5 * Degree, simTime: 1.2));
        Assert.True(states.Until(w => w.Alive["4001"].Bank < -0.02f));
        Assert.Equal(-1.5 * Degree, states.Alive["4001"].Bank, 5);

        // a change measured against what was last SENT: another 0.8 degrees is below the threshold again
        rig.Send(State(4001, bike, bank: -2.3 * Degree, simTime: 1.3));
        Thread.Sleep(150);
        states.Pump();
        Assert.Equal(-1.5 * Degree, states.Alive["4001"].Bank, 5);
    }
}
