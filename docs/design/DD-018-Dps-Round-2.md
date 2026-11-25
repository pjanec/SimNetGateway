# DD-018: DPS round 2 - HUD, type data, rounds, flames

| | |
|---|---|
| **Status** | implemented (T-035, six stages, section 9; R-133: the owner delegated the contract details; the contract is not handed to a client until the owner says so) |
| **Tasks** | T-035 |
| **Rulings honoured** | R-132 (DD-017), R-133, R-049, R-050, R-127 |
| **Date** | 2026-11-20 |

## 1. Context and goals

DD-017 round 1 published what HROT provided on 2026-11-18. Writing the client guide for the Unreal image generator showed what a
renderer and a player station still miss, measured against HROT's own 3-D map and FPS station (HROT `coordinator` 0b6cf8d96):

- **HUD:** `OwnUnit` leaves weapon and round names, fire mode, the use label and the use outcome empty; the kill feed, player messages,
  objectives, scores, the exercise result and the aircraft warnings are not on the wire.
- **Type data:** a renderer needs per-type facts (a munition's look, a weapon's category, openings, seats, body geometry) that live in
  HROT's TKB, which a scenario may replace, so they must come from the running simulation.
- **Rounds:** HROT draws real rounds (tracers, rockets with smoke trails, grenades in flight and lying); DPS does not say which entity
  is a round or how it looks.
- **Flames:** HROT CE-4635 added flamethrower jets, burning people, burning spots and scorch marks, all replicated state in HROT.
- **New readings:** `Demeanour` (fear, anger, civilian mode), `VehicleLook.RotorSpeed`, the crew's head out of a hatch.
- **Gizmos:** 2-D primitives (box, text) arrive with z = 0.

Goals: the topics and fields of section 3, filled by the HROT host from HROT's own readers, mapped by the `dps` plugin, tested as in
section 7. Out of scope: moving HROT's map-only presentation rules (gait, limb swing, rotors spinning, wheels, tracer streams) into
HROT's look library - that is HROT's work; the client guide links them for porting meanwhile. Roles and respawn (DD-017 step 2) and
the FPS station's extra keys (squad switch, seat switch, head out, lights, command wheel) are a later round.

## 2. Block diagram

```mermaid
flowchart LR
    subgraph HROT["HROT node (editor)"]
        W[HROT world: components, bus events, TKB]
    end
    subgraph Host["SimNetGateway.HrotHost"]
        G1[EcsHrotAdapter: entities, fire, detonation]
        G2[PersonDetailGather / LookGather: person, vehicle, flames]
        G3[HudGather: kills, messages, statuses, exercise]
        G4[TkbGather: type records]
        S[HrotPlayerStation: own unit HUD status]
        Z[HrotGizmoHost: ground height for 2-D primitives]
    end
    subgraph Plugin["SimNetGateway.Plugin.DdsPlayer"]
        O[DpsOutbound]
        P[DpsPlayerSlot]
        T[DpsTypeCatalog]
    end
    W --> G1 & G2 & G3 & G4 & S & Z
    G1 & G2 & G3 -->|Protocol.Hrot messages| O
    G4 -->|HrotTypeRecord| T
    S -->|PlayerUnitStatus| P
    O & P & T -->|DDS topics| C[Clients]
```

- The host is the only part that compiles against HROT; it reads with HROT's own readers (`FlameJets`, `PersonLook`, `VehicleLook`,
  `OperatorFireModes`, `MunitionTkbCatalog.VisualOf`, the TKB) and hands BCL-only `Protocol.Hrot` messages to the plugins (R-050).
- `DpsOutbound` maps the new messages to the new topics as it does for round 1; `DpsPlayerSlot` fills `OwnUnit` from the extended
  `PlayerUnitStatus`; `DpsTypeCatalog` publishes each type record once.
- The DIS and HLA plugins ignore the new messages (they subscribe by type).

## 3. Key concepts and decisions

### 3.1 Player HUD (P-1)

**`OwnUnit` filled and extended** (fields appended):

| Field | From HROT |
|---|---|
| `AmmoEntry.Weapon` | the mount's category name (`WeaponCategory`, as HROT's HUD `Hud.KindOf`); HROT has no per-weapon display name |
| `AmmoEntry.AmmoType` | the loaded round's name (`Hud.RoundName`: the munition template's name) |
| `AmmoEntry` appended: `AmmoTypeId` (long), `Max`, `Magazine` (-1 = no magazine model), `LoadedRounds`, `Busy`, `Overheated`, `HeatPercent` | `WeaponAmmo` |
| `ActiveSlot` | the last control frame's `Mount` |
| appended `FireMode` (Sight / Lock) | `OperatorFireModes.Of` with the last frame's `FirePref` |
| `UseLabel` | HROT's verb label (`Hud.Label`) |
| appended `UseSeq`, `UseOutcome` (Running / Done / Refused), `UseReason` | `OperatorUseStatus` on the unit; a client shows it when `UseSeq` equals its last press, as HROT's HUD |
| appended `SpreadRad` | `HitModel.Sigma` of the selected mount (half-width, radians) |
| appended `RadarWarning` (None / Searched / Tracked), `RadarBearingDeg`, `MissileLaunch`, `MissileBearingDeg`, `MissileTimeToImpactS`, `MissileSeeker` | `RadarWarning`, `MissileWarning` on the unit (bearing: degrees, 0 = north, clockwise) |

Not on the wire, computed by the client as HROT's HUD does (rules in the client guide): **shock** (rounds passing within 5 m of the eye,
+0.5 each, decaying over 0.4 s - the client sees the rounds, 3.3) and **hit arcs** (a `Detonation` whose target is the own unit: the arc
points against the travel, else toward the shooter; 2 s). Rejected: the gateway computing them (it has no eye; HROT keeps them as
presentation memory too).

