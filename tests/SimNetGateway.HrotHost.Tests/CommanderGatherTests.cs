using System.Numerics;
using Fdp.Core;
using Fdp.Core.CommandHierarchy;
using Fdp.Toolkit.Combat.Components;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>T-052 (DD-024 D-5): a body-less commander (HROT's <c>UnitEntity</c>) is published as an entity at its centroid with the unit DIS kind, and no body topic is gathered for it.</summary>
public class CommanderGatherTests
{
    private const long ManType = 300, UnitType = 400;
    private const int UnitKind = 11;

    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);
    }

    private static List<object> Gather(PackHarness h, double simTime)
    {
        var inbound = new InMemoryInbound(new SourceId(99), "test");
        h.Adapter.GatherEntities(h.World, inbound, 1000 + simTime, simTime);
        var sink = new Capture();
        inbound.Drain(sink, int.MaxValue);
        return sink.All;
    }

    [Fact]
    public void A_commander_is_an_entity_of_the_unit_kind_at_its_centroid_and_gets_no_body_topic()
    {
        using var h = new PackHarness(dis: true);
        h.RegisterTemplate(ManType, 3);
        h.RegisterTemplate(UnitType, UnitKind);
        h.World.RegisterComponent<UnitEntity>();
        h.Steps(3);
        var commander = h.AddHrotEntity(2001, new Vector3(12, 34, 5), "Alpha squad", tkb: UnitType);
        h.World.RemoveComponent<Health>(commander);   // a unit has no health
        h.World.AddComponent(commander, new UnitEntity { CommandDisruptionSeconds = 6f });

        var messages = Gather(h, 1.0);

        Assert.All(messages, m => Assert.True(m is HrotEntityState or HrotTypeRecord, "no person, appearance, vehicle look or animal for a commander, found " + m.GetType().Name));   // the state and the type's record only
        var state = Assert.Single(messages.OfType<HrotEntityState>());
        Assert.Equal(2001, state.Id);
        Assert.Equal(UnitKind, DisTypeCode.Unpack(state.DisType).Kind);
        Assert.Equal(new HrotVec3(12, 34, 5), state.Position);   // the unit's own SimTransform: its owner places it at its live members' centroid
    }
}
