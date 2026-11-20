using System.Numerics;
using Fbt;
using Fdp.Core;
using Fdp.Toolkit.Behavior;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Creatures;
using Fdp.Toolkit.Perception.Look;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Tkb.Domain;
using Hrot.MuscleCharacter.Animation.Components;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// Reads the semantic state of a HROT person beyond the pose (stance transition, held item, look, gesture, interaction action, seat) into
/// <see cref="HrotPersonDetail"/>, and remembers what was sent so that only a change is sent again. Every component is optional: a field HROT does not
/// provide stays at its None value. Main thread only.
/// </summary>
internal sealed class PersonDetailGather
{
    private sealed class Book
    {
        public HrotPersonDetail Sent;
        public bool HasSent;
        public ushort Action;
        public uint Instance;
        public double Started;
    }

    private readonly Dictionary<int, Book> _book = new();

    /// <summary>Registers the component types the gather reads, so that reading a world that never had one is safe.</summary>
    public static void EnsureRegistered(EntityRepository world)
    {
        Ensure<StanceStatus>(world);
        Ensure<LookState>(world);
        Ensure<Demeanour>(world);   // the gesture is Demeanour.Kind (read through Gestures.Of)
        Ensure<InteractionChannel>(world);
        Ensure<IsEmbarkedTag>(world);
        Ensure<CarrierSeats>(world);
    }

    private static void Ensure<T>(EntityRepository world)
        where T : unmanaged
    {
        if (!world.IsComponentTypeRegistered<T>())
        {
            world.RegisterComponent<T>();
        }
    }

    public void Forget(int id) => _book.Remove(id);

    public void Clear() => _book.Clear();

    /// <summary>True when the person's detail differs from the last one sent (or none was sent); <paramref name="detail"/> is then the new one.</summary>
    public bool TryGather(EntityRepository world, Entity e, int id, double simTime, out HrotPersonDetail detail)
    {
        if (!_book.TryGetValue(id, out var book))
        {
            _book[id] = book = new Book();
        }

        detail = Read(world, e, id, simTime, book);
        if (book.HasSent && book.Sent.Equals(detail))
        {
            return false;
        }

        book.Sent = detail;
        book.HasSent = true;
        return true;
    }

    private static HrotPersonDetail Read(EntityRepository world, Entity e, int id, double simTime, Book book)
    {
        var target = LogicalStance.Of(world, e) switch
        {
            StanceId.Crouched => HrotStance.Crouched,
            StanceId.Prone => HrotStance.Prone,
            _ => HrotStance.Standing,
        };
        float progress = 1f;
        if (world.HasComponent<StanceStatus>(e))
        {
            var status = world.GetComponentRO<StanceStatus>(e);
            if (status.Phase == StanceTransitionPhase.Transitioning)
            {
                progress = Math.Clamp(status.TransitionProgress, 0f, 1f);
            }
        }

        var held = HrotHeldItem.None;
        byte mount = 0;
        if (world.HasComponent<WeaponPosture>(e))
        {
            var posture = world.GetComponentRO<WeaponPosture>(e);
            held = (HrotHeldItem)(byte)posture.HeldItem;
            mount = posture.ActiveMount;
        }

        var lookTarget = HrotVec3.Zero;
        bool hasLook = false;
        float headYaw = 0f;
        if (world.HasComponent<LookState>(e))
        {
            var look = world.GetComponentRO<LookState>(e);
            headYaw = look.HeadYaw.Radians;
            if (look.HasTarget != 0)
            {
                hasLook = true;
                lookTarget = new HrotVec3(look.Target.X, look.Target.Y, look.Target.Z);
            }
        }

        var gesture = (HrotGesture)(byte)Gestures.Of(world, e);

        var action = HrotAction.None;
        var actionState = HrotActionState.None;
        int actionTarget = 0;
        double started = 0.0;
        if (world.HasComponent<InteractionChannel>(e))
        {
            var channel = world.GetComponentRO<InteractionChannel>(e);
            action = ActionOf(channel.ActiveAction);
            if (action != HrotAction.None)
            {
                actionState = channel.Status switch
                {
                    NodeStatus.Running => HrotActionState.Running,
                    NodeStatus.Success => HrotActionState.Success,
                    _ => HrotActionState.Failure,
                };
                if (book.Action != channel.ActiveAction || book.Instance != channel.ActionInstanceId)
                {
                    book.Action = channel.ActiveAction;
                    book.Instance = channel.ActionInstanceId;
                    book.Started = simTime;
                }

                started = book.Started;
                actionTarget = TargetOf(world, channel);
            }
        }

        if (action == HrotAction.None)
        {
            book.Action = 0;
            book.Instance = 0;
            book.Started = 0.0;
        }

        int seatVehicle = 0;
        var seatRole = HrotSeatRole.None;
        var carry = HrotCarryMode.None;
        if (world.HasComponent<IsEmbarkedTag>(e))
        {
            var tag = world.GetComponentRO<IsEmbarkedTag>(e);
            carry = tag.Mode switch
            {
                CarryMode.Carried => HrotCarryMode.Carried,
                CarryMode.Dragged => HrotCarryMode.Dragged,
                CarryMode.Stretcher => HrotCarryMode.Stretcher,
                _ => HrotCarryMode.Seated,
            };
            var vehicle = tag.VehicleEntity;
            seatVehicle = NetworkIdOf(world, vehicle);
            if (!vehicle.IsNull && world.IsAlive(vehicle) && world.HasComponent<CarrierSeats>(vehicle))
            {
                var seats = world.GetComponentRO<CarrierSeats>(vehicle);
                if (tag.Seat < seats.SeatCount && tag.Seat < PassengerBuffer.Capacity)
                {
                    seatRole = seats.Roles[tag.Seat] switch
                    {
                        SeatRole.Driver => HrotSeatRole.Driver,
                        SeatRole.Gunner => HrotSeatRole.Gunner,
                        SeatRole.Commander => HrotSeatRole.Commander,
                        SeatRole.Station => HrotSeatRole.Station,
                        _ => HrotSeatRole.Passenger,
                    };
                }
            }
        }

        return new HrotPersonDetail(id, target, progress, held, mount, lookTarget, hasLook, headYaw, gesture, action, actionTarget, actionState, started, seatVehicle,
            seatRole, carry);
    }

