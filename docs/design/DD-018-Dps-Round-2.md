# DD-018: DPS round 2 - HUD, type data, rounds, flames

| | |
|---|---|
| **Status** | approved (R-133: the owner delegated the contract details; the contract is not handed to a client until the owner says so) |
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
- Rejected: one topic for windows and one for breaches (HROT keeps them apart only so a glass hit does not re-send breaches on its
  internal wire; a client applies both to the same building); keying by HROT's building index (an index into one import's list; the
  label survives a re-export).

### 3.7 Contract rules kept

Fields are appended at the end of existing types; enumerations mirrored from HROT go by name with an unknown value 0 (DD-017 3.10);
every enumerator is prefixed with its enum name; strings and sequences are bounded, except `TkbType.Json`. The protocol stays 1.0
until the owner hands it to a client (R-133).

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
- Plugin (in-memory transport): each message -> the right sample; the type catalog writes once per type; name mapping with unknown
  fallback; the guard test of HROT's enumeration names (CivilianMode, WeaponCategory, HeatMarkKind, UseReason, UseOutcome,
  RadarWarningLevel).
- Reference client: shows the kill feed line, the weapon line with round name and fire mode, and the use verdict in its HUD text;
  counts the new topics in its headless summary.

## 8. Proposals

P-1 .. P-6 are sections 3.1 .. 3.6, approved under R-133 (the owner delegated the contract details and reviews before the hand-over).

## 9. As-built

(filled during T-035)
