using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>What the profile registers for the DIS side (H2b): one small module per PDU kind and concern, the identity map, the processor, and nothing at all without a DIS side.</summary>
public class DisProfileTests
{
    [Fact]
    public void The_dis_side_registers_one_small_module_per_pdu_kind_and_concern()
    {
        var h = new HrotDisHarness();
        var modules = h.Engine.Rules.Select(r => r.Module).Distinct().ToList();
        string[] expectedRuleModules =
        [
            nameof(DisEntityStateEgress), nameof(DisHeartbeatEgress), nameof(DisFireEgress), nameof(DisDetonationEgress),
            nameof(DisSessionEgress), "DisStaleReaper",
        ];
        foreach (var name in expectedRuleModules)
        {
            Assert.Contains(name, modules);
        }

        Assert.Equal(
            ["DisEntityStateEgress/Damage", "DisEntityStateEgress/Drift", "DisEntityStateEgress/Flush", "DisEntityStateEgress/Identity",
             "DisEntityStateEgress/Reconcile", "DisEntityStateEgress/Remove", "DisEntityStateEgress/Spatial"],
            h.Engine.Rules.Where(r => r.Module == nameof(DisEntityStateEgress)).Select(r => r.FullName).Order().ToArray());
        Assert.Equal(["DisHeartbeatEgress/Heartbeat"], h.Engine.Rules.Where(r => r.Module == nameof(DisHeartbeatEgress)).Select(r => r.FullName).ToArray());
        Assert.Equal(["DisFireEgress/Fire"], h.Engine.Rules.Where(r => r.Module == nameof(DisFireEgress)).Select(r => r.FullName).ToArray());
        Assert.Equal(["DisDetonationEgress/Detonation"], h.Engine.Rules.Where(r => r.Module == nameof(DisDetonationEgress)).Select(r => r.FullName).ToArray());
        Assert.Equal(["DisSessionEgress/Siman"], h.Engine.Rules.Where(r => r.Module == nameof(DisSessionEgress)).Select(r => r.FullName).ToArray());
        Assert.Equal(["DisStaleReaper/Reap"], h.Engine.Rules.Where(r => r.Module == "DisStaleReaper").Select(r => r.FullName).ToArray());
    }

    [Fact]
    public void The_gateway_builds_without_validation_errors_and_every_pdu_kind_has_a_handler()
    {
        var h = new HrotDisHarness();
        Assert.Empty(h.Binding.Validation.Errors);
        h.GoLive();
        h.DisStep(DisPdus.EntityState(1));
        h.DisStep(DisPdus.Fire(new DisEntityKey(5, 77, 1), null, default, default, 0));
        h.DisStep(DisPdus.Detonation(new DisEntityKey(5, 77, 1), null, default, default, default, 0));
        h.DisStep(DisPdus.RemoveEntity(new DisEntityKey(5, 77, 1)));
        h.DisStep(DisPdus.StartResume());
        h.DisStep(DisPdus.StopFreeze());
        Assert.Equal(0, h.Engine.Diagnostics.UnhandledMessages);
        // a kind the gateway does not translate is counted, not an error
        h.DisStep(DisPdus.ThroughTheWire(new CreateEntityPdu { ExerciseID = 1 }));
        Assert.Equal(1, h.Engine.Diagnostics.UnhandledMessages);
    }

    [Fact]
    public void The_dis_entity_map_exists_with_a_pool_allocator_only_when_the_dis_side_runs()
    {
        var h = new HrotDisHarness();
        var map = Assert.IsType<IdentityMap<DisEntityKey>>(h.Engine.Identities.Find("dis.entity"));
        var allocator = Assert.IsType<MappedAllocator<DisEntityKey>>(map.Allocator);
        var pool = Assert.IsType<NumberPool>(allocator.Numbers);
        Assert.Equal(65534, pool.Capacity);

        var small = new HrotDisHarness(numbering: new DisNumbering(10, 19, 5));
        var smallPool = (NumberPool)((MappedAllocator<DisEntityKey>)small.DisIds!.Allocator!).Numbers;
        Assert.Equal(10, smallPool.Capacity);

        var noDis = new HrotDisHarness(GatewayConfiguration.CreateDefault() with { Dis = null, Hla = new HlaConfiguration { Federation = "F" } });
        // the HLA side alone still needs the entity numbers: the one dis.entity pool serves both sides (T-024)
        Assert.NotNull(noDis.Engine.Identities.Find("dis.entity"));
        Assert.NotNull(noDis.DisIds);
        Assert.DoesNotContain(noDis.Engine.Rules, r => r.Module.StartsWith("Dis", StringComparison.Ordinal));
        Assert.False(noDis.Engine.Sources.TryGet("proc.reaper.dis", out _));
    }

    [Fact]
    public void The_numbering_is_validated()
    {
        Assert.Throws<ArgumentException>(() => new HrotDisProfile(null, new DisNumbering(0, 10)));
        Assert.Throws<ArgumentException>(() => new HrotDisProfile(null, new DisNumbering(1, 65535)));
        Assert.Throws<ArgumentException>(() => new HrotDisProfile(null, new DisNumbering(5, 4)));
        Assert.Throws<ArgumentException>(() => new HrotDisProfile(null, new DisNumbering(1, 10, -1)));
        _ = new HrotDisProfile(null, new DisNumbering(1, 65534, 0));
    }

    [Fact]
    public void Without_the_dis_side_the_hrot_half_runs_and_nothing_is_sent_to_dis()
    {
        var config = GatewayConfiguration.CreateDefault() with { Dis = null, Hla = new HlaConfiguration { Federation = "F" } };
        var h = new HrotDisHarness(config);
        h.GoLive();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 2, 3)));
        h.HrotStep(new HrotFire(1, 0, 501, 0, default, default, 1));
        h.Run(10);
        Assert.Null(h.DisOut);
        Assert.NotNull(h.EntityOfHrot(1));
    }

    [Fact]
    public void The_dis_maps_throw_when_the_side_does_not_run()
    {
        var maps = new HrotDisMaps(null!);
        Assert.False(maps.HasDis);
        Assert.Throws<InvalidOperationException>(() => maps.DisEntity);
    }
}
