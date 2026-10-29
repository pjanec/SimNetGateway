using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// Seeded random traffic from both sides, every step checked by the <see cref="InvariantChecker"/> (G-1, G-2, G-4, G-5, G-7, G-8, quiet
/// ticks send nothing) and after every operation against a model of what HROT must know: an eligible DIS entity is a replica
/// exactly while the origin is valid and the session is open (not Idle, not Replay).
/// </summary>
public class RandomizedTests
{
    public static TheoryData<int, bool> Seeds() => new()
    {
        { 1, false }, { 2, false }, { 3, true }, { 4, true }, { 5, false }, { 6, true },
    };

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_traffic_keeps_every_invariant(int seed, bool disControlsHrot)
    {
        var random = new Random(seed);
        var config = GatewayConfiguration.CreateDefault() with { Dis = new DisConfiguration { ExerciseId = 1, DisControlsHrot = disControlsHrot } };
        var h = new HrotDisHarness(config, idLast: 5_000_050);
        bool originValid = false;
        int generation = 0;
        var disEntities = new List<EntityId>();
        var hrotIds = new HashSet<long>();
        var sessionRows = SessionTests.Table;

        for (int op = 0; op < 1500; op++)
        {
            Prune(h, disEntities, hrotIds);
            switch (random.Next(17))
            {
                case 0:
                case 1:
                case 2:
                case 3:
                    long id = 1000 + random.Next(30);
                    h.Hrot(HrotDisHarness.State(id, RandomPoint(random), tkb: random.Next(4) == 0 ? 9999 : 100, rotation: ServiceTests.RandomQuaternion(random),
                        velocity: new HrotVec3(random.NextDouble() * 20, 0, 0), health: random.NextDouble() * 110, simTime: op * 0.1,
                        commander: random.Next(3) == 0 ? 1000 + random.Next(30) : 0));
                    hrotIds.Add(id);
                    break;
                case 4:
                    h.Hrot(new HrotEntityRemoved(1000 + random.Next(30)));
                    break;
                case 5:
                case 6:
                    if (originValid && disEntities.Count < 40)
                    {
                        disEntities.Add(h.DisCreate(RandomDis(h, random)));
                    }

                    break;
                case 7:
                case 8:
                case 9:
                    if (disEntities.Count > 0 && originValid)
                    {
                        h.DisUpdate(disEntities[random.Next(disEntities.Count)], RandomDis(h, random));
                    }

                    break;
                case 10:
                    if (disEntities.Count > 0)
                    {
                        int index = random.Next(disEntities.Count);
                        h.DisRemove(disEntities[index]);
                        disEntities.RemoveAt(index);
                    }

                    break;
                case 11:
                    if (random.Next(3) == 0 || !originValid)
                    {
                        generation++;
                        h.SendOrigin(random.NextDouble() * 160 - 80, random.NextDouble() * 360 - 180, random.NextDouble() * 1000, generation);
                        originValid = true;
                    }

                    break;
                case 12:
                case 13:
                    var row = sessionRows[random.Next(sessionRows.Length)];
                    h.Hrot(new HrotSession(row.Phase, row.Advancing, 1.0, op));
                    break;
                case 14:
                    h.Hrot(new HrotFire(1000 + random.Next(30), 1000 + random.Next(30), random.Next(3) == 0 ? 502 : 501, 0, RandomPoint(random), RandomPoint(random), 1));
                    if (random.Next(2) == 0)
                    {
                        h.Hrot(new HrotDetonation(1000 + random.Next(30), 1000 + random.Next(30), 501, 0, RandomPoint(random), default));
                    }

                    break;
                case 15:
                    if (disEntities.Count > 0 && originValid)
                    {
                        var shooter = disEntities[random.Next(disEntities.Count)];
                        var target = random.Next(2) == 0 && disEntities.Count > 1 ? disEntities[random.Next(disEntities.Count)] : (EntityId?)null;
                        PublishDisEvent(h, random, shooter, target);
                    }

                    break;
                default:
                    h.Hrot(new HrotTurretPose(1000 + random.Next(30), random.Next(3), random.NextDouble(), random.NextDouble()));
                    if (disControlsHrot && random.Next(3) == 0)
                    {
                        h.Engine.Execute(random.Next(2) == 0 ? SessionCommand.Pause : SessionCommand.Resume, h.DisSource);
                    }

                    break;
            }

            h.Step();
            h.Steps(random.Next(3));
            AssertModel(h, originValid);
        }

        // unload everything (an Idle session that never loaded keeps its entities, so load first): nothing may be left anywhere
        h.Session(ClusterPhase.Live);
        h.Session(ClusterPhase.Idle);
        Assert.Equal(0, h.Engine.State.Count);
        Assert.Equal(0UL, h.Ids.InUse);
        Assert.Empty(h.Checker.Introduced);
        Assert.Equal(0, h.HrotIds.Count);
        Assert.Equal(0, h.Engine.Diagnostics.RuleFailures);
    }

