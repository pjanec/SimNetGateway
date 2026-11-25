namespace SimNetGateway.Protocol.Hrot;

// T-080: the pivot carries HROT's enumerations as ENUMS, never as names. Each mirrors the DPS enum it ends in, member for member and in the same order (the DPS prefix dropped); the host maps
// HROT's own enum into it BY NAME (an unknown member -> value 0, the enum's unknown / none value) and the DPS outbound casts it - a rail pins that the two mirror in names and order.

/// <summary>Mirrors the DPS <c>BodyPart</c> (T-080).</summary>
public enum HrotBodyPart : byte
{
    Unknown = 0, Head, Torso, UpperArmL, ForearmL, UpperArmR, ForearmR, ThighL, ShinL, ThighR, ShinR,
}

/// <summary>Mirrors the DPS <c>MessageAddress</c> (T-080).</summary>
public enum HrotMessageAddress : byte
{
    Unknown = 0, All, Side, Station, Unit, Instructor,
}

/// <summary>Mirrors the DPS <c>ObjectiveRole</c> (T-080).</summary>
public enum HrotObjectiveRole : byte
{
    Unknown = 0, Objective, Measure, Trigger,
}

/// <summary>Mirrors the DPS <c>ObjectiveState</c> (T-080).</summary>
public enum HrotObjectiveState : byte
{
    Unknown = 0, Pending, Active, Achieved, Failed, Inactive, Skipped,
}

/// <summary>Mirrors the DPS <c>TriggerState</c> (T-080).</summary>
public enum HrotTriggerState : byte
{
    Unknown = 0, Disarmed, Armed, Holding, Waiting, Acting, Fired, Cooldown, Done,
}

/// <summary>Mirrors the DPS <c>ExercisePhase</c> (T-080).</summary>
public enum HrotExercisePhase : byte
{
    Unknown = 0, Running, Closing, Ended,
}

/// <summary>Mirrors the DPS <c>SideOutcome</c> (T-080).</summary>
public enum HrotSideOutcome : byte
{
    None = 0, Win, Lose, Draw,
}

/// <summary>Mirrors the DPS <c>AnimalMode</c> (T-080).</summary>
public enum HrotAnimalMode : byte
{
    Unknown = 0, Calm, Alert, Flee, Recover, Rest, Guard, Crouch,
}

/// <summary>Mirrors the DPS <c>FlockMode</c> (T-080).</summary>
public enum HrotFlockMode : byte
{
    Unknown = 0, Flying, Landed, Perched,
}

/// <summary>Mirrors the DPS <c>BreachKind</c> (T-080).</summary>
public enum HrotBreachKind : byte
{
    Unknown = 0, Hole, Passage,
}

/// <summary>Mirrors the DPS <c>BreachCause</c> (T-080).</summary>
public enum HrotBreachCause : byte
{
    Unknown = 0, Detonation, Ram, Blade, Push,
}

/// <summary>Mirrors the DPS <c>CraterKind</c> (T-080).</summary>
public enum HrotCraterKind : byte
{
    Unknown = 0, Small = 1, Large = 2,
}

/// <summary>Mirrors the DPS <c>MoverKind</c> (T-080).</summary>
public enum HrotMoverKind : byte
{
    Unknown = 0, Car, Pedestrian,
}

/// <summary>Mirrors the DPS <c>MoverSource</c> (T-080).</summary>
public enum HrotMoverSource : byte
{
    Unknown = 0, Sumo,
}

/// <summary>Mirrors the DPS <c>TrafficLightState</c> (T-080).</summary>
public enum HrotTrafficLightState : byte
{
    Off = 0, Red, Yellow, Green,
}

/// <summary>Mirrors the DPS <c>ViewSource</c> (T-080).</summary>
public enum HrotViewSource : byte
{
    None = 0, Director, Follow, Operator,
}

/// <summary>Mirrors the DPS <c>ViewMode</c> (T-080).</summary>
public enum HrotViewMode : byte
{
    Unknown = 0, Free, Orbit, Eyes, Shoulder, Chase, Fixed,
}

