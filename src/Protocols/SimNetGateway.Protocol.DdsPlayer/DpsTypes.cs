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

/// <summary>How a vehicle was destroyed (HROT's destruction kinds): None = not destroyed; Unknown = destroyed in a way this contract does not name.</summary>
public enum KillKind : byte { KillKindNone = 0, KillKindKnockedOut, KillKindBurntOut, KillKindCatastrophic, KillKindUnknown }

/// <summary>The effect's category; None = a kind this contract does not name (the effect's TKB <c>EffectType</c> still identifies it).</summary>
public enum EffectKind : byte { EffectKindNone = 0, EffectKindMuzzleFlash, EffectKindExplosion, EffectKindGroundMark, EffectKindWreckFlame, EffectKindWreckSmoke }

public enum EffectSize : byte { EffectSizeUnknown = 0, EffectSizeSmall, EffectSizeMedium, EffectSizeLarge }

public enum EffectSurface : byte { EffectSurfaceUnknown = 0, EffectSurfaceGround, EffectSurfaceWater, EffectSurfaceBuilding, EffectSurfaceVehicle, EffectSurfacePerson, EffectSurfaceAir }

public enum Sex : byte { SexUnknown = 0, SexMale, SexFemale }

public enum AgeClass : byte { AgeClassUnknown = 0, AgeClassChild, AgeClassAdult, AgeClassElder }

/// <summary>A civilian's mode (HROT's <c>CivilianMode</c>, by name); Unknown = none or a mode this contract does not name.</summary>
public enum CivilianMode : byte
{
    CivilianModeUnknown = 0, CivilianModeRoutine, CivilianModeAlarmed, CivilianModePanic, CivilianModeShelter, CivilianModeInside, CivilianModeCower,
    CivilianModeCasualty, CivilianModeProtest, CivilianModeRiot, CivilianModeDispersing, CivilianModeArmedHostile, CivilianModeGather, CivilianModeSurrender,
    CivilianModeWatch,
}

/// <summary>A weapon mount's category (HROT's <c>WeaponCategory</c>, by name); Unknown = none or a category this contract does not name.</summary>
public enum WeaponCategory : byte
{
    WeaponCategoryUnknown = 0, WeaponCategorySmallArms, WeaponCategoryMachineGun, WeaponCategoryAutocannon, WeaponCategoryTankGun, WeaponCategoryAtgm,
    WeaponCategoryMortar, WeaponCategoryGrenade, WeaponCategoryBomb, WeaponCategoryRocket, WeaponCategoryHowitzer, WeaponCategorySam,
    WeaponCategoryCountermeasure, WeaponCategoryAirToAir, WeaponCategoryBow, WeaponCategorySmokeDischarger, WeaponCategoryFlamethrower,
}

/// <summary>The form of a round (HROT's <c>MunitionShape</c>, by name); Unknown = a shape this contract does not name.</summary>
public enum MunitionShape : byte
{
    MunitionShapeUnknown = 0, MunitionShapeGrenade, MunitionShapeBomb, MunitionShapeShell, MunitionShapeRocket, MunitionShapeMissile, MunitionShapeBullet,
    MunitionShapeAxe, MunitionShapeSpear, MunitionShapeStar, MunitionShapeArrow,
}

/// <summary>What left a ground heat mark (HROT's <c>HeatMarkKind</c>, by name); Unknown = no mark or a kind this contract does not name.</summary>
public enum HeatMarkKind : byte { HeatMarkKindUnknown = 0, HeatMarkKindVehicleStand, HeatMarkKindBurnedArea, HeatMarkKindBodyImprint }

/// <summary>The hottest end of a heat mark (HROT's <c>HeatMarkEnd</c>, by name); Unknown = none or not named.</summary>
public enum HeatMarkEnd : byte { HeatMarkEndUnknown = 0, HeatMarkEndFront, HeatMarkEndRear }

/// <summary>How the selected weapon's trigger works (HROT's <c>OperatorFireMode</c>, by name): Sight = each round goes where the crosshair points, Lock = a press holds the point.</summary>
public enum FireMode : byte { FireModeSight = 0, FireModeLock }

/// <summary>The brain's answer to the last use press (HROT's <c>UseOutcome</c>, by name); None = no answer yet.</summary>
public enum UseOutcome : byte { UseOutcomeNone = 0, UseOutcomeRunning, UseOutcomeDone, UseOutcomeRefused }

/// <summary>Why a use press was refused (HROT's <c>UseReason</c>, by name); Unknown = a reason this contract does not name.</summary>
public enum UseReason : byte
{
    UseReasonUnknown = 0, UseReasonOk, UseReasonOutOfReach, UseReasonLocked, UseReasonDoorRefuses, UseReasonNotDown, UseReasonSeatTaken, UseReasonUnfit,
    UseReasonCarrying, UseReasonNothingToDo, UseReasonFailed, UseReasonOtherSide,
}

/// <summary>What the aircraft's radar warning receiver says (HROT's <c>RadarWarningLevel</c>, by name).</summary>
public enum RadarWarningLevel : byte { RadarWarningLevelNone = 0, RadarWarningLevelSearched, RadarWarningLevelTracked }

/// <summary>A missile's seeker (HROT's <c>GuidanceKind</c>, by name); Unknown = a kind this contract does not name.</summary>
public enum MissileSeeker : byte
{
    MissileSeekerUnknown = 0, MissileSeekerUnguided, MissileSeekerCommand, MissileSeekerFireAndForget, MissileSeekerInfraRed, MissileSeekerRadar,
}

/// <summary>Who a player message is for (HROT's <c>PlayerMessageEvent.ToKind</c>, by name); Unknown = an address this contract does not name.</summary>
public enum MessageAddress : byte { MessageAddressUnknown = 0, MessageAddressAll, MessageAddressSide, MessageAddressStation, MessageAddressUnit, MessageAddressInstructor }

/// <summary>What a rule entity is (HROT's <c>ObjectiveRole</c>, by name).</summary>
public enum ObjectiveRole : byte { ObjectiveRoleUnknown = 0, ObjectiveRoleObjective, ObjectiveRoleMeasure, ObjectiveRoleTrigger }

/// <summary>The lifecycle of an objective (HROT's <c>ObjectiveState</c>, by name).</summary>
public enum ObjectiveState : byte
{
    ObjectiveStateUnknown = 0, ObjectiveStatePending, ObjectiveStateActive, ObjectiveStateAchieved, ObjectiveStateFailed, ObjectiveStateInactive, ObjectiveStateSkipped,
}

/// <summary>Where a trigger is in its state machine (HROT's <c>TriggerState</c>, by name).</summary>
public enum TriggerState : byte
{
    TriggerStateUnknown = 0, TriggerStateDisarmed, TriggerStateArmed, TriggerStateHolding, TriggerStateWaiting, TriggerStateActing, TriggerStateFired, TriggerStateCooldown,
    TriggerStateDone,
}

/// <summary>Where the exercise is (HROT's <c>ExercisePhase</c>, by name).</summary>
public enum ExercisePhase : byte { ExercisePhaseUnknown = 0, ExercisePhaseRunning, ExercisePhaseClosing, ExercisePhaseEnded }

/// <summary>How a side came out (HROT's <c>SideOutcome</c>, by name); None = not decided or a value this contract does not name.</summary>
public enum SideOutcome : byte { SideOutcomeNone = 0, SideOutcomeWin, SideOutcomeLose, SideOutcomeDraw }
