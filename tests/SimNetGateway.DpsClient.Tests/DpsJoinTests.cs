using System.Globalization;
using CycloneDDS.Runtime;
using SimNetGateway.DpsClient;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.DpsClient.Tests;

/// <summary>T-060: the reference client joins like a real player of HROT's deployment (role / side / kit / area / observe), lists the offers and prints a refusal.</summary>
public class DpsJoinTests
{
    private static D.Offer Unit(int id, bool free = true) => new() { Kind = D.OfferKind.OfferKindUnit, Id = id, Name = "Unit" + id, Side = 2, Free = free };

    private static D.Offer Role(int id, string name, bool free = true, int count = 0, int max = 4, params string[] kits) => new()
    {
        Kind = D.OfferKind.OfferKindRole, Id = id, Name = name, Side = 2, Free = free, Count = count, Max = max,
        Kits = kits.Select(k => new D.NameItem { Name = k }).ToList(),
    };

    private static D.Offer Area(int id, string name, int points) => new()
    {
        Kind = D.OfferKind.OfferKindArea, Id = id, Name = name, Free = true,
        Outline = Enumerable.Range(0, points).Select(i => new D.Vec3d { X = i, Y = i * 2.5 }).ToList(),
    };

    /// <summary>Two roles and two areas so a join that takes the wrong offer's id is visible.</summary>
    private static D.PlayerSlot Choosing(string reason = "", params D.Offer[] extra)
    {
        var offers = new List<D.Offer> { Unit(1201), Role(10, "Medic", true, 1, 2, "bag"), Role(9, "Rifleman", true, 0, 4, "plain", "heavy"), Area(34, "South", 3), Area(33, "North", 4) };
        offers.AddRange(extra);
        return new D.PlayerSlot { State = D.SlotState.SlotStateChoosing, Reason = reason, Offers = offers };
    }

    // ---- the pure join plan and offer text

    [Fact]
    public void A_role_plan_builds_the_request_from_the_named_offers()
    {
        var req = new DpsJoinPlan(DpsJoinMode.Role, "rifleman", Side: 1, Kit: 1, Area: "North").Build(Choosing(), out var problem);
        Assert.Null(problem);
        var r = Assert.IsType<D.JoinRequest>(req);
        Assert.Equal(D.JoinKind.JoinKindRole, r.Kind);
        Assert.Equal(9, r.UnitId);
        Assert.Equal("Rifleman", r.Role);
        Assert.Equal((byte)1, r.Side);
        Assert.Equal((byte)1, r.Kit);
        Assert.Equal(33, r.AreaId);
    }

    [Fact]
    public void A_full_role_an_unknown_role_and_an_unknown_area_build_nothing_and_say_why()
    {
        var full = Choosing(string.Empty, Role(11, "Sniper", free: false, count: 2, max: 2));
        Assert.Null(new DpsJoinPlan(DpsJoinMode.Role, "Sniper").Build(full, out var p1));
        Assert.Equal("role 'Sniper' is full (2/2)", p1);
        Assert.Null(new DpsJoinPlan(DpsJoinMode.Role, "Cook").Build(full, out var p2));
        Assert.Contains("Cook", p2);
        Assert.Null(new DpsJoinPlan(DpsJoinMode.Role, "Rifleman", Area: "East").Build(full, out var p3));
        Assert.Contains("East", p3);
    }

    [Fact]
    public void The_observe_and_first_unit_plans_build_their_requests()
    {
        Assert.Equal(D.JoinKind.JoinKindObserve, new DpsJoinPlan(DpsJoinMode.Observe).Build(Choosing(), out _)!.Value.Kind);
        var unit = new DpsJoinPlan(DpsJoinMode.FirstUnit, Side: 3).Build(Choosing(), out _)!.Value;
        Assert.Equal(D.JoinKind.JoinKindUnit, unit.Kind);
        Assert.Equal(1201, unit.UnitId);
        Assert.Equal((byte)3, unit.Side);
    }

    [Fact]
    public void The_offer_list_prints_a_unit_a_full_role_and_an_area_in_invariant_culture()
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("cs-CZ");
        try
        {
            var lines = DpsOfferList.Lines(Choosing(string.Empty, Role(11, "Sniper", free: false, count: 2, max: 2, "ghillie"))).ToList();
            Assert.Contains("offer kind=unit id=1201 name=\"Unit1201\" side=2 free=true count=0/0 kits=[] outline_points=0", lines);
            Assert.Contains("offer kind=role id=9 name=\"Rifleman\" side=2 free=true count=0/4 kits=[plain,heavy] outline_points=0", lines);
            Assert.Contains("offer kind=role id=11 name=\"Sniper\" side=2 free=false count=2/2 kits=[ghillie] outline_points=0", lines);
            Assert.Contains("offer kind=area id=33 name=\"North\" side=0 free=true count=0/0 kits=[] outline_points=4", lines);
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    // ---- option parsing

    [Theory]
    [InlineData("--role")]
    [InlineData("--side", "x")]
    [InlineData("--side", "300")]
    [InlineData("--kit", "-1")]
    [InlineData("--area")]
    [InlineData("--area", "North")]
    [InlineData("--kit", "1")]
    [InlineData("--side", "1")]
    [InlineData("--role", "A", "--observe")]
    [InlineData("--join", "--observe")]
    [InlineData("--observe", "--side", "1")]
    public void Join_options_reject_bad_values_and_conflicts(params string[] args)
    {
        var error = new StringWriter();
        Assert.Equal(2, DpsClientProgram.Run(args, new StringWriter(), error));
        Assert.NotEqual(string.Empty, error.ToString());
    }

    // ---- the client against a fake gateway

    private sealed class Gateway : IDisposable
    {
        private readonly DdsParticipant _participant;
        private readonly DdsWriter<D.PlayerSlot> _slot;
        private readonly DdsReader<D.JoinRequest> _joins;
        public readonly List<D.JoinRequest> JoinsSeen = new();

        public Gateway(uint domain, string partition, D.PlayerSlot slot)
        {
            _participant = new DdsParticipant(domain, defaultPartition: partition);
            _slot = new DdsWriter<D.PlayerSlot>(_participant);
            _joins = new DdsReader<D.JoinRequest>(_participant);
            _slot.Write(slot);
        }

        public void Pump()
        {
            using var loan = _joins.Take(16);
            foreach (var s in loan)
            {
                if (s.IsValid)
                {
                    JoinsSeen.Add(s.Data);
                }
            }
        }

        public void Dispose()
        {
            _slot.Dispose();
            _joins.Dispose();
            _participant.Dispose();
        }
    }

    private static string RunClient(uint domain, string partition, double seconds, params string[] options)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string[] args = ["--domain", domain.ToString(CultureInfo.InvariantCulture), "--partition", partition, "--headless", "--seconds", seconds.ToString(CultureInfo.InvariantCulture), .. options];
        Assert.Equal(0, DpsClientProgram.Run(args, output, error));
        Assert.Equal(string.Empty, error.ToString());
        return output.ToString();
    }