/// <summary>Mirrors the DPS <c>CivilianMode</c> (T-080).</summary>
public enum HrotCivilianMode : byte
{
    Unknown = 0, Routine, Alarmed, Panic, Shelter, Inside, Cower, Casualty, Protest, Riot, Dispersing, ArmedHostile, Gather, Surrender, Watch,
}

/// <summary>Mirrors the DPS <c>WeaponCategory</c> (T-080).</summary>
public enum HrotWeaponCategory : byte
{
    Unknown = 0, SmallArms, MachineGun, Autocannon, TankGun, Atgm, Mortar, Grenade, Bomb, Rocket, Howitzer, Sam, Countermeasure, AirToAir, Bow, SmokeDischarger, Flamethrower,
}

/// <summary>Mirrors the DPS <c>MunitionShape</c> (T-080).</summary>
public enum HrotMunitionShape : byte
{
    Unknown = 0, Grenade, Bomb, Shell, Rocket, Missile, Bullet, Axe, Spear, Star, Arrow,
}

/// <summary>Mirrors the DPS <c>SeatPose</c> (T-080).</summary>
public enum HrotSeatPose : byte
{
    Unknown = 0, Sit, SitLegsOut, Stand, Kneel, Ride,
}

/// <summary>Mirrors the DPS <c>HeatMarkKind</c> (T-080).</summary>
public enum HrotHeatMarkKind : byte
{
    Unknown = 0, VehicleStand, BurnedArea, BodyImprint,
}

/// <summary>Mirrors the DPS <c>HeatMarkEnd</c> (T-080).</summary>
public enum HrotHeatMarkEnd : byte
{
    Unknown = 0, Front, Rear,
}

/// <summary>Mirrors the DPS <c>EffectSize</c> (T-080).</summary>
public enum HrotEffectSize : byte
{
    Unknown = 0, Small, Medium, Large,
}

/// <summary>Mirrors the DPS <c>EffectSurface</c> (T-080).</summary>
public enum HrotEffectSurface : byte
{
    Unknown = 0, Ground, Water, Building, Vehicle, Person, Air,
}

/// <summary>Mirrors the DPS <c>ImpactKind</c> (T-080).</summary>
public enum HrotImpactKind : byte
{
    Unknown = 0, Entered, Left, Stopped,
}

/// <summary>Mirrors the DPS <c>KillKind</c> (T-080).</summary>
public enum HrotKillKind : byte
{
    None = 0, KnockedOut, BurntOut, Catastrophic, Unknown,
}

/// <summary>Mirrors the DPS <c>EffectKind</c> (T-080). <c>Omitted</c> (pivot only, last) = a kind DPS deliberately does not carry (tracer, flare, chaff): the effect is not sent.</summary>
public enum HrotEffectKind : byte
{
    None = 0, MuzzleFlash, Explosion, GroundMark, WreckFlame, WreckSmoke, Omitted,
}

/// <summary>Mirrors the DPS <c>UseVerb</c> (T-080).</summary>
public enum HrotUseVerb : byte
{
    None = 0, OpenDoor, CloseDoor, LockDoor, UnlockDoor, BreachDoor, Drag, Carry, PutDown, Drive, Pilot, Board, Man, GetOut, Take, PickUpRound, Drop,
}

/// <summary>Mirrors the DPS <c>FireMode</c> (T-080).</summary>
public enum HrotFireMode : byte
{
    Sight = 0, Lock,
}

/// <summary>Mirrors the DPS <c>UseOutcome</c> (T-080).</summary>
public enum HrotUseOutcome : byte
{
    None = 0, Running, Done, Refused,
}

/// <summary>Mirrors the DPS <c>UseReason</c> (T-080).</summary>
public enum HrotUseReason : byte
{
    Unknown = 0, Ok, OutOfReach, Locked, DoorRefuses, NotDown, SeatTaken, Unfit, Carrying, NothingToDo, Failed, OtherSide,
}

/// <summary>Mirrors the DPS <c>RadarWarningLevel</c> (T-080).</summary>
public enum HrotRadarWarningLevel : byte
{
    None = 0, Searched, Tracked,
}

/// <summary>Mirrors the DPS <c>MissileSeeker</c> (T-080).</summary>
public enum HrotMissileSeeker : byte
{
    Unknown = 0, Unguided, Command, FireAndForget, InfraRed, Radar,
}
