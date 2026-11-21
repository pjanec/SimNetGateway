using CycloneDDS.Schema;

namespace SimNetGateway.Protocol.DdsPlayer;

// Enumerations of the DPS contract (DD-017 3.3-3.10). Every value 0 is the "unknown / none" value, so a field the simulation
// does not provide yet is sent as 0. Values are only ever appended.
// IDL enumerators share one scope per module, so every enumerator carries its enum's name as a prefix (StanceCrouched).

public enum Domain : byte { DomainOther = 0, DomainPerson, DomainGround, DomainAir, DomainSurface, DomainSubsurface, DomainMunition, DomainAnimal }

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

/// <summary>How a man sits in a vehicle seat; Unknown = not named or no seat.</summary>
public enum SeatPose : byte { SeatPoseUnknown = 0, SeatPoseSit, SeatPoseSitLegsOut, SeatPoseStand, SeatPoseKneel, SeatPoseRide }

/// <summary>How a man is held, or what he carries (the producer's carriage rule); None = free or seated.</summary>
public enum CarryRole : byte
{
    CarryRoleNone = 0, CarryRoleCarried, CarryRoleDragged, CarryRoleOnStretcher, CarryRoleCarrying, CarryRoleDragging,
    CarryRoleStretcherFront, CarryRoleStretcherRear, CarryRoleTeamLoadFront, CarryRoleTeamLoadRear,
}

public enum SlotState : byte { SlotStateWaiting = 0, SlotStateObserving, SlotStateChoosing, SlotStateRequesting, SlotStateSpawning, SlotStateTakingControl, SlotStatePlaying, SlotStateDead }

/// <summary>What a <see cref="JoinRequest"/> asks for. Role is answered in a later delivery step (DD-017 3.2b).</summary>
public enum JoinKind : byte { JoinKindObserve = 0, JoinKindUnit, JoinKindRole }

public enum OfferKind : byte { OfferKindUnit = 0, OfferKindRole }

public enum GizmoShape : byte { GizmoShapeLineStrip = 0, GizmoShapeArrow, GizmoShapeCircle, GizmoShapePolygon, GizmoShapeBox, GizmoShapeText, GizmoShapeIcon }

/// <summary>The condition of a vehicle part (intact, damaged or destroyed).</summary>
public enum PartCondition : byte { PartConditionIntact = 0, PartConditionDamaged, PartConditionDestroyed }

/// <summary>How a vehicle was destroyed (the producer's destruction kinds): None = not destroyed; Unknown = destroyed in a way this contract does not name.</summary>
public enum KillKind : byte { KillKindNone = 0, KillKindKnockedOut, KillKindBurntOut, KillKindCatastrophic, KillKindUnknown }

/// <summary>The effect's category; None = a kind this contract does not name (the effect's TKB <c>EffectType</c> still identifies it).</summary>
public enum EffectKind : byte { EffectKindNone = 0, EffectKindMuzzleFlash, EffectKindExplosion, EffectKindGroundMark, EffectKindWreckFlame, EffectKindWreckSmoke }

public enum EffectSize : byte { EffectSizeUnknown = 0, EffectSizeSmall, EffectSizeMedium, EffectSizeLarge }

public enum EffectSurface : byte { EffectSurfaceUnknown = 0, EffectSurfaceGround, EffectSurfaceWater, EffectSurfaceBuilding, EffectSurfaceVehicle, EffectSurfacePerson, EffectSurfaceAir }

public enum Sex : byte { SexUnknown = 0, SexMale, SexFemale }

public enum AgeClass : byte { AgeClassUnknown = 0, AgeClassChild, AgeClassAdult, AgeClassElder }

/// <summary>A civilian's mode; Unknown = none or a mode this contract does not name.</summary>
public enum CivilianMode : byte
{
    CivilianModeUnknown = 0, CivilianModeRoutine, CivilianModeAlarmed, CivilianModePanic, CivilianModeShelter, CivilianModeInside, CivilianModeCower,
    CivilianModeCasualty, CivilianModeProtest, CivilianModeRiot, CivilianModeDispersing, CivilianModeArmedHostile, CivilianModeGather, CivilianModeSurrender,
    CivilianModeWatch,
}

/// <summary>A weapon mount's category; Unknown = none or a category this contract does not name.</summary>
public enum WeaponCategory : byte
{
    WeaponCategoryUnknown = 0, WeaponCategorySmallArms, WeaponCategoryMachineGun, WeaponCategoryAutocannon, WeaponCategoryTankGun, WeaponCategoryAtgm,
    WeaponCategoryMortar, WeaponCategoryGrenade, WeaponCategoryBomb, WeaponCategoryRocket, WeaponCategoryHowitzer, WeaponCategorySam,
    WeaponCategoryCountermeasure, WeaponCategoryAirToAir, WeaponCategoryBow, WeaponCategorySmokeDischarger, WeaponCategoryFlamethrower,
}