**`ManDowned`** (event): `SimTime`, `VictimId`, `SourceId`, `OriginalSourceId` (entity ids, 0 = unknown), `State` (Injury),
`BodyPart` (0 = unknown, else HROT's body part id + 1), `VictimSide`. From `ManDownedEvent`, local and remote (HROT's one kill
authority, CE-3364). Feeds the kill feed and the killer on the death screen.

**`PlayerMessage`** (event): `MessageId`, `OriginNode`, `To` (All, Side, Station, Unit, Instructor), `ToValue`, `Text` (256),
`Seconds`, `Priority`, `SentAt`, `SourceId`. From `PlayerMessageEvent`. All messages are published with their addressing; the client
filters as HROT's `PlayerMessages.Addressed` (All; Side = its side; Unit = its unit; Station = its station; Instructor only for an
instructor view). Rejected: filtering in the gateway (an instructor client in the same partition needs the rest).

**`Status`** (state, keyed by `StatusId`): one per HROT `ObjectiveStatus` (objectives, measures, triggers), the fields a HUD, a
scoreboard and a debrief read: role, state, closed, owner, progress (cur, need), value, target, score, pass, points, changed at,
reason, title, kind, group, phase, required, scored, weight, visible sides, step (cur, count), hold for, deadline, expected at,
capture, challenger, area (centre, radius), ends exercise, `SubjectId`, `ParentId`. Disposed when the status entity goes.
**`Exercise`** (state, one instance): HROT's `ExerciseResult` (phase, ended at, reason, the result per side). Kinds and titles stay
data, so a new objective needs no contract change. Rejected: one topic per HUD element (tickets, capture points, scoreboard) - HROT
derives all of them from the same statuses (`RulesModel`), and so can a client.

### 3.2 Type data (P-2)

**`TkbType`** (state, keyed by `TypeId`): `TypeId` (int64), `Name`, `CategoryPath`, `DisType` (text), `Schema` (`"hrot/1"`),
`ContentHash` (FNV-1a 64 of `Json`), `Json` (unbounded). `Json` is HROT's own serialization of the template, as its debug API
`GET /tkb/types/{id}` makes it (`EventSerializationHelper.SerializeToJson` per descriptor), restricted to an allow-list of
presentation descriptors (munition visual, warhead, weapon suite / system / capabilities, turret suite, vehicle openings and lights,
vehicle parameters, carrier, body geometry, person, outfit, visual definition, effects, sounds, locomotion, stride render model,
character animation, flight parameters). Published once per type when first referenced by an entity, a fire, a round or a loaded
weapon. Rejected: typed IDL per TKB record (HROT changes them often; every change would be a protocol change); a file shipped with the
client (a scenario can replace the TKB, so a file drifts from the run).

**Type ids are 64-bit.** `Entity.TypeId`, `Fire.MunitionTypeId`, `Detonation.MunitionTypeId` become `long` (protocol 1.0 is not
released, R-133). Rejected: an extra 64-bit field beside the 32-bit one (two ids for one thing).

### 3.3 Rounds (P-3)

HROT gives a network id only to rounds that have a look (tracers, shells, rockets, missiles, grenades, mortar bombs; ball ammo has
none). They are published as `Entity` (Domain Munition) + `EntityState` like any entity (verify the gather includes them; if not, it is
added). New:

