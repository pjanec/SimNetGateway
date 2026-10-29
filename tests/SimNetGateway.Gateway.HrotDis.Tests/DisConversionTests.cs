using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Dis7;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>The plain DIS conversions, and 600 random HROT states through the whole egress and back against an independent oracle (DD-006 section 5).</summary>
public class DisConversionTests
{
    [Fact]
    public void Entity_types_and_identifiers_round_trip_for_random_values()
    {
        var random = new Random(31);
        for (int i = 0; i < 1000; i++)
        {
            var code = new DisTypeCode((byte)random.Next(256), (byte)random.Next(256), (ushort)random.Next(65536), (byte)random.Next(256),
                (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            var type = DisConversions.ToEntityType(code.Pack());
            Assert.Equal(code.Pack(), DisConversions.PackType(type));
            Assert.Equal(code.Kind, type.EntityKind);
            Assert.Equal(code.Country, type.Country);
            Assert.Equal(code.Extra, type.Extra);

            var key = new DisEntityKey((ushort)random.Next(65536), (ushort)random.Next(65536), (ushort)random.Next(65536));
            Assert.Equal(key, DisConversions.KeyOf(DisConversions.ToEntityId(key)));
            var eventId = new DisEventId((ushort)random.Next(65536), (ushort)random.Next(65536), (ushort)random.Next(65536));
            Assert.Equal(eventId, DisConversions.EventIdOf(DisConversions.ToEventIdentifier(eventId)));
        }
    }

    [Fact]
    public void No_entity_and_all_entities_are_the_standards_values()
    {
        Assert.True(DisConversions.IsNoEntity(DisConversions.NoEntity()));
        Assert.False(DisConversions.IsNoEntity(DisConversions.AllEntities()));
        Assert.Equal(new DisEntityKey(0xFFFF, 0xFFFF, 0xFFFF), DisConversions.KeyOf(DisConversions.AllEntities()));
    }

    [Fact]
    public void The_appearance_carries_damage_and_deactivated_and_nothing_else()
    {
        for (int damage = 0; damage <= 3; damage++)
        {
            foreach (bool deactivated in new[] { false, true })
            {
                uint appearance = DisConversions.AppearanceOf(damage, deactivated);
                Assert.Equal(damage, DisConversions.DamageOf(appearance));
                Assert.Equal(deactivated, EntityAppearance.GetDeactivated(appearance));
                Assert.Equal((uint)damage << 3 | (deactivated ? 1u << 23 : 0u), appearance);
            }
        }

        Assert.Equal(3, DisConversions.DamageOf(DisConversions.AppearanceOf(99, false))); // clamped, never garbage in the other bits
        Assert.Equal(0, DisConversions.DamageOf(DisConversions.AppearanceOf(-4, false)));
    }

    [Fact]
    public void A_clock_time_splits_into_the_hour_and_the_time_past_it()
    {
        var clock = DisConversions.ClockOf(2 * 3600 + 17.25);
        Assert.Equal(2u, clock.Hour);
        Assert.Equal(17.25, DisTimestamp.ToSecondsPastHour(clock.TimePastHour), 6);
        Assert.True(DisTimestamp.IsAbsolute(clock.TimePastHour));
        Assert.Equal(0u, DisConversions.ClockOf(double.NaN).Hour);
    }

    // ---- HROT states through the egress, 600 of them ---------------------------------------------------------------------------

    private static readonly Dictionary<long, long> DisTypeOfTkb = new()
    {
        [100] = new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack(),
        [101] = new DisTypeCode(1, 1, 225, 6, 1, 0, 0).Pack(),
        [102] = new DisTypeCode(1, 1, 225, 2, 1, 0, 0).Pack(),
        [200] = new DisTypeCode(1, 2, 225, 50, 1, 0, 0).Pack(),
        [300] = new DisTypeCode(1, 1, 225, 9, 0, 0, 0).Pack(),
        [400] = new DisTypeCode(1, 1, 222, 1, 2, 1, 0).Pack(),
    };

    [Fact]
    public void Random_hrot_states_arrive_on_dis_as_full_entity_state_pdus_matching_the_conversions()
    {
        var random = new Random(2024);
        var h = new HrotDisHarness(idLast: 5_000_999);
        h.GoLive(lat: 47.5, lon: 19.0, alt: 120.0);
        long[] tkbs = DisTypeOfTkb.Keys.ToArray();
        for (int i = 1; i <= 600; i++)
        {
            long tkb = tkbs[random.Next(tkbs.Length)];
            var enu = new HrotVec3(random.NextDouble() * 20000 - 10000, random.NextDouble() * 20000 - 10000, random.NextDouble() * 1000 - 100);
            var velocity = new HrotVec3(random.NextDouble() * 80 - 40, random.NextDouble() * 80 - 40, random.NextDouble() * 10 - 5);
            var omega = new HrotVec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            var rotation = ServiceTests.RandomQuaternion(random);
            double health = random.Next(4) switch { 0 => 100, 1 => 60, 2 => 20, _ => 0 };
            string marking = "M" + random.Next(1_000_000);
            int force = random.Next(4);

            h.HrotStep(HrotDisHarness.State(i, enu, tkb, marking, force, health, 100, rotation: rotation, velocity: velocity, angularVelocity: omega));

            var sentForIt = h.DisChecker!.States.Where(s => s.Pdu.EntityID.EntityNumber == i).ToList();
            Assert.Single(sentForIt); // exactly one PDU per new entity (other entities may heartbeat in the same tick), never a partial one
            var pdu = sentForIt[0].Pdu;
            var frame = h.Frame!;

            // location in metres, angles and velocities as the floats of the independent conversions
            Support.Near(frame.PointToEcef(Support.V(enu)), DisConversions.ToVec(pdu.EntityLocation), 1e-6, "location");
            var q = GeoFrame.ToQuaternion(rotation);
            var euler = LocalToGeocentricOrientation.ToDisEuler(q, frame);
            Assert.Equal((double)(float)euler.X, pdu.EntityOrientation.Psi, 9);
            Assert.Equal((double)(float)euler.Y, pdu.EntityOrientation.Theta, 9);
            Assert.Equal((double)(float)euler.Z, pdu.EntityOrientation.Phi, 9);
            var v = frame.VectorToEcef(Support.V(velocity));
            Assert.Equal((double)(float)v.X, pdu.EntityLinearVelocity.X, 9);
            Assert.Equal((double)(float)v.Y, pdu.EntityLinearVelocity.Y, 9);
            Assert.Equal((double)(float)v.Z, pdu.EntityLinearVelocity.Z, 9);
            var w = LocalToGeocentricOrientation.AngularVelocityEnuToBodyFrd(q, Support.V(omega));
            Assert.Equal((double)(float)w.X, pdu.DeadReckoningParameters.EntityAngularVelocity.X, 9);
            Assert.Equal((double)(float)w.Y, pdu.DeadReckoningParameters.EntityAngularVelocity.Y, 9);
            Assert.Equal((double)(float)w.Z, pdu.DeadReckoningParameters.EntityAngularVelocity.Z, 9);
            Assert.Equal(DrAlgorithms.Fpw, pdu.DeadReckoningParameters.DeadReckoningAlgorithm);
            Assert.Equal(0f, pdu.DeadReckoningParameters.EntityLinearAcceleration.X);

            // identity and damage
            Assert.Equal(DisTypeOfTkb[tkb], DisConversions.PackType(pdu.EntityType));
            Assert.Equal(marking, EntityMarkingText.Get(pdu.Marking));
            Assert.Equal(force, pdu.ForceId);
            int expectedDamage = health >= 100 ? 0 : health >= 50 ? 1 : health > 0 ? 2 : 3;
            Assert.Equal(expectedDamage, DisConversions.DamageOf(pdu.EntityAppearance));
            Assert.False(EntityAppearance.GetDeactivated(pdu.EntityAppearance));
            Assert.Equal(0, pdu.NumberOfVariableParameters);
            Assert.Equal(i, pdu.EntityID.EntityNumber); // numbers ascend while nothing was released
        }

        Assert.Equal(600, h.DisIds!.Count);
    }
}
