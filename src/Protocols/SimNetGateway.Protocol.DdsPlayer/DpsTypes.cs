using CycloneDDS.Schema;

namespace SimNetGateway.Protocol.DdsPlayer;

// Enumerations of the DPS contract (DD-017 3.3-3.10). Every value 0 is the "unknown / none" value, so a field the simulation
// does not provide yet is sent as 0. Values are only ever appended.
// IDL enumerators share one scope per module, so every enumerator carries its enum's name as a prefix (StanceCrouched).

public enum Domain : byte { DomainOther = 0, DomainPerson, DomainGround, DomainAir, DomainSurface, DomainSubsurface, DomainMunition }

public enum SessionMode : byte { SessionModeLive = 0, SessionModePaused, SessionModeReplay }

public enum Stance : byte { StanceStanding = 0, StanceCrouched, StanceProne }

public enum WeaponPosture : byte { WeaponPostureAtEase = 0, WeaponPostureReady, WeaponPostureAimed }

public enum HeldItem : byte { HeldItemNone = 0, HeldItemWeapon, HeldItemThrowable, HeldItemPhone, HeldItemBinoculars }

public enum Gesture : byte
{
    GestureNone = 0, GestureHandsUp, GestureFistRaised, GestureHoldSign, GestureWaving, GestureThrowing, GestureWarning,
    GestureIdleSmoking, GestureIdleShiftWeight, GestureIdleStretch, GestureIdleLookAbout,
}

public enum Injury : byte { InjuryOk = 0, InjuryWounded, InjuryIncapacitated, InjuryDead }

public enum ActionKind : byte
{
    ActionKindNone = 0, ActionKindEmbark, ActionKindDisembark, ActionKindOpenDoor, ActionKindCloseDoor, ActionKindLockDoor,
    ActionKindUnlockDoor, ActionKindBreachDoor, ActionKindPickUpCasualty, ActionKindPutDownCasualty, ActionKindPickUpRound,
    ActionKindEject,
}

public enum ActionState : byte { ActionStateNone = 0, ActionStateRunning, ActionStateSuccess, ActionStateFailure }

public enum SeatRole : byte { SeatRoleNone = 0, SeatRoleDriver, SeatRoleGunner, SeatRoleCommander, SeatRolePassenger, SeatRoleStation }

public enum CarryMode : byte { CarryModeNone = 0, CarryModeCarried, CarryModeDragged, CarryModeStretcher }

public enum SlotState : byte { SlotStateWaiting = 0, SlotStateObserving, SlotStateChoosing, SlotStateRequesting, SlotStateSpawning, SlotStateTakingControl, SlotStatePlaying, SlotStateDead }

/// <summary>What a <see cref="JoinRequest"/> asks for. Role is answered in a later delivery step (DD-017 3.2b).</summary>
public enum JoinKind : byte { JoinKindObserve = 0, JoinKindUnit, JoinKindRole }

public enum OfferKind : byte { OfferKindUnit = 0, OfferKindRole }

public enum GizmoShape : byte { GizmoShapeLineStrip = 0, GizmoShapeArrow, GizmoShapeCircle, GizmoShapePolygon, GizmoShapeBox, GizmoShapeText, GizmoShapeIcon }

/// <summary>The condition of a vehicle part (HROT's look library).</summary>
public enum PartCondition : byte { PartConditionIntact = 0, PartConditionDamaged, PartConditionDestroyed }

public enum KillKind : byte { KillKindNone = 0, KillKindMobility, KillKindFirepower, KillKindCatastrophic, KillKindOther }

public enum EffectKind : byte { EffectKindNone = 0, EffectKindMuzzleFlash, EffectKindExplosion, EffectKindGroundMark, EffectKindWreckFlame, EffectKindWreckSmoke }

public enum EffectSize : byte { EffectSizeUnknown = 0, EffectSizeSmall, EffectSizeMedium, EffectSizeLarge }

public enum EffectSurface : byte { EffectSurfaceGround = 0, EffectSurfaceWater, EffectSurfaceBuilding, EffectSurfaceVehicle, EffectSurfacePerson, EffectSurfaceAir }

public enum Sex : byte { SexUnknown = 0, SexMale, SexFemale }

public enum AgeClass : byte { AgeClassUnknown = 0, AgeClassChild, AgeClassAdult, AgeClassElder }
