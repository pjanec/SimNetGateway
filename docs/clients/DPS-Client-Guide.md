# DPS for the Unreal image generator

Client guide for DPS ([DD-017](../design/DD-017-Dds-Player-Station.md), [DD-018](../design/DD-018-Dps-Round-2.md), [DD-019](../design/DD-019-Dps-Round-3.md), [DD-020](../design/DD-020-Dps-Round-4.md)). This file is the source; change it by commit. Last reviewed 2026-11-20.

This page tells the Unreal image-generator session what to read and what to build. It points into the sources and does not copy them. The authoritative contract is [DD-017](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md), and §3.11 of it is the client checklist. Where this page and the code disagree, the code wins.

## What to check out

Only public repositories are needed. Clone both side by side under one folder, because the gateway builds against the sibling `hrot` folder.

| Repository | Branch | Why |
| --- | --- | --- |
| [pjanec/SimNetGateway](https://github.com/pjanec/SimNetGateway) | `main` | The DPS contract (C# types, generated IDL), the `dps` plugin, the HROT host, and the reference client |
| [pjanec/HROT](https://github.com/pjanec/HROT) (clone into a folder named `hrot`) | `coordinator` (tested at `c5f8c1971`) | The simulation, its player mode, its 3-D map: the reference for cameras, control and the look of things |
| [CycloneDDS](https://github.com/eclipse-cyclonedds/cyclonedds) (C, for the Unreal plugin) | `a current release (the gateway runs CycloneDDS.NET 1.2.20; DDSI-RTPS interoperates across versions)` | The DDS implementation on the Unreal side. Any DDS that reads the IDL works; CycloneDDS is the reference |

- **Layout:** `<root>/SimNetGateway` and `<root>/hrot`. Otherwise set `HROT_ROOT` or pass `-p:HrotRoot=<path>` ([HrotHost.csproj](https://github.com/pjanec/SimNetGateway/blob/main/src/Hosts/SimNetGateway.HrotHost/SimNetGateway.HrotHost.csproj)).
- **Toolchain:** .NET SDK per each repository's `global.json`. FDP, the toolkit HROT runs on (ECS, possession, combat, look direction), is inside the HROT repository, so there are no further dependencies.
- **The IDL:** committed in [docs/clients/idl](https://github.com/pjanec/SimNetGateway/tree/main/docs/clients/idl) (`DpsTopics.idl`, `DpsTypes.idl`; the first line names the protocol version), and checked against the build by a test. It is generated from the C# contract; `bash scripts/refresh-dps-idl.sh` refreshes the copies. Feed them to `idlc` (CycloneDDS C) for the Unreal plugin. The C# sources the IDL comes from carry the field documentation: [DpsTopics.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Protocols/SimNetGateway.Protocol.DdsPlayer/DpsTopics.cs), [DpsTypes.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Protocols/SimNetGateway.Protocol.DdsPlayer/DpsTypes.cs), [DpsProtocol.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Protocols/SimNetGateway.Protocol.DdsPlayer/DpsProtocol.cs) (topic names, version, default lease).
- **Terrain:** the client must render the terrain in the simulation's own local frame (metres, x east, y north, z up). The HROT-to-Unreal terrain importer belongs to the Unreal repository (DD-017 §7).

## How DPS works

The gateway runs inside a HROT node as a node extension. Its `dps` plugin publishes the world on DDS and acts as that node's player station for one client. The block and sequence diagrams are in [DD-017 §2 and §5](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#2-block-diagram).

- **One station = one node = one player** ([§3.1](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#31-one-station--one-simulation-node-p-1)). Today that is the HROT editor (offline node). Cluster player nodes (`-m player`) are step 2.
- **One DDS partition per station** ([§3.2a](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#32a-one-partition-per-station-p-2a)). The client joins the station's domain and partition, both configured in the gateway's `dps` section. View-only clients use the same partition and write nothing.
- **No handshake** ([§3.2](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#32-late-join-and-absence-without-a-handshake-p-2)). State topics are TRANSIENT\_LOCAL, so a late joiner gets the current picture. Event topics are VOLATILE with a history of 1024 samples. The client's presence is the DDS liveliness of its player writers.
- **QoS is not in the IDL.** It is in the [§3.11 table](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#311-what-a-client-must-do) and on the C# types' `[DdsQos]` attributes. A mismatch fails the match silently. Player writers must offer AUTOMATIC liveliness with a lease of 2 s or less.
- **Frames and units** ([§3.3](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#33-frames-units-and-identity-p-3)): metres, x east, y north, z up; quaternion orientation with body forward = +x; sim time in seconds. Convert to Unreal's centimetres and left-handed axes once, at the boundary.
- **Motion** ([§3.4](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#34-motion-p-4)): `EntityState` is sent on change plus a 5 s heartbeat, with velocities and accelerations. The client dead-reckons. [DpsWorld.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Hosts/SimNetGateway.DpsClient/DpsWorld.cs) is a minimal sample.
- **People** ([§3.5](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#35-people-semantic-state-the-client-animates-p-5)): `PersonState` says what the body does (stance and its transition as a start time and duration, posture, held item, gesture, injury, action, seat, carry). The client picks clips, blends and IK. **Where he aims and looks** is intent in `PersonAim` (a target entity, or a point; see [People aiming](#people-aiming-the-upper-body-twist)), not a moving point in `PersonState`. `Appearance` carries the resolved outfit.
- **Vehicles and effects** ([§3.10](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#310-hrots-look-library-p-9-hrot-ce-4671--ce-4670--r-4296)): `VehicleLook` (part damage, kill kind, burning, smoking, lights, openings and rotor spool as timed motions) and `Effect` (muzzle flash, explosion, ground mark, wreck flame and smoke). Both come from HROT's shared look library `Hrot.Look`, so they match HROT's own map.
- **Instances that go away, world boundaries, time:** follow §3.11 exactly. Any not-ALIVE sample means gone. A new `Session.WorldEpoch` means drop everything and join again.

**One trajectory model** ([DD-018 §3.6c](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-018-Dps-Round-2.md)). Everything that moves arrives as track points: time, position, orientation, velocity, and for live entities acceleration and angular velocity. An `EntityState` sample is one track point; a traffic knot is another. Write one curve routine and use it everywhere:

- **Between two points:** a cubic Hermite in position, with the velocities as tangents, and a slerp in orientation.
- **Past the newest point:** second-order dead reckoning, position + v·t + a·t²/2, with orientation advanced by angular velocity.
- **When a correction arrives:** blend from the pose on screen to the new curve over 0.25 s.

There are two playback modes:

- **Live:** shown at the newest simulation time by extrapolation. Use it for everything a player sees and interacts with: units, vehicles, aircraft, rounds and the own unit. It adds no latency.
- **Delayed:** shown about 1.2 s behind the newest point, by interpolation only. Use it for background traffic and crowds.

A traffic mover that HROT turns into a real entity switches from delayed to live; blend that change rather than letting it jump.

The full topic list is in [§4](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#4-topic-summary). The gateway's own side is in [the plugin](https://github.com/pjanec/SimNetGateway/tree/main/src/Hosts/SimNetGateway.Plugin.DdsPlayer): `DpsOutbound.cs` for what is published and when, `DpsInbound.cs` for how player samples are accepted.

## What the station sends back to HROT

The station writes four topics: `JoinRequest`, `ControlFrame`, `Leave` and `GizmoView`. Each carries a random nonzero `ClientId`. The model to copy is HROT's own FPS station, `FpsCameraDriver.HandleInput` in [PossessionStation.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/PossessionStation.cs) (from line \~1890). It builds exactly the frame DPS forwards: the gateway copies `ControlFrame` field for field into HROT's `OperatorControlFrameEvent` ([HrotPlayerStation.Control](https://github.com/pjanec/SimNetGateway/blob/main/src/Hosts/SimNetGateway.HrotHost/HrotPlayerStation.cs)). The design behind it is [DESIGN\_Entity\_Control.md §8](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Entity_Control.md), and the default keys are [EmbodiedKeys.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/EmbodiedKeys.cs).

**Session flow.** Read `PlayerSlot`, then write `JoinRequest` (write a `JoinRequest` only once your writer is matched to the gateway's reader: the topic is volatile, so a sample written earlier is lost for good and a one-shot join never happens; check `DdsWriter.CurrentStatus.CurrentCount > 0` in CycloneDDS.NET, which reads `dds_get_publication_matched_status` in C; the reference client does) (Kind Unit, the offer's `Id`, a growing `RequestSeq`). The slot goes TakingControl, then Playing. When the simulation serves deployment (the editor does), a join goes through HROT's deploy server: see "Deployment offers" for roles, kits, areas and respawn. Send one `ControlFrame` per rendered frame (the reference client sends 30 Hz) while Playing. Esc writes `Leave`. Closing the process without `Leave` is also handled, because liveliness drops and the unit returns to its AI. Keep the writers alive for the whole session.

**The control frame, as HROT's station fills it:**

| Field | Meaning | How HROT's station computes it |
| --- | --- | --- |
| `Seq` | Grows each frame; the newest wins | `++seq` |
| `Forward`, `Lateral` | -1..1, in the BODY's frame: forward +, **lateral + = LEFT** (HROT `ManualMotion`: "lateral + = left") | W/S, A/D: A = +1, D = -1 (HROT `EmbodiedKeys` MoveSide). In the shoulder view the keys are camera-relative and are turned into the body frame by `Wrap(yaw - bodyYaw)` |
| `Yaw` | The body heading the sim should turn to (radians, world frame, 0 = +x, counter-clockwise) | The body yaw, not the camera yaw. Shoulder view: the body lags the look, dragged past 45° when standing (`ShoulderLag`) and turned at 540°/s when moving (`BodyTurnRate`). Free look (V): the neck limit, `DragBody`. Flyer: own heading led by 0.8 rad while A/D are held, else held |
| `Steer` | Vehicles and animals: A/D | Lateral is 0 for them |
| `Vertical` | Flyers: climb (Q/Space) or descend (Z/C) | 0 otherwise |
| `GaitMode` (and the old `Gait` byte) | `GaitWalk` or `GaitRun`. The old byte: 0 = run (Shift held), 1 = walk | Send both; the gateway uses `GaitMode` when it is not Unknown (0), else the byte. Note that the old byte's 0 is run |
| `StanceWanted` (and the old `Stance` byte) | `StanceRequestStanding`, `Crouched`, `Prone`, or `None` (no request). The old byte: stance + 1, 0 = no request | Ctrl held = crouched, C toggles crouch, Z toggles prone. Send both; the gateway uses `StanceWanted` when it is not None (0), else the byte |
| `Trigger` | Level: fire held | LMB held, except with binoculars up |
| `ShotSeq`, `ShotTarget` | Press counter; entity id under the crosshair at the press | LMB press. For automatic fire, send the level and the counter |
| `CancelSeq` | Press counter: drop a locked shot | X |
| `AmmoSeq` | Press counter: next round type | R |
| `Mount` | Selected weapon index | 1-4 pick from the unit's weapon slots |
| `FirePref` | 0 default, 1 sight, 2 lock | L toggles |
| `AimPoint`, `LookFrom`, `LookDir` | Required on every frame, or nothing fires | `Aim(camera)`: a pick through the screen centre against terrain and entity parts, else 1000 m along the view (`AimReach`). `LookFrom` is the camera eye and `LookDir` the ray through the crosshair. This also holds in the shoulder view, where the shot converges from the muzzle on the crosshair point |
| `UseSeq`, `Verb` (and the old `UseVerb` byte), `UseTarget` | Use press counter, the verb (`UseVerb` enumeration, `UseVerbNone` = the primary action) and the target's entity id | F. `UseTarget` is what the crosshair rests on, sent every frame. The gateway uses `Verb` when it is not None: it sends HROT that verb's position in the offered list for `UseTarget` (`OwnUnit.Actions`; a verb not offered is refused by HROT as NothingToDo). Else the old byte, a raw position in that list (0 = the primary) |
| `Lean` | Level: 0 none, 1 left, 2 right | Q/E held (both or neither = 0). Leaning also moves the shoulder camera to that side |
| `Binoculars` | Level: 1 while held up | B held |

**Use prompt loop.** Send `UseTarget` every frame, not only on a press. The gateway answers in `OwnUnit`: `CanUse`, `Verb` and `UseTargetId` give the first available action on that target. The verbs are the contract's `UseVerb` enumeration (`OwnUnit.Verb`; the old `UseVerb` byte stays equal to HROT's number), named like HROT's `UseVerbKind` ([OperatorUse.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Behavior/Possession/OperatorUse.cs): open, close, lock, unlock and breach a door; drag, carry and put down a casualty; drive, pilot, board, man, get out; take, pick up round, drop). Show the prompt from `OwnUnit`, then bump `UseSeq` on F. The action's progress comes back in `PersonState.Action` and `ActionState`. `OwnUnit.Actions` carries HROT's whole offered-verb list for the target in HROT's order (verb, label, available, reason, hold-only; at most 8), the first entry being the primary; name the chosen verb in `ControlFrame.Verb` (the gateway turns it into its position). A Drive or Pilot press makes the seat drive the vehicle (`PlayerSlot.UnitId` becomes the vehicle), and a new press while driving it with the soldier aboard is Get out ([note](NOTE-2026-11-23-Gateway-Remote-Drive-And-Verb-List.md)).

**HUD.** `OwnUnit` carries health, ammo per slot and the use verdict. `PlayerSlot` carries the state (Dead, respawn time). HROT's own HUD is `PossessionStation.Hud` and `PlayerHudLayer.cs`, a reference for what to show.

**Not carried by DPS yet** (HROT's station has them): next squad member (J), next seat (G), head out of a hatch and vehicle lights, the command wheel (T), select (I), the map peek (M), and binocular designation. These need contract fields before Unreal can offer them.

## The player's HUD

DPS now carries what HROT's own player HUD shows, through `OwnUnit` and four topics added in [DD-018 §3.1](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-018-Dps-Round-2.md). Two cues remain client-side by design, computed the way HROT's HUD computes them. HROT's HUD is designed in [DESIGN\_Player\_HUD\_And\_Debrief.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Player_HUD_And_Debrief.md), and the FPS station's parts in [DESIGN\_Entity\_Control.md §8k](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Entity_Control.md). The models below are pure and read like a specification. The reference client's `DpsHud` is a worked example.

| HUD element | HROT source | In DPS |
| --- | --- | --- |
| Health | `PossessionStation.Hud.Lines` ([PossessionStation.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/PossessionStation.cs)) | `OwnUnit.Health`, `MaxHealth` |
| Weapon list: HUD word (RIFLE, MG, CANNON, HANDS), round name, rounds/max, magazine, loading, overheated, heat, selected weapon | the words: `HudWords.KindOf` and `HudWords.RoundName` ([HudWords.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Hud/HudWords.cs), shared; the gateway calls them and sends the result); the line layout: `Hud.WeaponLine` | `OwnUnit.Ammo[]` (`Weapon`, `AmmoType`, `AmmoTypeId`, `Rounds`, `Max`, `Magazine` with -1 meaning no magazine, `LoadedRounds`, `Busy`, `Overheated`, `HeatPercent`) and `ActiveSlot`. `Reserve` stays 0, because HROT does not say whether its count includes the magazine |
| Fire mode (sight or lock) | `OperatorFireModes.Of` | `OwnUnit.FireMode` |
| Use prompt and verdict ("Open door"; locked, out of reach, done or refused) | `Hud.Prompt`, `Hud.Verdict`, the label `HudWords.Label` and the refusal words `HudWords.ReasonText` ([HudWords.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Hud/HudWords.cs)), `UseReason` and `OperatorUseStatus` in [OperatorUse.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Behavior/Possession/OperatorUse.cs) | `OwnUnit` `CanUse`, `UseVerb`, `UseTargetId`, `UseLabel`, `UseSeq`, `UseOutcome`, `UseReason`. Show the verdict only when `UseSeq` equals your last press |
| Spread circle | `SpreadOf` | `OwnUnit.SpreadRad` (half-width, radians) |
| Incoming-fire shock | `Shock`, `RoundPasses` | Client-side: +0.5 per round passing within 5 m of the eye (you see the rounds), capped at 1, decaying over 0.4 s |
| Hit-direction arcs | `HitFeedback` | Client-side: a `Detonation` whose target is your unit; the arc points against its travel, else toward the shooter; it lasts 2 s |
| Blocked-shot X, beyond traverse, outside the seat's arc | `BlockedShot`, `BeyondTraverse`, `OutsideSeatArc` | Client-side, from your own trace and the seat's arc in `TkbType` |
| Compass strip | [FpsCompass.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/FpsCompass.cs) (0 = north, clockwise, a tick every 15°) | Client-side, from the camera |
| Seat, carried man, hatch and lights lines | `Hud.SeatLine`, `Hud.HoldLine`, the crew line | `PersonState` seat and carry fields, `VehicleLook` lights and openings |
| Flight line and fighter HUD (speed, altitude, heading, g, radar warning) | `Hud.FlightLine`, [FighterHud.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/FighterHud.cs) | Speed, altitude and heading from `EntityState`. `OwnUnit` has `RadarWarning` (searched or tracked) with its bearing, and `MissileLaunch` with its bearing, time to impact and seeker. Bearings are degrees, 0 = north, clockwise |
| Death screen and kill cam | [FallScreen.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/FallScreen.cs) | `PlayerSlot` Dead and the respawn time; the killer from `ManDowned` |
| Kill feed | [KillFeed.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/KillFeed.cs) from [ManDownedEvent](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Combat/Events/ManDownedEvent.cs) | `ManDowned` event: victim, source, original source, state, body part, victim side |
| Messages and subtitles | [PlayerMessages.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/PlayerMessages.cs) | `PlayerMessage` event with its addressing. The gateway filters PER SEAT with HROT's own rule (`PlayerMessageAddressing.Addressed`, T-054): a message is written on YOUR seat's partition only when it is addressed to All, your side (the side you play; a seat holding no unit is side-less), the unit you hold or your station; Instructor messages reach no seat. A client shows what it receives; one per id and origin |
| Training HUD, game HUD, scoreboard, score pop-ups, awards, round summary, debrief | [PlayerHudLayer.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/PlayerHudLayer.cs) over [RulesModel.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Rules/RulesModel.cs) and [ObjectiveStatus.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/ScenarioRules/ObjectiveStatus.cs) | `Status` (one per objective, measure or trigger; read it like `RulesModel` does) and `Exercise` (the result per side) |
| Binoculars, designation, command wheel | `DrawBinoculars`, `Designate`, `CommandWheel.cs` | The view is client-side. Designation and squad orders are not carried yet |

## Gizmos

Gizmos are HROT's diagnostic drawings: paths, vision cones, contacts, action status, EQS and cover candidates, fire traces, blast, hearing, routes, sectors, navmesh and more. The client asks for the layers and units it wants. The gateway then runs HROT's own gizmo projectors on the station node and sends each requested layer as a complete list of primitives. Unreal only draws them. The design is [DD-017 §3.7](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#37-gizmos-p-7), and the as-built mapping is in [§10, Gizmos (Task 6b)](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#gizmos-task-6b).

**Data structures** (all in [DpsTopics.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Protocols/SimNetGateway.Protocol.DdsPlayer/DpsTopics.cs), region "Gizmos"; `GizmoShape` in [DpsTypes.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Protocols/SimNetGateway.Protocol.DdsPlayer/DpsTypes.cs)):

- **`GizmoView`** (client to gateway, keyed by `ClientId`, player QoS with liveliness):
  - `Layers`: layer names.
  - `SelectedIds`: entities whose gizmos of every family are drawn.
  - `PinnedIds` with `PinnedFamilies`: entities drawn for the named families only; `*` means every family.
  - `RateHz`: default 10, at most 60.
- **Family layers need a selection.** HROT draws a unit's FAMILY gizmos (paths, contacts, brain state …) only for units in `GizmoView.SelectedIds`, or with the family pinned (`PinnedIds` / `PinnedFamilies`; a brain family: pinned only) - HROT's one rule, `GizmoFamilyVisibilityPolicy` (CE-3120). An empty selection gives empty family frames. The card layers (`Labels`, `UnitCards`, `UnitSymbols`) need none.
- **`GizmoFrame`** (gateway to client, keyed by `Producer` + `Layer`, state QoS): the complete picture of one layer. Each sample replaces the previous one, and a disposed instance means the layer was turned off. `Producer` is the station's partition. `SimTime` is 0 for now.
- **`GizmoPrimitive`**: `Shape`, `Points` (world coordinates, the simulation frame), `ColourRgba` (0xRRGGBBAA), `Width`, `Text`. Appended fields (DD-024): `Space` (`GizmoSpaceWorld`, the default, or `GizmoSpaceEntityCard`), `AnchorId`, `Row`, `CardX`, `CardY`, `CardW`, `CardH`, `FillRgba`. They apply only in card space, see Entity cards below; a world primitive leaves them 0.

| `Shape` | `Points` | `Width` |
| --- | --- | --- |
| LineStrip | 2 points (each HROT line is one strip) | line thickness |
| Arrow | from, to | thickness |
| Circle | centre (HROT draws a sphere) | radius |
| Polygon | the vertices of a filled triangle | thickness |
| Box | centre (x, y, z = 0), then (extent x, extent y, angle in degrees) | outline thickness |
| Text | position (x, y, z = 0); the string is in `Text` | not used |
| Icon | none; sent only on an entity card (see Entity cards) |  |

Box and Text come from HROT's 2-D primitives. The gateway gives them the ground height under their point, so they arrive on the terrain. Filled triangles (Polygon) still carry z = 0.

**Names to use:**

- **Layer names** are the constant names of HROT's [DebugTraceLayers.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Diagnostics/Gizmos/DebugTraceLayers.cs): FireTraces, Doors, Paths, Blast, Hearing, Roads, Cover, Navmesh, Sectors, Rounds, SquadCommand, Actions, Utility, Eqs, Routes, TacticalGraphics, Traffic, Contacts and so on.
- **Family names** are the member names of HROT's [AiOverlayFlags.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Behavior/Diagnostics/AiOverlayFlags.cs): Perception, TargetMemory, Eqs, UtilityDecision, SquadAssignment, Channels, Path, CommandPicture and so on.

**Rules:**

- **No view, no work.** Without a `GizmoView` no projector runs.
- **One view per station.** The gateway follows the player's view, or with no player the first client's. Another client's view is dropped with a warning.
- **Resend after a boundary.** The view is VOLATILE, so send it again after a new `Session.WorldEpoch`.
- **Keep sizes within the limits:** at most 4096 primitives per layer and 64 points per primitive.

**Gateway side:** [HrotGizmoHost.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Hosts/SimNetGateway.HrotHost/HrotGizmoHost.cs) runs HROT's projectors and converts primitives (`Convert`). [DpsGizmos.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Hosts/SimNetGateway.Plugin.DdsPlayer/DpsGizmos.cs) handles the topics.

**HROT side:** the projectors are the classes with `[GizmoProjector]` in HROT (for example under [Fdp.Toolkits/Diagnostics/Gizmos](https://github.com/pjanec/HROT/tree/coordinator/FDP/Toolkits/Fdp.Toolkits/Diagnostics/Gizmos) and `Hrot.Presentation/Gizmos`). Reading one shows what a layer draws and in which colors.

The reference client draws gizmos too: G toggles Paths and Contacts for the picked unit, in [DpsClientWindow.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Hosts/SimNetGateway.DpsClient/DpsClientWindow.cs). Gizmos have not been checked live yet (DD-017 §10), so expect fixes there.

## Entity cards (3-D labels, DD-024)

HROT's 3-D labels are gizmos too: small cards of rows above an entity (name, side frame, health or strength bar, the unit's NATO symbol, a BOUNTY mark and whatever HROT adds later). HROT's projectors decide what a card shows and who gets one. The client only DRAWS: group, stack, place, occlude. It rebuilds nothing from `Entity` fields. The design is [DD-024](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-024-Dps-Entity-Cards.md), HROT's card API is [EntityCards.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Diagnostics/Fdp.Diagnostics.Contracts/EntityCards.cs) and its renderer [EntityCardRenderer.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/ExtDeps/GizmoMap/GizmoMap.Presentation/Rendering/EntityCardRenderer.cs) (the reference layout).

**The fields.** A card primitive is a `GizmoPrimitive` with `Space = GizmoSpaceEntityCard` and an empty `Points`. A card is the set of primitives of one frame that share an `AnchorId`.

| Field | In card space |
| --- | --- |
| `AnchorId` | the `EntityId` of the entity the card belongs to (a primitive whose anchor the gateway does not publish is dropped by the gateway and counted) |
| `Row` | the row, stacked top-down in ascending order; **0 is card-wide**: the frame and the symbol cell |
| `Shape` Text | `CardX` = x in pixels from the row's left, `CardY` = y in pixels from the row's top (0), `Width` = the font size in pixels, `Text` = the line (at most 31 characters), `ColourRgba` its colour |
| `Shape` Box | a bar, a tick or the frame: `CardX` / `CardW` = the centre and half-width as fractions of the card's width; `CardY` / `CardH` = the centre's offset below the row's top and the half-height, in pixels; `FillRgba` = the fill (0 = none), `ColourRgba` = the outline of `Width` pixels (0 = none) |
| `Shape` Icon | the unit's NATO symbol: `Text` = the symbol code (a 2525C SIDC, optionally followed by `\|` and the producer's extension; the part after the first `\|` may be ignored), `CardH` = the symbol's height in pixels. It is on row 0: the card's left cell, spanning the rows |

Colours are 0xRRGGBBAA, as everywhere.

**The layout** (what HROT's renderer does):
1. Group the frame's primitives by `AnchorId`. A card whose `Entity` has not arrived yet is skipped until it does.
2. Sort each group by `Row` (stable). The `Row` 0 primitives are card-wide: the background and outline boxes span the whole card, the symbol is the left cell.
3. The rows are the runs of one `Row` value after row 0. A row is as high as its tallest primitive (a Box's `2 * CardH`, a Text's font size, a symbol's height) with a small gap between rows. The card is as wide as its widest text (measured at its font size) plus the symbol cell and the padding, at least a minimum width.
4. Place the card above the body (a billboard of constant pixel size, centred on the entity's head). A body-less commander (`Entity.IsUnit`) has no body: place its card at its position plus 2 m up.
5. Occlusion: depth-test the billboard against the scene, so terrain and buildings hide it.

**The three modes are layer subscriptions** in the client's `GizmoView` (they are HROT's own switches): mode 1 = no card layers; mode 2 = `UnitCards` + `UnitSymbols` (the commanders' cards with their symbol cell); mode 3 = those plus `Labels` (the men's and vehicles' cards). The symbol cell arrives inside the `UnitCards` frame, and only while `UnitSymbols` is subscribed too. The `UnitSymbols` frame itself carries the standalone map symbols of the 2-D map, which are not card primitives and are not sent.

**Distance is the client's choice.** Nothing about distance is on the wire. HROT's numbers are a suggestion, in metres per pixel (`2 * d * tan(fovV / 2) / viewportHeight`): the card (frame, bars, symbol) up to 1 m/px, the name up to 2 m/px, an aircraft's name at any distance. **Rule A (R-143): never cut a card whose anchor `Entity.Domain` is `DomainAir`.** Which aircraft get a card at all (a swarm is shown by its commander's card instead) is HROT's decision in its projectors.

**Commanders.** `Entity.IsUnit` is true for a body-less commander (a squad, a platoon: an organisation with a position, the centroid of its live members). It arrives as an entity with its `EntityState`, a unit DIS kind (11) in `Dis`, `Domain` Other and NO person, appearance or vehicle topic: do not give it a stand-in body. Its card (frame, strength bar, info line, symbol cell) is how it is seen.

**Views are per seat** (DD-023): each seat's client has its own `GizmoView` and receives its own `GizmoFrame` instances, so a second display that needs other layers takes a seat.

**A calm layer arrives only at the heartbeat** (T-061, R-145): the gateway writes a layer's `GizmoFrame` only when its content changed since its last write, plus a repeat every `dps.heartbeatSeconds` (default 5 s), and a new `GizmoView` makes it say every layer again. The topic is `TransientLocal` with keep-last 1, so the LAST sample persists: keep drawing the layer from it until a newer sample or a dispose arrives, and do not read a quiet layer as an empty one. Today every layer is written on the seat's own partition; the shared layers (one frame on the world partition for every seat, DD-024 T-061 part 1) are not built yet, so a client must keep reading `GizmoFrame` on its seat's partition.

**Limits.** A frame holds at most 4096 primitives; a card is about 6 to 10 primitives (an estimate), so one layer carries roughly 400 to 600 cards. Beyond that the gateway keeps the first 4096 (cards of 8 000 units at once have not been measured, see DD-024 §9).

## Entity status: the data to draw your own cards (DD-025)

A client may draw an entity's card from DATA instead of subscribing to the card layers above: the gateway sends the values HROT's own rules compute and the client keeps the layout. The `EntityStatus` topic is optional and costs nothing when not read. The design is [DD-025](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-025-Dps-Entity-Status.md). The card gizmos stay available (DD-024); a client uses one source for a card, never both.

**The topic.** `EntityStatus` is keyed by `EntityId`, Discrete, reliable, `TransientLocal` with keep-last 1, written once on the world partition (`<base>-*`) like `Entity`, so every seat gets it and a late joiner gets the last values at once. There is one instance for every entity HROT would give a card (HROT's `Seeable.OnMap`): not for a round, an effect, a cloud, a minefield, a camera or a rule (objective, trigger). It is disposed with the entity, and also when the entity stops having a card.

| Field | Meaning | None-value |
| --- | --- | --- |
| `Symbol` (string, at most 40) | the unit's NATO symbol in its side's frame: a 2525C SIDC, then optionally `\|` and HROT's extension (the part after the first `\|` may be ignored); the same text as the card's `Icon`. Only for a unit (a body-less commander) | empty = not a unit |
| `HealthCurrent`, `HealthMax` | HROT's health pool and its maximum. **No fraction is sent: derive `clamp(Current / Max, 0, 1)` yourself.** HROT's bar is green from 66 %, yellow from 33 %, else red | `HealthMax` 0 = no health |
| `StrengthPresent`, `StrengthAuthorized` | a unit's men present and authorized (HROT's `UnitCardFacts.Strength`; a stochastic unit's slots included). HROT draws the bar as `Present / Authorized` with ticks at 70 % and 90 % | `StrengthAuthorized` 0 = not known: draw no strength bar |
| `AmmoShare` | 0..1, the share of its rounds a unit still carries (HROT: green from 50 %, amber from 25 %, else red) | -1 = not known |
| `MindFill`, `MindBreak`, `MindBand` | a living man's mind-state bar: the fill 0..1 (stress for a soldier, fear for a civilian), the mark of his breaking point on it (a soldier only) and the band to draw it in: Calm (slate blue), Nervous (amber), Broken (red, with the word BROKEN under it), Fear (amber-orange) | `MindBand` None = draw no bar (a calm man, a unit, a vehicle, a dead man); `MindBreak` negative = no mark |
| `Behaviour` (string, at most 32) | the name of the behaviour the entity runs now (the simulation's behaviour registry names it) | empty = none, or not known (a node with no brain) |
| `Task` (string, at most 32) | the name of the task the entity is ordered to do (a running reaction reports the task it paused; what a mission plan or the unit's own doctrine chose is not an order and reads empty) | empty = none, or not known |

**On change.** A sample is written when an entity first has a card and then only when a value changed: the float fields (health, ammunition share, mind fill and mark) by at least 0.01 against the LAST SENT value, the integers, the symbol, the behaviour and the task exactly. A calm entity sends nothing, and there is no heartbeat: the last sample is the current one. The commander flag is `Entity.IsUnit` (not repeated here).

**What a client does with it.**
1. Join `EntityStatus` with `Entity` by `EntityId`. A status whose `Entity` has not arrived yet waits for it.
2. **The card modes** (the client's own switch, the same three as HROT's layers): mode 1 = no cards; mode 2 = the cards of entities with `Entity.IsUnit` only (symbol, strength bar, ammunition bar); mode 3 = mode 2 plus the cards of every other entity with a status (health bar, mind bar, name from `Entity.Name`).
3. **Rule A (R-143): never cut the card of an anchor whose `Entity.Domain` is `DomainAir`**, at any distance. Distance is otherwise the client's choice (HROT's suggestion: frame and bars up to 1 m/px, the name up to 2 m/px).
4. The look of a card is yours; HROT's own layout is the reference.

`Behaviour` and `Task` are the last two members of the topic (appended; a client built from the earlier IDL still reads the rest). Show them as text on a card; the names are the simulation's own.

## Camera controllers to build

Build three cameras that behave exactly like HROT's. Two of them, the follow camera and the player's camera, are one rig with a distance continuum: far away it orbits the unit, close in it is over the shoulder, at zero it is the eyes, and a little past zero it is ahead of the eyes. The design and its decisions are [DESIGN\_Unit\_Camera.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Unit_Camera.md) (§4 the continuum, §5 control over the shoulder, §8 as-built). The numbers are in [UnitCameraRig.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/UnitCameraRig.cs), a pure static class that ports directly.

| Camera | HROT source | Behavior to copy |
| --- | --- | --- |
| Free flight | [MapCamera3D.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Engine/Fdp.Presentation/Vis3D/MapCamera3D.cs) | RMB held looks around the eye. W/S/A/D fly along the view, E/Q go up and down. Speed is 0.4 m/s per metre of eye height above ground (at least 1.2 m/s, doubled near the ground). It starts at 25% and ramps up to 5× after 3 s held. Shift ×4, Ctrl ×20, both ×100. A lone wheel click steps along the view; fast clicks grow the step (`FlyBasisSpeed`, `WheelStep`) |
| Follow (watch any unit) | [FollowCameraDriver.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/FollowCameraDriver.cs) | The wheel walks the continuum (×1.2 per notch; below 0.8 m it snaps to the eyes; out of the eyes it jumps to 1.5 m). Far, it looks at the unit's centre; at the eyes it looks along the unit's own gaze (`PersonState.LookTarget` / `HeadYaw`). RMB orbits; at the eyes RMB offsets the look and springs back. W/A/S/D, Esc or a drag hands control back to free flight at the same spot |
| Player (possessed) | `FpsCameraDriver` in [PossessionStation.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/PossessionStation.cs) (`Place`, `BeforeDraw`, `ShoulderCamera`, `Recentres`) | The shoulder view is the default; O toggles eyes and shoulder. The wheel sets the distance in 0.5 m steps (`DistanceStep`), and weapons are on 1-4 only. RMB held aims from the eyes with zoom (`FpsZoom`: 45° normal, 30° iron sights; binoculars 10°). Q/E lean and move the camera to that shoulder |

Rig numbers (person; a vehicle ×4, a flyer ×8, `ScaleOf`):

- **Continuum:** beyond 12 m it orbits the centre; between 12 and 3 m the pivot blends (smoothstep) toward the shoulder; under 3 m it is over the shoulder. The shoulder offset is 0.55 m to the side and 0.2 m up, reached at 1.5 m. The default play distance is 2.2 m for a person, 9 m for a vehicle (2.5 m higher) and 18 m for a flyer (4.5 m higher). The play maximum is 5, 24 and 45 m. The wheel goes past the eyes by up to 0.75 × scale (0.75, 3 or 6 m) to clear a cockpit's nose.
- **Playing vs watching:** playing, the camera goes back along the LEVEL look, so looking down does not swing it up. Watching, it orbits with pitch (`Camera(orbitPitch)`).
- **Spring arm (walls):** a ray from the pivot to the camera, stopped 0.25 m in front of the first hit (glass does not stop it). It pulls in at once and eases out at 4 m/s × scale. When squeezed, the camera rises toward 0.6 m above the eyes so it never ends up at belly height (`AboveHeadWhenSqueezed`).
- **Eye point:** a person's is the drawn head, so use the character's head socket. A crewman's is his seat eye. Aboard a vehicle, the camera is the vehicle's chase camera hung from that seat eye, so a gunner's camera stands behind his gun. Mouse look is 0.0025 rad per pixel (`LookSensitivity`), with pitch clamped to ±1.4 rad.
- **Driver re-centre:** a ground vehicle's look left alone for 1.5 s eases back to the front at 90°/s. This does not apply when the selected weapon lays a turret, because there the look is the turret's order. V locks the view to the hull.
- **Body vs camera:** over the shoulder the upper body twists to the aim at once (up to 75°, `HumanGait.MaxAimTwist`). The legs follow past 45°, or immediately when moving. That is the `Yaw` the control frame sends (previous section).
- **Place the camera just before drawing** (`BeforeDraw`, CE-1352), from the unit's pose as it will be drawn this frame. Otherwise the camera lags a fast vehicle by a tick and stutters.
- **The aim follows the camera:** the crosshair ray from the camera gives `AimPoint` / `LookFrom` / `LookDir` in every mode. HROT draws a red X where the muzzle's round would really stop when that is more than 0.5 m short of the crosshair point (`BlockedShot`); the client can show the same from its own trace.

The rails in HROT's `PossessionStationTests.CE1293_*`, `CE1338_*` and `CE1352_*` pin these behaviors and make good acceptance cases for the Unreal port.

## How HROT's map derives what it draws

Unreal must look like HROT's 3-D map wherever the simulation leaves the look open. DPS sends simulation state. Everything derived from that state for presentation lives in one of two places in HROT, and the two need different treatment:

- **Shared look library [`Hrot.Look`](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look).** Pure functions used by HROT's map and the gateway alike. **Treat `Hrot.Look` as the reference for these rules**: where DPS carries the result (`Effect`, `Appearance`, `VehicleLook`, `Round`, `Animal`, `PersonState`, `View`, `Impact`) Unreal draws what it receives and does not re-derive it; where DPS carries the inputs but not the result (spin rates, upper-body limits and easing, tracer streams, round streaks and trails, animal gaits, the HUD words) the table names the `Hrot.Look` function whose constants and branches to use, and when HROT changes one, the function is the truth. The map calls the same functions, so the two cannot disagree.
- **Drawing inside the map ([`Hrot.Presentation`](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation)).** Block figures, kits, layers and camera drivers: how HROT turns the rules' answers into pictures. A reference for poses and drawing order, nothing Unreal has to match. The person limb gait and stride constants (`MotionTracker`, `LocomotionBlend`) are still here too; port them from the linked files.

| What | Where HROT derives it | Status for DPS / what Unreal does |
| --- | --- | --- |
| Muzzle flash, explosion, ground mark, impact puffs and marks, backblast and launch puff, wreck flame and smoke | [EffectRules.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Effects/EffectRules.cs), [FiringBlasts.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Classification/FiringBlasts.cs), [AirMissiles.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Classification/AirMissiles.cs) | Shared. Arrives as `Effect` (kind, size class, surface, munition DIS type, anchor or position, lifetime, size; `SimTime` is when the effect began). Draw it. The gateway reads the effect entities HROT's spawner makes (`EffectRules.SpecOf`, [EffectRules.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Effects/EffectRules.cs)), the same live and in a replay, and sends each entity once |
| Person outfit, colors, camouflage, face | [PersonLook.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/People/PersonLook.cs), [Outfits.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/People/Outfits.cs), [Wardrobe.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/People/Wardrobe.cs) | Shared. Arrives as `Appearance` |
| Vehicle part damage, kill kind, lights, brake lights, burning, smoking, hatch and door opening progress | [VehicleLook.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Vehicles/VehicleLook.cs), [BrakeLights.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Vehicles/BrakeLights.cs), [VehicleSockets.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Vehicles/VehicleSockets.cs) | Shared. Arrives as `VehicleLook`; `Openings` give a timed motion per TKB opening id (`TargetProgress`, `ChangedAt`, `Seconds`; `Progress` is the value at the sample). Unreal maps opening ids to its hatch and door meshes and animates the motion itself |
| Rotor speed, engine level, crew head out | `VehicleLook.RotorSpeed` / `EngineLevel` / `CrewExposed` (same file, new in HROT CE-4670) | On DPS (built): the spool as data, `VehicleLook.RotorTarget`, `RotorFrom`, `RotorChangedAt`, `RotorSpoolSeconds` (see Engine level); `RotorSpeed` is the value at the sample. Spin the rotor by the spool value times the next row's rate. `OpeningLook.CrewExposed` |
| Main and drone rotors spinning, wheels rolling, antennas, mine flails, aircraft bank | [MotionRules.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Vehicles/MotionRules.cs) (`Hrot.Look`, shared: `Spool`, `Spin`, `RotorRuns`, `DroneProp`, `BankTarget`, `EaseBank`, `Roll`, `CarWheelRadius` and the constants) and [FlailActivity.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Vehicles/FlailActivity.cs) (`FlailActivity.IsOn`, the flail's on/off); the map's `MotionTracker` calls them | Shared rules; the per-frame angle accumulator is the renderer's own (it advances on sim time). Derived from the entity's own motion and sim time. A rotor turns 4.3 rev/s (a drone 7.3), spools over 3 s and runs on 20 s after landing. Wheels roll by distance over radius. Bank is up to 0.61 rad over 0.15 s (DPS sends it as `EntityState.Bank`; the flail as `VehicleLook.FlailActive`). Use the `MotionRules` constants |
| Person limbs and gait: walk/run blend, stride, lean, crawl | [LocomotionBlend.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Animation/LocomotionBlend.cs) (the shared gait rule) and [MotionTracker.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/MotionTracker.cs) (1.6 m per stride cycle, swing 0.45 walk / 0.75 run, lean 0.035 / 0.21, crawl 0.7 m) | Map only. Gait comes from speed (DD-017 §3.4 has no gait field). Port the blend weights and drive Unreal's own clips with them |
| Upper body twist to the aim, aim pitch, head turn | [UpperBody.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/People/UpperBody.cs) and [PostureBlend.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/People/PostureBlend.cs) (twist up to 75°, pitch 35°, head = `HeadYaw` less the drawn twist; prone and dead do not twist) | Shared rules. The inputs are in `PersonAim` (aim point or target, look, head yaw). Use the limits and easing from `UpperBody` and `PostureBlend` |
| Held weapons and items, riding, rappelling and parachute descent, animals | [BlockFigureWeapons.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/BlockFigureWeapons.cs), [BlockFigureHeldItems.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/BlockFigureHeldItems.cs), [BlockFigureRiding.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/BlockFigureRiding.cs), [BlockFigureDescent.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/BlockFigureDescent.cs) | Map drawing only. A reference for poses; Unreal's skeletal animation replaces the block figures. The animal rules (quadruped gaits and gait bands, flock birds, the grazing cycle) are shared in [AnimalMotion.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Animals/AnimalMotion.cs) |
| Machine-gun and autocannon tracer streams | [FireStreams.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Effects/FireStreams.cs) (`Hrot.Look`, shared) | Shared rule, not on the wire. On each `Fire` from a machine gun or autocannon: dashes 1.8 m long every 5 m, running out at 900 m/s from the muzzle toward the aim, up to 500 m, for 0.35 s, fading. `FireStreams.StreamOf`, `TowardOf` and `Dashes` are the reference; the constants are `FireStreams.Window`, `Spacing`, `DashLength`, `Speed`, `MaxLength` |
| Tracer streaks of real rounds; rocket and missile bodies and smoke trails; grenades in flight and lying | [RoundLook.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Effects/RoundLook.cs) (`Hrot.Look`, shared: `TracerOf` = last 0.03 s of flight, at most 25 m; trail puffs every 0.9 m, at most 60; `BurnOf` and `TrailFactor`: trail full while the motor burns, fading 0.8 s after; the lying and stopped rule; tumble and spin) with the munition's look [MunitionVisualDto.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Tkb/Domain/MunitionVisualDto.cs). The map draws them in [ProjectileLayer.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Effects/ProjectileLayer.cs) | Shared rule; the data is on DPS (built). Rounds with a look are entities (`DomainMunition`) with a `Round` record (munition type, shooter, muzzle point, lying, stopped, fuze left, `LaunchedAt`, `BurnSeconds`), and the look comes from `MunitionLook`. Treat `RoundLook` as the reference for the streak and trail. Bullets without a look only appear as a `Fire` (origin, velocity, munition type). |
| Flamethrower jet, burning men, burning spots, scorch marks | [FlameLook.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Effects/FlameLook.cs) (`Hrot.Look`, shared: jets, burning spots sized clamp(radius x 0.7, 0.3, 8), burning people, scorch marks) from `FlameJets.TryGet` ([FlameJets.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Combat/FlameJets.cs)) and `PersonLook.IsBurning` | Shared rule, on DPS (built): FlameJet per lit jet (aim point, range, half-angle, intensity), PersonState.Burning, FireSpots per holder in world coordinates, GroundMarks (BurnedArea is a scorch). Draw those; `FlameLook` is the reference for their look |
| Doors (leaf swing) | [DoorLeaf3D.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/DoorLeaf3D.cs) | Doors are sent as entities with no door state (DD-017 §10 gap) |

The design notes behind these rules are [DESIGN\_Visual\_Effects.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Visual_Effects.md), [DESIGN\_Map\_3D\_Mode.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Map_3D_Mode.md) (§6: "things come alive") and [DESIGN\_Vehicle\_Appearance\_State.md](https://github.com/pjanec/HROT/blob/coordinator/docs/designs/vehicle-appearance/DESIGN_Vehicle_Appearance_State.md). Everything in the map is driven by sim time and the entity's own state, so a pause freezes it and a replay seek is correct. Keep that property in the port.

## People aiming: the upper-body twist

An aimed soldier in HROT does not turn his whole body to his target; the simulation turns the body. In HROT's map the upper body twists and pitches toward the aim, the legs keep the body's heading, and the head turns on top of that. Copy these rules so that Unreal's rifle points where HROT's round goes. The rules are shared in `Hrot.Look`, and Unreal should treat them as the reference: `UpperBody.TryAim`, `UpperBody.AimInSeat` and `UpperBody.HeadTurnOf` in [UpperBody.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/People/UpperBody.cs), the timing in `PostureBlend` in [PostureBlend.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/People/PostureBlend.cs). The map's `FigureMotion` only turns their answers into a pose. The design is [DESIGN\_Weapon\_Layers.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Weapon_Layers.md) W-13 and W-14.

1. **The aim point.** It applies only when `PersonState.Posture` is Aimed. The aim is intent in `PersonAim` (DD-020), not a moving point. If `PersonAim.AimTargetId` is not 0, the aim point is that entity's chest: its own `EntityState` position (dead-reckoned every frame) plus 1 m up. That is HROT's single definition of the aim (`WeaponPostureOf.TryAimPoint` in [WeaponPostureOf.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Combat/WeaponPostureOf.cs)): the target's chest, 1 m above its position. The gateway sends no further sample as the target moves. If the client has no such entity (not published, or outside its view), use `PersonAim.AimPoint`, which carries the chest as of the sample. With `AimTargetId` 0, `AimPoint` is the point aimed at (all 0 = not aiming), and a new sample arrives when it moves. `PersonState.AimPoint` is the old copy: the value at the last `PersonState` sample, no longer re-sent when it moves.
2. **From the shoulder.** Take the vector from the shoulder to the aim point, in the body's frame (the `EntityState` orientation). The shoulder is at 1.31/1.8 of body height when standing aimed (the athletic stance) or 0.98/1.8 when crouched.
3. **Twist and pitch.** Twist = atan2(left, forward), clamped to ±75° (`HumanGait.MaxAimTwist`, the same limit the simulation's fire arc uses). Pitch = atan2(up, horizontal), clamped to ±35°. Under 0.1 m of horizontal distance there is no aim.
4. **Ease, do not snap.** Twist and pitch approach their targets with a 0.12 s time constant on simulation time, and return to 0 when the soldier stops aiming. A posture change (at ease, ready, aimed) moves the rifle over 0.3 s.
5. **Stance.** Standing aimed is the athletic stance: hips 0.10 × height lower, upper body leaning about 10° forward, shorter gliding steps on the move. A crouched run leans forward about 26°, less while aiming.
6. **The head.** The look is intent too: `PersonAim.LookTargetId` (an entity: look at its position, not its chest) or `LookTarget` (a point), valid while `HasLookTarget`. `PersonAim.HeadYaw` is the head's turn at the time of the sample, and a sample is written only when the head goes off or back onto the body (about 1 degree), not as it turns. So HeadYaw 0 means straight on the body, and HeadYaw not 0 means turned toward the aim or look target; between samples derive the turn yourself: the bearing from the body to the aim point (aiming) or the look point, relative to the body's heading, clamped to the neck limit (HROT's `LookFollowSystem.Toward`). Head turn = that, minus the drawn twist, eased with a 0.15 s time constant. So it shows where he looks: an aim, an ordered look, a glance. `PersonState.HasLookTarget`, `LookTarget` and `HeadYaw` are the old copies (values at the last `PersonState` sample, not re-sent).
7. **Exceptions.** Prone and dead figures neither twist nor lean.

The legs follow the aim through the simulation, not the renderer. Inside 75° the body heading turns toward the aim at a rate proportional to the angle; beyond it, at full turn rate (`HumanGait` in [HumanGait.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/CarKinem/Controllers/HumanGait.cs)). Unreal gets that through the orientation. A sideways lean around a corner is a shear about the feet that the simulation also uses for sight and hits (`LeanShear`, same file).

HROT also has a skeletal animation backend on the Stride engine (`Hrot/Subsystems/Hrot.MuscleCharacter.Animation.Stride`, [DESIGN\_Stride\_Port.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Stride_Port.md)). It reads the same aim point and is the closest existing example of a skeletal renderer driven by HROT's state.

## Animals and riders

HROT's animals are cattle, sheep, goats, pigs, horses (herd, riding and war horses), dogs, cats, rabbits, hens, single birds and flocks. The design is [DESIGN\_Animals.md](https://github.com/pjanec/HROT/blob/coordinator/docs/designs/animals/DESIGN_Animals.md): the species in §3, the decisions in §4, the drawing in §6.3a and [DESIGN\_Map\_3D\_Mode.md §6al.3](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Map_3D_Mode.md), the mockup [animals-visuals.svg](https://github.com/pjanec/HROT/blob/coordinator/docs/designs/animals/animals-visuals.svg). The gaits, gait bands, flock birds and grazing cycle are rules in [AnimalMotion.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Animals/AnimalMotion.cs) (`Hrot.Look`, shared: treat it as the reference); the drawing code is [AnimalKits.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/AnimalKits.cs) and the state is [AnimalComponents.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Animals/AnimalComponents.cs).

**State the simulation replicates** (on DPS through DD-018 §3.5a, built: a new `DomainAnimal` and an `Animal` topic):

- **Mode:** Calm (grazing, wandering, perched), Alert (head up), Flee, Recover (walking back), Rest (lying, roosting), Guard (a dog barking or attacking), Crouch (a frozen rabbit).
- **Fear band** 0 to 3, and flags: Bolting (a ridden horse running away with its rider), Dangerous (attacking right now), Calling (open the mouth for a moo or bark).
- **A flock** is one entity drawn as many birds: count alive, initial count, seed, mode (flying, landed, perched).
- **The species** is the entity's type; read it from `TkbType`.

**How HROT animates them:**

- **Gaits** come from the smoothed speed using per-species bands (`QuadrupedGaits.BandsOf`). The animal stands below 0.15 m/s. Leg swing is amplitude × sin(phase + 2π × offset), with each leg's offset per gait: walk (four beats), trot (diagonal pairs), canter (three beats), gallop (pairs). Rabbits hop. The phase advances by distance, so feet never slide; a paused clock freezes it. For example, a horse walks below 2.2 m/s, trots to 4.8, canters to 7.8, then gallops, with strides of 1.7, 3.0, 3.8 and 5.6 m.
- **Heads.** The neck pitches about the withers and the head counter-tilts (45%). A calm animal standing still grazes in a seeded 9 to 15 s cycle; an alert one holds its head up. Hens peck.
- **Rest** uses a lying pose (belly down, legs folded). A jump (horses clear obstacles up to about 1.2 m) tucks the legs, read from the height above ground.
- **Death:** the body rolls onto its side over 0.6 s of simulation time with stiff legs. A dead animal never burns.
- **Birds** beat their wings; a hen flutters. A flock's birds are closed-form offsets of (seed, index, simulation time, mode), never integrated, so a seek, a replay and a late joiner all show the same birds. Up to 400 are drawn, and the highest indices go first when birds die (`FlockBirds` in AnimalKits.cs).

**Riders.** A riding horse is a carrier with seats, like a motorcycle. Seat 0 is the rider, a firing seat: he shoots from the saddle. Seat 1 is an optional second rider who can fire to the sides and rear. A war horse has seat 0 only.

- **The pose:** the rider's `PersonState` gives the carrier (`SeatVehicleId`) and the seat role; the seat's pose (astride) and position come from the carrier's type data (`TkbType`). HROT draws the rider astride ([BlockFigureRiding.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/BlockFigureRiding.cs), `BlockFigure.Mounted` and `MountedAiming`): thighs splayed over a barrel about ±0.35 m wide, knees at ±0.46 m and 0.25 m below the hips, feet in the stirrups 0.72 m below the hips.
- **Firing from the saddle:** a rider who fires holds the rifle shouldered and twists his upper body to the aim by the rules above.
- **Bolting:** a bolting horse shows a cue on the rider's HUD.

**The dragon** is planned in HROT but not built yet ([CE-4634](https://github.com/pjanec/HROT/blob/coordinator/docs/blueprints/Blueprint_Issues_Tracker.md), [BACKLOG\_Fun\_Ideas.md](https://github.com/pjanec/HROT/blob/coordinator/docs/designs/fun/BACKLOG_Fun_Ideas.md) F-11). It is to be the bird flight model scaled up (banked turns), with wings and a rider seat. Its fire breath is the shared flame of [DESIGN\_Flame\_Weapons.md](https://github.com/pjanec/HROT/blob/coordinator/docs/designs/weapons/DESIGN_Flame_Weapons.md), so the breath will arrive as a `FlameJet` on the dragon's mount, and the rider as on a horse. Unreal can prepare for it as an animal carrier (`DomainAnimal`, flying) with a rider seat; its wingbeat and banking come from its motion, like the birds.

## Motorcycles: the lean and the riders

The motorcycle's lean into a turn is a drawing rule in HROT, not simulation state: the simulation drives the bike upright and the 3-D map tilts the bike and its riders. **DPS sends the lean ready to use:** `EntityState.Bank` (DD-018 §3.5b, built) is the roll about the body's forward axis in radians, positive meaning the top leans right (a left turn is negative). The gateway computes it with HROT's rule; it is 0 for everything but two-wheelers. Unreal applies it about the ground line under the bike and seats the riders in the leaned body. `Orientation` stays the simulation's own pose. The rule below explains what the number means. The design is [DESIGN\_Motorcycle.md](https://github.com/pjanec/HROT/blob/coordinator/docs/designs/vehicle-tactics/DESIGN_Motorcycle.md) (MC-1, the firing seat PF-1 to PF-4, the player PL-1 to PL-3); the drawing is [DESIGN\_Map\_3D\_Mode.md §6al.2](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Map_3D_Mode.md), with the mockup [motorcycle-visuals.svg](https://github.com/pjanec/HROT/blob/coordinator/docs/designs/vehicle-tactics/motorcycle-visuals.svg).

**The lean** (`MotionRules.BankTarget` and `EaseBank` in [MotionRules.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Vehicles/MotionRules.cs), the shared rule the map's `MotionTracker` calls; the gateway calls it for `EntityState.Bank`):

- **Target angle** = atan(v × ω / g): v is the forward speed, ω the yaw rate, g = 9.81 m/s². It is clamped to ±35° (`MaxBank` 0.61 rad). A left turn tilts the top to the left. This is the lean a real rider needs to balance the turn.
- **Easing:** the drawn lean approaches the target with a 0.15 s time constant (`BankSeconds`). HROT measures v and ω from the bike's drawn movement between frames. A parked bike, or one seen for the first time, stands upright.
- **Pivot:** the body rotates about its own ground line, the line where the wheels touch the ground, so the wheels stay on the road ([EntityBodyLayer3D.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/EntityBodyLayer3D.cs), around line 386; drawing, stays in the map).
- **In Unreal:** nothing to compute. Read `Bank`, and interpolate or extrapolate it like the other state values.
- **Wheels** roll by distance over their radius (0.31 m).

**The riders lean with the bike.** Each seated rider is placed in his seat, and the seat frame is tilted by the bike's current lean (`BankOf(holder)`, around line 1289 of the same file). So when the bike leans, both men lean with it about the same ground line.

**The riders' poses** ([BlockFigureRiding.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/BlockFigureRiding.cs)): both sit astride (seat pose `Ride`), with feet on the pegs. Seat 0 (hips 0.1 m ahead of the bike center, 0.75 m up) is the driver. Seat 1 (0.45 m behind, 0.85 m up) is the pillion.

- **The driver** keeps his hands on the bars with his upper body leaning forward about 17° (`DriverLean` 0.30 rad). He never aims: his seat may shoot only when the bike is stopped (PF-4).
- **The pillion** holds the driver's waist and leans about 7° (`PillionLean` 0.12 rad), rifle slung across the back.
- **A pillion firing** (his posture Aimed, on a seat with a traverse arc) is drawn shouldered and level (`RidingAiming`). His upper body turns to the aim point in the seat's frame, up to 160° (`MaxSeatTwist`, `AimInSeat`), eased like a standing man's (0.12 s). His head turns to where he looks.
- **The seat's firing arc** comes from the bike's type data (`TraverseMinDeg` / `TraverseMaxDeg` on the seat, read counter-clockwise from min to max). For a player in that seat, HROT shows a "outside the seat's arc" cue before the shot (`PossessionStation.OutsideSeatArc`).

**For the player.** A player can drive the bike (the use verb Drive) or ride pillion and shoot (Board). The camera is the vehicle chase camera hung from his seat eye, as described in the camera section.

## Boarding, riding, carrying the wounded

In HROT one mechanism covers all of these: a person is held by a holder. The holder can be a vehicle or an animal with seats, a man carrying or dragging a casualty, two men with a stretcher, or a weapon system's gunner seat. HROT's map has one classifier answering "how is this man held" (`Carriage.Of` in [Carriage.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/People/Carriage.cs), in the shared `Hrot.Look`; the gateway calls it and sends the result as `PersonState.CarryRole`, `CarryOther` and `Partner`) and draws each answer as a pose plus a placement. The design is [DESIGN\_Map\_3D\_Mode.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Map_3D_Mode.md): §6m covers carried, dragged, wounded, seated and the jump down; §6t the stretcher and two-man weapons; §6n weapon systems; §6al.2 the motorcycle. The seats are type data (`SeatDto` in [CarrierDefDto.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Tkb/Domain/CarrierDefDto.cs): role, exposed, place X Y Z, facing, pose, firing arc), which Unreal reads from `TkbType`.

**Boarding and leaving a vehicle:**

- **The action.** Boarding is a use action. `PersonState.Action` is Embark or Disembark, `ActionState` Running then Success or Failure, with the vehicle as `ActionTargetId`. HROT states no duration on purpose, so Unreal animates climbing in or out for as long as it is Running.
- **Enclosed or exposed.** Seated, `PersonState` gives the vehicle (`SeatVehicleId`) and the seat role. In an **enclosed** seat (inside an APC or a car) the man is not drawn at all. In an **exposed** seat (an open helicopter door, a pickup bed, a pintle gun, a motorcycle, a horse) he is drawn at the seat's place, facing its way, in the seat's pose:
  - `Sit`: on a bench.
  - `SitLegsOut`: on a sill, legs hanging out.
  - `Stand`: at a weapon.
  - `Kneel`: kneeling at a weapon.
  - `Ride`: astride.
- **Getting out.** HROT puts the man in a column beside the vehicle. If his hips were more than 0.5 m above where he now stands, the map draws a **jump down**: free fall from the seat height (at least 0.3 s), then a crouched landing for 0.3 s (`DismountDrop` in Carriage.cs). A man not seen riding just before simply stands there.
- **What the client sees of a weapon system** (a tripod MG, a mortar): its crew sits in its `Gunner` seat, drawn at the weapon.

**Riding: horse versus motorcycle.** Both use the `Ride` seat pose, but they differ:

|  | Motorcycle | Riding horse |
| --- | --- | --- |
| Carrier | a vehicle | an animal (`DomainAnimal`) with seats |
| Driver / rider | hands on the bars, leaning forward 17°; never aims while moving | legs splayed wider over the barrel; fires from the saddle, upper body twisting to the aim |
| Second seat | pillion: hands on the driver's waist, can fire within its arc (up to 160° twist) | second rider: fires to the sides and rear |
| Body motion | leans into turns (`EntityState.Bank`), riders lean with it | gait from speed (walk, trot, canter, gallop); a jump tucks the legs |
| Extras |  | Bolting: the horse runs off with its rider (HUD cue) |

**Carrying and dragging the wounded.** Carrying is embarking: the carrier is the holder and the casualty its passenger.

- **Carried:** draped across the carrier's shoulders, crosswise, head at the carrier's left (`Carriage.OnShoulders`).
- **Dragged:** on his back, 1 m behind the carrier, with the carrier's arms back holding him.
- **Stretcher:** two carriers, the litter midway between them at hand height, facing the front man's way.
- **Two-man weapon:** carried by a front and a rear man the same way.
- **The casualty's state:** an incapacitated man lies on his back when nobody holds him. A wounded man walking limps at 0.6× speed. Death collapses him over 0.6 s.
- **The actions:** drag, carry and put down are use verbs (`PickUpCasualty` / `PutDownCasualty` in `PersonState.Action`).
- **The pose numbers** are in `BlockFigure` (`Draped`, `Towed`, `CarrierArms`, `Downed`) in [ShapeKit.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/ShapeKit.cs) and the riding files.

**On the wire** (DD-018 §3.5c, built), in `PersonState`:

- **`SeatIndex`** is the seat number on the holder (255 = none). Use it to find the seat's place, facing and firing arc in the holder's `TkbType`.
- **`SeatExposed`** and **`SeatPose`** (Sit, SitLegsOut, Stand, Kneel, Ride) say whether and how to draw him.
- **`CarryRole`** is one of Carried, Dragged, OnStretcher, Carrying, Dragging, StretcherFront, StretcherRear, TeamLoadFront or TeamLoadRear.
- **`CarryOtherId`** depends on who he is:
  - a held man: his holder;
  - a carrier: the man or load he carries;
  - a seated man: the vehicle.
- **`PartnerId`** is the other man of a stretcher or two-man-weapon pair.

**Minefields: nothing special to render the mines.** Mines are not visible objects, and drawing them would show the player what his side does not know. HROT's 3-D map shows minefields only as a gizmo, gated by what the viewer's side knows ([DESIGN\_Minefields.md](https://github.com/pjanec/HROT/blob/coordinator/docs/designs/minefields/DESIGN_Minefields.md) §12): a dashed outline, and for a field known in full, pickets 2.2 m tall with a red point-down sign. That arrives through the DPS gizmo layer for diagnostics. In play:

- **A mine going off** is a normal detonation. An anti-personnel mine makes a medium explosion and a small scorch; an anti-tank mine or a line charge a huge explosion and a large scorch. Both arrive as `Detonation` and `Effect`.
- **The crater** belongs to HROT's ground damage (craters, road blockage), which DPS does not carry yet (see the building damage section).
- **The mine clearer** is an engineer tank whose flail drum spins while it clears (HROT's `FlailActivity`, 4 rev/s after a 1 s spool). DD-018 §3.5c (built) sends a flail flag (FlailActive) in `VehicleLook` so Unreal knows when to spin it.
- **Marking signs** on a marked field are a possible later addition, if a physically marked field should look marked to everyone near it.

## Ambient traffic (SUMO cars and pedestrians)

Unreal receives traffic as world-space movers: positions with altitude, headings, a short smooth track per mover. There is no road network and no SUMO code involved. The design is [DD-018 §3.6b](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-018-Dps-Round-2.md), built; the reference client's MoverPlayback is a working example of the delayed playback.

**Where it comes from.** HROT simulates civilian traffic with SUMO through SumoSharp ([pjanec/SumoSharp](https://github.com/pjanec/SumoSharp), vendored in HROT under [FDP/ExtDeps/SumoSharp](https://github.com/pjanec/HROT/tree/coordinator/FDP/ExtDeps/SumoSharp)). Inside HROT, SUMO's wire is lane-relative. HROT turns it into smooth world poses every frame ([TrafficMirror.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Traffic/TrafficMirror.cs), into the `TrafficFrame` singleton), with SUMO's lane heights or the terrain under a flat net (HROT design [AQ93](https://github.com/pjanec/HROT/blob/coordinator/docs/blueprints/Architect_Question_93_Sumo_Traffic_Node.md) E1, E2, E2b). The gateway reads those finished poses, so all of SUMO's specifics stay inside HROT.

**The topics:**

- **`Mover`** (state, keyed by `MoverId`): kind (car, pedestrian), `TypeId` (a TKB type; describe it from `TkbType`), color (already painted, see below), seed (a pedestrian's clothes and face), length, width. It appears at spawn and is disposed at despawn.
- **`MoverTracks`** (batched, 10 Hz): new knots for many movers in one sample. A knot is a time, a position (x, y, z in the simulation frame), heading, pitch and speed. The gateway adds a knot only when a smooth curve through the knots already sent would miss the real pose by more than 0.10 m or 2°, and at least once a second. A car on a straight road costs about one knot per second.
- **`TrafficLights`** (state, keyed by signal): each signal head's position, heading and state (red, yellow, green, off), already placed in the world.

**How Unreal plays it back:**

- **Delay.** Play traffic about 1.2 s (`PlaybackDelay`) behind the newest knot time.
- **Interpolate.** Use a cubic Hermite curve through the knots, with the speed and heading as tangents. Lane changes, junction turns and stops then come out exactly as HROT reconstructed them, and nothing is predicted.
- **Late data.** Only when a knot is late, extrapolate briefly along the last heading and speed.
- **New movers.** Show a mover from its second knot.
- **Ground contact.** Wheels and feet can follow Unreal's own terrain at the interpolated position. Heights already match the terrain HROT uses.

**Looks:**

- **Cars:** the type comes from the TKB, with HROT's generic traffic car as the default. Colors arrive already decided: HROT's paint rule ([TrafficPaint.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Traffic/TrafficPaint.cs), a street palette chosen by vehicle id) is applied in the gateway, so every viewer agrees.
- **Pedestrians:** each is a civilian type. Clothes and face come from the seed, by the same rule as other people (`PersonLook`). HROT draws traffic with its normal figures and car kits ([TrafficAmbientBodies.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/TrafficAmbientBodies.cs)).

**One copy per car.** HROT makes SUMO cars near its units into local entities, so its sensors and weapons see them. DPS leaves those out of `Entity`, so every SUMO car arrives as a `Mover` only. A pedestrian that HROT turns into a real civilian (panic) disappears as a mover and appears as an `Entity`.

**Roads** are part of the terrain export (the importer's job), not of DPS. HROT draws them from the SUMO network file that ships with its terrain ([TrafficLayer3D.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/TrafficLayer3D.cs)). For background on the reconstruction HROT does before the gateway samples it, see SumoSharp's [IGBRIDGE-DESIGN.md](https://github.com/pjanec/SumoSharp/blob/main/docs/IGBRIDGE-DESIGN.md). Unreal does not need it.

## Building damage: breaches and collapse

Buildings in HROT are destructible, and the damage is replicated state like the glass. The simulation node decides it; every other node, and Unreal, only draws it. HROT's own map never changes its terrain copy for this: it re-cuts the drawing from the damage state. The design and rulings are in [DESIGN\_Terrain\_Import\_UTM.md §8](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Terrain_Import_UTM.md) (D-1 to D-8, then B-1 to B-12 and the as-built §8.3 to §8.7). The drawing is in [DESIGN\_Map\_3D\_Mode.md §6aj](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Map_3D_Mode.md), with the mockup [UX/terrain-destruction-3d.svg](https://github.com/pjanec/HROT/blob/coordinator/docs/UX/terrain-destruction-3d.svg).

**The model** ([BuildingDamage.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Terrain/BuildingDamage.cs)):

- **A breach is an extra opening on a wall panel.** It is a rectangle in the panel's own coordinates: metres along the panel from its A end (from, to) and absolute height (bottom, top), clipped to the panel and its storey. A breach that overlaps another on the same panel is merged into their union.
- **Two kinds.** A **Passage** reaches the floor and is at least 0.8 × 1.8 m; people can walk through it. Anything smaller is a **Hole**: sight and fire pass, people do not.
- **Collapse replaces the building.** The building becomes a rubble mound: the footprint up to ⅓ of the roof height, material `rubble`, not enterable, with no floors. Its doors read as destroyed, and everyone inside or on the roof becomes a casualty. Floors are never removed one by one. A collapsed building gets no further breaches.
- **Glass goes with the wall.** A breach over a window, or a collapse, removes the pane (see the glass section below).
- **Identity.** One record per building, keyed by the building's terrain label (`<terrain>/<building label>`). Panels are numbered within the building, the same numbering HROT saves in scenarios.

**What causes it** (the rule is data in [breaches.json](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Terrain/Data/breaches.json), applied in `AreaEffectSystem` and `BuildingDamageSystem`):

- **Detonations only.** A kinetic round makes no breach. An impact round bursts 0.1 m off the wall it hit, so it counts as a burst against that wall.
- **Scaled distance.** The rule uses Z = d / ∛kg, where d is the burst's distance to the panel and kg is the explosive. The first matching row decides:
  - a heavy bomb close by (40 kg or more, Z up to 1) takes the whole storey panel as a passage;
  - a time-fuzed or placed charge against the wall makes a 1 × 2 m doorway;
  - a shell or missile at the wall makes a hole of about 1.5 × 1.2 m (tank HE) to 1.9 × 1.5 m (155 mm);
  - a burst further off makes a small hole.
- **Scaling.** Sizes scale with ∛(kg / 8) and with the wall material's resistance.
- **Collapse.** 80 kg or more, with the burst inside the footprint or within 0.5 × ∛kg m of it. The Mk-82 bomb collapses the building it hits; a 155 mm shell does not.
- **Vehicles.** Vehicles breaking through walls and fences is designed in [DESIGN\_Vehicle\_Breaching.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Vehicle_Breaching.md) and is being built in stages. A barrier that breaks or is bumped open (\`ContactBreachEvent\`) already makes a dust puff, which arrives as an `Effect` (`EffectRules.ForContactBreach`).

**How HROT draws it** ([DamagedBuildings.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Terrain/DamagedBuildings.cs), [TerrainWorldMesh.Rubble.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Terrain/TerrainWorldMesh.Rubble.cs)):

- **Breached panel.** The panel is re-cut from its own openings plus every breach as a gap, holes and passages alike. HROT reuses the function that built the wall from the template (`TerrainBuildingExpander.ExpandPanelPieces`) and the one gap merge (`TerrainObstacles.WithGaps`). The 2-D plan and the 3-D view ask the same helper, so they cannot disagree.
- **Collapsed building.** All its pieces disappear and a heap is drawn instead: the footprint at the base, shrunk to 55% around its centroid at the top, with sloped sides. It uses the rubble material's color and an earth pattern.
- **Chunks.** Only the touched chunks are rebuilt, off the frame thread. The old mesh is drawn until the new one is ready.
- **For Unreal.** Rebuild the affected wall panel's mesh with the breach rectangles cut out, rather than overlaying decals, so that the holes really see through. Replace a collapsed building's model with a debris mound. Debris and dust at the moment of the breach are local effects; HROT draws none beyond the detonation's own explosion.

**On the wire** (DD-018 §3.6a, built): one `BuildingDamage` instance per damaged building, keyed by its terrain label (`<terrain>/<building label>`). It carries `Collapsed`, up to 64 breaches (panel index within the building, from, to, bottom, top, kind Hole or Passage) and up to 256 hit windows. The instance is disposed when the damage goes, for example on a reload. Since T-074 (DD-030) it also carries **when and from where**: `Breach.MadeAt` and `Breach.Blast`, `WindowDamage.HitAt`, `BuildingDamage.CollapsedAt`, HROT's own sim times (0 = from a loaded save: draw, never animate) - see [NOTE-2026-11-24-Gateway-Damage-Times-And-Craters.md](NOTE-2026-11-24-Gateway-Damage-Times-And-Craters.md); craters travel in the new `GroundDamage` topic, one instance per 64 m cell, keyed `"<cellX>,<cellY>"`. **For the terrain importer:** keep the building labels and the panel order within each building as HROT's terrain files define them, so breaches land on the right wall.

**Other terrain changes** that HROT replicates and DPS does not carry yet: felled and damaged trees ([TreeDamage.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Terrain/TreeDamage.cs)), ground patches such as craters and road blockage ([DESIGN\_Ground\_Patches\_And\_Road\_Blockage.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Ground_Patches_And_Road_Blockage.md)), and door open or closed states. HROT's own outsider view of its wire is [NED\_DDS\_Protocol.md](https://github.com/pjanec/HROT/blob/coordinator/docs/reference/NED_DDS_Protocol.md); it is the place to look for what other state exists.

## Glass windows: cracks and shattering

In HROT, glass is simulation state and not decoration. Every glazed window has a pane, and every hit on it is replicated. Unreal draws that state and adds only local effects. The design, with the owner's rulings, is [DESIGN\_Glass\_Windows.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Glass_Windows.md) (decisions D-1 to D-10, the look in §5 and §5a), with the mockup [UX/glass-windows.svg](https://github.com/pjanec/HROT/blob/coordinator/docs/UX/glass-windows.svg).

**What the simulation decides** ([WindowDamage.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Terrain/WindowDamage.cs)):

- **Which windows have glass.** A window opening is glazed by default; a template may say `glazed: false`. Doors and gaps never have glass. This is static terrain, so Unreal's terrain import must carry the flag.
- **Hits.** A round through an intact pane adds a hit and keeps its crack point (u, v). The third hit breaks the pane.
- **Firing out.** A round fired with the muzzle within 1.5 m of the pane breaks it at once (a man firing out of a window).
- **Explosions.** A blast breaks every intact pane within its pressure radius (6 m × the cube root of the explosive kg, through walls). A fragmenting warhead also breaks panes within 8 m that have a clear line to the burst.
- **Breaches and collapse.** A wall breach over a window, or a collapse, removes the pane with the wall.
- **What glass changes.** Cracks do not change visibility. A broken pane opens the window for sound.
- **The record.** Per building, at most 64 hit windows. Each has its panel (index within the building), its opening on the panel, hits (1-2 cracked, 3 broken) and up to two crack points. u runs along the opening from its start and v up from the sill, both as bytes 0..255. When a pane breaks its cracks are dropped.

The two radii are tunable starting values in [glass.json](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Terrain/Data/glass.json). The simulation applies them, so Unreal only needs them to explain what it sees.

**How HROT draws it** ([GlassLayer3D.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Presentation/Map3D/GlassLayer3D.cs)):

| State | Look |
| --- | --- |
| Intact | A pale-blue translucent sheet, color #9FD7FF at alpha 0.45 (RGBA 159, 215, 255, 115), with a white sheen strip (alpha 46). Both faces. Drawn within 220 m of the eye |
| 1 or 2 hits | A crack star at each kept (u, v), on both faces: a white impact dot, seven radial lines and two dashed rings. The radius is 0.32 × the pane's smaller side, color white at alpha 235 |
| Broken | The open hole, with three shards left in the frame (shard color #9FD7FF at alpha 150) |
| Breaking | Only when this viewer sees a pane turn broken: 26 shards burst outward and down under gravity for 1.4 s, stepped by the sim clock, so a pause freezes them. A pane that was already broken when the viewer joined bursts nothing. A glass sound plays: a crack, then tinkling, audible to 150 m |
| Under a breach or in a collapsed building | Gone (`GlassLayer3D.LookOf`: collapsed means gone, a breach over the pane's centre means gone) |

The rendering setup is a separate translucent pass after the opaque world: depth test on, so a wall in front hides the glass, and depth write off, so the room and people behind show through. The picture to compare against is HROT's rail `GlassFrameRail` (intact and cracked, broken, the burst).

**On the wire** (built): the windows travel in the same `BuildingDamage` topic as the breaches, per building. Each entry has the panel index within the building, the opening index on the panel, hits, and the two crack points as bytes 0..255. Unreal must draw breaches too, or a shot wall stays whole. **For the terrain importer:** keep HROT's building labels, the order of panels within a building, the order of openings on a panel, and each window's `glazed` flag. The wire refers to windows and breaches by these.

## Type data from HROT's TKB

Unreal needs facts about each entity type that the per-entity topics do not repeat: a munition's look, a weapon's category, a vehicle's openings, lights and seats (place, facing, pose, firing arc), a person's body geometry and outfit kinds, effect visuals, the display name. These live in HROT's TKB, which a scenario can replace, so they come from the running simulation over DPS ([DD-018 §3.2](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-018-Dps-Round-2.md), built).

**`TkbType`** is a state topic, one instance per type, keyed by `TypeId` (64-bit, as are `Entity.TypeId` and the munition ids). It is sent once per type when the type is first referenced: by an entity, a fire, a round, a loaded weapon, a mover. Its fields:

- `Name` and `CategoryPath`.
- `DisType`, as dotted text such as `1.1.225.1.0.0.0`.
- `Schema` = `hrot/1`.
- `ContentHash`, the FNV-1a 64 of the JSON, for caching across runs.
- `Json`, an unbounded string holding HROT's own serialization of the type: `{tkbType, name, categoryPath, disType, descriptors: [{type, partId, data}]}`. This is the shape of HROT's debug API `GET /tkb/types/{id}`.

**What the JSON holds.** Only presentation descriptors are included; the allow-list is `TkbPresentation.Descriptors` in the gateway's `TkbGather.cs`. AI, injury, sensor and signature tables are left out. The schema of each descriptor is HROT's DTO source in [Tkb/Domain](https://github.com/pjanec/HROT/tree/coordinator/FDP/Toolkits/Fdp.Toolkits/Tkb/Domain): for example [MunitionVisualDto.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Tkb/Domain/MunitionVisualDto.cs), the seats in [CarrierDefDto.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Tkb/Domain/CarrierDefDto.cs), `VehicleOpeningsDto`, `VehicleLightsDto`, `WeaponSuiteDto`, `BodyGeometryDto`, `VisualDefinitionDto`. Read only the keys you use; HROT adds fields over time. A descriptor that fails to serialize appears with a `serializationError` instead of `data`.

**Typed alongside.** A munition's resolved look is also sent as the typed `MunitionLook` topic. It includes HROT's built-in looks, which no TKB record carries.

To browse what a type holds while developing, use a running HROT's `GET /tkb/types` and `GET /tkb/types/{tkbType}`.

## Rate classes and intent (DD-020)

Every topic belongs to one rate class, stated on its C# type by the `[DpsRate]` attribute (a test checks that every topic has one). A field goes into a topic of its class, so a topic never carries a slowly changing value next to a fast one.

| Class | Changes | Topics | What a client does |
| --- | --- | --- | --- |
| Fixed | once per instance or type | `Entity`, `Appearance`, `TkbType`, `MunitionLook`, `Mover` | Cache; never expect a re-send unless the thing itself changed |
| Discrete | on events | `Session`, `EntityStatus`, `PersonState`, `VehicleLook`, `Animal`, `Round`, `Status`, `Exercise`, `BuildingDamage`, `GroundDamage`, `FireSpots`, `GroundMarks`, `OwnUnit`, `PlayerSlot`, `TrafficLights`, `View`, `JoinRequest`, `Leave`, `GizmoView` | No continuous field: a value that moves over time is a start time + target + duration; animate it yourself |
| Continuous | by threshold plus a heartbeat | `EntityState`, `Articulation`, `PersonAim`, `MoverTracks`, `FlameJet`, `ControlFrame`, `GizmoFrame` | Small samples; extrapolate between them |
| Event | as they happen | `Fire`, `Detonation`, `Effect`, `Impact`, `ManDowned`, `PlayerMessage` | A reader keeps up to 1024 samples |

**Intent instead of the moving result.** Aim and look are in `PersonAim` (see [People aiming](#people-aiming-the-upper-body-twist)): a target entity or a point, the chest rule stated there. A stance change is three fields of `PersonState`: `StanceChangedAt` (the simulation time HROT's muscle began the blend, `StanceStatus.TransitionStartedAt`), `StanceSeconds` (its duration, `StanceStatus.TransitionSeconds`: HROT's one rule `StanceTiming.Effective`, the order's `BlendTime` when set, else 0.35 s between standing and crouched and 0.8 s into or out of prone) and `StanceFrom` (the stance the blend leaves, `StanceStatus.FromStance`, equal to `TargetStance` when no blend is stated). Progress = `clamp((now - StanceChangedAt) / StanceSeconds, 0, 1)`, blending from `StanceFrom` toward `TargetStance`: this is HROT's own [`StanceTiming.ProgressAt`](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Tkb/Domain/StanceComponents.cs), so a man first seen mid-blend, a late joiner and a replay seek all show the right pose. `StanceChangedAt` 0 and `StanceSeconds` 0 mean no blend is stated (a body with no muscle, or none run yet): use your own clip length. `StanceProgress` is superseded: it is 0 while HROT's blend runs and 1 when settled, never streamed in between. Spooling rotors and timed openings are in "Engine level" below. A value that is not a simple timed motion would stay continuous; none is left today.

## Producer vocabulary and extensions (DD-020)

The wire is standalone, but part of its vocabulary comes from the simulation that produces it. `Session.Vocabulary` names it as `name/version`; this gateway writes `hrot/1`. `Session.TerrainFormat` names the terrain export that `BuildingDamage.Building` keys refer to: `hrot-terrain/1`. A client that does not know a vocabulary treats every name below as opaque text.

| Where the names are used | The `hrot/1` names come from |
| --- | --- |
| `Appearance` slot and garment kinds, `Camouflage`, `Hair`, beard in `FaceTraits` | HROT's outfit enumerations in [Outfits.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/People/Outfits.cs) and [Wardrobe.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/People/Wardrobe.cs) |
| `Status.Kind` and `Group` | The kind and group names HROT's scenario rules give an objective, measure or trigger (its objective status) |
| `AmmoEntry.Weapon`, `AmmoType` | The HUD names RIFLE, MG, AUTOCANNON, CANNON, MISSILE, MORTAR, GRENADE, BOMB, ROCKETS, HOWITZER, SAM, WEAPON, HANDS (`Hrot.Look.Hud.HudWords.KindOf`; the gateway calls it) and the round's TKB name |
| `Effect.EffectType`, `Entity.TypeId`, `Round.MunitionTypeId` | HROT's type database (TKB), by id; `TkbType` carries the records |
| `Impact.Material` | the names of HROT's `ImpactMaterial` class (Asphalt, Concrete, Brick, Metal, Glass, Wood, Soil, Sand, Vegetation, Water, Snow; empty = Unknown), the terrain material folded through its thermal class |
| `GizmoView` layers and families, `GizmoFrame.Layer` | HROT's gizmo projectors (see Gizmos) |
| `ControlFrame.Verb`, `OwnUnit.Verb`, `ManDowned.Part`, `Gait`, `StanceRequest`, `ArticulationKind` | Contract enumerations (neutral); the gateway maps HROT's `UseVerbKind` and its person parts (`PersonPart`: Head, Torso, UpperArmL, ForearmL, UpperArmR, ForearmR, ThighL, ShinL, ThighR, ShinR) by name, an unknown name is the enumeration's value 0 |

**The terrain a client must have.** `Session.Terrain` is the name of the terrain resident in the simulation, `Session.TerrainBaseHash` the content hash of the base terrain and `Session.TerrainOverlayHash` the content hash of the overlay merged into it (all three empty = the simulation states none). A client that loads terrain bundles picks the bundle whose name and hashes match; when it has none, it reports `ClientWorld` state Failed with a reason that names the terrain and the hashes it lacks. `Session` is sent again whenever any of them changes, and with a new `WorldEpoch` at a world boundary.

**Deployment offers, roles, kits, areas and respawn.** Where the simulation serves HROT's deploy flow (the editor; a node without a deploy server keeps the placed-unit join below) `PlayerSlot.Offers` lists three kinds, each with `Id`, `Name`, `Side` (DIS force: 1 friendly, 2 opposing, 3 neutral), `Free`, `Count` and `Max`:

| `Offer.Kind` | What it is | How to take it |
| --- | --- | --- |
| `OfferKindUnit` | a placed `Playable` unit; `Free` = nobody holds it | `JoinRequest` Kind Unit, `UnitId` = `Id` |
| `OfferKindRole` | a role: a new man is spawned for you; `Count` men alive of at most `Max` (0 = unlimited); `Free` is false while the role is full; `Kits` = the kit names, index 0 is the role's own, up to 8 | `JoinRequest` Kind Role, `UnitId` = the offer's `Id` (or `UnitId` 0 and `Role` = its name), `Side`, `Kit` = the index into `Kits`, `AreaId` |
| `OfferKindArea` | a spawn area a role's man may start in; `Outline` = its corners in world metres (the frame of `EntityState.Position`, up to 32 points, more are thinned evenly), the area's own height for every point | not joined: put its `Id` in a role join's `AreaId`; 0 = any area of the side |

`JoinRequest.Side` is the DIS force the player wants (0 = the offer's own side). `PlayerSlot.Side` is the side you play once you hold a unit. A role's man is a NEW entity: `PlayerSlot.UnitId` is his id (not the role's) from TakingControl on. A refused join (a unit another seat holds: `HeldByAnotherStation (held by station N)`; a full role: `RoleFull`; a wrong side or area: `WrongSide`, `NoSuchArea`; a role with no free point: `no free point`) takes the slot Requesting then back to Choosing with HROT's own text in `PlayerSlot.Reason` (cut to 64 characters); a successful join clears it. The `Reason` is the last refusal only: a timeout is HROT's text too (`the server did not answer`, `taking control timed out`). **Death and respawn:** when your man dies the slot is Dead with `UnitId` = the dead man and `RespawnAtSim` = the simulation time it returns to Choosing (0 = never: you observe for good); then you choose again, and a role may be taken again. `Leave` (or a lost client) frees the seat, the server's slot and every unit the seat held. **A paused simulation:** a join made while time is paused waits (Requesting or TakingControl, no refusal) and completes when time runs; HROT's possession needs running time. The seat's other seats and a world load: a load returns every seat to Waiting and it chooses again in the new world.

Today's placed-unit join (no deploy server on the node): only `OfferKindUnit` offers, `Kits` and `Outline` empty.

**Producer-specific extensions, optional for a client:** `TkbType` (its `Json` is tagged by `Schema`, `hrot/1`), `BuildingDamage` (keyed by the HROT terrain export, `Session.TerrainFormat`) and `GroundDamage` (craters per ground cell). A client that renders another terrain, or does not need the type data, ignores both topics.

**The old fields stay.** Where a raw number became an enumeration (`ControlFrame.Gait`, `Stance`, `UseVerb`, `OwnUnit.UseVerb`, `ManDowned.BodyPart`, `Articulation.Kind`), the new field is appended and the old one stays equal to it and is superseded. On the way in, a new enumeration that is set wins and a new enumeration at 0 falls back to the old byte, so a client may fill either or both.

## Conventions a client must know

**Articulation (turrets).** `Articulation` is keyed by `(EntityId, PartId)`; `PartId` is an opaque key (do not decode it). What the part is comes from `PartKind` (`ArticulationKindTurret`; Unknown for a kind the contract does not name) and `Index` (the ordinal among parts of that kind, from 0). The old string `Kind` = "Turret" stays equal. The gateway sends turrets only; HROT's `PartId` is 2000 + the turret index and `Index` is that turret index; index 0 is the first turret of the type's turret suite (HROT's `TurretParts.FirstPartId`, [TurretParts.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Combat/TurretParts.cs)). `Azimuth` is the traverse in radians, counter-clockwise from the hull's forward, so it is relative to the hull. `Elevation` is the gun elevation in radians, up from the turret plane, so it is relative to the turret; each turret has its own elevation (one per turret). `Extension` and `Rate` are unused (0). A turret is sent when it moves by more than 0.5 degrees and at least once a second ([TurretPose.cs](https://github.com/pjanec/HROT/blob/coordinator/FDP/Toolkits/Fdp.Toolkits/Combat/Components/TurretPose.cs); the gateway's side is `EcsHrotAdapter.GatherTurrets`). A turret that is gone is disposed with its entity.

**Angle conventions.**

| Where | Unit | Zero and direction |
|---|---|---|
| `EntityState.Orientation` (quaternion), `Bank` | radians | body forward = +x (east); `Bank` is the right-hand roll about forward (top toward the right is positive; a left turn is negative) |
| Yaw, heading, `Articulation.Azimuth`, `PersonState.HeadYaw` | radians | 0 = +x (east), counter-clockwise seen from above |
| `Articulation.Elevation`, pitch | radians | positive = up |
| `OwnUnit.RadarBearingDeg`, `OwnUnit.MissileBearingDeg` and a compass drawn from them | degrees | 0 = north, clockwise (the only compass-style angles in the contract) |
| `MoverKnot.HeadingCentiDeg` | hundredths of a degree, -18000 .. 18000 | 0 = +x (east), counter-clockwise |
| `MoverKnot.PitchCentiDeg` | hundredths of a degree | positive = nose up |
| `TrafficLights.HeadingDeg` | degrees | the lane's direction of travel at the stop line (0 = +x, counter-clockwise; not a compass bearing) |

Convert once at the boundary: a bearing of 90 degrees (east) is a heading of 0 radians.

**Seats.** Unreal keeps its own seat placements, keyed by `(TypeId, SeatIndex)`. `PersonState.SeatIndex` is the index into the type's seat list in the TKB (`CarrierDefDto.Seats` in the `TkbType` JSON; 255 = no seat). It is stable while that type's `TkbType.ContentHash` is unchanged; when the hash changes, read the seat list again.

**Engine level.** `VehicleLook.RotorSpeed` is HROT's engine level, 0 to 1, for every vehicle that has a power plant, not only aircraft: HROT's `VehicleLook.RotorSpeed` follows the engine's heat source (running = 1, idle = a low level, off or dead = 0, spin-up and spin-down by the type's seconds). The gateway fills it for every vehicle it publishes; a type without a power plant reads 0. Use it for the engine sound and the exhaust as well as for rotors. **The spool is data, not a stream** (DD-020): `RotorTarget` is the level it moves to, `RotorFrom` the level when it began, `RotorChangedAt` the simulation time it began (0 = held from the session's start: already at the target) and `RotorSpoolSeconds` the seconds for a full scale (0 to 1 or 1 to 0; 0 = instant). The level at time t is `RotorFrom` moved toward `RotorTarget` by `(t - RotorChangedAt) / RotorSpoolSeconds`, not past the target (HROT's `VehicleLook.RotorSpeed`). `RotorSpeed` is the level at the sample only. A new `VehicleLook` arrives when a spool starts, not while it runs. **Openings** are the same kind of data: `OpeningLook.TargetProgress` (1 open, 0 closed), `ChangedAt` and `Seconds` (the whole motion; 0 = instant); progress = `lerp(1 - target, target, clamp((t - ChangedAt) / Seconds, 0, 1))` (HROT's `VehicleOpenings.Progress`); `Progress` is the value at the sample.

**The terrain importer.** Building labels, panel order, opening order and the glazed flag exist only in HROT's terrain and building templates, not in footprints extruded from a map. The importer must build from those templates so that `BuildingDamage` (keyed by the building label, with panel and opening indices) lands on the right wall and window; see HROT's [DESIGN\_Terrain\_Import\_UTM.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Terrain_Import_UTM.md) and [DESIGN\_Terrain\_Authoring.md](https://github.com/pjanec/HROT/blob/coordinator/docs/DESIGN_Terrain_Authoring.md).

**The suggested view (`View`, [DD-019 §3.1](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-019-Dps-Round-3.md)).** One keyless state instance: what HROT suggests the camera shows (the film director's shot, a follow camera, the operator's camera): `Source`, `Mode` (Free, Orbit, Eyes, Shoulder, Chase, Fixed), `TargetId`, `Distance` and `Side` (the over-the-shoulder continuum), the pose HROT computed (`Position`, `Orientation`, `FovDeg`), `Shot` and `SimTime`. The client owns its camera: follow it, follow only its binding with your own controller, or ignore it. `CutSeq` grows at every hard cut (a new source, mode, target or shot, or a cut HROT counts): blend between samples of one `CutSeq` and jump on a new one. `Source` None means HROT suggests nothing now. The source is HROT's own `CameraSuggestion` ([CameraSuggestion.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Core/Components/Map/CameraSuggestion.cs), read through [CameraAdvice.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/CameraAdvice.cs)): a node-local singleton that HROT's map writes every frame while its 3-D view is on, from the film director, the follow camera or the operator's camera. A node with no map, or one showing 2-D, has none: then there is no `View` instance, and you use your own default camera. HROT's `FixedKey` mode is `Fixed`; the director names its follow unit by network id, the other drivers by entity (both arrive as `TargetId`). `CutSeq` also grows when HROT's own cut counter does.

**Impacts (`Impact`, [DD-019 §3.2](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-019-Dps-Round-3.md)).** An event for each surface of the static world a round enters, leaves or stops on (`Kind` Entered, Left, Stopped), with `Position`, `Normal` (all 0 = not stated), `MunitionTypeId` and `ShooterId`. `Surface` is HROT's classification for the round's effect (Ground within 0.25 m of the terrain, else Building); Unknown when HROT draws no effect for it. `Material` is the name of HROT's ImpactMaterial the effect's cause carries (the terrain material folded through its thermal class: Asphalt, Concrete, Brick, Metal, Glass, Wood, Soil, Sand, Vegetation, Water, Snow; a ground stop is Soil; empty = Unknown). A strike on a unit is still a `Detonation` with a `TargetId`. The puff and the mark arrive as `Effect` as before. Since T-051 an `Impact` is made from the effect entity HROT's spawner makes of the round's impact (`EffectCause.Impact` = kind + 1, `Shooter`), once per impact (HROT stamps one effect of an impact, T-057), live AND in a replay or after a seek (a restored effect entity gives the same `Impact`; entering a replay or seeking sends nothing for what was already there). A detonation's effect makes no `Impact`. `Normal` is the face normal HROT stamps on the effect (all 0 = not stated). `Surface` is what the cause says.

**Missile motor (`Round.LaunchedAt`, `Round.BurnSeconds`).** `LaunchedAt` is the simulation time the round was fired: HROT stamps it on the round's marker when it is made (`LightweightRound.LaunchedAt`) and sends it with the round, so a replica and a recording carry it too. `BurnSeconds` is the motor's burn from the TKB (HROT's `RoundLook.BurnOf`, 0 = no motor data). The trail is full while `now - LaunchedAt < BurnSeconds`, then fades over HROT's linger time (0.8 s, `RoundLook.TrailLingerSeconds`, `RoundLook.TrailFactor` in [RoundLook.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Effects/RoundLook.cs)); no extra sample is sent when the motor stops. A round whose launch time HROT does not know (0: a recording made before the field existed) gets the time the gateway first saw it.

**Airborne animals (`Animal.Airborne`).** HROT's own reader, `Hrot.Look.Airborne.Of` ([Airborne.cs](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look/Airborne.cs)): a flyer's flight state, a flock whose mode is Flying, or a lone bird with its in-flight flag. The gateway calls it for every animal. Do not infer it from height.

**Event history.** `Fire`, `Detonation`, `Effect`, `Impact`, `ManDowned` and `PlayerMessage` are KEEP\_LAST 1024 (a minigun fires 50 to 100 rounds a second, one `Fire` per shot). Match that depth on the reader, or a burst between two reads loses samples.

**Several channels (a cluster of render nodes).** Every channel subscribes as a view-only client (same domain and partition, no player writers); a station has one player, so at most one client sends `JoinRequest`. Keeping the cameras of the channels in step is the image generator's own cluster rendering (for example nDisplay), not DPS: DPS gives every channel the same world, and the suggested `View` is advice for the one camera that the cluster renders.

**The committed IDL.** `docs/clients/idl/DpsTopics.idl` and `docs/clients/idl/DpsTypes.idl` are committed, with the protocol version in the first line, so a client needs no .NET build. A test fails when they differ from what the build generates; after a contract change run `bash scripts/refresh-dps-idl.sh`.

## Film rendering: the render client (DD-022 part D)

A render client is its own process (view-only, no player writers) on the station's **render seat** partition (`dps.renderSeat`, default 1: with one seat the base partition). The station films through it when the HROT director renders a reel and the client is announced; otherwise HROT's own 3-D map takes the pictures. Design: [DD-022](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-022-Dps-Director-Rendering.md) 3e, 3f, 3h and "Part D as built".

| Topic | Who writes | What you do |
|---|---|---|
| `RenderClient` | you, repeatedly (volatile; the station needs your liveliness) | `Ready` true when you can take a job, your limits (`MaxWidth`, `MaxHeight`, `MaxSupersample`, `Formats` bit 1 = PNG, `AudioFormats` bits 1 = WAV PCM16, 2 = float32, `MaxSampleRate`). A job over a limit fails before frame 0 naming it. When the job's `RenderJob` Starting arrives and you are warmed up for it (shaders, streaming; the station waits 180 s), write `ReadyJobId` = its `JobId` |
| `RenderJob` | station (state per `JobId`) | Starting, Running, then Done / Failed / Cancelled. `Dir` is relative to the render root (the folder you and the gateway both reach; refuse `..` and absolute paths). `Audio` true = build the soundtrack `<JobId>.wav` (`SampleRate`, `Channels`) on simulation time |
| `FrameRequest` | station (event, in `FrameIndex` order, none skipped) | One film frame. `Kind` **Scene**: render at `SimTime` with `Position`, `Orientation` (the body convention of `EntityState.Orientation`: +x forward, +z up, so the quaternion takes +x to the camera's forward and +z to its up, **bank included**), the vertical `FovDeg`, `Width` x `Height` x `Supersample`, burn the overlay (`Subtitle`, `Graphics` in world points, colour `ColourRgb` 0xRRGGBB and `Alpha`), the `LayersOn` / `LayersOff` of the frame, and **`Fade`** (0..1: the dip to black over the picture, applied last) into `File`. `Kind` **Repeat** (slow motion past one frame per step): write no image (HROT copies the previous scene's file), advance your soundtrack by one film frame. `Kind` **Silence** (a title card, HROT draws it): no image, one film frame of silence in the track |
| `ClockStep` | station | In a job every step the frame owns has `AckRequired` true, the last with **`Samples`**: the instances and events the station wrote in that gather (a keyed topic counts an instance once however often it was written, a keyless topic once, an event per sample). Count the world topics since your last presentation, present when your count reaches `Samples`, then render the frame. Nothing counted is written between a `ClockStep` and your `FrameDone` (the station holds it for the next frame). A frame that owns no step (the first frame of a job, a Repeat, a Silence) has no new `ClockStep` and names the last one in `StepSeq` |
| `FrameDone` | you | Written (the file is complete) or Failed with a `Reason` (the job fails with it). 30 s without one fails the job |
| `RenderJobResult` | you | After `RenderJob` Done, when `Audio` was set: `TrackFile` (relative to the render root), `TrackSamples`; within 30 s |

## Reference client and testing against HROT

The reference client [SimNetGateway.DpsClient](https://github.com/pjanec/SimNetGateway/tree/main/src/Hosts/SimNetGateway.DpsClient) is a small working client: [DpsWorld.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Hosts/SimNetGateway.DpsClient/DpsWorld.cs) for subscriptions, caches, dead reckoning and player writers, and `DpsClientWindow.cs` for drawing and input. Read it before writing the Unreal plugin. The join options are `--join`, `--role`, `--side`, `--kit`, `--area`, `--observe`, `--retry` and `--list-offers` (see "Deployment offers"). Its headless mode makes a good scripted counterpart. Usage is in [DD-017 §10 Reference client](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#reference-client).

```
dotnet run --project src/Hosts/SimNetGateway.DpsClient -- --domain 0 --partition station-1
```

To run a live station:

1. Build HROT, then package the gateway with [scripts/package-extension.sh](https://github.com/pjanec/SimNetGateway/blob/main/scripts/package-extension.sh).
2. Put the packaged folder in `extensions/simnetgateway` next to HROT's binaries. HROT's node-extension loader scans that folder.
3. Give the gateway a `simnetgateway.json` (path in `SIMNETGATEWAY_CONFIG`, else the working directory) with only a `dps` section: domain, partition and lease. The keys are in [DpsSettings.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Hosts/SimNetGateway.Plugin.DdsPlayer/DpsSettings.cs).
4. Start the HROT editor with a scenario that has units marked `Playable`, and let time run, since possession needs running time.
5. Point the Unreal client at the same domain and partition.

HROT's debug HTTP API (`GET /entities/{id}/brain`, `POST /entities/{id}/control`, `/view/follow`) helps check from outside what the station did. The live check in [DD-017 §10](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#live-check-task-8---2026-11-19-hrot-editor-hidden-window-only-the-dps-section-configured) lists the expected results: late join within 1 s, join to Playing in under 1 s, one press = one round, Leave and a lost client both release the unit.

## Runbook: build, change, deploy, run

This is how to run HROT with the gateway on one machine (Windows or Linux, .NET 8). It covers changing the gateway or the contract, rebuilding, and running HROT, the gateway and a client together. Paths assume `<root>/hrot` and `<root>/SimNetGateway` side by side.

**1. Get the sources**

```
git clone -b coordinator https://github.com/pjanec/HROT.git hrot
git clone https://github.com/pjanec/SimNetGateway.git
```

The gateway is tested against a specific HROT commit (currently `c5f8c1971`), recorded in [DD-017 §10](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md#spike-task-1-dds-features-and-hrot-entry-points). If the newest `coordinator` does not build with the gateway, check out that commit.

**2. Build HROT** (Release; the editor and every node run from the cluster runner):

```
dotnet build hrot/Hrot/Runner/Hrot.ClusterRunner/Hrot.ClusterRunner.csproj -c Release
```

The binaries land in `hrot/Hrot/Runner/Hrot.ClusterRunner/bin/Release/net8.0`. Call this `<bin>`. HROT's own `run_Editor.bat` / `run_Editor.sh` start the Debug build instead; use one configuration consistently.

**3. Build and test the gateway** (it builds against `../hrot`; elsewhere set `HROT_ROOT`):

```
cd SimNetGateway
dotnet build SimNetGateway.sln
dotnet test SimNetGateway.sln
```

Run the whole suite. Do not use `--filter`: the xUnit adapter misreports filtered runs. The IDL for the Unreal plugin is committed in `docs/clients/idl` (`DpsTopics.idl`, `DpsTypes.idl`); a contract change fails the IDL snapshot test until you run `bash scripts/refresh-dps-idl.sh` and commit the copies. Regenerate the C or C++ bindings with `idlc` whenever they change.

**4. Deploy the gateway into HROT** (Git Bash on Windows):

```
bash scripts/package-extension.sh <bin>/extensions/simnetgateway
```

This publishes the host and every protocol plugin into the folder that HROT's node-extension loader scans next to its binaries ([DD-016](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-016-Hrot-Host-And-Side-Plugins.md)). The script refuses a result that contains HROT's, FDP's or CycloneDDS's own assemblies, because the HROT process provides those. Re-run it after every gateway change. HROT does not need rebuilding unless HROT itself changed.

**5. Configure the gateway.** Write `simnetgateway.json` and point `SIMNETGATEWAY_CONFIG` at it, or put it in the working directory. With no file, the gateway does not start. For DPS alone:

```json
{ "dps": { "domain": 0, "partition": "station-1" } }
```

Optional keys are `leaseSeconds` (2), `heartbeatSeconds` (5), `positionThresholdM`, `angleThresholdDeg` and `velocityThreshold` and `seats` (1; with N >= 2 the gateway serves N players and a client uses the partition `<partition>-n`, see [DD-023](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-023-Dps-Multiple-Players.md)), `renderSeat` (1; the seat the render client sits on, 1 .. `seats`) and `renderRoot` (the folder the render client and the gateway both reach; empty = film jobs fail saying so) ([DpsSettings.cs](https://github.com/pjanec/SimNetGateway/blob/main/src/Hosts/SimNetGateway.Plugin.DdsPlayer/DpsSettings.cs)). Unknown keys are errors. A configuration problem prints one line on stderr, and the gateway stays off.

**Pick a DDS domain below 167 on Windows.** DDS uses UDP ports 7400 + 250 × domain and up. Windows reserves or hands out ports above 49152, so a domain of 167 or more fails at random with "failed to create participant". The gateway's tests found this the hard way. Domain 0 is the usual choice; use different partitions, not different domains, to separate stations.

**6. Start the HROT editor with the gateway:**

```
cd <bin>
set SIMNETGATEWAY_CONFIG=<path>\simnetgateway.json
set HROT_DEBUG_API_PORT=8111
dotnet Hrot.ClusterRunner.dll --mode editor
```

- `HROT_DEBUG_API_PORT` turns on HROT's HTTP API, which is useful for checking things from outside. `GET /status` answers `ok:true` once the editor is up.
- `HROT_HIDDEN_WINDOW=1` runs the editor without showing its window, for headless or automated runs.
- On Windows, always start the same binaries from the same path. The firewall asks again for every new path that opens sockets, which DDS does.

**7. Load a scenario and make a unit playable.**

- **Load the player-station showcase** over HTTP: `POST /scenario/load/live` with `{"name": "showcases/player-station", "waitForReady": true}`. It is HROT's test scenario for an external player station (generated by `scripts/gen-showcase-player-station.py`) and **lives on HROT's `ui` branch (commit fe2d6312f) until it is merged into `coordinator`**; check that branch out in `hrot` until then. It runs on the terrain demo-city (SUMO traffic, enterable houses with glazed windows) and loads live in about 6 s. Blue `Rifleman 1` and `Rifleman 2` are Playable (their AI holds a post until a client takes one); there is a Blue AI squad, a Red squad 150 m to the south that engages, an empty HMMWV, a motorcycle and a riding horse with free seats, a flamethrower at a timber shed, a timed 8 kg charge that breaches a timber hut's wall, civilians, a dog, cattle, birds and traffic. The rules give the objective "Hold the house", Blue tickets, a score and a welcome message (Blue only). Within 60 s there are dozens of shots, the breach and men down. **Known HROT limit (CE-1992):** in `--mode all` possession and the scenario's flamethrower do not work; use `--mode editor` as in step 6.
- **Any other scenario:** load it in the editor, or over HTTP with the same call and its name. `GET /scenarios` lists them; terrains are under `hrot/assets/terrain`. To make a unit of another scenario playable, tick Playable on a soldier in the editor's Details and save: a client can join only units marked Playable, and shipped scenarios may have none.
- **Let time run:** possession needs running time. Through the deploy flow (the editor) a join while paused waits until time runs; on a node without a deploy server it times out after 10 s.

**8. Run a client.** Use the same domain and partition. The reference client is a check that the station works before Unreal is involved:

```
dotnet run --project src/Hosts/SimNetGateway.DpsClient -- --domain 0 --partition station-1
```

Press J to join, WASD and the mouse to play, Esc to leave. `--headless --seconds 20 --join` prints a summary line instead of opening a window. The Unreal client joins the same way.

**9. The change loop.**

- **A gateway or contract change:** edit, run step 3 (full build and tests), step 4 (re-package), restart the editor (step 6), then the client.
- **An Unreal-only change** needs none of that. Restart the client only, at any time: the station releases the unit when the client goes and accepts the next join.
- **A HROT change:** step 2, then the rest.

**10. Stop.** Close the editor, or `POST /shutdown` on its HTTP API.

**Contract changes.** The contract types are C# in `src/Protocols/SimNetGateway.Protocol.DdsPlayer`, and the IDL is generated from them. Append fields only at the end of a type, prefix enumerators with their enum's name, and keep strings and sequences bounded. A change to the contract is a change to [DD-017](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-017-Dds-Player-Station.md) and [DD-018](https://github.com/pjanec/SimNetGateway/blob/main/docs/design/DD-018-Dps-Round-2.md) first: propose it to the gateway's owner rather than changing it from the Unreal side. The gateway repository's working rules are in its [CLAUDE.md](https://github.com/pjanec/SimNetGateway/blob/main/CLAUDE.md).

## Known gaps and open points

DD-017 round 1, DD-018 round 2, DD-019 round 3 and DD-020 round 4 are built. The gateway owner hands the contract to Unreal once it settles, and protocol 1.0 stays open to additions until then. These items are not on the wire. Raise them with the gateway owner rather than working around them in the client.

- **Rules that stay in HROT's map.** The look rules moved to [`Hrot.Look`](https://github.com/pjanec/HROT/blob/coordinator/Hrot/Engine/Hrot.Look) (see "How HROT's map derives what it draws"); what is left in `Hrot.Presentation` is drawing (block figures, kits, layers, camera drivers) and the person limb gait and stride constants (`MotionTracker`, `LocomotionBlend`). Port those from the linked files.
- **Terrain changes:** door states (doors are entities without a state today, and most of the entity traffic), felled and damaged trees, ground patches (craters, road blockage), marking signs of a marked minefield.
- **Replay:** `Effect` flows in replay by the same path as live: the gateway rebuilds each recorded effect entity into the effect HROT's rules describe (`EffectRules.SpecOf`: what a hit struck, the damage and the munition come from the entity's own `EffectCause`), sends it once, and sends nothing for what a seek skipped. `Fire` and `Detonation` still come from the recorded muzzle flashes and detonation memory. `Impact` flows in replay by the same path as live (see Impacts): the restored effect entities say kind and shooter. A replay has no `View` (HROT's camera suggestion is node-local and never recorded). The effect path needs the gateway's node to run HROT's effect spawner (every map host does); on a node with none, `Effect` stays silent and the gateway says so once on its error stream.
- **The player station:** cluster player nodes (DD-017 step 2), and the HROT station's extra keys (squad switch, seat switch, head out, lights, command wheel, designation). Roles, kits, areas and respawn work through the deploy flow where the node serves it (the editor); on a node without it clients join placed `Playable` units only; HROT has a test scenario with Playable units (step 7 of the runbook). The reference client (`DpsClient`) joins like a real player of that deployment (T-060): headless `--join` takes the first free placed unit, `--role NAME [--side N] [--kit N] [--area NAME]` joins a role offer (the area offer's `Id` goes in `AreaId`, the kit is an index into the offer's kits), `--observe` observes, `--list-offers` prints every offer once the slot is Choosing, `--retry` asks again after a refusal. A refusal is printed with HROT's reason (`join refused: RoleFull`), a join that cannot even be built (full or unknown role) as `join not sent: ...`, and the status line shows `reason=` and `respawn_at=`.
- **Gizmos** have not been checked against a live HROT yet. Filled gizmo triangles arrive with z = 0 (boxes and text carry the ground height).
- **The dragon** is not built in HROT. It will arrive as an animal carrier with a rider seat and a `FlameJet` breath.
- **The Exercise topic** has no key, so it is not disposed per instance. It ends with its writer at a world boundary.
