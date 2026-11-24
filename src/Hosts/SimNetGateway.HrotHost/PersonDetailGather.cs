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
using Hrot.UI.Common.Map3D;
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

    /// <summary>A head turn smaller than this is "straight on the body" (about 1 degree, radians).</summary>
    private const float HeadOffRadians = 0.02f;

    /// <summary>
    /// <paramref name="now"/> with the values that must not cause a send replaced by the ones last sent (DD-020 3.2): the stance progress, the head turn while it stays on the
    /// same side of "straight on", and the look point while the look follows an entity (the client derives both from the entity).
    /// </summary>
    private static HrotPersonDetail Normalized(in HrotPersonDetail sent, in HrotPersonDetail now)
    {
        bool headFlip = Math.Abs(sent.HeadYaw) >= HeadOffRadians != Math.Abs(now.HeadYaw) >= HeadOffRadians;
        return now with
        {
            StanceProgress = sent.StanceProgress,
            HeadYaw = headFlip ? now.HeadYaw : sent.HeadYaw,
            LookTarget = now.LookTargetId != 0 ? sent.LookTarget : now.LookTarget,
        };
    }

    /// <summary>True when the person's detail differs from the last one sent (or none was sent); <paramref name="detail"/> is then the new one.</summary>
    public bool TryGather(EntityRepository world, ITkbDatabase? tkb, Entity e, int id, double simTime, out HrotPersonDetail detail)
    {
        if (!_book.TryGetValue(id, out var book))
        {
            _book[id] = book = new Book();
        }

        detail = Read(world, tkb, e, id, simTime, book);
        if (book.HasSent && book.Sent.Equals(Normalized(in book.Sent, in detail)))
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
        // when HROT's muscle began the current blend, how long it lasts and the stance it leaves (StanceStatus, written by StanceTransitionSystem, replicated and recorded; DD-020 9 Round 6);
        // a blend is the current one only while it leaves another stance than the ordered one (one tick after an order the status still holds the previous blend); none run = not stated
        float progress = 1f;
        float stanceSeconds = 0f;
        double stanceStart = 0.0;
        var stanceFrom = target;
        if (world.HasComponent<StanceStatus>(e))
        {
            var status = world.GetComponentRO<StanceStatus>(e);
            if (status.Phase == StanceTransitionPhase.Transitioning)
            {
                progress = 0f;
            }

            var from = status.FromStance switch
            {
                StanceId.Crouched => HrotStance.Crouched,
                StanceId.Prone => HrotStance.Prone,
                _ => HrotStance.Standing,
            };
            if (status.TransitionSeconds.Seconds > 0f && from != target)
            {
                stanceStart = status.TransitionStartedAt.Seconds;
                stanceSeconds = status.TransitionSeconds.Seconds;
                stanceFrom = from;
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

        // DD-031: what is in his hands and slung, and its marking - the SAME calls HROT's renderer makes (EntityBodyLayer3D), so the client draws what HROT draws
        var heldLook = HrotHeldLook.Rifle;
        var slungLook = HrotHeldLook.Rifle;
        var marking = HrotHeldMarking.None;
        if (world.HasComponent<WeaponPosture>(e))
        {
            var (inHands, slung) = HeldWeapons.Of(world, e, tkb, out _);
            heldLook = HeldLookOf(inHands);
            slungLook = HeldLookOf(slung);
            marking = MarkingOf(HeldWeapons.MarkingOf(world, HeldWeapons.OwnMountInHands(world, e)));
        }

        var lookTarget = HrotVec3.Zero;
        bool hasLook = false;
        float headYaw = 0f;
        int lookTargetId = 0;
        if (world.HasComponent<LookState>(e))
        {
            var look = world.GetComponentRO<LookState>(e);
            headYaw = look.HeadYaw.Radians;
            if (look.HasTarget != 0)
            {
                hasLook = true;
                lookTarget = new HrotVec3(look.Target.X, look.Target.Y, look.Target.Z);
                lookTargetId = LookIds.NetworkIdOf(world, look.TargetEntity);
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
            seatRole, carry, burning, fear, anger, mode, carriage.SeatIndex, carriage.SeatExposed, carriage.SeatPose, carriage.Role, carriage.Other, carriage.Partner,
            stanceStart, stanceSeconds, lookTargetId, stanceFrom, heldLook, slungLook, marking);
    }

    // HROT's look enums by NAME, so a member HROT appends or reorders is never silently mislabelled (an unknown one falls back to the rifle / none)
    private static HrotHeldLook HeldLookOf(HeldLook look) => look switch
    {
        HeldLook.Pistol => HrotHeldLook.Pistol,
        HeldLook.MachineGun => HrotHeldLook.MachineGun,
        HeldLook.ScopedRifle => HrotHeldLook.ScopedRifle,
        HeldLook.Rpg => HrotHeldLook.Rpg,
        HeldLook.MissileTube => HrotHeldLook.MissileTube,
        HeldLook.Manpads => HrotHeldLook.Manpads,
        HeldLook.Mortar => HrotHeldLook.Mortar,
        HeldLook.Bow => HrotHeldLook.Bow,
        HeldLook.Crossbow => HrotHeldLook.Crossbow,
        _ => HrotHeldLook.Rifle,
    };

    private static HrotHeldMarking MarkingOf(HeldMarking marking) => marking switch
    {
        HeldMarking.Toy => HrotHeldMarking.Toy,
        HeldMarking.LessLethal => HrotHeldMarking.LessLethal,
        _ => HrotHeldMarking.None,
    };

    /// <summary>The carriage facts of one person (DD-018 3.5c), in network ids.</summary>
    internal readonly record struct CarriageFacts(HrotCarryRole Role, int Other, int Partner, byte SeatIndex, bool SeatExposed, string SeatPose)
    {
        public static readonly CarriageFacts Free = new(HrotCarryRole.None, 0, 0, HrotPersonDetail.NoSeat, false, string.Empty);
    }

    /// <summary>
    /// How a person is held or what he carries: HROT's own classifier <c>Hrot.UI.Common.Map3D.Carriage.Of</c> (Hrot.Look), its kinds mapped to the contract's roles and its
    /// entities to network ids. HROT's answer names the holder of a carried, dragged, seated or manning person and the load or partner of a team carry, but not the casualty a
    /// carrier holds: for the carrier's roles the casualty is the first man in his passenger list (a weapon system there is the load, HROT's own test). A man in an enclosed seat
    /// is "Free" to HROT (it draws nobody there), but the seat fields are still reported from his embarked tag.
    /// </summary>
    internal static CarriageFacts CarriageOf(EntityRepository world, ITkbDatabase? tkb, Entity e)
    {
        var c = Carriage.Of(world, e, tkb);
        switch (c.Kind)
        {
            case CarriageKind.Carried:
                return CarriageFacts.Free with { Role = HrotCarryRole.Carried, Other = NetworkIdOf(world, c.Holder) };
            case CarriageKind.Dragged:
                return CarriageFacts.Free with { Role = HrotCarryRole.Dragged, Other = NetworkIdOf(world, c.Holder) };
            case CarriageKind.OnStretcher:
                return CarriageFacts.Free with { Role = HrotCarryRole.OnStretcher, Other = NetworkIdOf(world, c.Holder), Partner = NetworkIdOf(world, c.Partner) };
            case CarriageKind.Carrying:
                return CarriageFacts.Free with { Role = HrotCarryRole.Carrying, Other = NetworkIdOf(world, CasualtyOf(world, e)) };
            case CarriageKind.Dragging:
                return CarriageFacts.Free with { Role = HrotCarryRole.Dragging, Other = NetworkIdOf(world, CasualtyOf(world, e)) };
            case CarriageKind.StretcherFront:
                return CarriageFacts.Free with { Role = HrotCarryRole.StretcherFront, Other = NetworkIdOf(world, CasualtyOf(world, e)), Partner = NetworkIdOf(world, c.Partner) };
            case CarriageKind.StretcherRear:
                return CarriageFacts.Free with { Role = HrotCarryRole.StretcherRear, Other = NetworkIdOf(world, CasualtyOf(world, c.Holder)), Partner = NetworkIdOf(world, c.Holder) };
            case CarriageKind.TeamFront:
                return CarriageFacts.Free with { Role = HrotCarryRole.TeamLoadFront, Other = NetworkIdOf(world, c.Load), Partner = NetworkIdOf(world, c.Partner) };
            case CarriageKind.TeamRear:
                return CarriageFacts.Free with { Role = HrotCarryRole.TeamLoadRear, Other = NetworkIdOf(world, c.Load), Partner = NetworkIdOf(world, c.Holder) };
            case CarriageKind.Seated:
            case CarriageKind.Manning:
                return SeatFacts(world, tkb, e, c.Holder);
            default:
                // Free, Downed, and the seats HROT does not draw: an embarked man still reports where he sits
                return world.HasComponent<IsEmbarkedTag>(e) && world.GetComponentRO<IsEmbarkedTag>(e).VehicleEntity is { IsNull: false } holder && world.IsAlive(holder)
                    ? Embarkation.IsPersonCarrier(world, holder) ? CarriageFacts.Free with { Other = NetworkIdOf(world, holder) } : SeatFacts(world, tkb, e, holder)
                    : CarriageFacts.Free;
        }
    }

    private static CarriageFacts SeatFacts(EntityRepository world, ITkbDatabase? tkb, Entity e, Entity holder)
    {
        var tag = world.GetComponentRO<IsEmbarkedTag>(e);
        var seat = Carriage.SeatOf(world, holder, tag.Seat, tkb);
        return new CarriageFacts(HrotCarryRole.None, NetworkIdOf(world, holder), 0, tag.Seat, tag.Exposed != 0, seat?.Pose.ToString() ?? string.Empty);
    }

    // the casualty a carrier holds: the first man (not a weapon system) in his passenger list
    private static Entity CasualtyOf(EntityRepository world, Entity carrier)
    {
        if (!world.HasComponent<PassengerBuffer>(carrier))
        {
            return default;
        }

        var list = world.GetComponentRO<PassengerBuffer>(carrier);
        for (int i = 0; i < PassengerBuffer.Capacity; i++)
        {
            var p = list.Passengers[i];
            if (!p.IsNull && world.IsAlive(p) && !WeaponSystems.IsWeaponSystem(world, p))
            {
                return p;
            }
        }

        return default;
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