    private static HrotAction ActionOf(ushort actionId) => actionId switch
    {
        BehaviorConstants.ActionIdEmbark => HrotAction.Embark,
        BehaviorConstants.ActionIdDisembark => HrotAction.Disembark,
        BehaviorConstants.ActionIdOpenDoor => HrotAction.OpenDoor,
        BehaviorConstants.ActionIdCloseDoor => HrotAction.CloseDoor,
        BehaviorConstants.ActionIdLockDoor => HrotAction.LockDoor,
        BehaviorConstants.ActionIdUnlockDoor => HrotAction.UnlockDoor,
        BehaviorConstants.ActionIdBreachDoor => HrotAction.BreachDoor,
        BehaviorConstants.ActionIdPickUp => HrotAction.PickUpCasualty,
        BehaviorConstants.ActionIdPutDown => HrotAction.PutDownCasualty,
        BehaviorConstants.ActionIdPickUpRound => HrotAction.PickUpRound,
        BehaviorConstants.ActionIdEject => HrotAction.Eject,
        _ => HrotAction.None,
    };

    /// <summary>
    /// The entity an action is about: the first field of the action's parameter block for the actions that have one (vehicle, door, casualty, load, round).
    /// Disembark and eject carry no entity.
    /// </summary>
    private static unsafe int TargetOf(EntityRepository world, in InteractionChannel channel)
    {
        switch (channel.ActiveAction)
        {
            case BehaviorConstants.ActionIdEmbark:
            case BehaviorConstants.ActionIdOpenDoor:
            case BehaviorConstants.ActionIdCloseDoor:
            case BehaviorConstants.ActionIdLockDoor:
            case BehaviorConstants.ActionIdUnlockDoor:
            case BehaviorConstants.ActionIdBreachDoor:
            case BehaviorConstants.ActionIdPickUp:
            case BehaviorConstants.ActionIdPutDown:
            case BehaviorConstants.ActionIdPickUpRound:
                fixed (byte* p = channel.Params)
                {
                    return NetworkIdOf(world, *(Entity*)p);
                }
            default:
                return 0;
        }
    }

    private static int NetworkIdOf(EntityRepository world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.HasComponent<NetworkIdentity>(entity) ? world.GetComponentRO<NetworkIdentity>(entity).Value : 0;
}