- **`Round`** (state, keyed by `EntityId`): `MunitionTypeId`, `ShooterId`, `Muzzle` (launch point), `Lying`, `FuzeLeftS` (negative =
  no time fuze), `Stopped`. Sent on creation and on change of lying / stopped. The client draws the round's look at the entity's pose,
  the tracer streak and the smoke trail back toward `Muzzle` (HROT's `ProjectileLayer` rules, linked in the client guide).
- **`MunitionLook`** (state, keyed by `MunitionTypeId`): the resolved look `MunitionTkbCatalog.VisualOf` (shape, length, diameter,
  colour, smoke trail, tracer, tracer colour, sticks, bolt), sent once per munition type that is fired or seen. Typed rather than only in
  `TkbType`, because `VisualOf` falls back to HROT's built-in looks that no TKB record carries, and because every renderer needs it.
- **`Fire` appended `WeaponCategory`** (the shooter's mount category by name, unknown fallback): a client draws HROT's tracer streams for
  machine guns and autocannons (`FireStreams.IsAutomatic`) and knows a flamethrower.

Rate: rounds move fast; `EntityState` carries velocity and acceleration and the client dead-reckons, so the existing thresholds hold.
Bullets without a look stay off the wire (`Fire` is enough).

### 3.4 Flames (P-4)

- **`FlameJet`** (state, keyed by `EntityId` + `Mount`): `AimPoint`, `TargetId`, `RangeM`, `HalfAngleRad`, `Intensity`,
  `BurstStartedAt`, `BurnSeconds`; written while `FlameJets.TryGet` says lit, disposed when it goes out.
- **`PersonState` appended `Burning`** (`PersonLook.IsBurning`).
- **`FireSpots`** (state, keyed by the holder's `EntityId`): up to 8 spots, each world position (the host applies the holder's
  transform, as `EffectLayer.HolderPoint`), radius, intensity, since, seconds. From `FireState` (vehicles, buildings, terrain cells);
  disposed when none burns.
- **`GroundMarks`** (state, keyed by the cell's `EntityId`): up to 16 marks, each kind (VehicleStand, BurnedArea, BodyImprint), centre
  (x, y; z = ground height), heading, length, width, hot end, stand since, left at. From `GroundHeatMarks`; a renderer draws
  `BurnedArea` as a scorch, an IR renderer all three.

### 3.5 People and vehicles (P-5)

- **`PersonState` appended** `Fear`, `Anger` (quarters 0..4), `CivilianMode` (HROT's `CivilianMode` by name: Routine, Alarmed, Panic,
  Shelter, Inside, Cower, Casualty, Protest, Riot, Dispersing, ArmedHostile, Gather, Surrender, Watch; unknown fallback). From
  `Demeanour`.
- **`VehicleLook.RotorSpeed`** filled from `VehicleLook.RotorSpeed` (0..1), sent on a change of 0.05 or more and when it reaches 0 or 1.
- **`OpeningLook` appended `CrewExposed`** (`VehicleLook.CrewExposed` per opening id).

### 3.5a Animals, riders and the aim (P-5a)

HROT has animals (HROT `docs/designs/animals/DESIGN_Animals.md`): cattle, sheep, goats, pigs, horses (herd, riding, war), dogs,
cats, rabbits, hens, birds and flocks. Ridden horses are carriers with seats like a motorcycle (a firing rider seat). A dragon
(HROT CE-4634, open) is planned as the bird flight model scaled up with a rider seat and a flame breath on the shared flame.

- **`Domain` gains `DomainAnimal`** (appended): today a DIS kind-3 animal (category 200 - 208) maps to `DomainPerson`, wrongly.
- **`Animal`** (state, keyed by `EntityId`, for animals only): `Mode` (HROT's `AnimalMode` by name: Calm, Alert, Flee, Recover, Rest,
  Guard, Crouch), `FearBand` (0..3), flags `Bolting`, `Dangerous`, `Calling`; and for a flock (one entity drawn as many birds) `FlockCount`,
  `FlockInitialCount`, `FlockSeed`, `FlockMode` (Flying, Landed, Perched; count 0 = a single body). From HROT's `AnimalState` /
  `FlockState`; the species is the type (`TkbType`). The client animates (gait from speed, grazing, lying when resting, the flock's
  birds) by HROT's rules, linked in the client guide.
- **Riders:** a rider's `PersonState` already names the carrier (`SeatVehicleId`) and the seat role; the seat's pose (ride astride)
  and position come from the carrier's type data (`TkbType`: HROT's carrier seats with `SeatPose`). A dragon will be the same: an
  animal carrier with a rider seat, its breath a `FlameJet` on its mount.
- **The aim point is HROT's own:** `PersonState.AimPoint` is filled by HROT's one reading of the posture's aim
  (`WeaponPostureOf.TryAimPoint`: the target's chest, 1 m above its position, or the aimed point). Round 1 sent the target's position
  (its feet), so a client's upper body pitched too low. The client turns and pitches the upper body toward it from the shoulder,
  within HROT's limits (twist 75 degrees, pitch 35 degrees, eased with a 0.12 s time constant; none when prone or dead), and turns
  the head by `HeadYaw` less the drawn twist, within the neck - the rules are in the client guide.

### 3.5b Two-wheeler lean (P-5b)

HROT's 3-D map leans a motorcycle into a turn (HROT `MotionTracker`, `case Motorcycle`; `DESIGN_Map_3D_Mode.md` 6al.2): target
`atan(v * yawRate / g)`, clamped to 35 degrees, eased with a 0.15 s time constant, about the body's ground line; the seated riders lean
with it (their seat frame is the leaned body). The simulation itself drives the bike upright. The owner: the client should not have to
derive it.

- **`EntityState` appended `Bank`** (radians, roll about the body's forward axis, right-hand rule: positive = the top toward the
  right; a left turn is negative), computed by the host with HROT's rule from the simulation's own forward speed and yaw rate, eased on
  simulation time, for two-wheelers only (HROT's motorcycle class); 0 for everything else. A change of the angle threshold (1 degree)
  sends a sample as any orientation change does.
- `Orientation` stays the simulation's (hit boxes, the seat eye, the chase camera read it); the client applies `Bank` about the
  ground line under the drawn body, and the riders, placed in the carrier's seat frame, lean with it.
- The rule is a few lines in a presentation assembly the host must not reference, so the host carries it with a pointer to HROT's
  source and a test pinning the same numbers.
- Rejected: baking the lean into `Orientation` (a different pose from the simulation's, which the client also uses for the seat eye
  and the player's camera); leaving it to every client (the owner).

### 3.5c Carriage and the mine clearer (P-5c)

A client that draws people needs to know who sits where and who carries whom, and a client that draws a mine clearer needs to know when its flail turns. HROT's
3-D map answers both from simulation facts (`Carriage.Of`, `FlailActivity.IsOn`, Hrot.Presentation), which the host must not reference. The owner: the client
should not have to derive it.

- **`PersonState` appended** `SeatIndex` (byte, 255 = not seated), `SeatExposed`, `SeatPose` (new enum `SeatPose`: Unknown = 0, Sit, SitLegsOut, Stand, Kneel, Ride,
  mirrored by name from HROT's `SeatPose` in `CarrierDefDto.cs`; HROT's own `Sit` is 0, the contract's 0 is unknown), `CarryRole` (new enum: None = 0, Carried, Dragged,
  OnStretcher, Carrying, Dragging, StretcherFront, StretcherRear, TeamLoadFront, TeamLoadRear) and `PartnerId`. The existing `CarryOtherId` (0 since round 1) is filled.
- **Meaning.** For a man held by someone (carried, dragged, on a stretcher, seated in a vehicle, an animal or at a weapon) `CarryOtherId` is his holder's entity id. For a
  carrier it is what he carries: the casualty, or the weapon of a two-man weapon team. `PartnerId` is the other man of a pair carrying one load (a stretcher's two
  carriers, the casualty on it, a two-man weapon's two men), 0 when there is none. A seated man has `CarryRole` None and the seat fields set.
- **Source (HROT facts).** `IsEmbarkedTag` {VehicleEntity, Exposed, Seat, Mode} on the held man, the holder's `PassengerBuffer` (passengers, their modes, the assistant) and
  `IsAssistingTag` on the second man, `PersonCarrier` on the holder to tell a person from a vehicle, the seat's pose from the TKB seat (`SeatDto`, the holder's `Gen.Carrier`).
  The host ports `Carriage.Of` with the same order of questions and a comment naming it. It leaves out HROT's drawing-only kinds (downed, wounded, knocked down, manning:
  the seat fields tell it) and it reports a man in an enclosed seat too (HROT draws nobody there, a renderer may still want the seat).
- **`VehicleLook` appended `FlailActive`:** terrain's `MineClearing` is active with the flail tool (HROT `FlailActivity.IsOn`; a plough or a line charge never turns the
  drum). It travels with the look, so a change of the flag sends the look again.
- **`VehicleLook` appended `Airborne` (T-079):** HROT's one flight-state rule `Hrot.Look.Airborne.Of` (a flyer with `FlightState`: aircraft, helicopters, the dragon) - so any flyer says flying or landed, not only an entity with `AnimalState` (the `Animal` topic's `Airborne`, unchanged). False on the ground and for a ground vehicle; a change alone sends the look again.
- Rejected: one combined "held by" id for pairs (a stretcher needs both the load and the partner); carrying the rule in the client (the owner).

### 3.6 Gizmo heights (P-6)

The host gives 2-D primitives (box, text) the ground height at their point from HROT's terrain height query, so a client never sees
z = 0 for them. Rejected: a 2-D flag for the client to drape (every client would need the terrain query the host already has).

### 3.6a Building damage and glass (P-7)

HROT replicates a building's damage on runtime damage entities (HROT `docs/DESIGN_Glass_Windows.md`, `DESIGN_Terrain_Import_UTM.md` §8):
`BuildingDamage` (wall breaches as rectangles on a panel, collapsed) and `WindowDamage` (per hit window: hits 1..2 = cracked with up
to two crack points (u, v), 3 = broken). A renderer that draws HROT's terrain must show both, or a shot window stays whole and a
breached wall stays closed.

- **`BuildingDamage`** (state, keyed by the building's `TerrainObjectKey` label `"<terrain>/<building label>"`, the identity the
  terrain export keeps): `Collapsed`; breaches (panel index within the building, from / to metres along the panel from its A end,
  bottom / top absolute z, kind: Hole / Passage); windows (panel index within the building, opening index on the panel, hits, the two
  crack points as bytes 0..255). Several HROT entities for one building (capacity overflow) are merged into one sample. Disposed when the
  building's damage goes (a reload).
- The pane geometry is static terrain (a `glazed` flag per window opening) and comes with the terrain export, not over DPS.
- Not in this round, same family, a later round: felled / damaged trees (`TreeDamage`), ground patches (craters, road blockage), door
  states (DD-017 section 10 gap). The client guide links them.
- Rejected: one topic for windows and one for breaches (HROT keeps them apart only so a glass hit does not re-send breaches on its
  internal wire; a client applies both to the same building); keying by HROT's building index (an index into one import's list; the
  label survives a re-export).

### 3.6b Ambient traffic: world-space tracks (P-8, revised 2026-11-20)

HROT runs SUMO through SumoSharp (vendored in HROT `FDP/ExtDeps/SumoSharp`, upstream public `pjanec/SumoSharp`). Two existing wires
were checked and neither fits DPS:

- **SumoSharp's DDS wire** (`sumo/vehicles` etc., HROT AQ93 D-wire, R-4020) is lane-relative dead reckoning (lane, position along
  the lane, lateral offset, speed, upcoming lanes): a receiver needs the SUMO net, SumoSharp's lane numbering and its reconstruction
  code to get a pose. The owner: the image generator must not deal with a road network, and DPS must not depend on SumoSharp.