    private static void Settle(Gateway gw)
    {
        for (int i = 0; i < 25; i++)
        {
            gw.Pump();
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void Role_with_side_kit_and_area_writes_that_join_request()
    {
        uint domain = DpsTestDomains.Next();
        using var gw = new Gateway(domain, "j1", Choosing());
        RunClient(domain, "j1", 2.5, "--role", "Rifleman", "--side", "1", "--kit", "1", "--area", "North", "--retry");
        Settle(gw);

        Assert.NotEmpty(gw.JoinsSeen);
        Assert.All(gw.JoinsSeen, r =>
        {
            Assert.Equal(D.JoinKind.JoinKindRole, r.Kind);
            Assert.Equal(9, r.UnitId);
            Assert.Equal("Rifleman", r.Role);
            Assert.Equal((byte)1, r.Side);
            Assert.Equal((byte)1, r.Kit);
            Assert.Equal(33, r.AreaId);
        });
    }

    [Fact]
    public void Observe_writes_an_observe_request()
    {
        uint domain = DpsTestDomains.Next();
        using var gw = new Gateway(domain, "j2", Choosing());
        RunClient(domain, "j2", 2.5, "--observe", "--retry");
        Settle(gw);

        Assert.NotEmpty(gw.JoinsSeen);
        Assert.All(gw.JoinsSeen, r => Assert.Equal(D.JoinKind.JoinKindObserve, r.Kind));
    }

    [Fact]
    public void A_role_join_asks_once_without_retry()
    {
        uint domain = DpsTestDomains.Next();
        using var gw = new Gateway(domain, "j3", Choosing());
        RunClient(domain, "j3", 3.5, "--role", "Rifleman");
        Settle(gw);

        Assert.InRange(gw.JoinsSeen.Count, 0, 1);
    }

    [Fact]
    public void A_full_role_sends_nothing_and_prints_why()
    {
        uint domain = DpsTestDomains.Next();
        using var gw = new Gateway(domain, "j4", Choosing(string.Empty, Role(11, "Sniper", free: false, count: 2, max: 2)));
        string text = RunClient(domain, "j4", 2.5, "--role", "Sniper", "--retry");
        Settle(gw);

        Assert.Empty(gw.JoinsSeen);
        Assert.Contains("join not sent: role 'Sniper' is full (2/2)", text);
    }

    [Fact]
    public void List_offers_prints_every_offer_once_while_choosing()
    {
        uint domain = DpsTestDomains.Next();
        using var gw = new Gateway(domain, "j5", Choosing(string.Empty, Role(11, "Sniper", free: false, count: 2, max: 2)));
        string text = RunClient(domain, "j5", 2.5, "--list-offers");

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Single(lines, l => l == "offer kind=unit id=1201 name=\"Unit1201\" side=2 free=true count=0/0 kits=[] outline_points=0");
        Assert.Single(lines, l => l == "offer kind=role id=11 name=\"Sniper\" side=2 free=false count=2/2 kits=[] outline_points=0");
        Assert.Single(lines, l => l == "offer kind=area id=34 name=\"South\" side=0 free=true count=0/0 kits=[] outline_points=3");
        Assert.Equal(6, lines.Count(l => l.StartsWith("offer ", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_refusal_is_printed_with_its_reason_and_the_status_line_shows_it()
    {
        uint domain = DpsTestDomains.Next();
        using var gw = new Gateway(domain, "j6", Choosing("WrongSide"));
        string text = RunClient(domain, "j6", 2.5, "--role", "Rifleman", "--retry");

        Assert.Contains("join refused: WrongSide", text);
        Assert.Contains("reason=\"WrongSide\"", text);
    }

    [Fact]
    public void A_dead_slot_shows_the_respawn_time()
    {
        uint domain = DpsTestDomains.Next();
        using var gw = new Gateway(domain, "j7", new D.PlayerSlot { State = D.SlotState.SlotStateDead, UnitId = 5, RespawnAtSim = 42.5, Offers = [] });
        string text = RunClient(domain, "j7", 2.5);

        Assert.Contains("slot=SlotStateDead/5 respawn_at=42.5", text);
    }
}
