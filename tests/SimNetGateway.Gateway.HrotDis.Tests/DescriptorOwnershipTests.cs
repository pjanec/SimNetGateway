using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>Per-descriptor ownership of HROT entities by a side defined outside the gateway (DD-012 sections 4a and 6.1), with the test HROT model.</summary>
public class DescriptorOwnershipTests
{
    private static HrotDisHarness Live(out ExtSide side, out InMemoryInbound ext)
    {
        side = new ExtSide();
        var h = ExternalSideTests.Build(side, out ext);
        h.GoLive();
        return h;
    }

    private static void Ext<T>(HrotDisHarness h, InMemoryInbound ext, T message)
    {
        ext.Enqueue(in message);
        h.Hrot(new HrotSession(ClusterPhase.Live, true, 1, 0)); // a stimulus for the quiet-tick check of the harness
        h.Step();
    }

    [Fact]
    public void The_sequence_of_section_6_1_ext_takes_the_damage_of_a_hrot_entity_and_gives_it_back()
    {
        var h = Live(out var side, out var ext);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 0, 0), marking: "LION-1", health: 100, healthMax: 100));
        h.Steps(2);
        var entity = h.EntityOfHrot(1)!;

        Ext(h, ext, new ExtTransfer(1, "Damage", true));
        Assert.Equal([new HrotDescriptorOwnership(1, HrotDescriptor.Damage)], h.HrotModel.Received);
        Assert.Equal(h.Engine.Sources.Register("ext"), h.Schema.Groups.OwnerOf(h.EntityOfHrot(1)!, h.Schema.DamageGroup));

        // HROT keeps reporting everything: Spatial and Info still update, Damage does not, and the state is not rejected
        long failures = h.Engine.Diagnostics.TransactionFailures;
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(150, 0, 0), marking: "LION-X", health: 10, healthMax: 100));
        Assert.Equal(failures, h.Engine.Diagnostics.TransactionFailures);
        entity = h.EntityOfHrot(1)!;
        Assert.Equal("LION-X", entity.Get(h.Schema.Marking).AsString());
        Support.Near(new Vec3(150, 0, 0), Support.H(h.Frame!.PointFromEcef(entity.Get(h.Schema.Position).AsVec3())), 1e-6, "position");
        Assert.Equal(100.0, entity.Get(h.Schema.Health).AsVec3().X);

        // ext writes the damage: HROT is told the health, once
        Ext(h, ext, new ExtDamage(1, 40, 100));
        var external = Assert.Single(h.HrotModel.Received.OfType<ExternalState>());
        Assert.Equal(new ExternalState(1, HrotDescriptor.Damage, default, string.Empty, 0, 40, 100), external);
        Assert.Equal(40, h.HrotModel.AdoptedOf(1)!.HealthCurrent);

        // DIS publishes the pivot value (ext's)
        var pdu = h.DisOut!.OfType<DISnet.EntityStatePdu>().Last();
        Assert.Equal(h.Services.Damage.DamageOf(40, 100), DisConversions.DamageOf(pdu.EntityAppearance));
        Assert.NotEqual(0, DisConversions.DamageOf(pdu.EntityAppearance));

        // a DIS-source write of that damage is refused, the pivot keeps ext's value
        var cs = h.Engine.BeginChange(h.DisSource);
        cs.Set(h.EntityOfHrot(1)!.Id, h.Schema.Damage, AttrValue.From(3L));
        Assert.False(cs.Commit().Succeeded);
        Assert.Equal(40.0, h.EntityOfHrot(1)!.Get(h.Schema.Health).AsVec3().X);

        // ext hands it back: HROT is told "all local" and its health applies again
        Ext(h, ext, new ExtTransfer(1, "Damage", false));
        Assert.Equal(new HrotDescriptorOwnership(1, HrotDescriptor.None), h.HrotModel.Received.Last());
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(150, 0, 0), marking: "LION-X", health: 10, healthMax: 100));
        Assert.Equal(10.0, h.EntityOfHrot(1)!.Get(h.Schema.Health).AsVec3().X);
        Assert.Equal(0, h.Engine.Diagnostics.RuleFailures);
        Assert.Empty(side.Refusals);
    }

    [Fact]
    public void A_write_of_the_engine_to_a_local_group_sends_no_external_state_and_to_an_external_group_does()
    {
        var h = Live(out _, out var ext);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 0, 0), marking: "A"));
        var id = h.EntityOfHrot(1)!.Id;
        var cs = h.Engine.BeginChange(SourceRegistry.Engine);
        cs.Set(id, h.Schema.Marking, AttrValue.From("ENGINE"));
        Assert.True(cs.Commit().Succeeded);
        h.Step();
        Assert.Empty(h.HrotModel.Received); // Info is local: nothing to tell HROT

        Ext(h, ext, new ExtTransfer(1, "Info", true));
        cs = h.Engine.BeginChange(SourceRegistry.Engine);
        cs.Set(id, h.Schema.Marking, AttrValue.From("ENGINE2"));
        Assert.True(cs.Commit().Succeeded);
        h.Step();
        Assert.Equal(new ExternalState(1, HrotDescriptor.Info, default, "ENGINE2", 1, 0, 0), h.HrotModel.Received.OfType<ExternalState>().Single());
    }

    [Fact]
    public void Reset_to_master_when_the_node_leaves_tells_hrot_everything_is_local_again()
    {
        var h = Live(out _, out var ext);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 0, 0)));
        h.HrotStep(HrotDisHarness.State(2, new HrotVec3(200, 0, 0)));
        Ext(h, ext, new ExtTransfer(1, "Damage", true));
        Ext(h, ext, new ExtTransfer(1, "Info", true));
        Ext(h, ext, new ExtTransfer(2, "Spatial", true));
        Assert.Equal(new HrotDescriptorOwnership(1, HrotDescriptor.Damage | HrotDescriptor.Info), h.HrotModel.Received.OfType<HrotDescriptorOwnership>().Where(o => o.Id == 1).Last());

        Ext(h, ext, new ExtLeave());
        Assert.Equal(HrotDescriptor.None, h.HrotModel.ExternalOf(1));
        Assert.Equal(HrotDescriptor.None, h.HrotModel.ExternalOf(2));
        Assert.Equal(new HrotDescriptorOwnership(1, HrotDescriptor.None), h.HrotModel.Received.OfType<HrotDescriptorOwnership>().Where(o => o.Id == 1).Last());
        Assert.Equal(new HrotDescriptorOwnership(2, HrotDescriptor.None), h.HrotModel.Received.OfType<HrotDescriptorOwnership>().Where(o => o.Id == 2).Last());
        Assert.Equal(HrotDescriptor.None, h.Schema.ExternalSet(h.EntityOfHrot(1)!, h.HrotSource));
    }

    [Fact]
    public void The_set_is_sent_only_when_it_changes()
    {
        var h = Live(out _, out var ext);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 0, 0)));
        Ext(h, ext, new ExtTransfer(1, "Damage", true));
        Ext(h, ext, new ExtTransfer(1, "Damage", true)); // the same again
        Ext(h, ext, new ExtTransfer(1, "Info", true));
        Ext(h, ext, new ExtTransfer(1, "Damage", false));
        Ext(h, ext, new ExtTransfer(1, "Damage", false));
        Assert.Equal(
            [HrotDescriptor.Damage, HrotDescriptor.Damage | HrotDescriptor.Info, HrotDescriptor.Info],
            h.HrotModel.Received.OfType<HrotDescriptorOwnership>().Select(o => o.External).ToArray());
    }

    [Fact]
    public void Info_and_spatial_values_of_ext_reach_hrot_and_hrot_ingress_leaves_them_alone()
    {
        var h = Live(out _, out var ext);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 0, 0), marking: "A"));
        Ext(h, ext, new ExtTransfer(1, "Info", true));
        Ext(h, ext, new ExtTransfer(1, "Spatial", true));
        Ext(h, ext, new ExtInfo(1, "EXT", 3));
        var info = Assert.Single(h.HrotModel.Received.OfType<ExternalState>());
        Assert.Equal(new ExternalState(1, HrotDescriptor.Info, default, "EXT", 3, 0, 0), info);

        var target = h.Frame!.PointToEcef(new Vec3(500, 0, 0));
        Ext(h, ext, new ExtSpatial(1, target, 9.5));
        var spatial = h.HrotModel.Received.OfType<ExternalState>().Last();
        Assert.Equal(HrotDescriptor.Spatial, spatial.Descriptors);
        Support.Near(new Vec3(500, 0, 0), spatial.State.Position, 1e-3, "external position");

        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(999, 0, 0), marking: "B", force: 1));
        var entity = h.EntityOfHrot(1)!;
        Assert.Equal("EXT", entity.Get(h.Schema.Marking).AsString());
        Assert.Equal(3, entity.Get(h.Schema.Force).AsInt());
        Support.Near(target, entity.Get(h.Schema.Position).AsVec3(), 1e-6, "pivot position stays ext's");
    }

    [Fact]
    public void A_turret_pose_for_an_externally_owned_articulation_is_left_out()
    {
        var h = Live(out _, out var ext);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(100, 0, 0)));
        h.HrotStep(new HrotTurretPose(1, 1, 0.5, 0.1));
        Assert.Single(h.EntityOfHrot(1)!.Get(h.Schema.ArticulationParts).AsObject<ArticulationParts>().Parts);
        Ext(h, ext, new ExtTransfer(1, "Articulation", true));
        long failures = h.Engine.Diagnostics.TransactionFailures;
        h.HrotStep(new HrotTurretPose(1, 2, 0.7, 0.2));
        Assert.Equal(failures, h.Engine.Diagnostics.TransactionFailures);
        Assert.Single(h.EntityOfHrot(1)!.Get(h.Schema.ArticulationParts).AsObject<ArticulationParts>().Parts);
    }

    [Fact]
    public void A_group_of_a_dis_entity_cannot_be_moved_and_nothing_is_sent()
    {
        var h = Live(out var side, out var ext);
        var dis = h.DisCreate(h.DisStateAt(new HrotVec3(10, 0, 0)));
        h.Steps(2);
        int before = h.HrotModel.Received.Count;
        Ext(h, ext, new ExtTransferEntity(dis, "Damage"));
        Assert.Single(side.Refusals);
        Assert.False(h.Engine.State.Find(dis)!.Has(h.Schema.OwnerDamage));
        Assert.Equal(before, h.HrotModel.Received.Count);
    }

    [Fact]
    public void Seeded_random_interleaving_keeps_the_ownership_invariants_every_tick()
    {
        int externalStates = 0, ownershipMessages = 0;
        long refused = 0;
        for (int seed = 1; seed <= 25; seed++)
        {
            var r = new Random(seed);
            var h = Live(out var side, out var ext);
            var disEntities = new List<EntityId>();
            string[] groups = ["Spatial", "Info", "Damage", "Articulation"];
            for (int step = 0; step < 120; step++)
            {
                int id = r.Next(1, 4);
                switch (r.Next(9))
                {
                    case 0:
                    case 1:
                        long before = h.Engine.Diagnostics.TransactionFailures;
                        h.HrotStep(HrotDisHarness.State(id, new HrotVec3(r.Next(1000), r.Next(1000), 0), marking: "M" + r.Next(9),
                            force: r.Next(4), health: r.Next(1, 101), healthMax: 100));
                        Assert.True(before == h.Engine.Diagnostics.TransactionFailures, $"seed {seed} step {step}: a HROT state was rejected");
                        break;
                    case 2:
                    case 3:
                        Ext(h, ext, new ExtTransfer(id, groups[r.Next(4)], r.Next(3) != 0));
                        break;
                    case 4:
                        Ext(h, ext, new ExtDamage(id, r.Next(1, 100), 100));
                        break;
                    case 5:
                        Ext(h, ext, new ExtInfo(id, "E" + r.Next(9), r.Next(4)));
                        break;
                    case 6:
                        if (h.Frame is not null)
                        {
                            Ext(h, ext, new ExtSpatial(id, h.Frame.PointToEcef(new Vec3(r.Next(1000), r.Next(1000), 0)), 1 + step));
                        }

                        break;
                    case 7:
                        if (r.Next(4) == 0)
                        {
                            Ext(h, ext, new ExtLeave());
                        }
                        else if (disEntities.Count < 3)
                        {
                            disEntities.Add(h.DisCreate(h.DisStateAt(new HrotVec3(r.Next(500), 0, 0))));
                            h.Step();
                        }

                        break;
                    default:
                        if (disEntities.Count > 0)
                        {
                            var dis = disEntities[r.Next(disEntities.Count)];
                            h.DisUpdate(dis, h.DisStateAt(new HrotVec3(r.Next(500), 0, 0), damage: r.Next(4)));
                            Ext(h, ext, new ExtTransferEntity(dis, groups[r.Next(4)]));
                        }

                        break;
                }

                h.Step();
                foreach (var dis in disEntities)
                {
                    Assert.False(h.Engine.State.Find(dis)!.Has(h.Schema.OwnerDamage) || h.Engine.State.Find(dis)!.Has(h.Schema.OwnerSpatial), $"seed {seed}: a dis entity changed owner");
                }

                // what HROT adopted for an external damage is what the pivot holds
                for (int hid = 1; hid <= 3; hid++)
                {
                    var entity = h.EntityOfHrot(hid);
                    if (entity is not null && (h.HrotModel.ExternalOf(hid) & HrotDescriptor.Damage) != 0 && h.HrotModel.AdoptedOf(hid)?.HealthCurrent is { } adopted &&
                        entity.TryGet(h.Schema.Health, out var health))
                    {
                        Assert.Equal(adopted, health.AsVec3().X);
                    }
                }
            }

            Assert.Equal(0, h.Engine.Diagnostics.RuleFailures);
            externalStates += h.HrotModel.Received.OfType<ExternalState>().Count();
            ownershipMessages += h.HrotModel.Received.OfType<HrotDescriptorOwnership>().Count();
            refused += h.Engine.Diagnostics.TransactionFailures;
        }

        // the run really exercised the paths: values forwarded, ownership moved, writes of non-owners refused
        Assert.True(externalStates > 50, $"{externalStates} ExternalState messages");
        Assert.True(ownershipMessages > 50, $"{ownershipMessages} ownership messages");
        Assert.True(refused > 10, $"{refused} refused change sets");
    }
}
