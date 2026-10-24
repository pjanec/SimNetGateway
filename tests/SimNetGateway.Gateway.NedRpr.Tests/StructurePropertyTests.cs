using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// Seeded random sequences of NED formation messages and RPR platform / aggregate messages (members added, removed, moved,
/// re-listed, units disposed) in random order. After every operation the pivot structure must equal what the last messages
/// said, the reverse reference index must equal a brute-force scan, NED must hold every RPR-origin entity exactly once (as an
/// individual or inside its unit's last formation), and the harness invariants (G-1, G-2, G-4, G-5, G-8 and the structure
/// invariant) hold after every tick.
/// </summary>
public class StructurePropertyTests
{
    private const int Platforms = 12;
    private static readonly ulong[] FormationIds = [7, 8];
    private static readonly uint[] AggregateHandles = [0x700, 0x701];

    private sealed class Model
    {
        public Dictionary<ulong, Dictionary<int, NedFormationMember>> Formations { get; } = new();
        public HashSet<int> AlivePlatforms { get; } = new();
        public Dictionary<int, int[]> AggregateLists { get; } = new(); // alive aggregate index -> last member numbers
        public Dictionary<int, string> AggregateNames { get; } = new();
    }

    public static IEnumerable<object[]> Seeds => Enumerable.Range(1, 40).Select(seed => new object[] { seed });

    private static int PoolStart(int aggregate) => aggregate == 0 ? 1 : 7;

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_sequences_keep_the_structure_consistent_with_the_last_messages(int seed)
    {
        var random = new Random(seed);
        var h = new GatewayHarness(new GatewayConfiguration("property", new NedRprProfile(), new NedRprSettings { SplitFireAssembly = true }));
        var model = new Model();
        for (int op = 0; op < 160; op++)
        {
            Apply(h, model, random, op);
            h.Step();
            while (random.Next(3) == 0)
            {
                h.Step();
            }

            Verify(h, model, $"seed {seed}, operation {op}");
        }

        h.Steps(4);
        Verify(h, model, $"seed {seed}, end");
        VerifyRprSide(h, model, $"seed {seed}, end");
    }

    // ---- the operations -----------------------------------------------------------------------------------------------

    private static void Apply(GatewayHarness h, Model m, Random random, int op)
    {
        switch (random.Next(8))
        {
            case 0:
            case 1:
            {
                ulong id = FormationIds[random.Next(FormationIds.Length)];
                var members = new Dictionary<int, NedFormationMember>();
                for (int index = 0; index < 5; index++)
                {
                    if (random.Next(10) < 6)
                    {
                        members[index] = S.Member(index, tkb: 100 + random.Next(2), damage: random.Next(4),
                            lat: 50.0 + random.Next(100) * 1e-5, speed: random.Next(2) * 10.0);
                    }
                }

                m.Formations[id] = members;
                h.Ned(S.Formation(id, "F" + id, members.Values.OrderBy(x => random.Next()).ToArray()));
                break;
            }

            case 2:
            {
                ulong id = FormationIds[random.Next(FormationIds.Length)];
                if (m.Formations.Remove(id))
                {
                    h.Ned(new NedEntityDisposed(id));
                }

                break;
            }

            case 3:
            case 4:
            {
                int n = 1 + random.Next(Platforms);
                m.AlivePlatforms.Add(n);
                h.Rpr(S.Platform(n, lonOffset: random.Next(50) * 1e-5, speed: random.Next(2) * 5.0));
                break;
            }

            case 5:
            {
                int n = 1 + random.Next(Platforms);
                if (m.AlivePlatforms.Remove(n))
                {
                    h.Rpr(new RprObjectRemoved(S.PlatformHandle(n)));
                }

                break;
            }

            case 6:
            {
                int a = random.Next(2);
                var pool = Enumerable.Range(PoolStart(a), Platforms / 2).OrderBy(x => random.Next()).Take(random.Next(1, 5)).ToArray();
                m.AggregateLists[a] = pool;
                h.Rpr(S.Aggregate(AggregateHandles[a], "Pack" + a, pool));
                break;
            }

            default:
            {
                int a = random.Next(2);
                if (m.AggregateLists.Remove(a))
                {
                    h.Rpr(new RprObjectRemoved(new RprObjectHandle(AggregateHandles[a])));
                }

                break;
            }
        }
    }

    // ---- the oracle ---------------------------------------------------------------------------------------------------