/// <summary>The form of a round; Unknown = a shape this contract does not name.</summary>
public enum MunitionShape : byte
{
    MunitionShapeUnknown = 0, MunitionShapeGrenade, MunitionShapeBomb, MunitionShapeShell, MunitionShapeRocket, MunitionShapeMissile, MunitionShapeBullet,
    MunitionShapeAxe, MunitionShapeSpear, MunitionShapeStar, MunitionShapeArrow,
}

/// <summary>What left a ground heat mark; Unknown = no mark or a kind this contract does not name.</summary>
public enum HeatMarkKind : byte { HeatMarkKindUnknown = 0, HeatMarkKindVehicleStand, HeatMarkKindBurnedArea, HeatMarkKindBodyImprint }

/// <summary>The hottest end of a heat mark; Unknown = none or not named.</summary>
public enum HeatMarkEnd : byte { HeatMarkEndUnknown = 0, HeatMarkEndFront, HeatMarkEndRear }

/// <summary>How the selected weapon's trigger works: Sight = each round goes where the crosshair points, Lock = a press holds the point.</summary>
public enum FireMode : byte { FireModeSight = 0, FireModeLock }

/// <summary>The brain's answer to the last use press; None = no answer yet.</summary>
public enum UseOutcome : byte { UseOutcomeNone = 0, UseOutcomeRunning, UseOutcomeDone, UseOutcomeRefused }

/// <summary>Why a use press was refused; Unknown = a reason this contract does not name.</summary>
public enum UseReason : byte
{
    UseReasonUnknown = 0, UseReasonOk, UseReasonOutOfReach, UseReasonLocked, UseReasonDoorRefuses, UseReasonNotDown, UseReasonSeatTaken, UseReasonUnfit,
    UseReasonCarrying, UseReasonNothingToDo, UseReasonFailed, UseReasonOtherSide,
}

/// <summary>What the aircraft's radar warning receiver says.</summary>
public enum RadarWarningLevel : byte { RadarWarningLevelNone = 0, RadarWarningLevelSearched, RadarWarningLevelTracked }

/// <summary>A missile's seeker; Unknown = a kind this contract does not name.</summary>
public enum MissileSeeker : byte
{
    MissileSeekerUnknown = 0, MissileSeekerUnguided, MissileSeekerCommand, MissileSeekerFireAndForget, MissileSeekerInfraRed, MissileSeekerRadar,
}

/// <summary>Who a player message is for; Unknown = an address this contract does not name.</summary>
public enum MessageAddress : byte { MessageAddressUnknown = 0, MessageAddressAll, MessageAddressSide, MessageAddressStation, MessageAddressUnit, MessageAddressInstructor }

/// <summary>What a rule entity is.</summary>
public enum ObjectiveRole : byte { ObjectiveRoleUnknown = 0, ObjectiveRoleObjective, ObjectiveRoleMeasure, ObjectiveRoleTrigger }

/// <summary>The lifecycle of an objective.</summary>
public enum ObjectiveState : byte
{
    ObjectiveStateUnknown = 0, ObjectiveStatePending, ObjectiveStateActive, ObjectiveStateAchieved, ObjectiveStateFailed, ObjectiveStateInactive, ObjectiveStateSkipped,
}

/// <summary>Where a trigger is in its state machine.</summary>
public enum TriggerState : byte
{
    TriggerStateUnknown = 0, TriggerStateDisarmed, TriggerStateArmed, TriggerStateHolding, TriggerStateWaiting, TriggerStateActing, TriggerStateFired, TriggerStateCooldown,
    TriggerStateDone,
}

/// <summary>Where the exercise is.</summary>
public enum ExercisePhase : byte { ExercisePhaseUnknown = 0, ExercisePhaseRunning, ExercisePhaseClosing, ExercisePhaseEnded }

/// <summary>How a side came out; None = not decided or a value this contract does not name.</summary>
public enum SideOutcome : byte { SideOutcomeNone = 0, SideOutcomeWin, SideOutcomeLose, SideOutcomeDraw }

/// <summary>What an animal is doing; Unknown = a mode this contract does not name.</summary>
public enum AnimalMode : byte
{
    AnimalModeUnknown = 0, AnimalModeCalm, AnimalModeAlert, AnimalModeFlee, AnimalModeRecover, AnimalModeRest, AnimalModeGuard, AnimalModeCrouch,
}

/// <summary>A flock's mode; Unknown = not a flock or a mode this contract does not name.</summary>
public enum FlockMode : byte { FlockModeUnknown = 0, FlockModeFlying, FlockModeLanded, FlockModePerched }

/// <summary>What kind of hole a wall breach is: a Hole is a gap to see or shoot through, a Passage is one to walk through; Unknown = a kind this contract does not name.</summary>
public enum BreachKind : byte { BreachKindUnknown = 0, BreachKindHole, BreachKindPassage }

/// <summary>What an ambient mover is.</summary>
public enum MoverKind : byte { MoverKindUnknown = 0, MoverKindCar, MoverKindPedestrian }

/// <summary>Where an ambient mover comes from (any simulator's traffic or crowd can feed the contract).</summary>
public enum MoverSource : byte { MoverSourceUnknown = 0, MoverSourceSumo }

