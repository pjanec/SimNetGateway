using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using static SimNetGateway.Gateway.HrotDis.Tests.DisPdus;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>The DIS stale reaper (DD-006 7, R-068): DIS entities silent for dis.timeoutS are removed, and only the DIS side's own writes keep an entity alive.</summary>
public class DisReaperTests
{
    private static readonly DisEntityKey Key = new(PeerSite, PeerApplication, 5);

    private static HrotDisHarness Live(double timeoutS = 12)
    {
        var config = GatewayConfiguration.CreateDefault() with { Dis = new DisConfiguration { ExerciseId = 1, TimeoutS = timeoutS } };
        var h = new HrotDisHarness(config);
        h.GoLive();
        return h;
    }

    private static bool Exists(HrotDisHarness h, DisEntityKey key) => h.DisIds!.TryResolve(key, out var id) && h.Engine.State.Find(id) is not null;

    [Fact]
    public void A_dis_entity_silent_longer_than_the_timeout_is_removed_and_hrot_is_told()
    {
        var h = Live();
        h.DisStep(EntityState(5, h.Frame!.PointToEcef(new Vec3(10, 10, 0))));
        long replica = h.Sent<ReplicaState>().Single().Id;
        h.Run(11.5);
        Assert.True(Exists(h, Key));
        h.Run(2.0); // 13.5 s: past the timeout and the check period
        Assert.False(Exists(h, Key));
        Assert.Equal(replica, h.Sent<ReplicaRemoved>().Single().Id);
        Assert.Equal(0, h.DisIds!.Count);
        Assert.Equal(0UL, h.Ids.InUse);
    }

    [Fact]
    public void An_entity_that_is_updated_within_the_timeout_is_never_removed()
    {
        var h = Live();
        for (int i = 0; i < 12; i++)
        {
            h.DisStep(EntityState(5, h.Frame!.PointToEcef(new Vec3(i, 0, 0))));
            h.Run(11.0);
            Assert.True(Exists(h, Key), $"removed after update {i}");
        }
    }

    [Fact]
    public void An_entity_that_keeps_sending_one_pdu_every_ten_seconds_survives_and_one_every_fifteen_does_not()
    {
        var h = Live();
        h.DisStep(EntityState(5));
        h.DisStep(EntityState(6));
        for (int i = 0; i < 6; i++)
        {
            h.Run(10);
            h.DisStep(EntityState(5));
            if (i % 2 == 0)
            {
                h.Run(5);
                h.DisStep(EntityState(6));
            }
        }

        Assert.True(Exists(h, Key));
    }

    [Fact]
    public void Writes_of_other_sources_do_not_keep_a_dis_entity_alive()
    {
        var h = Live();
        h.DisStep(EntityState(5));
        var derived = h.Engine.Sources.Register("test.derived");
        for (int second = 0; second < 14; second++)
        {
            if (Exists(h, Key))
            {
                var changes = h.Engine.BeginChange(derived);
                changes.Set(h.DisIds!.TryResolve(Key, out var id) ? id : default, h.Schema.Marking, AttrValue.From("D" + second));
                changes.Commit();
            }

            h.Run(1);
        }

        Assert.False(Exists(h, Key)); // a processor kept writing, the exercise did not
    }

    [Fact]
    public void Hrot_entities_are_not_reaped_and_keep_being_published()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(5, 5, 0)));
        h.Run(60);
        Assert.NotNull(h.EntityOfHrot(1));
        Assert.True(h.DisChecker!.States.Count >= 12); // a heartbeat every 5 s
    }

    [Fact]
    public void The_timeout_comes_from_the_configuration()
    {
        var h = Live(timeoutS: 3);
        h.DisStep(EntityState(5));
        h.Run(2.5);
        Assert.True(Exists(h, Key));
        h.Run(1.5);
        Assert.False(Exists(h, Key));
    }

    [Fact]
    public void A_removed_dis_entity_that_comes_back_is_a_new_entity_with_a_new_replica()
    {
        var h = Live();
        h.DisStep(EntityState(5));
        h.Run(14);
        Assert.False(Exists(h, Key));
        h.DisStep(EntityState(5));
        Assert.True(Exists(h, Key));
        Assert.Equal(2, h.Sent<ReplicaState>().Count);
    }

    [Fact]
    public void The_reaper_names_its_own_source()
    {
        var h = Live();
        Assert.True(h.Engine.Sources.TryGet("proc.reaper.dis", out _));
    }
}