    private static void Verify(GatewayHarness h, Model m, string where)
    {
        var s = h.Schema;

        // NED formations: the last message is the truth
        foreach (ulong id in FormationIds)
        {
            if (!m.Formations.TryGetValue(id, out var expected))
            {
                Assert.False(h.NedIds.TryResolve(id, out _), $"{where}: formation {id} was disposed but still exists");
                continue;
            }

            var unit = S.NedUnit(h, id);
            var byIndex = S.MembersOf(h, unit);
            Assert.True(expected.Keys.OrderBy(x => x).SequenceEqual(byIndex.Select(e => (int)e.Get(s.MemberIndex).AsInt())),
                $"{where}: members of formation {id} differ from the last message");
            Assert.Equal(byIndex.Select(e => e.Id), S.MembersByScan(h, unit).Select(e => e.Id));
            foreach (var member in byIndex)
            {
                int index = (int)member.Get(s.MemberIndex).AsInt();
                var wire = expected[index];
                Assert.Equal(unit.Id, member.Get(s.Parent).AsEntity());
                Assert.Same(member, S.NedMember(h, id, index));
                Assert.Equal(wire.TkbType, member.Get(s.TypeTkb).AsInt());
                Assert.Equal(wire.Damage, member.Get(s.Damage).AsInt());
                Assert.Equal($"F{id}-{index}", member.Get(s.Name).AsString());
            }
        }

        int nedMembers = m.Formations.Values.Sum(f => f.Count);

        // RPR aggregates: structure and NED's view
        int rprMembersInFormations = 0;
        for (int a = 0; a < 2; a++)
        {
            var handle = new RprObjectHandle(AggregateHandles[a]);
            if (!m.AggregateLists.TryGetValue(a, out var list))
            {
                Assert.False(h.RprHandles.TryResolve(handle, out _), $"{where}: aggregate {a} was removed but still exists");
                continue;
            }

            var unit = S.RprEntity(h, AggregateHandles[a]);
            Assert.True(list.SequenceEqual(unit.Get(s.AggregateMemberIds).AsObject<DisEntityId[]>().Select(d => (int)d.Entity)),
                $"{where}: member list of aggregate {a}");
            var linked = S.MembersOf(h, unit);
            Assert.Equal(linked.Select(e => e.Id), S.MembersByScan(h, unit).Select(e => e.Id));
            var expectedLinked = list.Select((number, position) => (number, position))
                .Where(x => m.AlivePlatforms.Contains(x.number))
                .ToList();
            Assert.True(expectedLinked.Select(x => x.position).SequenceEqual(linked.Select(e => (int)e.Get(s.MemberIndex).AsInt())),
                $"{where}: linked members of aggregate {a}: expected positions " +
                $"{string.Join(",", expectedLinked.Select(x => x.position))}, got {string.Join(",", linked.Select(e => e.Get(s.MemberIndex).AsInt()))}");
            for (int i = 0; i < linked.Count; i++)
            {
                Assert.Same(S.RprEntity(h, (uint)(0x600 + expectedLinked[i].number)), linked[i]);
            }

            // what NED was last told about the unit
            if (h.NedIds.TryGetKey(unit.Id, out ulong unitNed))
            {
                var last = h.NedSent<NedFormation>().LastOrDefault(f => f.Id == unitNed);
                Assert.NotNull(last);
                Assert.True(expectedLinked.Select(x => x.position).SequenceEqual(last!.Members.Select(x => x.Index)),
                    $"{where}: the last NedFormation of aggregate {a} does not list its linked members");
                Assert.Equal("Pack" + a, last.Name);
                foreach (var member in linked)
                {
                    Assert.True(h.Host.NedMembers.TryResolve(new NedMemberKey(unitNed, (int)member.Get(s.MemberIndex).AsInt()), out var e) &&
                                e == member.Id, $"{where}: ned.member of a linked member");
                }

                rprMembersInFormations += linked.Count;
            }
            else
            {
                Assert.True(linked.Count == 0, $"{where}: aggregate {a} has linked members but NED never got its formation");
            }
        }

        // every RPR platform is in NED exactly once: as an individual, or as a member of its unit
        foreach (int n in m.AlivePlatforms)
        {
            var platform = S.RprEntity(h, (uint)(0x600 + n));
            bool member = platform.Has(s.Parent);
            Assert.Equal(!member, h.NedIds.Contains(platform.Id));
            Assert.Equal(member, h.Host.NedMembers.Contains(platform.Id));
        }

        Assert.Equal(nedMembers + rprMembersInFormations, h.Host.NedMembers.Count);

        // brute force reverse index over everything
        foreach (var entity in h.Engine.State.Entities)
        {
            var viaIndex = S.MembersOf(h, entity);
            var viaScan = S.MembersByScan(h, entity);
            Assert.Equal(viaScan.Select(e => e.Id), viaIndex.Select(e => e.Id));
        }
    }

    private static void VerifyRprSide(GatewayHarness h, Model m, string where)
    {
        foreach (ulong id in FormationIds)
        {
            if (!m.Formations.ContainsKey(id))
            {
                continue;
            }

            foreach (var member in S.MembersOf(h, S.NedUnit(h, id)))
            {
                Assert.True(h.RprHandles.Contains(member.Id), $"{where}: member of formation {id} has no RPR handle");
                Assert.True(h.RprEntityIds.Contains(member.Id));
            }
        }

        Assert.All(h.RprSent<RprRegisterObject>().Select(r => r.Token).GroupBy(t => t), g => Assert.Single(g));
    }
}
