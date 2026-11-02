using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using static SimNetGateway.Gateway.HrotDis.Tests.RprMessages;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>HROT entity ids and the node id are 32-bit (T-030): the largest ids pass every path, the allocator stays inside its range, 0 is invalid.</summary>
public class HrotIdWidthTests
{
    [Fact]
    public void The_range_allocator_rejects_a_first_below_one_and_an_empty_range()
    {
        Assert.Throws<ArgumentException>(() => new HrotIdRangeAllocator(0, 10));
        Assert.Throws<ArgumentException>(() => new HrotIdRangeAllocator(-5, 10));
        Assert.Throws<ArgumentException>(() => new HrotIdRangeAllocator(11, 10));
        Assert.Equal(1UL, new HrotIdRangeAllocator(1, 1).Available);
    }

    [Fact]
    public void The_allocator_near_int_max_exhausts_cleanly_without_wrapping_to_a_negative_id()
    {
        var allocator = new HrotIdRangeAllocator(int.MaxValue - 2, int.MaxValue);
        var got = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(allocator.TryAllocate(out int id));
            got.Add(id);
        }

        Assert.Equal([int.MaxValue - 2, int.MaxValue - 1, int.MaxValue], got.Order().ToArray());
        Assert.False(allocator.TryAllocate(out int none));
        Assert.Equal(0UL, allocator.Available);
        Assert.False(allocator.TryAllocate(out _));
        allocator.Release(got[1]);
        Assert.True(allocator.TryAllocate(out int again));
        Assert.Equal(got[1], again);
        Assert.All(got, id => Assert.True(id > 0));
    }

    [Fact]
    public void Seeded_random_allocate_and_release_never_leave_the_range_and_never_duplicate()
    {
        for (int seed = 1; seed <= 100; seed++)
        {
            var r = new Random(seed);
            int first = r.Next(1, 3) == 1 ? r.Next(1, 50) : int.MaxValue - r.Next(1, 40);
            int last = first + r.Next(0, Math.Min(40, int.MaxValue - first) + 1);
            var allocator = new HrotIdRangeAllocator(first, last);
            var held = new HashSet<int>();
            for (int step = 0; step < 300; step++)
            {
                if (r.Next(3) != 0)
                {
                    bool ok = allocator.TryAllocate(out int id);
                    Assert.Equal((long)held.Count < (long)last - first + 1, ok);
                    if (ok)
                    {
                        Assert.InRange(id, first, last);
                        Assert.True(held.Add(id), $"seed {seed}: {id} issued twice");
                    }
                }
                else if (held.Count > 0)
                {
                    int id = held.ElementAt(r.Next(held.Count));
                    held.Remove(id);
                    allocator.Release(id);
                }

                Assert.Equal((ulong)held.Count, allocator.InUse);
            }
        }
    }

    [Fact]
    public void A_replica_gets_the_largest_ids_and_a_hrot_event_can_name_it()
    {
        var h = new HrotDisHarness(idFirst: int.MaxValue - 1, idLast: int.MaxValue);
        h.GoLive();
        var a = h.DisCreate(h.DisStateAt(new HrotVec3(100, 0, 0)));
        var b = h.DisCreate(h.DisStateAt(new HrotVec3(200, 0, 0)));
        var ids = h.Sent<ReplicaState>().Select(s => s.Id).Order().ToArray();
        Assert.Equal([int.MaxValue - 1, int.MaxValue], ids);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(0, 0, 0)));
        h.HrotIds.TryGetKey(b, out int idB);
        h.HrotStep(new HrotFire(1, idB, 501, 0, default, default, 1));
        Assert.Equal(b, Support.LastEvent(h, h.Schema.WeaponFire).Get(h.Schema.EventTarget).AsEntity());
        h.DisRemove(a);
        h.DisRemove(b);
        h.Steps(2);
        Assert.Equal(2, h.Sent<ReplicaRemoved>().Count);
    }

    [Fact]
    public void Hrot_entities_with_the_largest_ids_reach_dis_and_hla_under_one_entity_number()
    {
        var h = new HrotDisHlaHost();
        h.GoLive();
        h.HrotStep(HrotDisHarness.State(int.MaxValue, new HrotVec3(100, 0, 0), tkb: 100, marking: "MAX"));
        h.HrotStep(HrotDisHarness.State(int.MaxValue - 1, new HrotVec3(200, 0, 0), tkb: 100, marking: "MAX-1"));
        h.Steps(4);

        Assert.Equal(2, h.Peer.Objects.Count());
        var numbers = new List<ushort>();
        foreach (var marking in new[] { "MAX", "MAX-1" })
        {
            var pdu = h.DisOut.Messages.Select(m => m.Message).OfType<EntityStatePdu>()
                .First(p => Protocol.Dis7.EntityMarkingText.Get(p.Marking) == marking);
            var key = DisConversions.KeyOf(pdu.EntityID);
            var obj = h.Peer.Objects.Single(o => o.Marking!.Value.AsciiText == marking);
            numbers.Add(key.Number);
            Assert.Equal(new EntityIdentifierStruct(new FederateIdentifierStruct(key.Site, key.Application), key.Number), obj.EntityIdentifier);
        }

        Assert.Equal(2, numbers.Distinct().Count());
    }

    [Fact]
    public void A_federate_entity_becomes_a_replica_with_the_largest_id()
    {
        var h = new HrotDisHlaHost(hrotIds: new HrotIdRangeAllocator(int.MaxValue - 1, int.MaxValue));
        h.GoLive();
        h.Peer.Inject(Update(h.Frame!, Peer(1), new Vec3(-300, 0, 0)));
        h.Steps(3);
        var replica = Assert.Single(h.HrotOut.OfType<ReplicaState>());
        Assert.InRange(replica.Id, int.MaxValue - 1, int.MaxValue);
        h.HrotStep(new HrotEntityRemoved(replica.Id)); // an echo of a replica: ignored
        Assert.True(h.HrotIds.TryResolve(replica.Id, out _));
    }

    [Fact]
    public void A_node_id_above_int_max_in_the_file_is_exactly_one_error_naming_the_key()
    {
        var result = GatewayConfigurationFile.Parse("""{ "hrot": { "replicaOwnerNodeId": 2147483648 } }""", "test.json");
        Assert.Equal(ConfigurationLoadKind.Invalid, result.Kind);
        var error = Assert.Single(result.Errors);
        Assert.Contains("hrot.replicaOwnerNodeId", error);
        var max = GatewayConfigurationFile.Parse("""{ "hrot": { "replicaOwnerNodeId": 2147483647 } }""", "test.json");
        Assert.Equal(int.MaxValue, max.Configuration!.Hrot.ReplicaOwnerNodeId);
    }
}