- **SumoSharp's IG bridge** (`docs/IGBRIDGE-DESIGN.md`) reconstructs world-space poses for an IG with no prediction, but emits plain
  `[id, pos, quat, t]` samples at 20 - 30 Hz per mover: one sample stream per car, too many for thousands of movers.

**Decision: the gateway reconstructs, the client interpolates world-space tracks.** HROT already turns SUMO's wire into smooth
world-space poses every frame (`TrafficMirror`: SumoSharp's `DrClock` + `KinematicReconstructor`, terrain-local metres, SUMO's lane z or
the draped ground, into the `TrafficFrame` singleton). The host reads those poses and sends each mover as a short **track**: timed
knots (time, position x y z, heading, pitch, speed) on a smooth curve. A knot is added only when a cubic Hermite curve through the
knots already sent would miss the real pose by more than a tolerance (0.10 m position, 2 degrees heading), and at least every
`MaxKnotInterval` (1.0 s). The client plays traffic `PlaybackDelay` (default 1.2 s) behind the newest knot time, so it always
interpolates between real knots (a lane change, a junction turn, a stop all come out right) and never needs a road network; it
falls back to extrapolating along the last knot's heading and speed only when a knot is late.

- **`Mover`** (state, keyed by `MoverId`): kind (Car, Pedestrian), `TypeId` (the HROT TKB type; for a SUMO car 0 = HROT's generic
  traffic car), colour (0xRRGGBBAA; HROT's paint rule already applied), seed (clothes and face of a pedestrian), length, width,
  source. Written at spawn, disposed at despawn. A mover HROT turns into its own entity (a panicking pedestrian becomes a civilian)
  is disposed here and appears as an `Entity`.
- **`MoverTracks`** (event-like batch, reliable, volatile, keep-last): `SimTime` of the batch and up to 4096 entries
  `{MoverId, Knot}` (time as an offset from `SimTime`, float position, heading and pitch as degrees x 100, speed) - every knot added since
  the last batch, sent at 10 Hz. A late joiner gets the `Mover` instances from TRANSIENT_LOCAL and the next knots within
  `MaxKnotInterval`; it shows a mover from its second knot.
- **`TrafficLights`** (state, keyed by signal id): position, heading, state (Red, Yellow, Green, Off) per signal head, from SUMO's
  traffic-light state and the net's controlled lanes - placed in the world by the host, so the client needs no net.
- **One source per car:** HROT's local replica entities of SUMO cars (`TrafficReplicas`, within 300 m + 50 m of its units) are left
  out of `Entity`; every SUMO car reaches the client only as a `Mover`.
- **Roads:** the road surface comes with the terrain export (the terrain importer's business), not over DPS.
- Not SUMO-specific: any simulator's ambient traffic or crowd can feed `Mover` / `MoverTracks`.
- Budget: a car on a straight road needs one knot per second, a turning one a few; about 24 B a knot, so 10 000 movers at 2 knots/s
  are about 0.5 MB/s, batched into a few samples per tick.
- Rejected: SumoSharp's wire in the partition (needs the net and SumoSharp's code); dense pose samples per mover (IG bridge style;
  the rate); `EntityState` per mover (one keyed DDS instance and a heartbeat per car); predictive future segments from the route (the
  gateway knows the route only as lanes; delayed interpolation is exact and simpler).

### 3.6c One trajectory model, two playback modes (P-9)

The owner (2026-11-20): generalise the traffic tracks to every entity if it fits gameplay with background traffic, but keep two
lanes if a unified one would add latency or cost for ordinary entities (thousands of soldiers). Decision: unify the MODEL, keep the
two TRANSPORTS and two PLAYBACK modes.

- **Track point** (the one model): time, position, orientation, linear velocity, and (live entities) linear acceleration and angular
  velocity. An `EntityState` sample IS a track point already; a `MoverTracks` knot is one with orientation reduced to heading and
  pitch.
- **Curve rule** (the one math, in the client guide with a reference implementation in the reference client): between two points a
  cubic Hermite in position with the velocities as tangents and a slerp in orientation; past the newest point second-order dead
  reckoning (position + v t + a t^2 / 2, orientation by angular velocity); a correction blends from the shown pose to the new curve
  over 0.25 s.
- **Send rule** (the one policy, two references): send a point when the client's curve would leave a tolerance. For a **live**
  entity the reference is the extrapolation from the last sent point (today's `EntityState` thresholds: 0.05 m, 1 degree, 0.1 m/s,
  heartbeat 5 s); for a **delayed** mover it is the interpolation through the sent points (0.10 m, 2 degrees, at most 1 s apart).
- **Playback modes:** **Live** - shown at the newest simulation time by extrapolation; for everything a player sees and interacts
  with (units, vehicles, aircraft, rounds, the own unit). **Delayed** - shown `PlaybackDelay` behind the newest point by
  interpolation only; for background movers. Nothing is added to the latency of a live entity: its points and extrapolation are
  today's.
- **Transports:** live entities stay one keyed `EntityState` instance each (late join and disposal for free; thousands are fine);
  movers stay batched in `MoverTracks` (tens of thousands). A mover that HROT turns into an entity leaves `Mover` and appears as an
  `Entity`; the client blends the hand-over over the playback delay rather than jumping forward.
- Rejected: one topic for both (a batch loses keyed late join and disposal for entities; a keyed instance per mover is the cost the
  batch exists to avoid); delayed playback for every entity (a player would aim at where a target was a second ago, HROT judges the
  shot where it is now).

### 3.7 Contract rules kept

Fields are appended at the end of existing types; enumerations mirrored from HROT go by name with an unknown value 0 (DD-017 3.10);
every enumerator is prefixed with its enum name; strings and sequences are bounded, except `TkbType.Json`. The protocol stays 1.0
until the owner hands it to a client (R-133).

### 3.8 Presentation data bypasses the pivot repository

The `dps` plugin reads the `Protocol.Hrot` message stream directly. None of the presentation data (person detail, looks, rounds, flames, HUD, type records,
building damage, movers) is stored in the pivot repository: `PivotSchema` and the engine's entity tables are unchanged since round 1.

```mermaid
flowchart LR
    hrot[HROT world] --> gather[HrotHost gathers]
    gather --> msgs[Protocol.Hrot messages]
    msgs --> pivot[Pivot repository: DIS and HLA state]
    msgs --> dps[dps plugin]
    dps --> dds[DDS topics]
    pivot --> disside[DIS and HLA sides]
```

- **Why.** Only DPS needs this data. Storing it would cost memory and a second change detection (the gathers already send on change, the plugin keeps the latest value
  per entity for the heartbeat). DDS durability serves late joiners and HROT records the topics for replay. The price, accepted: repository queries, ownership rules and the
  monitor views do not see presentation data.
- **Owner, 2026-11-20:** we do not need to go via the intermediate model where it is not beneficial.
- **Consequences.** `Protocol.Hrot` grew from 12 to 30 inbound message types, the DIS profile declares the ones it deliberately ignores in one list (`HrotIgnoredIngress.Types`),
  and a new message must be either handled or listed (a profile test fails otherwise). `Protocol.Hrot` now carries a presentation contract beside the simulation one. A
  neutral name and split of the presentation contract is deferred until a second simulator feeds DPS.

## 4. Class diagram

```mermaid
classDiagram
    class PlayerUnitStatus {
        +Health, HealthMax
        +Weapons: WeaponStatus[]
        +ActiveMount, FireMode
        +UseVerb, UseTarget, UseLabel, UseSeq, UseOutcome, UseReason
        +SpreadRad
        +Radar, Missile warnings
    }
    class HudGather {
        +Gather(world, inbound)
    }
    class TkbGather {
        +Reference(typeId)
        +Gather(world, inbound)
    }
    class DpsOutbound
    class DpsPlayerSlot
    class DpsTypeCatalog {
        +OnTypeRecord()
        +OnMunitionLook()
    }
    HrotPlayerStation --> PlayerUnitStatus : reads HROT
    HudGather ..> HrotManDowned
    HudGather ..> HrotPlayerMessage
    HudGather ..> HrotStatus
    HudGather ..> HrotExercise
    TkbGather ..> HrotTypeRecord
    TkbGather ..> HrotMunitionLook
    LookGather ..> HrotFlameJet
    LookGather ..> HrotFireSpots
    LookGather ..> HrotGroundMarks
    EcsHrotAdapter ..> HrotRound
    DpsOutbound --> HrotManDowned
    DpsOutbound --> HrotRound
    DpsPlayerSlot --> PlayerUnitStatus
    DpsTypeCatalog --> HrotTypeRecord
```

- `HudGather` (new, host): kills and messages from the bus (local and remote), statuses and the exercise result from components,
  change-detected.
- `TkbGather` (new, host): collects the type ids referenced this frame (entities, fires, rounds, loaded rounds), and for each id not yet
  sent this runtime emits its record and, for munitions, its look. Cleared at a world boundary.
- `LookGather` gains flames; `PersonDetailGather` gains demeanour and burning; `EcsHrotAdapter` gains rounds and the fire's weapon
  category.
- `DpsTypeCatalog` (new, plugin) writes `TkbType` and `MunitionLook`.

## 5. Sequence diagrams

A client joins and shows a burst of tracer fire:

```mermaid
sequenceDiagram
    participant W as HROT
    participant H as HrotHost
    participant P as dps plugin
    participant C as Client
    W->>H: WeaponFireNotification, round entity with a look
    H->>P: HrotFire(+WeaponCategory), HrotEntityState(round), HrotRound
    H->>P: HrotMunitionLook, HrotTypeRecord (first time only)
    P->>C: Fire, Entity, EntityState, Round, MunitionLook, TkbType
    C->>C: draw the round look, tracer streak and smoke trail, or an MG tracer stream
    W->>H: round gone
    H->>P: HrotEntityRemoved
    P->>C: dispose Entity, EntityState, Round
```

The player uses a door and reads the outcome:

```mermaid
sequenceDiagram
    participant C as Client
    participant P as dps plugin
    participant H as HrotPlayerStation
    participant W as HROT
    C->>P: ControlFrame(UseTarget = door, every frame)
    P->>H: Control
    H->>W: OperatorControlFrameEvent
    H-->>P: PlayerUnitStatus(UseVerb OpenDoor, label "Open door")
    P-->>C: OwnUnit(CanUse, UseLabel)
    C->>P: ControlFrame(UseSeq + 1)
    W-->>H: OperatorUseStatus(UseSeq, Refused, Locked)
    H-->>P: PlayerUnitStatus(UseOutcome Refused, UseReason Locked)
    P-->>C: OwnUnit -> client shows "Locked"
```

## 6. Edge cases and error handling

- A world boundary: the type catalog forgets what it sent (new writers; TRANSIENT_LOCAL serves late joiners); statuses, rounds, jets,
  spots and marks are disposed with their writers as in round 1.
- Replay: statuses, the exercise result, rounds, spots and marks follow the replayed world; kills and messages come from recorded
  events if HROT replays them (no extra work); `OwnUnit` stays empty (no player in replay).
- A TKB descriptor that fails to serialize is left out of `Json` with an entry `{type, serializationError}` (as HROT's debug API).
- An enumeration value HROT appends maps to the unknown value; the guard test lists HROT's names (as for round 1).
- `TkbType.Json` size: a template is a few kB; the allow-list keeps AI and damage tables out.

## 7. Test plan

- Protocol: round trip of every new and extended type; the generated IDL contains the new topics; QoS per category.
- Host (staged HROT world, as round 1): a man with `Demeanour` and `BurningUntil` -> person detail; a lit flame jet -> jet, then
  disposed; `FireState` and `GroundHeatMarks` -> spots and marks in world coordinates; a round with a look -> entity + `HrotRound` +
  munition look once; a fire from an MG mount -> category; `ManDownedEvent` and `PlayerMessageEvent` -> messages; an `ObjectiveStatus`
  -> status, then disposed; the TKB gather emits each referenced type once and again after a boundary; `PlayerUnitStatus` carries the
  weapon names, fire mode, use outcome and warnings; a gizmo box gets the ground height.
- Plugin (in-memory transport): each message -> the right sample; the plugin writes each type record the host sends (the once-per-type rule is the host gather'''s: the host test checks it, and again after a boundary); name mapping with unknown
  fallback; the guard test of HROT's enumeration names (CivilianMode, WeaponCategory, HeatMarkKind, UseReason, UseOutcome,
  RadarWarningLevel, GuidanceKind, ObjectiveRole, ObjectiveState, TriggerState, ExercisePhase, SideOutcome, MunitionShape, OperatorFireMode, InjuryState). Added in the
  build: the track curve and knot policy (Common), the building damage union, the traffic gather and signal heads, the lean rule, the reference client's mover
  playback, HUD text and filters.
- Reference client: shows the kill feed line, the weapon line with round name and fire mode, and the use verdict in its HUD text;
  counts the new topics in its headless summary.

## 8. Proposals

P-1 .. P-6 are sections 3.1 .. 3.6 (3.5c and 3.8 followed the owner's requests of 2026-11-20), approved under R-133 (the owner delegated the contract details and reviews before the hand-over).

## 9. As-built

T-035 was built in six stages, each reviewed and committed on its own. The contract is still protocol 1.0 (R-133). Topic count: 17 in round 1, 32 now.

### Stage 1 (fa9e527): rounds, flames, people, vehicles, 64-bit ids

- **Contract:** `Entity.TypeId`, `Fire.MunitionTypeId`, `Detonation.MunitionTypeId` are `long`. `PersonState` appended `Burning`, `Fear`, `Anger`, `CivilianMode`;
  `OpeningLook.CrewExposed`; `Fire.WeaponCategory`; `VehicleLook.RotorSpeed` is filled. New state topics `Round`, `MunitionLook`, `FlameJet`
  (key entity + mount), `FireSpots` (bounded 8), `GroundMarks` (bounded 16).
- **Host:** `RoundGather`, `MunitionLookGather` (once per munition type per runtime, built-in looks included), `FlameJetGather`, `FireSpotGather`
  (holder transform applied as `EffectLayer.HolderPoint`), `GroundMarkGather`; rotor speed is sent on a change of 0.05 and on reaching 0 or 1.
  Rounds with a look were already published as entities (Domain Munition) by the adapter's query; only `HrotRound`, the look and the disposal were new.
- **Deviations:** a `HeatMarkEnd` enum (mirrored by name) for the marks' hot end, which DD-018 does not name; the DIS gateway got no-op ingress modules for
  every new message (the "no unhandled message" test requires one per message); a ground mark's z was 0 in this stage (filled in stage 4).

### Stage 2 (de1d33b): the player HUD

- **Contract:** `AmmoEntry` appended `AmmoTypeId`, `Max`, `Magazine`, `LoadedRounds`, `Busy`, `Overheated`, `HeatPercent`; `OwnUnit` appended `FireMode`,
  `UseSeq`, `UseOutcome`, `UseReason`, `SpreadRad`, `RadarWarning` (+ bearing), `MissileLaunch` (+ bearing, time to impact, seeker). New `ManDowned` and
  `PlayerMessage` (event QoS), `Status` (state, keyed by `StatusId`) and `Exercise` (state, one instance).
- **Host:** `HudNames` ports HROT's HUD words (`Hud.KindOf`, `RoundName`, `TypeName`, `Label`, `Resolve`; they live in Hrot.Presentation, which the host must
  not reference); `HrotPlayerStation.ReadUnit` fills the weapon line, the last control frame's mount and fire preference, the use label and verdict, the
  spread (a controlled weapon system for slots of 32 and up) and the warnings; `HudGather` reads kills, messages, statuses (as `RulesReader` does) and the
  exercise result.
- **Deviations:** `AmmoEntry.Weapon` is HROT's HUD word ("RIFLE", "MG", "HANDS"), not the enum name (the enum name is on `Fire.WeaponCategory`);
  `AmmoEntry.Reserve` stays 0 (HROT does not say whether `AmmoOf` includes the magazine); where HROT's own value 0 is a real value (`Objective`, `Pending`,
  `Running`, `Ok`) the contract's 0 is Unknown and the real values start at 1 (HROT's "none" values map to 0); sides, owners, challengers and the visible-sides
  mask use the contract's force numbers (1 friendly, 2 opposing, 3 neutral, the mask remapped); `Status` carries `TriggerState` (DD-018 3.1 does not name it);
  `MissileTimeToImpactS` is the time left when written and is refreshed about once a second; the keyless `Exercise` topic cannot be disposed per instance
  (CycloneDDS.NET has no key to dispose by): it ends with the writers at a world boundary.

### Stage 3 (d201a9f): type data, animals, the aim point, the two-wheeler lean

- **Contract:** `TkbType` (state, keyed by the 64-bit type id; `Json` is an unbounded string, which the IDL expresses as `string Json;`; a text over 1 MB round
  trips), `Domain` appended `DomainAnimal`, `Animal` (state), `EntityState` appended `Bank`.
- **Host:** `TkbPresentation.Descriptors` (the allow-list, matched by CLR type so a rename in HROT breaks the build), `TkbGather` (once per type per runtime;
  referenced by entity types, fire and detonation munitions, loaded ammunition and a unit's own mount round); `AnimalGather`; `BankRule` / `BankTracker`
  (HROT's `MotionTracker` rule: target = clamp(-atan(v * yawRate / g), +-0.61), eased with 0.15 s on simulation time; v and yaw rate from the simulation's own
  velocity); `PersonState.AimPoint` now comes from `WeaponPostureOf.TryAimPoint` (the target's chest, 1 m up, or the aimed point).
- **Deviations:** animal categories are 200..209 (HROT's `Animals.IsAnimalDis`; DD-018 said 200..208); a two-wheeler is found with HROT's own
  `VisualFamilies.Of(dis) == Motorcycle` (Hrot.Look), not a TKB class lookup; the aim change also reaches the DIS side (`HrotEntityState.AimPoint`), and a
  non-null target that is gone now reads as "ready" instead of using the stored point.

### Stage 4 (8a911a7): building damage, ground heights

- **Contract:** `BuildingDamage` (state, keyed by the terrain object label "terrain/building label"): `Collapsed`, bounded breaches (64) and windows (256).
- **Host:** `BuildingDamageGather` unites HROT's damage entities per building as its own scenario save does (`BuildingDamages.KeyOf`, panel as the index
  within the building through `BuildingPanelIndex.ToLocal`). Ground heights come from `WorldQuery.Of(world).GroundHeightAt` (FDP, public): gizmo box and text
  primitives and `GroundMarks.Centre.z`.
- **Deviations:** the bounds are 64 breaches and 256 windows (HROT holds 16 and 64 per damage entity); filled gizmo triangles still arrive with z = 0.
  The test DDS domains were made disjoint per assembly.

### Stage 5 (ec9d3ac): ambient traffic as world-space tracks

- **Contract:** `Mover` (state), `MoverTracks` (event QoS, at most 4096 knots), `TrafficLights` (state, one instance per signal head). A knot is 28 bytes:
  int id, float dt (relative to the batch, at most 0), float x y z, short heading and short pitch in hundredths of a degree, float speed.
- **Shared curve:** `SimNetGateway.Common.Tracks` holds `TrackCurve` (cubic Hermite with the velocities as tangents, shortest-way heading, straight
  extrapolation past the newest knot) and `TrackBuilder` (the delayed-mode send rule: a knot at the first pose, when the curve through the sent knots would miss a
  pose by 0.10 m or 2 degrees, and at least one a second). The sender and the reference client use the same code.
- **Host:** `TrafficGather` reads HROT's `TrafficFrame` (Hrot.Traffic) and the road net's signal heads (`TrafficNetCache.Roads.Signals`). The traffic replica
  entities are on the SumoSharp route and were already excluded from the entity gather, so a car is only ever a mover and the DIS side is unchanged.
- **Deviations:** referencing `Hrot.Traffic` pulls HROT's vendored SumoSharp projects into the build (compile-only; the package keeps only `SimNetGateway.*`;
  the first cold build prints one `CS8602` from HROT's `Sim.Core`); a pose with network id 0 (a SUMO id that is not an integer) has no mover; a signal head's
  position is the lane's stop line, 5 m up (HROT's own placement), and its heading is computed by the host from the lane's last segment (HROT stores none);
  speed is the 3-D speed along the heading so that speed * (heading, pitch) is the velocity.
- **Knot rates measured on synthetic poses at 20 Hz:** a straight road at 14 m/s about 1.0 - 1.3 knots/s; standing 1.0 - 1.2; a 6 m radius turn at 10 m/s about
  1.75; random acceleration and steering 1 - 6.7 (55 - 400 knots per 60 s over 25 seeded runs). A straight mover costs about 28 B/s.

### Stage 6: the reference client and the closing work

- **Reference client** (`src/Hosts/SimNetGateway.DpsClient`): `DpsWorld` (still free of raylib) subscribes to every new topic and keeps the latest state (not
  alive = gone, a new world epoch drops it); `MoverPlayback` plays ambient movers delayed (`PlaybackDelay` = 1.2 s, one constant) with `TrackCurve`, never
  past the newest knot, from a mover's second knot; `DpsHud` words the weapon line (weapon word, round, rounds, fire mode), the use prompt and the verdict
  (shown only when `UseSeq` equals the client's last press), the kill line, the message filter of HROT's HUD (All, Side, Unit, Station only when the client
  knows its station, Instructor only in an instructor's view) and the objective filter (visible sides or owner, titled leaves); `DpsClientWindow` draws
  movers, signal heads, fire spots, flame jets, ground marks and the HUD text, and F presses use. The headless summary counts every topic.
- **Building damage bounds:** the plugin logs one warning per building when breaches or windows beyond the bounds are left out.
- **Test infrastructure:** DDS uses UDP port 7400 + 250 * domain, and Windows excludes or hands out the ports above 49152, so a domain of 167 or more fails to
  create a participant at random; the old DpsClient range (130 .. 209) did that. All test domains are now below 167 and disjoint per assembly.

### Stage 7: the data path, the ignore list and carriage

- **3.8 (documentation):** presentation data bypasses the pivot repository, as built since stage 1; the section records the why and the accepted cost.
- **Ignore list:** the 23 no-op ingress modules of stages 1 to 6 (and round 1's `HrotPersonDetailIngress`) are gone. `HrotIgnoredIngress` is one module with one list
  (`Types`), the profile registers it where they were. The engine has no declared-ignore mechanism (an unrouted message is counted as unhandled), so the module registers one
  empty handler per listed type. The profile test now asserts that the contract's messages are exactly the handled ones (origin, session, entity state, entity removed, turret
  pose, fire, detonation) plus the list, with no overlap, and that a message that is neither (a test-only type) is still counted as unhandled.
- **3.5c, contract:** `PersonState` appended `SeatIndex`, `SeatExposed`, `SeatPose`, `CarryRole`, `PartnerId`, `CarryOtherId` filled; enums `SeatPose`, `CarryRole`;
  `VehicleLook` appended `FlailActive`. `HrotPersonDetail` appended `SeatIndex` (255 default), `SeatExposed`, `SeatPose` (name), `CarryRole`, `CarryOther`, `Partner`;
  `HrotVehicleLook` appended `FlailActive`; new enum `HrotCarryRole`.
- **3.5c, host:** `PersonDetailGather.CarriageOf` (port of `Carriage.Of`) and `VehicleLookGather.FlailOn` (port of `FlailActivity.IsOn`); the person gather now takes the TKB
  for the seat. The plugin maps the new fields (`LookNames.SeatPoseOf`) and resends a person when any of them changes.
- **Deviations:** the stretcher's rear man and a two-man weapon's rear man name the load (the casualty or the weapon) in `CarryOtherId` and the front man in `PartnerId`; a
  carrier with only a one-man weapon in his carry station has `CarryRole` None and `CarryOtherId` 0; a seated man's `CarryOtherId` is the vehicle (as `SeatVehicleId`), also
  for an enclosed seat. The two-man weapon team has no test (no weapon-system fixture in the host tests): the stretcher pair covers the partner logic.

### Open items

- The map-only presentation rules (gait, limb swing, rotors spinning, wheels, tracer streams, the lean of the riders) are not in HROT's look library; the client
  guide links them for porting (HROT's work).
- Not in the contract: door states (DD-017 section 10 gap), felled trees (`TreeDamage`), ground patches (craters, road blockage), replay effects beyond
  today's, filled gizmo triangles with a ground height (z = 0), and an exercise result that goes away inside a run.
- A flock's birds and a dragon are described by the type data and the animal state; the client draws them by HROT's rules.

**Round 5 note:** the ports named in this as-built (`HudNames`, `BankRule`, `PersonDetailGather.CarriageOf`, `FlailOn`, `RoundGather.BurnOf`) were replaced by calls to HROT's `Hrot.Look` functions once they existed; see DD-019 section 9, "Round 5: on HROT's look library".
