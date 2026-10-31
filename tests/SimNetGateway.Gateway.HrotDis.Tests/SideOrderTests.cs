using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// The order invariant of the profile (DD-011 section 3a rule 1): for DIS only, HLA only and both, the ordered ports, sources,
/// identity maps and modules are exactly the lists recorded before the sides moved onto the <c>IProtocolSide</c> seam (T-026a).
/// </summary>
public class SideOrderTests
{
    private static readonly HlaConfiguration Hla = new() { Federation = "F" };

    private static (string[] In, string[] Out, string[] Sources, string[] Maps, string[] Modules) Record(GatewayConfiguration config)
    {
        var h = new HrotDisHarness(config);
        var sources = new List<string>();
        for (int i = 1; i <= h.Engine.Sources.Count; i++)
        {
            sources.Add(h.Engine.Sources.NameOf(new SourceId(i)) + "=" + i);
        }

        return (h.Binding.InboundPorts.Select(p => p.Name).ToArray(),
            h.Binding.OutboundPorts.Select(p => p.Name).ToArray(),
            sources.ToArray(),
            h.Engine.Identities.All.Select(m => m.Name).ToArray(),
            h.Engine.Rules.Select(r => r.Module).Distinct().ToArray());
    }

    [Fact]
    public void Both()
    {
        var r = Record(GatewayConfiguration.CreateDefault() with { Hla = Hla });
        Assert.Equal(
            new[] { "hrot", "dis", "rpr" },
            r.In);
        Assert.Equal(
            new[] { "dis", "rpr", "hrot" },
            r.Out);
        Assert.Equal(
            new[] { "engine=1", "hrot=2", "dis=3", "rpr=4", "proc.reaper.dis=5", "proc.reaper.rpr=6" },
            r.Sources);
        Assert.Equal(
            new[] { "hrot.entity", "dis.entity", "rpr.handle" },
            r.Maps);
        Assert.Equal(
            new[] { "ReplicaStateEgress", "ReplicaRemovedEgress", "InjectFireEgress", "InjectDetonationEgress", "TimeRequestEgress", "DisEntityStateEgress", "DisHeartbeatEgress", "DisFireEgress", "DisDetonationEgress", "DisSessionEgress", "DisStaleReaper", "RprUpdateRequestedResponder", "RprEntityEgress", "RprFireEgress", "RprDetonationEgress", "RprStaleReaper" },
            r.Modules);
    }

    [Fact]
    public void Dis_only()
    {
        var r = Record(GatewayConfiguration.CreateDefault());
        Assert.Equal(
            new[] { "hrot", "dis" },
            r.In);
        Assert.Equal(
            new[] { "dis", "hrot" },
            r.Out);
        Assert.Equal(
            new[] { "engine=1", "hrot=2", "dis=3", "proc.reaper.dis=4" },
            r.Sources);
        Assert.Equal(
            new[] { "hrot.entity", "dis.entity" },
            r.Maps);
        Assert.Equal(
            new[] { "ReplicaStateEgress", "ReplicaRemovedEgress", "InjectFireEgress", "InjectDetonationEgress", "TimeRequestEgress", "DisEntityStateEgress", "DisHeartbeatEgress", "DisFireEgress", "DisDetonationEgress", "DisSessionEgress", "DisStaleReaper" },
            r.Modules);
    }

    [Fact]
    public void Hla_only()
    {
        var r = Record(GatewayConfiguration.CreateDefault() with { Dis = null, Hla = Hla });
        Assert.Equal(
            new[] { "hrot", "rpr" },
            r.In);
        Assert.Equal(
            new[] { "rpr", "hrot" },
            r.Out);
        Assert.Equal(
            new[] { "engine=1", "hrot=2", "dis=3", "rpr=4", "proc.reaper.rpr=5" },
            r.Sources);
        Assert.Equal(
            new[] { "hrot.entity", "dis.entity", "rpr.handle" },
            r.Maps);
        Assert.Equal(
            new[] { "ReplicaStateEgress", "ReplicaRemovedEgress", "InjectFireEgress", "InjectDetonationEgress", "TimeRequestEgress", "RprUpdateRequestedResponder", "RprEntityEgress", "RprFireEgress", "RprDetonationEgress", "RprStaleReaper" },
            r.Modules);
    }
}
