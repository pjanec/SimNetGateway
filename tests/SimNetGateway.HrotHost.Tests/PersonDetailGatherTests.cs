using System.Numerics;
using Fbt;
using Fdp.Core;
using Fdp.Toolkit.Behavior;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Behavior.Executors;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Creatures;
using Fdp.Toolkit.Perception.Look;
using Fdp.Toolkit.Tkb.Domain;
using Hrot.MuscleCharacter.Animation.Components;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>The person-detail gather of the pack: stance transition, held item, look, gesture, interaction action and seat of HROT life forms (DD-017 3.5).</summary>
public class PersonDetailGatherTests
{
    private sealed class Capture : IIngressSink
    {
        public readonly List<HrotPersonDetail> Details = new();

        public void Deliver<T>(in T message, double? receivedAt = null)
        {
            if (message is HrotPersonDetail d)
            {
                Details.Add(d);
            }
        }
    }

    private static double _now;

    /// <summary>Runs the adapter's gather directly (the harness's own system gather is not stepped in between) and returns the person details it sent.</summary>
    private static List<HrotPersonDetail> Gather(PackHarness h, double simTime)
    {
        var inbound = new InMemoryInbound(new SourceId(99), "test");
        _now += 0.1;
        h.Adapter.GatherEntities(h.World, inbound, _now, simTime);
        var sink = new Capture();
        inbound.Drain(sink, int.MaxValue);
        return sink.Details;
    }

    private static PackHarness Live()
    {
        var h = new PackHarness(dis: true);
        h.RegisterTemplate(100, 1);   // a tank
        h.RegisterTemplate(300, 3);   // a soldier (DIS kind 3: life form)
        h.Steps(3);
        _now = 1000;
        return h;
    }

    private static Entity Soldier(PackHarness h, int id)
    {
        var e = h.AddHrotEntity(id, new Vector3(0, 0, 0), tkb: 300);
        h.World.AddComponent(e, new WeaponPosture { Kind = WeaponPostureKind.Ready, HeldItem = HeldItemKind.None });
        return e;
    }

    private static HrotPersonDetail Single(List<HrotPersonDetail> details, int id) => Assert.Single(details, d => d.Id == id);

    [Fact]
    public void Stance_transition_gives_target_and_progress()
    {
        using var h = Live();
        var e = Soldier(h, 2001);
        h.World.AddComponent(e, new StanceIntent { TargetStance = StanceId.Prone });
        h.World.AddComponent(e, new StanceStatus { CurrentStance = StanceId.Standing, Phase = StanceTransitionPhase.Transitioning, TransitionProgress = 0.5f });

        var d = Single(Gather(h, 1.0), 2001);

        Assert.Equal(HrotStance.Prone, d.TargetStance);
        Assert.Equal(0.5f, d.StanceProgress);
    }

    [Fact]
    public void A_settled_stance_has_progress_one()
    {
        using var h = Live();
        var e = Soldier(h, 2001);
        h.World.AddComponent(e, new StanceIntent { TargetStance = StanceId.Crouched });

        var d = Single(Gather(h, 1.0), 2001);

        Assert.Equal(HrotStance.Crouched, d.TargetStance);
        Assert.Equal(1f, d.StanceProgress);
    }

    [Fact]
    public void Held_item_and_mount_come_from_weapon_posture()
    {
        using var h = Live();
        var e = Soldier(h, 2001);
        h.World.SetComponent(e, new WeaponPosture { Kind = WeaponPostureKind.Ready, HeldItem = HeldItemKind.Throwable, ActiveMount = 2 });

        var d = Single(Gather(h, 1.0), 2001);

        Assert.Equal(HrotHeldItem.Throwable, d.HeldItem);
        Assert.Equal((byte)2, d.ActiveMount);
    }

    [Fact]
    public void Look_target_comes_from_look_state()
    {
        using var h = Live();
        var e = Soldier(h, 2001);
        h.World.AddComponent(e, new LookState { Target = new Vector3(10, 20, 1), HasTarget = 1, HeadYaw = new Angle(0.5f) });

        var d = Single(Gather(h, 1.0), 2001);

        Assert.True(d.HasLookTarget);
        Assert.Equal(new HrotVec3(10, 20, 1), d.LookTarget);
        Assert.Equal(0.5f, d.HeadYaw);
    }

    [Fact]
    public void A_look_state_without_a_target_has_no_look_target()
    {
        using var h = Live();
        var e = Soldier(h, 2001);
        h.World.AddComponent(e, new LookState { Target = new Vector3(10, 20, 1), HasTarget = 0, HeadYaw = new Angle(0.25f) });

        var d = Single(Gather(h, 1.0), 2001);

        Assert.False(d.HasLookTarget);
        Assert.Equal(HrotVec3.Zero, d.LookTarget);
        Assert.Equal(0.25f, d.HeadYaw);
    }

    [Fact]
    public void Gesture_is_published()
    {
        using var h = Live();
        var e = Soldier(h, 2001);
        h.World.AddComponent(e, new Demeanour { Kind = GestureKind.Waving });

        Assert.Equal(HrotGesture.Waving, Single(Gather(h, 1.0), 2001).Gesture);
    }

