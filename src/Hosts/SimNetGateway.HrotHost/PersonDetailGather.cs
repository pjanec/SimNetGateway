using System.Numerics;
using Fbt;
using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Behavior;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Civilians;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Creatures;
using Fdp.Toolkit.Perception.Look;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Tkb.Domain;
using Hrot.Look.People;
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
        Ensure<EffectState>(world);   // PersonLook.IsBurning reads EffectState.BurningUntil
        Ensure<InteractionChannel>(world);
        Ensure<IsEmbarkedTag>(world);
        Ensure<CarrierSeats>(world);
        Ensure<PassengerBuffer>(world);   // the carriage rule (DD-018 3.5c)
        Ensure<IsAssistingTag>(world);
        Ensure<PersonCarrier>(world);
        Ensure<WeaponSystemParams>(world);
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
    public bool TryGather(EntityRepository world, ITkbDatabase? tkb, Entity e, int id, double simTime, out HrotPersonDetail detail)
    {
        if (!_book.TryGetValue(id, out var book))
        {
            _book[id] = book = new Book();
        }

        detail = Read(world, tkb, e, id, simTime, book);
        if (book.HasSent && book.Sent.Equals(detail))
        {
            return false;
        }

        book.Sent = detail;
        book.HasSent = true;
        return true;
    }

    private static HrotPersonDetail Read(EntityRepository world, ITkbDatabase? tkb, Entity e, int id, double simTime, Book book)
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

        // fear, anger (quarters) and the civilian mode by name: HROT's demeanour; burning: HROT's own person look
        byte fear = 0, anger = 0;
        string mode = string.Empty;
        if (world.HasComponent<Demeanour>(e))
        {
            var demeanour = world.GetComponentRO<Demeanour>(e);
            fear = demeanour.Fear;
            anger = demeanour.Anger;
            mode = demeanour.Mode == CivilianMode.None ? string.Empty : demeanour.Mode.ToString();
        }

        bool burning = world.IsComponentTypeRegistered<EffectState>() && PersonLook.IsBurning(world, e, simTime);

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

        var carriage = CarriageOf(world, tkb, e);

        return new HrotPersonDetail(id, target, progress, held, mount, lookTarget, hasLook, headYaw, gesture, action, actionTarget, actionState, started, seatVehicle,
            seatRole, carry, burning, fear, anger, mode, carriage.SeatIndex, carriage.SeatExposed, carriage.SeatPose, carriage.Role, carriage.Other, carriage.Partner);
    }

    /// <summary>The carriage facts of one person (DD-018 3.5c), in network ids.</summary>
    internal readonly record struct CarriageFacts(HrotCarryRole Role, int Other, int Partner, byte SeatIndex, bool SeatExposed, string SeatPose)
    {
        public static readonly CarriageFacts Free = new(HrotCarryRole.None, 0, 0, HrotPersonDetail.NoSeat, false, string.Empty);
    }

    /// <summary>
    /// How a person is held or what he carries. A port of HROT's classifier Hrot.UI.Common.Map3D.Carriage.Of (Hrot.Presentation, which the host must not reference),
    /// same order of questions: embarked on a person carrier (carried, dragged, stretcher), embarked elsewhere (a seat), the second man of a team carry, a carrier
    /// with a casualty or a two-man weapon. Differences: the downed / wounded / knocked-down looks are not carriage, and a man seated in an enclosed seat is reported
    /// too (the seat fields; HROT draws nobody there).
    /// </summary>
    internal static CarriageFacts CarriageOf(EntityRepository world, ITkbDatabase? tkb, Entity e)
    {
        if (world.HasComponent<IsEmbarkedTag>(e))
        {
            var tag = world.GetComponentRO<IsEmbarkedTag>(e);
            var holder = tag.VehicleEntity;
            if (!holder.IsNull && world.IsAlive(holder))
            {
                int holderId = NetworkIdOf(world, holder);
                if (Embarkation.IsPersonCarrier(world, holder))
                {
                    return tag.Mode switch
                    {
                        CarryMode.Carried => CarriageFacts.Free with { Role = HrotCarryRole.Carried, Other = holderId },
                        CarryMode.Dragged => CarriageFacts.Free with { Role = HrotCarryRole.Dragged, Other = holderId },
                        CarryMode.Stretcher => CarriageFacts.Free with { Role = HrotCarryRole.OnStretcher, Other = holderId, Partner = NetworkIdOf(world, AssistantOf(world, holder)) },
                        _ => CarriageFacts.Free with { Other = holderId },
                    };
                }

                // a seat of a vehicle, an animal or a weapon system
                var seat = SeatOf(world, holder, tag.Seat, tkb);
                return new CarriageFacts(HrotCarryRole.None, holderId, 0, tag.Seat, tag.Exposed != 0, seat?.Pose.ToString() ?? string.Empty);
            }
        }

        if (world.HasComponent<IsAssistingTag>(e))
        {
            var front = world.GetComponentRO<IsAssistingTag>(e).Carrier;
            if (!front.IsNull && world.IsAlive(front) && world.HasComponent<PassengerBuffer>(front))
            {
                var (stretcher, load) = TeamLoadOf(world, front);
                if (!load.IsNull)
                {
                    return CarriageFacts.Free with
                    {
                        Role = stretcher ? HrotCarryRole.StretcherRear : HrotCarryRole.TeamLoadRear, Other = NetworkIdOf(world, load), Partner = NetworkIdOf(world, front),
                    };
                }
            }
        }

        if (world.HasComponent<PassengerBuffer>(e) && Embarkation.IsPersonCarrier(world, e))
        {
            // the casualty seat and the carry station are read apart: a weapon system in the list is the load he carries, only a man in it makes him a carrier
            var list = world.GetComponentRO<PassengerBuffer>(e);
            Entity load = default;
            Entity casualty = default;
            int casualtySeat = -1;
            for (int i = 0; i < PassengerBuffer.Capacity; i++)
            {
                var p = list.Passengers[i];
                if (p.IsNull || !world.IsAlive(p))
                {
                    continue;
                }

                if (WeaponSystems.IsWeaponSystem(world, p))
                {
                    if (load.IsNull)
                    {
                        load = p;
                    }
                }
                else if (casualtySeat < 0)
                {
                    casualtySeat = i;
                    casualty = p;
                }
            }

            int assistant = NetworkIdOf(world, list.Assistant);
            if (casualtySeat >= 0)
            {
                int other = NetworkIdOf(world, casualty);
                return list.ModeOf(casualtySeat) switch
                {
                    CarryMode.Dragged => CarriageFacts.Free with { Role = HrotCarryRole.Dragging, Other = other },
                    CarryMode.Stretcher => CarriageFacts.Free with { Role = HrotCarryRole.StretcherFront, Other = other, Partner = assistant },
                    _ => CarriageFacts.Free with { Role = HrotCarryRole.Carrying, Other = other },
                };
            }

            if (!load.IsNull && assistant != 0 && Embarkation.IsTwoManWeapon(world, load))
            {
                return CarriageFacts.Free with { Role = HrotCarryRole.TeamLoadFront, Other = NetworkIdOf(world, load), Partner = assistant };
            }
        }

        return CarriageFacts.Free;
    }

    private static Entity AssistantOf(EntityRepository world, Entity carrier) =>
        world.HasComponent<PassengerBuffer>(carrier) ? world.GetComponentRO<PassengerBuffer>(carrier).Assistant : default;

    // what a team carrier carries with his assistant: a stretcher (its casualty is the load), or the two-man weapon in his carry station
    private static (bool Stretcher, Entity Load) TeamLoadOf(EntityRepository world, Entity carrier)
    {
        var list = world.GetComponentRO<PassengerBuffer>(carrier);
        Entity weapon = default;
        for (int i = 0; i < PassengerBuffer.Capacity; i++)
        {
            var p = list.Passengers[i];
            if (p.IsNull || !world.IsAlive(p))
            {
                continue;
            }

            if (WeaponSystems.IsWeaponSystem(world, p))
            {
                if (weapon.IsNull && Embarkation.IsTwoManWeapon(world, p))
                {
                    weapon = p;
                }
            }
            else if (list.ModeOf(i) == CarryMode.Stretcher)
            {
                return (true, p);
            }
        }

        return (false, weapon);
    }

    // seat <seat> of the holder's type (its Gen.Carrier descriptor), or null (HROT: Carriage.SeatOf)
    private static SeatDto? SeatOf(EntityRepository world, Entity holder, int seat, ITkbDatabase? tkb)
    {
        if (tkb is null || !world.IsComponentTypeRegistered<TkbIdentity>() || !world.HasComponent<TkbIdentity>(holder)
            || !tkb.TryGetByType(world.GetComponentRO<TkbIdentity>(holder).TkbType, out var template) || template is null)
        {
            return null;
        }

        var seats = template.GetDescriptor<CarrierDefDto>()?.Seats;
        return seats is not null && seat >= 0 && seat < seats.Count ? seats[seat] : null;
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