/// <summary>A signal head's lamp (0 = off or unknown).</summary>
public enum TrafficLightState : byte { TrafficLightStateOff = 0, TrafficLightStateRed, TrafficLightStateYellow, TrafficLightStateGreen }

/// <summary>What a suggested camera view comes from: the film director's shot, the follow camera on a unit, or the operator's own camera. None = no suggestion.</summary>
public enum ViewSource : byte { ViewSourceNone = 0, ViewSourceDirector, ViewSourceFollow, ViewSourceOperator }

/// <summary>How the suggested camera sits: Free (no binding), Orbit (around a target), Eyes, Shoulder, Chase, Fixed (a scripted camera key); Unknown = a mode this contract does not name.</summary>
public enum ViewMode : byte { ViewModeUnknown = 0, ViewModeFree, ViewModeOrbit, ViewModeEyes, ViewModeShoulder, ViewModeChase, ViewModeFixed }

/// <summary>What a round did to a surface of the static world (the producer's impact kind): it Entered a piece it passes through, Left it, or Stopped on it.</summary>
public enum ImpactKind : byte { ImpactKindUnknown = 0, ImpactKindEntered, ImpactKindLeft, ImpactKindStopped }

// ---- DD-020 3.3: producer-neutral enumerations (replace raw numbers; the old fields stay, equal and superseded) ----------------------------------------------

/// <summary>What the use action of a player's unit does (the use key): None = no action available, or one this contract does not name.</summary>
public enum UseVerb : byte
{
    UseVerbNone = 0, UseVerbOpenDoor, UseVerbCloseDoor, UseVerbLockDoor, UseVerbUnlockDoor, UseVerbBreachDoor, UseVerbDrag, UseVerbCarry, UseVerbPutDown,
    UseVerbDrive, UseVerbPilot, UseVerbBoard, UseVerbMan, UseVerbGetOut, UseVerbTake, UseVerbPickUpRound, UseVerbDrop,
}

/// <summary>The part of a man's body a hit landed on; Unknown = not stated or a part this contract does not name.</summary>
public enum BodyPart : byte
{
    BodyPartUnknown = 0, BodyPartHead, BodyPartTorso, BodyPartUpperArmL, BodyPartForearmL, BodyPartUpperArmR, BodyPartForearmR,
    BodyPartThighL, BodyPartShinL, BodyPartThighR, BodyPartShinR,
}

/// <summary>How fast a walking unit moves on the forward axis: Walk = at most the unit's walking speed, Run = up to its top speed; Unknown = not stated.</summary>
public enum Gait : byte { GaitUnknown = 0, GaitWalk, GaitRun }

/// <summary>The stance a player asks for; None = no request (the unit keeps its stance).</summary>
public enum StanceRequest : byte { StanceRequestNone = 0, StanceRequestStanding, StanceRequestCrouched, StanceRequestProne }

/// <summary>What kind of movable part an <see cref="Articulation"/> is; Unknown = a kind this contract does not name.</summary>
public enum ArticulationKind : byte { ArticulationKindUnknown = 0, ArticulationKindTurret }

// ---- DD-022 3h: round 7 (clock, steps, client world, rendering) ---------------------------------------------------------------------------------------------

/// <summary>How the simulation clock runs: Continuous (a formula over wall time) or Stepped (held, moved only by steps; paused = Stepped with no steps).</summary>
public enum ClockMode : byte { ClockModeUnknown = 0, ClockModeContinuous, ClockModeStepped }

/// <summary>A client's state with respect to the world the station presents.</summary>
public enum ClientWorldState : byte { ClientWorldStateUnknown = 0, ClientWorldStateLoading, ClientWorldStateReady, ClientWorldStateFailed }

/// <summary>What a render job produces: a Movie (every frame of a reel) or Stills (single frames).</summary>
public enum RenderKind : byte { RenderKindUnknown = 0, RenderKindMovie, RenderKindStills }

/// <summary>The state of a render job.</summary>
public enum RenderState : byte { RenderStateUnknown = 0, RenderStateStarting, RenderStateRunning, RenderStateDone, RenderStateFailed, RenderStateCancelled }

/// <summary>What a film frame is: a Scene to render, or Silence (a title card; silence in the soundtrack).</summary>
public enum FrameKind : byte { FrameKindUnknown = 0, FrameKindScene, FrameKindSilence }

/// <summary>The outcome of a frame or of a job's result.</summary>
public enum FrameStatus : byte { FrameStatusUnknown = 0, FrameStatusWritten, FrameStatusFailed }

/// <summary>Where a frame's subtitle sits.</summary>
public enum SubtitlePosition : byte { SubtitlePositionUnknown = 0, SubtitlePositionBottom, SubtitlePositionTop, SubtitlePositionCentre }

/// <summary>What a <see cref="FilmGraphic"/> draws: a Ring, an Arrow, a Route, an Area or a Label.</summary>
public enum FilmGraphicKind : byte { FilmGraphicKindUnknown = 0, FilmGraphicKindRing, FilmGraphicKindArrow, FilmGraphicKindRoute, FilmGraphicKindArea, FilmGraphicKindLabel }
