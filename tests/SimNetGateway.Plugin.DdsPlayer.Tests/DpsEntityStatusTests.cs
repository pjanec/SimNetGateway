using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>T-062 (DD-025): the pivot status of an entity's card becomes the EntityStatus topic - every field mapped, written once on the world participant whatever the seats, late joiners get the last values, a removed entity disposes its status.</summary>
public class DpsEntityStatusTests
{
    private static string Id(int id) => id.ToString();

    private static HrotEntityStatus Status(int id, string symbol = "", float health = 60f, float max = 100f, int present = 0, int authorized = 0, float ammo = -1f, float fill = 0f, float mark = -1f,
        HrotMindBand band = HrotMindBand.None, string behaviour = "", string task = "") => new(id, symbol, health, max, present, authorized, ammo, fill, mark, band, behaviour, task);

    [Fact]
    public void Every_field_reaches_the_topic_the_symbol_is_bounded_and_every_band_maps()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.EntityStatus>(domain, "station-1", s => Id(s.EntityId));
        using var rig = new Rig(domain);

        rig.Send(Status(2001, "SFGPUCI---D---|Inf", 59.5f, 120f, 3, 4, 0.4375f, 0.62f, 0.8f, HrotMindBand.Nervous));
        rig.Send(Status(2002, new string('s', 60), 1f, 2f));   // a symbol past the bound is cut
        rig.Send(Status(2003, band: HrotMindBand.Calm, fill: 0.1f));
        rig.Send(Status(2004, band: HrotMindBand.Broken, fill: 1f, mark: 0.7f));
        rig.Send(Status(2005, band: HrotMindBand.Fear, fill: 0.6f));
        rig.Send(Status(2006, health: float.NaN, max: float.NaN, ammo: float.NaN, fill: float.NaN, mark: float.NaN));   // a non-finite value is its none-value

        Assert.True(watch.Until(w => w.Alive.Count == 6));
        var s1 = watch.Alive["2001"];
        Assert.Equal(("SFGPUCI---D---|Inf", 59.5f, 120f, 3, 4, 0.4375f, 0.62f, 0.8f, D.MindBand.MindBandNervous),
            (s1.Symbol, s1.HealthCurrent, s1.HealthMax, s1.StrengthPresent, s1.StrengthAuthorized, s1.AmmoShare, s1.MindFill, s1.MindBreak, s1.MindBand));
        Assert.Equal(new string('s', 40), watch.Alive["2002"].Symbol);
        Assert.Equal([D.MindBand.MindBandCalm, D.MindBand.MindBandBroken, D.MindBand.MindBandFear], new[] { "2003", "2004", "2005" }.Select(k => watch.Alive[k].MindBand).ToArray());
        Assert.Equal(D.MindBand.MindBandNone, watch.Alive["2002"].MindBand);
        var none = watch.Alive["2006"];
        Assert.Equal((0f, 0f, -1f, 0f, -1f), (none.HealthCurrent, none.HealthMax, none.AmmoShare, none.MindFill, none.MindBreak));
    }

    /// <summary>T-063: the behaviour and the task names reach the topic, a name past the bound (32) is cut, a missing one is empty.</summary>
    [Fact]
    public void The_behaviour_and_the_task_reach_the_topic_bounded_and_empty_when_missing()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.EntityStatus>(domain, "station-1", s => Id(s.EntityId));
        using var rig = new Rig(domain);

        rig.Send(Status(2001, behaviour: "Patrol", task: "Halt"));
        rig.Send(Status(2002, behaviour: new string('b', 50), task: new string('t', 50)));
        rig.Send(Status(2003));
        rig.Send(Status(2004) with { Behaviour = null!, Task = null! });

        Assert.True(watch.Until(w => w.Alive.Count == 4));
        Assert.Equal(("Patrol", "Halt"), (watch.Alive["2001"].Behaviour, watch.Alive["2001"].Task));
        Assert.Equal((new string('b', 32), new string('t', 32)), (watch.Alive["2002"].Behaviour, watch.Alive["2002"].Task));
        Assert.Equal((string.Empty, string.Empty), (watch.Alive["2003"].Behaviour, watch.Alive["2003"].Task));
        Assert.Equal((string.Empty, string.Empty), (watch.Alive["2004"].Behaviour, watch.Alive["2004"].Task));
    }

    /// <summary>One writer, on the world participant: every seat's client gets each sample once, and the channel is written once per status.</summary>
    [Fact]
    public void A_status_is_written_once_on_the_world_participant_whatever_the_seats()
    {
        uint domain = Watch.NewDomain();
        using var one = new Watch.Watch<D.EntityStatus>(domain, "station-1-1", s => Id(s.EntityId));
        using var two = new Watch.Watch<D.EntityStatus>(domain, "station-1-2", s => Id(s.EntityId));
        using var rig = new Rig(domain, seats: 2);
        int written = 0;
        rig.Transport.EntityStatus.Tap = _ => written++;

        rig.Send(Status(2001, "S", 60f, 100f));

        Assert.Equal(1, written);
        foreach (var watch in new[] { one, two })
        {
            Assert.True(watch.Until(w => w.Events.Count >= 1));
            Thread.Sleep(200);
            watch.Pump();
            Assert.Single(watch.Events);   // a writer per seat would show it twice on the seat that matches both partitions, or not at all on another
            Assert.Equal(2001, watch.Events[0].EntityId);
        }
    }

    [Fact]
    public void A_late_client_gets_the_last_values_only()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        rig.Send(Status(2001, health: 60f));
        rig.Send(Status(2001, health: 50f, band: HrotMindBand.Nervous, fill: 0.5f));
        rig.Send(Status(2002, health: 10f));

        using var late = new Watch.Watch<D.EntityStatus>(domain, "station-1", s => Id(s.EntityId));

        Assert.True(late.Until(w => w.Alive.Count == 2));
        Assert.Equal((50f, D.MindBand.MindBandNervous, 10f), (late.Alive["2001"].HealthCurrent, late.Alive["2001"].MindBand, late.Alive["2002"].HealthCurrent));
    }

    [Fact]
    public void A_removed_or_withdrawn_entity_disposes_its_status_and_a_removal_of_one_that_never_had_one_changes_nothing()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.EntityStatus>(domain, "station-1", s => Id(s.EntityId));
        using var rig = new Rig(domain);
        var written = new List<(int Id, string Symbol)>();
        rig.Transport.EntityStatus.Tap = s => written.Add((s.EntityId, s.Symbol));
        rig.Send(Status(2001));
        rig.Send(Status(2002));
        rig.Send(Status(2003));
        Assert.True(watch.Until(w => w.Alive.Count == 3));
        int before = written.Count;

        rig.Send(new HrotEntityRemoved(2001));
        Assert.True(watch.Until(w => !w.Alive.ContainsKey("2001") && w.Alive.Count == 2));

        rig.Send(new HrotEntityStatusRemoved(2002));   // the entity stays, its card is gone
        Assert.True(watch.Until(w => !w.Alive.ContainsKey("2002") && w.Alive.Count == 1));

        rig.Send(new HrotEntityRemoved(2002));   // already withdrawn
        rig.Send(new HrotEntityRemoved(9999));   // never had one
        rig.Send(new HrotEntityStatusRemoved(9999));
        Assert.Equal(before, written.Count);   // a disposal writes no sample
        Thread.Sleep(200);
        watch.Pump();
        Assert.Equal(["2003"], watch.Alive.Keys.ToArray());   // the others stay as they were
    }
}
