using System.Diagnostics;
using CycloneDDS.Runtime;
using SimNetGateway.DpsClient;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.DpsClient.Tests;

/// <summary>The reference client's world against a fake gateway: real CycloneDDS readers and writers in one process.</summary>
public class DpsWorldTests
{
    private static uint NewDomain() => DpsTestDomains.Next();

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 6000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }

    /// <summary>The gateway side: writers of the state topics, readers of the player topics, in one partition.</summary>
    private sealed class FakeGateway : IDisposable
    {
        private readonly DdsParticipant _participant;
        public readonly DdsWriter<D.Entity> Entities;
        public readonly DdsWriter<D.EntityState> States;
        public readonly DdsWriter<D.PlayerSlot> Slot;
        public readonly DdsWriter<D.GizmoFrame> Gizmos;
        public readonly DdsWriter<D.Session> Session;
        public readonly DdsReader<D.GizmoView> Views;
        public readonly List<D.GizmoView> ViewsSeen = new();
        public readonly DdsReader<D.JoinRequest> Joins;
        public readonly DdsReader<D.ControlFrame> Controls;
        public readonly List<D.JoinRequest> JoinsSeen = new();
        public readonly List<D.ControlFrame> ControlsSeen = new();

        public FakeGateway(uint domain, string partition)
        {
            _participant = new DdsParticipant(domain, defaultPartition: partition);
            Entities = new DdsWriter<D.Entity>(_participant);
            States = new DdsWriter<D.EntityState>(_participant);
            Slot = new DdsWriter<D.PlayerSlot>(_participant);
            Gizmos = new DdsWriter<D.GizmoFrame>(_participant);
            Session = new DdsWriter<D.Session>(_participant);
            Views = new DdsReader<D.GizmoView>(_participant);
            Joins = new DdsReader<D.JoinRequest>(_participant);
            Controls = new DdsReader<D.ControlFrame>(_participant);
        }

        public void Pump()
        {
            using (var loan = Joins.Take(16))
            {
                foreach (var s in loan)
                {
                    if (s.IsValid)
                    {
                        JoinsSeen.Add(s.Data);
                    }
                }
            }

            using (var loan = Views.Take(16))
            {
                foreach (var s in loan)
                {
                    if (s.IsValid)
                    {
                        ViewsSeen.Add(s.Data);
                    }
                }
            }

            using (var loan = Controls.Take(64))
            {
                foreach (var s in loan)
                {
                    if (s.IsValid)
                    {
                        ControlsSeen.Add(s.Data);
                    }
                }
            }
        }

        public void Dispose()
        {
            Entities.Dispose();
            States.Dispose();
            Slot.Dispose();
            Gizmos.Dispose();
            Session.Dispose();
            Views.Dispose();
            Joins.Dispose();
            Controls.Dispose();
            _participant.Dispose();
        }
    }

    private static D.Entity Ent(int id, D.Domain domain = D.Domain.DomainGround, byte side = 2) =>
        new() { EntityId = id, TypeId = 100 + id, Domain = domain, Side = side, Name = "e" + id };

    private static D.EntityState St(int id, double x, double y, float vx = 0, float vy = 0) =>
        new() { EntityId = id, Position = new D.Vec3d { X = x, Y = y }, LinearVelocity = new D.Vec3f { X = vx, Y = vy } };

    private static bool Until(DpsWorld world, FakeGateway gw, Func<bool> condition, int timeoutMs = 6000) => WaitFor(() =>
    {
        world.Pump();
        gw.Pump();
        return condition();
    }, timeoutMs);

    [Fact]
    public void World_mirrors_entities_and_drops_disposed()
    {
        uint domain = NewDomain();
        using var gw = new FakeGateway(domain, "p1");
        using var world = new DpsWorld(domain, "p1");

        gw.Entities.Write(Ent(1));
        gw.Entities.Write(Ent(2, D.Domain.DomainPerson, 1));
        gw.States.Write(St(1, 10, 20));
        gw.States.Write(St(2, 30, 40));

        Assert.True(Until(world, gw, () => world.Entities.Count == 2 && world.Entities.Values.All(e => e.State is not null)));
        Assert.Equal("e2", world.Entities[2].Info!.Value.Name);
        Assert.Equal(30, world.Entities[2].State!.Value.Position.X);

        gw.Entities.DisposeInstance(Ent(1));
        gw.States.DisposeInstance(St(1, 0, 0));

        Assert.True(Until(world, gw, () => world.Entities.Count == 1));
        Assert.True(world.Entities.ContainsKey(2));
    }

    [Fact]
    public void Dead_reckoning_moves_an_entity_between_samples()
    {
        uint domain = NewDomain();
        double now = 100.0;
        using var gw = new FakeGateway(domain, "p2");
        using var world = new DpsWorld(domain, "p2", clock: () => now);

        gw.Entities.Write(Ent(7));
        gw.States.Write(St(7, 100, 200, vx: 10, vy: -4));
        Assert.True(Until(world, gw, () => world.Entities.TryGetValue(7, out var e) && e.State is not null));

        var e7 = world.Entities[7];
        Assert.Equal(100, e7.PositionAt(100.0).X, 6);
        now = 100.5;
        var p = e7.PositionAt(now);
        Assert.Equal(105, p.X, 6);
        Assert.Equal(198, p.Y, 6);

        // a newer sample resets the base
        gw.States.Write(St(7, 500, 500, vx: 0, vy: 0));
        Assert.True(Until(world, gw, () => world.Entities[7].State!.Value.Position.X == 500));
        Assert.Equal(500, world.Entities[7].PositionAt(now + 3).X, 6);
    }

    [Fact]
    public void Join_and_frames_are_written_in_the_configured_partition()
    {
        uint domain = NewDomain();
        using var gw = new FakeGateway(domain, "p3");
        using var other = new FakeGateway(domain, "elsewhere");
        using var world = new DpsWorld(domain, "p3");

        gw.Slot.Write(new D.PlayerSlot
        {
            State = D.SlotState.SlotStateChoosing,
            Offers =
            [
                new D.Offer { Kind = D.OfferKind.OfferKindRole, Id = 9, Name = "Rifleman", Free = true },
                new D.Offer { Kind = D.OfferKind.OfferKindUnit, Id = 1201, Name = "Rifleman 1", Free = true },
                new D.Offer { Kind = D.OfferKind.OfferKindUnit, Id = 1202, Name = "Rifleman 2", Free = true },
            ],
        });
        Assert.True(Until(world, gw, () => world.Slot is not null));
        Assert.NotEqual(0UL, world.ClientId);

        // the first unit offer, never a role; the writer may not be matched yet, so retry until the gateway sees one
        Assert.True(Until(world, gw, () =>
        {
            world.JoinFirstUnit();
            return gw.JoinsSeen.Count > 0;
        }));
        var join = gw.JoinsSeen[0];
        Assert.Equal(D.JoinKind.JoinKindUnit, join.Kind);
        Assert.Equal(1201, join.UnitId);
        Assert.Equal(world.ClientId, join.ClientId);

        world.PressFire();
        for (int i = 1; i <= 3; i++)
        {
            // KeepLast 1: one frame per round trip, as a 30 Hz client sends them
            world.SetMove(1f, 0f);
            world.SendControl();
            Assert.True(Until(world, gw, () => gw.ControlsSeen.Count >= i));
        }

        Assert.All(gw.ControlsSeen, c => Assert.Equal(world.ClientId, c.ClientId));
        Assert.Equal(gw.ControlsSeen.Select(c => c.Seq).OrderBy(s => s), gw.ControlsSeen.Select(c => c.Seq));
        Assert.Equal(gw.ControlsSeen.Count, gw.ControlsSeen.Select(c => c.Seq).Distinct().Count());
        Assert.Equal(1f, gw.ControlsSeen[^1].Forward);
        Assert.Equal((byte)1, gw.ControlsSeen[^1].ShotSeq);

        // nothing leaked into the other partition
        other.Pump();
        Assert.Empty(other.JoinsSeen);
        Assert.Empty(other.ControlsSeen);
    }

    [Fact]
    public void Headless_run_reports_entity_and_slot_counts()
    {
        uint domain = NewDomain();
        using var gw = new FakeGateway(domain, "p4");
        gw.Entities.Write(Ent(1));
        gw.Entities.Write(Ent(2));
        gw.Entities.Write(Ent(3, D.Domain.DomainAir));
        gw.Slot.Write(new D.PlayerSlot { State = D.SlotState.SlotStateChoosing, Offers = [] });
        gw.Gizmos.Write(new D.GizmoFrame { Producer = "p4", Layer = "Paths", Primitives = [] });

        var output = new StringWriter();
        var error = new StringWriter();
        int code = DpsClientProgram.Run(["--domain", domain.ToString(), "--partition", "p4", "--headless", "--seconds", "2"], output, error);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error.ToString());
        string text = output.ToString();
        Assert.Contains("entities=3", text);
        Assert.Contains("slot=SlotStateChoosing", text);
        Assert.Contains("gizmo_layers=1", text);
    }

    [Fact]
    public void Headless_play_options_parse_and_print_lines_and_summary()
    {
        uint domain = NewDomain();
        using var gw = new FakeGateway(domain, "p5");
        gw.Entities.Write(Ent(1));
        gw.Slot.Write(new D.PlayerSlot
        {
            State = D.SlotState.SlotStateChoosing,
            Offers = [new D.Offer { Kind = D.OfferKind.OfferKindUnit, Id = 1201, Name = "R", Free = true }],
        });

        var output = new StringWriter();
        var error = new StringWriter();
        int code = DpsClientProgram.Run(
            ["--domain", domain.ToString(), "--partition", "p5", "--headless", "--seconds", "2.5", "--join", "--forward", "1", "--drive-seconds", "1", "--fire-after", "0.5", "--leave-after", "2"],
            output, error);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error.ToString());
        string text = output.ToString();
        Assert.Contains("t=1 sim=", text);
        Assert.Contains("slot=SlotStateChoosing/0", text);
        Assert.Contains("summary Entity=1 EntityState=0 PersonState=0 PersonAim=0 Appearance=0 VehicleLook=0 Fire=0 Detonation=0 Effect=0 Articulation=0", text);

        // the join went to the gateway (retried each second while Choosing)
        for (int i = 0; i < 50 && gw.JoinsSeen.Count == 0; i++)
        {
            gw.Pump();
            Thread.Sleep(20);
        }

        Assert.NotEmpty(gw.JoinsSeen);
        Assert.Equal(1201, gw.JoinsSeen[0].UnitId);
    }

    [Theory]
    [InlineData("--forward", "x")]
    [InlineData("--drive-seconds", "-1")]
    [InlineData("--leave-after")]
    public void Headless_play_options_reject_bad_values(params string[] args)
    {
        var error = new StringWriter();
        Assert.Equal(2, DpsClientProgram.Run(args, new StringWriter(), error));
    }

    private static D.PlayerSlot Slot(D.SlotState state, int unit = 0) => new()
    {
        State = state,
        UnitId = unit,
        Offers = [new D.Offer { Kind = D.OfferKind.OfferKindUnit, Id = 1201, Name = "R", Free = true }],
    };

    [Fact]
    public void Control_frames_aim_along_the_own_units_facing()
    {
        uint domain = NewDomain();
        using var gw = new FakeGateway(domain, "p6");
        using var world = new DpsWorld(domain, "p6", clock: () => 10.0);

        // yaw 90 degrees: facing north
        float h = (float)Math.Sqrt(0.5);
        gw.Entities.Write(Ent(1201, D.Domain.DomainPerson, 1));
        gw.States.Write(new D.EntityState { EntityId = 1201, Position = new D.Vec3d { X = 50, Y = 60, Z = 2 }, Orientation = new D.Quat { Z = h, W = h } });
        gw.Slot.Write(Slot(D.SlotState.SlotStatePlaying, 1201));
        Assert.True(Until(world, gw, () => world.IsPlaying && world.Entities.TryGetValue(1201, out var e) && e.State is not null));

        Assert.True(Until(world, gw, () =>
        {
            world.SendControl();
            return gw.ControlsSeen.Count > 0;
        }));
        var f = gw.ControlsSeen[^1];
        Assert.Equal(50, f.LookFrom.X, 6);
        Assert.Equal(60, f.LookFrom.Y, 6);
        Assert.Equal(3.6, f.LookFrom.Z, 6);
        Assert.Equal(0f, f.LookDir.X, 4);
        Assert.Equal(1f, f.LookDir.Y, 4);
        Assert.Equal(0f, f.LookDir.Z);
        Assert.Equal(50, f.AimPoint.X, 4);
        Assert.Equal(160, f.AimPoint.Y, 4);
        Assert.Equal(3.6, f.AimPoint.Z, 6);

        // a hand aim stays, the look ray is still filled
        world.SetAim(7, 8, 0);
        Assert.True(Until(world, gw, () =>
        {
            world.SendControl();
            return gw.ControlsSeen[^1].AimPoint.X == 7;
        }));
        Assert.Equal(1f, gw.ControlsSeen[^1].LookDir.Y, 4);
    }

    [Fact]
    public void No_automatic_join_after_leave_and_sim_time_follows_entity_state()
    {
        uint domain = NewDomain();
        double now = 0;
        using var gw = new FakeGateway(domain, "p7");
        using var world = new DpsWorld(domain, "p7", clock: () => now);

        gw.Slot.Write(Slot(D.SlotState.SlotStateChoosing));
        gw.Entities.Write(Ent(1));
        gw.States.Write(new D.EntityState { EntityId = 1, SimTime = 42.5 });
        Assert.True(Until(world, gw, () => world.Slot is not null && world.LatestSimTime > 0));
        Assert.Equal(42.5, world.LatestSimTime);

        Assert.True(Until(world, gw, () =>
        {
            now += 1.1;
            world.TryAutoJoin();
            return gw.JoinsSeen.Count > 0;
        }));
        Until(world, gw, () => false, 300);
        int joins = gw.JoinsSeen.Count;

        world.Leave();
        for (int i = 0; i < 10; i++)
        {
            now += 1.1;
            Assert.False(world.TryAutoJoin());
            Until(world, gw, () => false, 30);
        }

        Assert.Equal(joins, gw.JoinsSeen.Count);

        // an explicit join is allowed again
        Assert.True(world.JoinFirstUnit());
    }

    [Fact]
    public void A_new_world_epoch_resends_the_gizmo_view_and_restarts_sim_time()
    {
        uint domain = NewDomain();
        using var gw = new FakeGateway(domain, "p8");
        using var world = new DpsWorld(domain, "p8");

        gw.Session.Write(new D.Session { WorldEpoch = 1, Terrain = "", StationName = "" });
        gw.Entities.Write(Ent(1));
        gw.States.Write(new D.EntityState { EntityId = 1, SimTime = 42.5 });
        Assert.True(Until(world, gw, () => world.WorldEpoch == 1 && world.LatestSimTime > 0));
        world.SetGizmoView(["Paths"], [1]);
        Assert.True(Until(world, gw, () => gw.ViewsSeen.Count == 1));

        gw.Session.Write(new D.Session { WorldEpoch = 2, Terrain = "", StationName = "" });   // a world boundary: the gateway's readers are new

        Assert.True(Until(world, gw, () => world.WorldEpoch == 2 && gw.ViewsSeen.Count == 2), "the view is sent again for the new world");
        Assert.Equal("Paths", gw.ViewsSeen[1].Layers[0].Name);
        Assert.Equal(world.ClientId, gw.ViewsSeen[1].ClientId);
        Assert.Equal(0.0, world.LatestSimTime);
    }

    [Fact]
    public void Command_line_rejects_unknown_arguments()
    {
        var error = new StringWriter();
        Assert.Equal(2, DpsClientProgram.Run(["--bogus"], new StringWriter(), error));
        Assert.NotEqual(string.Empty, error.ToString());
    }
}