    private static HrotVec3 RandomPoint(Random random) =>
        new(random.NextDouble() * 4000 - 2000, random.NextDouble() * 4000 - 2000, random.NextDouble() * 100);

    private static DisState RandomDis(HrotDisHarness h, Random random) =>
        h.DisStateAt(RandomPoint(random), new HrotVec3(random.NextDouble() * 10, random.NextDouble() * 10, 0), damage: random.Next(4)) with
        {
            Marking = "D" + random.Next(100),
            Force = random.Next(4),
            DrAlgorithm = random.Next(1, 10),
            Deactivated = random.Next(20) == 0,
        };

    private static void PublishDisEvent(HrotDisHarness h, Random random, EntityId shooter, EntityId? target)
    {
        bool fire = random.Next(2) == 0;
        h.DisEvent(fire ? h.Schema.WeaponFire : h.Schema.MunitionDetonation, bag =>
        {
            bag.Set(h.Schema.EventId, AttrValue.FromObject(new DisEventId(1, 3002, (ushort)random.Next(1, 100))));
            bag.Set(h.Schema.EventFiring, AttrValue.From(shooter));
            if (target is { } t)
            {
                bag.Set(h.Schema.EventTarget, AttrValue.From(t));
            }

            bag.Set(h.Schema.EventMunitionType, AttrValue.From(0L));
            bag.Set(h.Schema.EventLocation, AttrValue.From(h.Frame!.PointToEcef(Support.V(RandomPoint(random)))));
            bag.Set(h.Schema.EventVelocity, AttrValue.From(Vec3.Zero));
        });
    }

    /// <summary>Forget the entities the engine no longer has (an unload or a reaper destroys them behind the test's back).</summary>
    private static void Prune(HrotDisHarness h, List<EntityId> disEntities, HashSet<long> hrotIds)
    {
        disEntities.RemoveAll(id => h.Engine.State.Find(id) is null);
        hrotIds.RemoveWhere(id => h.EntityOfHrot(id) is null);
    }

    /// <summary>A DIS entity with type, position and state time is a replica exactly while the origin is valid and the session is open.</summary>
    private static void AssertModel(HrotDisHarness h, bool originValid)
    {
        var session = h.Engine.Session;
        bool open = originValid && session.State != SessionState.Idle && session.Mode == SessionMode.Live;
        var s = h.Schema;
        int expected = 0;
        foreach (var entity in h.Entities(h.DisSource))
        {
            bool eligible = entity.Has(s.EntityType) && entity.Has(s.Position) && entity.Has(s.StateTime);
            bool shown = h.HrotIds.TryGetKey(entity.Id, out long key) && h.Checker.Introduced.Contains(key);
            Assert.True(shown == (open && eligible),
                $"entity {entity.Id.Value}: shown={shown}, open={open}, eligible={eligible}, session {session.State}/{session.Mode}");
            if (shown)
            {
                expected++;
            }
        }

        Assert.Equal(expected, h.Checker.Introduced.Count);
        Assert.Equal((ulong)expected, h.Ids.InUse);
    }
}
