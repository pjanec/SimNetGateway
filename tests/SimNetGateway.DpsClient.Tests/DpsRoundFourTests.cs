using System.Diagnostics;
using CycloneDDS.Runtime;
using SimNetGateway.DpsClient;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.DpsClient.Tests;

/// <summary>The reference client of DPS round 4 (DD-020): the aim point derived from the aim target, and the control frame's new enumerations beside the old bytes.</summary>
public class DpsRoundFourTests
{
    private static uint NewDomain() => DpsTestDomains.Next();

    private static bool Until(DpsWorld world, Func<bool> condition, int timeoutMs = 6000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            world.Pump();
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        world.Pump();
        return condition();
    }

    private sealed class Gateway : IDisposable
    {
        private readonly DdsParticipant _participant;

        public Gateway(uint domain, string partition)
        {
            _participant = new DdsParticipant(domain, defaultPartition: partition);
            Entities = new DdsWriter<D.Entity>(_participant);
            States = new DdsWriter<D.EntityState>(_participant);
            Aims = new DdsWriter<D.PersonAim>(_participant);
            Own = new DdsWriter<D.OwnUnit>(_participant);
            Slot = new DdsWriter<D.PlayerSlot>(_participant);
            Controls = new DdsReader<D.ControlFrame>(_participant);
        }

        public DdsWriter<D.Entity> Entities { get; }

        public DdsWriter<D.EntityState> States { get; }

        public DdsWriter<D.PersonAim> Aims { get; }

        public DdsWriter<D.OwnUnit> Own { get; }

        public DdsWriter<D.PlayerSlot> Slot { get; }

        public DdsReader<D.ControlFrame> Controls { get; }

        public void Dispose()
        {
            Controls.Dispose();
            _participant.Dispose();
        }
    }

    private static D.EntityState Unit(int id, double x, double y, float vx) => new()
    {
        EntityId = id, SimTime = 1.0, Position = new D.Vec3d { X = x, Y = y, Z = 0 }, Orientation = new D.Quat { W = 1 }, LinearVelocity = new D.Vec3f { X = vx },
    };

    [Fact]
    public void The_aim_point_is_the_targets_chest_derived_from_its_own_pose_and_the_fallback_when_the_target_is_unknown()
    {
        uint domain = NewDomain();
        using var gw = new Gateway(domain, "r4a");
        using var world = new DpsWorld(domain, "r4a");
        gw.Entities.Write(new D.Entity { EntityId = 77, Name = "Target" });
        gw.States.Write(Unit(77, 10, 20, 1f));
        gw.Aims.Write(new D.PersonAim { EntityId = 5, SimTime = 1.0, AimTargetId = 77, AimPoint = new D.Vec3d { X = 99, Y = 99, Z = 99 } });
        gw.Aims.Write(new D.PersonAim { EntityId = 6, SimTime = 1.0, AimTargetId = 4040, AimPoint = new D.Vec3d { X = 7, Y = 8, Z = 1 } });   // a target this client does not have
        gw.Aims.Write(new D.PersonAim { EntityId = 7, SimTime = 1.0, AimPoint = new D.Vec3d { X = 3, Y = 4, Z = 1 } });   // a point aim
        gw.Aims.Write(new D.PersonAim { EntityId = 8, SimTime = 1.0 });   // not aiming
        Assert.True(Until(world, () => world.Aims.Count == 4 && world.Entities.TryGetValue(77, out var e) && e.State is not null));

        double now = world.Entities[77].ReceivedAt + 2.0;   // the target walked on at 1 m/s
        var chest = world.AimPointOf(5, now);
        Assert.NotNull(chest);
        Assert.Equal((12.0, 20.0, DpsWorld.ChestHeight), (Math.Round(chest!.Value.X, 3), Math.Round(chest.Value.Y, 3), chest.Value.Z));   // the aim sample's own point (99, 99, 99) is not used
        Assert.Equal((7.0, 8.0, 1.0), (world.AimPointOf(6, now)!.Value.X, world.AimPointOf(6, now)!.Value.Y, world.AimPointOf(6, now)!.Value.Z));
        Assert.Equal(3.0, world.AimPointOf(7, now)!.Value.X);
        Assert.Null(world.AimPointOf(8, now));
        Assert.Null(world.AimPointOf(12345, now));
    }

    [Fact]
    public void The_look_point_follows_its_entity_and_is_absent_with_the_head_straight()
    {
        uint domain = NewDomain();
        using var gw = new Gateway(domain, "r4b");
        using var world = new DpsWorld(domain, "r4b");
        gw.Entities.Write(new D.Entity { EntityId = 88, Name = "Looked at" });
        gw.States.Write(Unit(88, 30, 40, 0f));
        gw.Aims.Write(new D.PersonAim { EntityId = 5, HasLookTarget = true, LookTargetId = 88, LookTarget = new D.Vec3d { X = 1, Y = 1, Z = 1 } });
        gw.Aims.Write(new D.PersonAim { EntityId = 6 });
        Assert.True(Until(world, () => world.Aims.Count == 2 && world.Entities.TryGetValue(88, out var e) && e.State is not null));

        Assert.Equal((30.0, 40.0), (world.LookPointOf(5, 0)!.Value.X, world.LookPointOf(5, 0)!.Value.Y));
        Assert.Null(world.LookPointOf(6, 0));
    }

    [Fact]
    public void A_control_frame_carries_the_new_enumerations_beside_the_old_bytes()
    {
        uint domain = NewDomain();
        using var gw = new Gateway(domain, "r4c");
        using var world = new DpsWorld(domain, "r4c");
        gw.Own.Write(new D.OwnUnit { UnitId = 1201, CanUse = true, UseVerb = 3, Verb = D.UseVerb.UseVerbCloseDoor, UseTargetId = 55, UseLabel = string.Empty, Ammo = [] });
        Assert.True(Until(world, () => world.Own is not null));

        world.PressUse();
        world.CycleStance();   // standing -> crouched
        world.SendControl();

        var frames = new List<D.ControlFrame>();
        Assert.True(Until(world, () =>
        {
            using var loan = gw.Controls.Take(8);
            foreach (var s in loan)
            {
                if (s.IsValid)
                {
                    frames.Add(s.Data);
                }
            }

            return frames.Count > 0;
        }));
        var f = frames[^1];
        Assert.Equal((D.Gait.GaitRun, (byte)0), (f.GaitMode, f.Gait));   // both say run
        Assert.Equal((D.StanceRequest.StanceRequestCrouched, (byte)2), (f.StanceWanted, f.Stance));
        Assert.Equal((D.UseVerb.UseVerbCloseDoor, (byte)3), (f.Verb, f.UseVerb));
    }
}
