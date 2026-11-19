using System.Diagnostics;
using CycloneDDS.Runtime;
using SimNetGateway.DpsClient;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.DpsClient.Tests;

/// <summary>The reference client's world against a fake gateway: real CycloneDDS readers and writers in one process.</summary>
public class DpsWorldTests
{
    private static int _domainCounter = (Environment.ProcessId + 17) % 50;

    private static uint NewDomain() => (uint)(130 + Interlocked.Increment(ref _domainCounter) % 80);

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
    public void Command_line_rejects_unknown_arguments()
    {
        var error = new StringWriter();
        Assert.Equal(2, DpsClientProgram.Run(["--bogus"], new StringWriter(), error));
        Assert.NotEqual(string.Empty, error.ToString());
    }
}
