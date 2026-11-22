using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Behavior;
using Fdp.Toolkit.Civilians;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Squad.Command;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// T-064 (DD-026): what HROT's men shout, report and order. The expected values are written by HROT's own writers (<c>Utterances.Say</c>, <c>CommandHistoryOf.Report</c> / <c>Order</c>); the gather turns each NEW state into one event
/// (a held utterance and an unchanged history give none), names an order by the node's registry, remembers silently on the first gather, forgets at a world boundary and allocates nothing when steady.
/// </summary>
public class VoiceGatherTests
{
    private const long ManType = 300;
    private const int Leader = 3001, Platoon = 3002, Soldier = 3003, Squad = 3004;

    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public List<T> Of<T>() => All.OfType<T>().ToList();
    }

    private sealed class Stage : PackHarness
    {
        private readonly InMemoryInbound _inbound = new(new SourceId(99), "test");
        private readonly VoiceGather _voice = new();
        private readonly BehaviorRegistry _registry = new();

        public Stage()
            : base(dis: true)
        {
            RegisterTemplate(ManType, 3);
            Ensure<Utterance>();
            Ensure<CommandReportHistory>();
            Ensure<CommandOrderHistory>();
            foreach (string name in new[] { "Attack", "Move", "Defend" })
            {
                _registry.Register(name, new BehaviorDefinition { Name = name, BrainTier = 1 });
            }

            Steps(3);
        }

        public double Now { get; set; } = 10;

        public bool UseRegistry { get; set; } = true;

        public VoiceGather Voice => _voice;

        private void Ensure<T>()
            where T : unmanaged
        {
            if (!World.IsComponentTypeRegistered<T>())
            {
                World.RegisterComponent<T>();
            }
        }

        /// <summary>A man; a commander keeps both histories (HROT provisions them with the squad state).</summary>
        public Entity AddMan(int id, bool commander)
        {
            var e = AddHrotEntity(id, new Vector3(id % 100, 0, 0), "man" + id, ManType, ForceId.Friend, 100f);
            if (commander)
            {
                World.AddComponent(e, default(CommandReportHistory));
                World.AddComponent(e, default(CommandOrderHistory));
            }

            return e;
        }

        /// <summary>One gather at the next sim time (0.1 s later); returns what it sent.</summary>
        public Capture Gather()
        {
            Now += 0.1;
            _voice.Gather(World, _inbound, Now, UseRegistry ? _registry : null, _ => false);
            var sink = new Capture();
            _inbound.Drain(sink, int.MaxValue);
            return sink;
        }

        public void Say(Entity man, UtteranceKind kind, Entity target = default, float seconds = 1f) => Utterances.Say(World, man, kind, target, Now, seconds);

        public void Report(Entity from, Entity to, in CommandReport report) => CommandHistoryOf.Report(World, from, to, in report, (SimInstant)Now, ReportFate.Folded);

        public void Order(Entity giver, Entity member, string behaviour) => CommandHistoryOf.Order(World, giver, member, BehaviorHash.FromName(behaviour), (SimInstant)Now);
    }

    // ---- utterances ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void One_event_per_new_utterance_none_while_held_a_new_one_after_it_was_over_even_if_identical()
    {
        using var h = new Stage();
        var man = h.AddMan(Soldier, commander: false);
        var contact = h.AddMan(Leader, commander: false);
        h.Gather();   // the first gather only remembers

        h.Say(man, UtteranceKind.CallContact, contact, 1f);
        var first = Assert.Single(h.Gather().Of<HrotUtterance>());
        Assert.Equal((Soldier, "CallContact", Leader), (first.SpeakerId, first.Kind, first.TargetId));
        Assert.Equal(h.Now, first.SimTime, 6);

        for (int i = 0; i < 30; i++)   // held for three seconds, past its end
        {
            Assert.Empty(h.Gather().Of<HrotUtterance>());
        }

        h.Say(man, UtteranceKind.CallContact, contact, 1f);   // the very same shout, after it was over
        Assert.Single(h.Gather().Of<HrotUtterance>());
        Assert.Empty(h.Gather().Of<HrotUtterance>());

        h.Say(man, UtteranceKind.CallMoving, contact, 1f);   // a new kind
        Assert.Equal("CallMoving", Assert.Single(h.Gather().Of<HrotUtterance>()).Kind);

        h.Say(man, UtteranceKind.CallMoving, default, 1f);   // the same kind to nobody: a changed target
        Assert.Equal(0, Assert.Single(h.Gather().Of<HrotUtterance>()).TargetId);

        h.Say(man, UtteranceKind.UtterNone, default, 1f);   // saying nothing is not a shout
        Assert.Empty(h.Gather().Of<HrotUtterance>());
    }

    /// <summary>A seeded random sequence of shouts and silences: the events are exactly the shouts, one each, in order, and nothing between.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(2026)]
    public void Seeded_sequences_give_exactly_one_event_per_shout(int seed)
    {
        using var h = new Stage();
        var a = h.AddMan(Soldier, false);
        var b = h.AddMan(Leader, false);
        var kinds = new[] { UtteranceKind.CallContact, UtteranceKind.CallMoving, UtteranceKind.CallCovering, UtteranceKind.CmdHandsUp, UtteranceKind.Talk };
        var rng = new Random(seed);
        h.Gather();
        var expected = new List<(int Speaker, string Kind)>();
        var got = new List<(int Speaker, string Kind)>();
        for (int step = 0; step < 300; step++)
        {
            if (rng.Next(4) == 0)
            {
                var speaker = rng.Next(2) == 0 ? a : b;
                var kind = kinds[rng.Next(kinds.Length)];
                h.Say(speaker, kind, rng.Next(2) == 0 ? default : (speaker == a ? b : a), (float)(0.2 + rng.NextDouble()));
                expected.Add((speaker == a ? Soldier : Leader, kind.ToString()));
            }

            got.AddRange(h.Gather().Of<HrotUtterance>().Select(u => (u.SpeakerId, u.Kind)));
        }

        Assert.Equal(expected, got);
    }

    [Fact]
    public void A_first_gather_and_a_world_boundary_replay_nothing_and_a_shout_still_going_is_said_for_a_newcomer()
    {
        using var h = new Stage();
        var man = h.AddMan(Soldier, true);
        h.Say(man, UtteranceKind.CallContact);
        Assert.Empty(h.Gather().Of<HrotUtterance>());   // already said when the gather first looked

        var other = h.AddMan(Leader, false);
        h.Say(other, UtteranceKind.CmdHandsUp, default, 5f);   // a newcomer whose shout is still going
        Assert.Single(h.Gather().Of<HrotUtterance>());

        h.Voice.Clear();   // a world boundary
        Assert.Empty(h.Gather().Of<HrotUtterance>());
        Assert.Empty(h.Gather().Of<HrotUtterance>());
    }

    // ---- reports -------------------------------------------------------------------------------------------------------------------------

    private static CommandReport Contact() => new()
    {
        Kind = ReportKind.Contact, Count = 3, Size = ElementSize.Team, Activity = ElementActivity.Moving, Equipment = ElementEquipment.MachineGun | ElementEquipment.Vehicle,
        Strength = StatusColour.Amber, Ammo = StatusColour.Red, Wia = 2, Kia = 1, Seq = 5,
    };

    [Fact]
    public void One_event_per_report_with_the_ids_the_numbers_and_the_synthetic_flag_copied_whoever_keeps_a_history()
    {
        using var h = new Stage();
        var squad = h.AddMan(Squad, true);
        var platoon = h.AddMan(Platoon, true);
        var soldier = h.AddMan(Soldier, false);
        h.Gather();

        // both commanders: HROT writes an outgoing and an incoming entry - one event
        var r = Contact();
        h.Report(squad, platoon, in r);
        var one = Assert.Single(h.Gather().Of<HrotReport>());
        Assert.Equal((Squad, Platoon, (byte)ReportKind.Contact, (byte)0, (byte)3, (byte)ElementSize.Team, (byte)ElementActivity.Moving, (byte)(ElementEquipment.MachineGun | ElementEquipment.Vehicle),
                (byte)StatusColour.Amber, (byte)StatusColour.Red, (byte)2, (byte)1, false),
            (one.FromId, one.ToId, one.Kind, one.Sub, one.Count, one.Size, one.Activity, one.Equipment, one.Strength, one.Ammo, one.Wia, one.Kia, one.Synthetic));
        Assert.Equal(h.Now - 0.1, one.SimTime, 6);   // when HROT took it in (the entry's time), a gather before now
        Assert.Empty(h.Gather().Of<HrotReport>());

        // a man with no history reports to his commander: only the addressee keeps an entry - still one event
        var progress = new CommandReport { Kind = ReportKind.Progress, Subtype = (byte)ProgressKind.PhaseLine, Count = 2, Seq = 6, Synthetic = 1 };
        h.Report(soldier, squad, in progress);
        var p = Assert.Single(h.Gather().Of<HrotReport>());
        Assert.Equal((Soldier, Squad, (byte)ReportKind.Progress, (byte)ProgressKind.PhaseLine, (byte)2, true), (p.FromId, p.ToId, p.Kind, p.Sub, p.Count, p.Synthetic));

        // a commander reports down to a man with no history: only the sender keeps one
        var request = new CommandReport { Kind = ReportKind.Request, Subtype = (byte)RequestKind.Casevac, Seq = 7 };
        h.Report(squad, soldier, in request);
        var q = Assert.Single(h.Gather().Of<HrotReport>());
        Assert.Equal((Squad, Soldier, (byte)ReportKind.Request, (byte)RequestKind.Casevac), (q.FromId, q.ToId, q.Kind, q.Sub));
    }

    [Fact]
    public void A_report_to_a_superior_hrot_does_not_simulate_has_to_id_zero()
    {
        using var h = new Stage();
        var squad = h.AddMan(Squad, true);
        h.Gather();
        var r = Contact();
        h.Report(squad, Entity.Null, in r);   // "HQ": HROT stores NetIdOf(Entity.Null)
        var one = Assert.Single(h.Gather().Of<HrotReport>());
        Assert.Equal((Squad, 0), (one.FromId, one.ToId));
    }

    /// <summary>Every kind of every HROT enum the report carries goes through as its own number (the contract's enums are numbered alike, checked in the plugin tests).</summary>
    [Fact]
    public void Every_report_kind_size_activity_and_colour_goes_through_unchanged_and_several_reports_come_oldest_first()
    {
        using var h = new Stage();
        var squad = h.AddMan(Squad, true);
        var platoon = h.AddMan(Platoon, true);
        h.Gather();
        uint seq = 1;
        var sent = new List<CommandReport>();
        foreach (var kind in Enum.GetValues<ReportKind>())
        {
            foreach (var size in Enum.GetValues<ElementSize>())
            {
                var report = new CommandReport { Kind = kind, Size = size, Activity = (ElementActivity)((seq + 1) % 6), Strength = (StatusColour)(seq % 5), Ammo = (StatusColour)((seq + 2) % 5), Seq = seq++ };
                sent.Add(report);
                h.Report(squad, platoon, in report);
            }
        }

        var got = h.Gather().Of<HrotReport>();   // 35 reports in one gather: more than a history keeps (32), so the newest 32, oldest first
        Assert.Equal(CommandReportHistory.Capacity, got.Count);
        var expected = sent.Skip(sent.Count - CommandReportHistory.Capacity).ToList();
        Assert.Equal(expected.Select(x => ((byte)x.Kind, (byte)x.Size, (byte)x.Activity, (byte)x.Strength, (byte)x.Ammo)), got.Select(x => (x.Kind, x.Size, x.Activity, x.Strength, x.Ammo)));
    }

    // ---- orders --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void One_event_per_order_named_by_the_registry_a_resend_is_not_news_and_an_unknown_name_is_not_sent()
    {
        using var h = new Stage();
        var platoon = h.AddMan(Platoon, true);
        var squad = h.AddMan(Squad, true);
        var soldier = h.AddMan(Soldier, false);
        h.Gather();

        h.Order(platoon, squad, "Attack");   // giver and member both keep a history: one event
        var one = Assert.Single(h.Gather().Of<HrotOrder>());
        Assert.Equal((Platoon, Squad, "Attack"), (one.FromId, one.ToId, one.Name));
        Assert.Equal(h.Now - 0.1, one.SimTime, 6);

        h.Order(platoon, squad, "Attack");   // the runner's re-send: HROT counts it, does not list it
        Assert.Empty(h.Gather().Of<HrotOrder>());

        h.Order(platoon, squad, "Move");
        Assert.Equal("Move", Assert.Single(h.Gather().Of<HrotOrder>()).Name);
        h.Order(platoon, squad, "Attack");   // not the newest any more: new again
        Assert.Equal("Attack", Assert.Single(h.Gather().Of<HrotOrder>()).Name);

        h.Order(squad, soldier, "Defend");   // a member with no history: only the giver keeps one
        var d = Assert.Single(h.Gather().Of<HrotOrder>());
        Assert.Equal((Squad, Soldier, "Defend"), (d.FromId, d.ToId, d.Name));

        h.Order(platoon, soldier, "Defend");   // a giver with no history: only the member keeps one
        var g = Assert.Single(h.Gather().Of<HrotOrder>());
        Assert.Equal((Platoon, Soldier), (g.FromId, g.ToId));

        h.Order(platoon, squad, "NoSuchBehaviour");   // the registry does not know the hash: names, not numbers
        Assert.Empty(h.Gather().Of<HrotOrder>());
        h.UseRegistry = false;   // a node with no brain
        h.Order(platoon, squad, "Attack");
        Assert.Empty(h.Gather().Of<HrotOrder>());
    }

    // ---- first sight, world boundary -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_first_gather_and_a_world_boundary_replay_no_old_entry_and_the_next_one_is_sent()
    {
        using var h = new Stage();
        var squad = h.AddMan(Squad, true);
        var platoon = h.AddMan(Platoon, true);
        var r = Contact();
        h.Report(squad, platoon, in r);
        h.Order(platoon, squad, "Attack");
        var first = h.Gather();   // the history was there before the gather looked
        Assert.Empty(first.Of<HrotReport>());
        Assert.Empty(first.Of<HrotOrder>());

        h.Voice.Clear();   // a world boundary: the entries are still in the components, the gather forgot
        var after = h.Gather();
        Assert.Empty(after.Of<HrotReport>());
        Assert.Empty(after.Of<HrotOrder>());

        var next = Contact() with { Seq = 9 };
        h.Report(squad, platoon, in next);
        Assert.Single(h.Gather().Of<HrotReport>());
    }

    // ---- hot path ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_steady_voice_gather_allocates_nothing()
    {
        using var h = new Stage();
        var squad = h.AddMan(Squad, true);
        var platoon = h.AddMan(Platoon, true);
        var soldier = h.AddMan(Soldier, false);
        h.Gather();
        var r = Contact();
        h.Report(squad, platoon, in r);
        h.Order(platoon, squad, "Attack");
        h.Say(soldier, UtteranceKind.CallContact, default, 100f);
        Assert.NotEmpty(h.Gather().All);   // the events are sent once; from here the world is steady

        for (int i = 0; i < 100; i++)
        {
            h.Gather();
        }

        Thread.Sleep(250);   // past the runtime's tier-up delay
        for (int i = 0; i < 100; i++)
        {
            h.Gather();
        }

        var inbound = new InMemoryInbound(new SourceId(98), "alloc");
        var voice = h.Voice;
        var registry = (BehaviorRegistry?)null;
        Func<int, bool> none = _ => false;
        for (int i = 0; i < 50; i++)
        {
            voice.Gather(h.World, inbound, h.Now, registry, none);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            voice.Gather(h.World, inbound, h.Now, registry, none);
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- through the host and the dps plugin ---------------------------------------------------------------------------------------------

    /// <summary>The whole chain with two seats: the adapter's gather, the pivot messages, the plugin's module and the world participant's writers - each event is written once, not once per seat.</summary>
    [Fact]
    public void Through_the_host_and_the_plugin_each_voice_event_is_written_once_with_two_seats()
    {
        var plugin = new CountingDdsPlugin();
        var written = new List<string>();
        plugin.OnBound = t =>
        {
            t.Utterance.Tap = u => written.Add($"U {u.SpeakerId} {u.Kind} {u.TargetId}");
            t.Report.Tap = r => written.Add($"R {r.FromId} {r.ToId} {r.Kind} {r.Sub} {r.Synthetic}");
            t.Order.Tap = o => written.Add($"O {o.FromId} {o.ToId} {o.Name}");
        };
        using var h = new PackHarness(dis: false, plugins: [plugin], configure: c => c.WithSide(Plugin.DdsPlayer.DpsSide.SideKey, new Plugin.DdsPlayer.DpsSettings { Domain = 97, Partition = "t64-voice", Seats = 2 }));
        h.RegisterTemplate(ManType, 3);
        h.World.RegisterComponent<Utterance>();
        h.World.RegisterComponent<CommandReportHistory>();
        h.World.RegisterComponent<CommandOrderHistory>();
        var registry = new BehaviorRegistry();
        registry.Register("Attack", new BehaviorDefinition { Name = "Attack", BrainTier = 1 });
        h.BehaviorRegistry = registry;
        h.Steps(3);
        var squad = h.AddHrotEntity(Squad, new Vector3(1, 0, 0), "squad", ManType, ForceId.Friend, 100f);
        var platoon = h.AddHrotEntity(Platoon, new Vector3(2, 0, 0), "platoon", ManType, ForceId.Friend, 100f);
        h.World.AddComponent(squad, default(CommandReportHistory));
        h.World.AddComponent(squad, default(CommandOrderHistory));
        h.World.AddComponent(platoon, default(CommandReportHistory));
        h.World.AddComponent(platoon, default(CommandOrderHistory));
        h.Steps(5);
        Assert.Empty(written);

        Utterances.Say(h.World, squad, UtteranceKind.CallContact, platoon, 1.0, 1f);
        var report = Contact();
        CommandHistoryOf.Report(h.World, squad, platoon, in report, (SimInstant)1.0, ReportFate.Folded);
        CommandHistoryOf.Order(h.World, platoon, squad, BehaviorHash.FromName("Attack"), (SimInstant)1.0);
        h.Steps(5);

        Assert.Equal([$"U {Squad} CallContact {Platoon}", $"R {Squad} {Platoon} ReportKindContact 0 False", $"O {Platoon} {Squad} Attack"], written);
        h.Steps(30);
        Assert.Equal(3, written.Count);   // steady: nothing more
    }

}