    private static unsafe void Channel(PackHarness h, Entity who, ushort action, NodeStatus status, uint instance, Entity target)
    {
        var channel = new InteractionChannel { ActiveAction = action, Status = status, ActionInstanceId = instance };
        *(Entity*)channel.Params = target;
        if (h.World.HasComponent<InteractionChannel>(who))
        {
            h.World.SetComponent(who, channel);
        }
        else
        {
            h.World.AddComponent(who, channel);
        }
    }

    [Fact]
    public void Interaction_channel_running_open_door_gives_action_with_start_time()
    {
        using var h = Live();
        var e = Soldier(h, 2001);
        var door = h.AddHrotEntity(3001, new Vector3(5, 0, 0));
        Channel(h, e, BehaviorConstants.ActionIdOpenDoor, NodeStatus.Running, 1, door);

        var first = Single(Gather(h, 5.0), 2001);
        Assert.Equal(HrotAction.OpenDoor, first.Action);
        Assert.Equal(HrotActionState.Running, first.ActionState);
        Assert.Equal(3001, first.ActionTarget);
        Assert.Equal(5.0, first.ActionStartedAt);

        // still running later: nothing changed, nothing is sent
        Assert.Empty(Gather(h, 6.0));

        // it settles: the start time stays the time it was first seen running
        Channel(h, e, BehaviorConstants.ActionIdOpenDoor, NodeStatus.Success, 1, door);
        var done = Single(Gather(h, 7.0), 2001);
        Assert.Equal(HrotActionState.Success, done.ActionState);
        Assert.Equal(5.0, done.ActionStartedAt);

        // a new run of the same action restarts the clock
        Channel(h, e, BehaviorConstants.ActionIdOpenDoor, NodeStatus.Running, 2, door);
        Assert.Equal(9.0, Single(Gather(h, 9.0), 2001).ActionStartedAt);
    }

    [Fact]
    public void A_channel_with_no_action_gives_none()
    {
        using var h = Live();
        var e = Soldier(h, 2001);
        Channel(h, e, 0, NodeStatus.Success, 0, Entity.Null);

        var d = Single(Gather(h, 1.0), 2001);

        Assert.Equal(HrotAction.None, d.Action);
        Assert.Equal(HrotActionState.None, d.ActionState);
        Assert.Equal(0.0, d.ActionStartedAt);
    }

    [Fact]
    public void Embarked_soldier_has_vehicle_and_seat_role()
    {
        using var h = Live();
        var vehicle = h.AddHrotEntity(2100, new Vector3(3, 0, 0));
        var seats = new CarrierSeats { SeatCount = 2 };
        seats.Roles[0] = SeatRole.Driver;
        seats.Roles[1] = SeatRole.Gunner;
        h.World.AddComponent(vehicle, seats);
        var e = Soldier(h, 2001);
        h.World.AddComponent(e, new IsEmbarkedTag { VehicleEntity = vehicle, Seat = 1, Mode = CarryMode.Seated });

        var d = Single(Gather(h, 1.0), 2001);

        Assert.Equal(2100, d.SeatVehicle);
        Assert.Equal(HrotSeatRole.Gunner, d.SeatRole);
        Assert.Equal(HrotCarryMode.Seated, d.Carry);
    }

    [Fact]
    public void A_carried_casualty_has_the_carry_mode()
    {
        using var h = Live();
        var carrier = Soldier(h, 2002);
        var seats = new CarrierSeats { SeatCount = 1 };
        seats.Roles[0] = SeatRole.Passenger;
        h.World.AddComponent(carrier, seats);
        var casualty = Soldier(h, 2001);
        h.World.AddComponent(casualty, new IsEmbarkedTag { VehicleEntity = carrier, Seat = 0, Mode = CarryMode.Dragged });

        var d = Single(Gather(h, 1.0), 2001);

        Assert.Equal(2002, d.SeatVehicle);
        Assert.Equal(HrotSeatRole.Passenger, d.SeatRole);
        Assert.Equal(HrotCarryMode.Dragged, d.Carry);
    }

    [Fact]
    public void Vehicle_gets_no_person_detail()
    {
        using var h = Live();
        var tank = h.AddHrotEntity(2100, new Vector3(3, 0, 0));
        h.World.AddComponent(tank, new Demeanour { Kind = GestureKind.Waving });
        h.World.AddComponent(tank, new LookState { HasTarget = 1, Target = new Vector3(1, 2, 3) });

        Assert.Empty(Gather(h, 1.0));
    }

    [Fact]
    public void Unchanged_person_sends_nothing_again()
    {
        using var h = Live();
        var e = Soldier(h, 2001);
        h.World.AddComponent(e, new Demeanour { Kind = GestureKind.HandsUp });

        Assert.Single(Gather(h, 1.0));
        Assert.Empty(Gather(h, 2.0));
        Assert.Empty(Gather(h, 3.0));

        h.World.SetComponent(e, new Demeanour { Kind = GestureKind.None });
        Assert.Equal(HrotGesture.None, Single(Gather(h, 4.0), 2001).Gesture);
    }

    [Fact]
    public void A_person_that_leaves_and_returns_is_sent_again()
    {
        using var h = Live();
        var e = Soldier(h, 2001);
        Assert.Single(Gather(h, 1.0));

        h.RemoveHrotEntity(e);
        Assert.Empty(Gather(h, 2.0));   // gone: the entity is removed, no detail
        Soldier(h, 2001);

        Assert.Single(Gather(h, 3.0));
    }
}
