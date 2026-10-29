using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>Seeded randomised properties of the conversions: HROT state -> pivot -> replica state of a DIS-origin copy.</summary>
public class ConversionPropertyTests
{
    [Fact]
    public void A_random_hrot_state_survives_pivot_and_replica_conversion_at_any_origin()
    {
        const int Samples = 800;
        var random = new Random(20261029);
        var h = new HrotDisHarness();
        h.Session(ClusterPhase.Live);
        EntityId? copy = null;
        var mapping = new DamageMapping(h.Config.Shared);

        for (int i = 0; i < Samples; i++)
        {
            double lat = random.NextDouble() * 160.0 - 80.0;
            double lon = random.NextDouble() * 360.0 - 180.0;
            double alt = random.NextDouble() * 3000.0;
            h.Origin(lat, lon, alt, generation: i + 1);

            var position = new HrotVec3(random.NextDouble() * 2e4 - 1e4, random.NextDouble() * 2e4 - 1e4, random.NextDouble() * 1e3 - 500);
            var rotation = ServiceTests.RandomQuaternion(random);
            var velocity = new HrotVec3(random.NextDouble() * 120 - 60, random.NextDouble() * 120 - 60, random.NextDouble() * 20 - 10);
            var omega = new HrotVec3(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
            double health = random.NextDouble() * 120.0;
            double max = 100.0;
            h.HrotStep(HrotDisHarness.State(1, position, rotation: rotation, velocity: velocity, angularVelocity: omega,
                health: health, healthMax: max, simTime: i * 0.5, force: random.Next(0, 4), marking: "M" + i));

            // the pivot, against an independent computation from Common
            var frame = h.Frame!;
            var e = h.EntityOfHrot(1)!;
            var q = new Quaternion(rotation.W, rotation.X, rotation.Y, rotation.Z);
            Support.Near(frame.PointToEcef(Support.V(position)), e.Get(h.Schema.Position).AsVec3(), 1e-6, $"sample {i} pivot position");
            Support.Near(frame.VectorToEcef(Support.V(velocity)), e.Get(h.Schema.Velocity).AsVec3(), 1e-9, $"sample {i} pivot velocity");
            Support.Near(LocalToGeocentricOrientation.ToDisEuler(q, frame), e.Get(h.Schema.Orientation).AsVec3(), 1e-12, $"sample {i} pivot orientation");

            // a DIS-origin copy of it
            var state = new DisState
            {
                EntityType = e.Get(h.Schema.EntityType).AsInt(),
                Marking = e.Get(h.Schema.Marking).AsString(),
                Force = (int)e.Get(h.Schema.Force).AsInt(),
                Position = e.Get(h.Schema.Position).AsVec3(),
                Orientation = e.Get(h.Schema.Orientation).AsVec3(),
                Velocity = e.Get(h.Schema.Velocity).AsVec3(),
                Acceleration = e.Get(h.Schema.Acceleration).AsVec3(),
                AngularVelocity = e.Get(h.Schema.AngularVelocity).AsVec3(),
                DrAlgorithm = (int)e.Get(h.Schema.DrAlgorithm).AsInt(),
                StateTime = e.Get(h.Schema.StateTime).AsDouble() + 1000.0,
                Damage = (int)e.Get(h.Schema.Damage).AsInt(),
            };
            if (copy is null)
            {
                copy = h.DisCreate(state);
            }
            else
            {
                h.DisUpdate(copy.Value, state);
            }

            h.Step();
            var replica = h.Sent<ReplicaState>().Last();
            Support.Near(position, replica.State.Position, 1e-6, $"sample {i} replica position");
            Support.SameRotation(rotation, replica.State.Rotation, 1e-9, $"sample {i} replica orientation");
            Support.Near(velocity, replica.State.Velocity, 1e-9, $"sample {i} replica velocity");
            Support.Near(omega, replica.State.AngularVelocity, 1e-9, $"sample {i} replica angular velocity");
            Support.Near(HrotVec3.Zero, replica.State.Acceleration, 1e-12, $"sample {i} replica acceleration");
            Assert.Equal(DrAlgorithms.Fpw, replica.State.DrAlgorithm);
            Assert.Equal(i * 0.5 + 1000.0, replica.State.StateTime);
            Assert.Equal("M" + i, replica.Marking);
            Assert.Equal(e.Get(h.Schema.Force).AsInt(), replica.Force);
            Assert.Equal(e.Get(h.Schema.EntityType).AsInt(), replica.DisType);
            Assert.Equal(mapping.HealthOf(mapping.DamageOf(health, max)), replica.HealthCurrent);
            Assert.Equal(max, replica.HealthMax);
            Assert.True(replica.State.Rotation.W >= 0.0);
            Assert.InRange(Math.Sqrt(Math.Pow(replica.State.Rotation.X, 2) + Math.Pow(replica.State.Rotation.Y, 2) + Math.Pow(replica.State.Rotation.Z, 2) + Math.Pow(replica.State.Rotation.W, 2)), 1 - 1e-12, 1 + 1e-12);
        }
    }

    [Fact]
    public void Health_and_damage_are_monotonic_through_the_whole_gateway()
    {
        var random = new Random(77);
        for (int round = 0; round < 40; round++)
        {
            double moderate = random.NextDouble() * 0.8;
            double slight = moderate + 0.02 + random.NextDouble() * (0.97 - moderate);
            var shared = new SharedConfiguration { EntityMaxHealth = 10 + random.NextDouble() * 500, DamageThresholds = new DamageThresholds { Slight = slight, Moderate = moderate } };
            var config = GatewayConfiguration.CreateDefault() with { Shared = shared };
            var h = new HrotDisHarness(config);
            h.GoLive();

            // HROT health -> pivot damage: more health never means more damage
            int previous = DamageMapping.Destroyed;
            for (int step = 0; step <= 60; step++)
            {
                double health = shared.EntityMaxHealth * (step / 50.0); // up to 120 %
                h.HrotStep(HrotDisHarness.State(1, health: health, healthMax: shared.EntityMaxHealth));
                int damage = (int)h.EntityOfHrot(1)!.Get(h.Schema.Damage).AsInt();
                Assert.True(damage <= previous, $"damage rose with health at {health}");
                previous = damage;
                double fraction = health / shared.EntityMaxHealth;
                int expected = fraction >= 1.0 ? 0 : fraction >= slight ? 1 : fraction > moderate ? 2 : 3;
                Assert.Equal(expected, damage);
            }

            // pivot damage -> replica health: more damage means less health, and the damage reads back
            var e = h.DisCreate(h.DisStateAt(new HrotVec3(0, 0, 0), damage: 0));
            double last = double.MaxValue;
            for (int damage = 0; damage <= 3; damage++)
            {
                if (damage > 0)
                {
                    h.DisUpdate(e, h.DisStateAt(new HrotVec3(0, 0, 0), damage: damage));
                }

                var s = h.Sent<ReplicaState>().Last();
                Assert.True(damage == 0 || s.HealthCurrent < last);
                last = s.HealthCurrent;
                Assert.Equal(damage, new DamageMapping(shared).DamageOf(s.HealthCurrent, s.HealthMax));
            }

            Assert.Equal(0.0, last);
        }
    }

    [Fact]
    public void Nothing_positional_leaves_before_a_valid_origin_and_a_new_generation_changes_only_the_enu_side()
    {
        var random = new Random(5);
        var h = new HrotDisHarness();
        h.Session(ClusterPhase.Live);
        // DIS entities in ECEF, no origin yet
        var ecef = new List<Vec3>();
        var ids = new List<EntityId>();
        for (int i = 0; i < 10; i++)
        {
            var p = new Vec3(4_000_000 + random.NextDouble() * 1e5, 3_000_000 + random.NextDouble() * 1e5, 3_500_000 + random.NextDouble() * 1e5);
            ecef.Add(p);
            var changes = h.Engine.BeginChange(h.DisSource);
            var id = changes.CreateEntity();
            changes.Set(id, h.Schema.EntityType, AttrValue.From(new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack()));
            changes.Set(id, h.Schema.Position, AttrValue.From(p));
            changes.Set(id, h.Schema.StateTime, AttrValue.From(1.0 + i));
            changes.Commit();
            ids.Add(id);
            h.HrotStep(HrotDisHarness.State(100 + i, new HrotVec3(i, i, 0)));
        }

        h.Run(0.5);
        Assert.Empty(h.Sent<ReplicaState>());
        Assert.All(h.Entities(h.HrotSource), e => Assert.False(e.Has(h.Schema.Position)));

        // the first origin shows everything; every later generation re-bases the ENU side only
        for (int generation = 1; generation <= 12; generation++)
        {
            h.Origin(random.NextDouble() * 160 - 80, random.NextDouble() * 360 - 180, random.NextDouble() * 500, generation);
            var latest = h.Sent<ReplicaState>().TakeLast(10).ToList();
            Assert.Equal(10, latest.Count);
            Assert.Equal(10, latest.Select(s => s.Id).Distinct().Count());
            foreach (var s in latest)
            {
                int index = (int)(s.State.StateTime - 1.0 + 0.5);
                Support.Near(h.Frame!.PointFromEcef(ecef[index]), s.State.Position, 1e-6, "re-based ENU position");
                Support.Near(ecef[index], h.Engine.State.Find(ids[index])!.Get(h.Schema.Position).AsVec3(), 0.0, "ECEF unchanged");
            }
        }
    }
}
