# DPS (DDS Player Station) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A public DDS contract (DPS) and a `dps` side plugin on the HROT host that publishes the simulated world to external player stations and lets one station play one HROT soldier, with a reference client, round 1 in the HROT editor.

**Architecture:** C# DDS types (CycloneDDS.NET attributes; the IDL is generated) in a new protocol project. The HROT host gains new pivot data for people (read from HROT components that exist today) and a player-station seam that drives HROT's own player mode (Deploy, possession, control frames, release). The `dps` plugin maps the pivot to DPS topics in one DDS partition per station and maps the inbound player topics to the seam; losing the client releases the unit.

**Tech Stack:** .NET 8, CycloneDDS.NET 1.2.20 (the version HROT pins; HROT provides the native `ddsc` and the managed assembly in-process), xUnit, raylib-cs (reference client window).

**Spec:** `docs/design/DD-017-Dds-Player-Station.md` (approved, R-132). Builds on `docs/design/DD-016-Hrot-Host-And-Side-Plugins.md`.

## Global Constraints

- Public repository: no product names other than HROT and Unreal in code, comments, docs or commit messages; commits as `pjanec@hotmail.com` (the repository's git config), trailers `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_01E3A2bv43zabKsjHXfgqu3i`.
- Layer rules R-049 / R-050 (`tests/SimNetGateway.Architecture.Tests`): protocols reference nothing above them; plugins and hosts are Host-layer; only `SimNetGateway.HrotHost` (and its tests) compile against HROT (`$(HrotRoot)`, pass it WITH a trailing slash: `-p:HrotRoot=D:/Work/HROT-BDC-gw/external/hrot/`).
- CycloneDDS.NET version = `$(CycloneDdsNetVersion)` = 1.2.20; the packaged extension never ships `CycloneDDS*` / `ddsc*` (`scripts/package-extension.sh` fails otherwise): inside HROT the plugin uses HROT's copy.
- DDS types: `[DdsTopic]`, `[DdsQos]`, `[DdsKey]`, `[DdsManaged]` for strings / sequences, extensibility `Appendable` (fields only ever added at the end), bounded strings and sequences; topic names prefixed `DPS_`.
- QoS (DD-017 P-2): state topics Reliable + TransientLocal + KeepLast 1; event topics Reliable + Volatile + KeepLast 16; player topics Reliable + Volatile + KeepLast 1.
- Frames (DD-017 P-3): HROT local metres, x east, y north, z up; quaternion (x, y, z, w); sim time in seconds (double).
- Round 1 = what HROT provides today (DD-017 P-2d); fields HROT does not provide are sent with their Unknown / None value.
- Build serially (`-m:1`; the memory guard kills parallel builds); never touch `D:\Work\HROT`.

## Review Focus

1. A client that joins while the gateway already publishes hundreds of entities must receive every live entity's `Entity`, `EntityState`, `PersonState` once from the writer caches (no gap until the next change). Test: Task 2 `Late_joiner_gets_every_live_instance` (500 instances).
2. A client that crashes while playing: the unit is released exactly once, HROT's AI resumes, the slot returns to Waiting; the same client restarting can join again. Test: Task 6 `Lost_client_releases_once_and_can_rejoin`.
3. A second client writing player topics into an occupied partition: ignored, one warning, the first keeps playing. Test: Task 6 `Second_player_writer_is_ignored_with_one_warning`.
4. A world boundary (scenario reload) while a client is connected: every entity instance is disposed, `Session` changes, the slot returns to Waiting, no stale instance survives. Test: Task 5 `World_boundary_disposes_every_instance`.
5. Replay: state keeps flowing, `JoinRequest` is answered with `PlayerSlot.State = Observing` and no HROT call is made. Test: Task 6 `Join_in_replay_is_refused_as_observing`.

---

### Task 1: Spike - DDS features and HROT player-mode entry points

Answers two questions the later tasks rely on; no product code.

**Files:**
- Create: `tests/SimNetGateway.Protocol.DdsPlayer.Tests/SpikeTests.cs` (kept as regression tests)
- Modify: `docs/design/DD-017-Dds-Player-Station.md` (new section `## 10. As-built`, subsection "Spike")

- [ ] **Step 1:** In a test with two `DdsParticipant`s on one domain, verify on CycloneDDS.NET 1.2.20: (a) a partition can be set on publisher / subscriber QoS and readers in another partition get nothing; (b) a writer's liveliness lease and the reader's liveliness-changed (or publication-matched) status are exposed; (c) disposing an instance reaches a TransientLocal late joiner as not-alive. Tests: `Partition_isolates_two_stations`, `Lost_writer_is_reported`, `Disposed_instance_is_not_delivered_to_late_joiner`.
- [ ] **Step 2:** If (a) is not exposed, the fallback is the topic name `DPS_<partition>_<Topic>`; if (b) is not exposed, the fallback is a heartbeat: the client writes `Heartbeat` every 0.5 s and the gateway treats `dps.leaseSeconds` (default 2) without any player sample as lost. Record which applies in DD-017 section 10.
- [ ] **Step 3a:** Read HROT's gizmo pipeline (`FDP/Toolkits/Fdp.Toolkits/Diagnostics/Gizmos`: `IStatelessGizmo`, `IEntityStatefulGizmo`, `IGlobalStatelessGizmo`, `IDebugDrawBuilder`, `GizmoRegistry`, `GizmoExecutionController`, `IGizmoVisibilityPolicy`, `IGizmoViewFilter`, `IActiveViewProvider`; `docs/DESIGN_Uniform_Gizmo_Membership.md`, `docs/GUIDE_Debug_Gizmos.md`) and record in DD-017 section 10 how a node extension runs the projectors into its own draw builder with its own visibility policy (layers, selected, pinned), without disturbing the editor's own map.
- [ ] **Step 3:** Read HROT (`external/hrot`, read-only) and record in DD-017 section 10 the exact entry points the host will call on the editor node: the Deploy client request for a role or a `Playable` unit (`Hrot/Engine/Hrot.Gameplay/DeployFlowSystem.cs`, `GameplayTopics.cs`), possession (`EntityOrders.Possess`), `OperatorControlFrameEvent` publication (`FDP/Toolkits/Fdp.Toolkits/Behavior/Possession/Possession.cs`), `ReleasePossession`, and where the slot state (`PlayerSlotTopic`, `PossessedBy`) is read. Confirm whether the deploy server runs in the editor (feature `gameplay.deploy-server`).
- [ ] **Step 4:** Run `dotnet test tests/SimNetGateway.Protocol.DdsPlayer.Tests -m:1 --filter Spike`; expected: the tests document the behaviour (pass on the supported path).
- [ ] **Step 5:** Commit `test(dps): spike - partitions, liveliness, dispose; HROT player-mode entry points (DD-017 section 10)`.

### Task 2: DPS types

**Files:**
- Create: `src/Protocols/SimNetGateway.Protocol.DdsPlayer/SimNetGateway.Protocol.DdsPlayer.csproj` (references `CycloneDDS.NET`), `DpsTypes.cs` (enums), `DpsTopics.cs` (topic structs), `DpsProtocol.cs` (`public const int Major = 1, Minor = 0;`, topic names)
- Create: `tests/SimNetGateway.Protocol.DdsPlayer.Tests/DpsTopicTests.cs`
- Modify: `SimNetGateway.sln`, `tests/SimNetGateway.Architecture.Tests` layer table (Protocol layer)

**Interfaces:**
- Produces (namespace `SimNetGateway.Protocol.DdsPlayer`): topic structs `Session`, `Entity`, `EntityState`, `Articulation`, `PersonState`, `Appearance`, `Fire`, `Detonation`, `Effect`, `PlayerSlot`, `OwnUnit`, `JoinRequest`, `ControlFrame`, `Leave`, (`Heartbeat` only if Task 1 chose the fallback), `GizmoView`, `GizmoFrame`; enums `Domain`, `Stance`, `WeaponPosture`, `HeldItem`, `Gesture`, `Injury`, `ActionKind`, `ActionState`, `SeatRole`, `CarryMode`, `SlotState`, `SessionMode` (Live, Paused, Replay), `GizmoShape`. Fields per DD-017 sections 3.3-3.7; `EntityId` is `int`; `Articulation` is keyed by (`EntityId`, `PartId`); `GizmoFrame` by (`Producer` string, `Layer` string); `GizmoFrame.Primitives` is a bounded sequence (4096) of `GizmoPrimitive {Shape, Points (bounded 64), ColourRgba, Width, Text}`.

- [ ] **Step 1: Failing tests** in `DpsTopicTests`: `Every_topic_round_trips` (write and read each topic with non-default values in one participant), `Late_joiner_gets_every_live_instance` (write 500 `EntityState` instances, then create the reader; it reads 500), `Disposed_entity_is_gone_for_a_late_joiner`, `Generated_idl_contains_every_topic` (the build output's generated IDL lists `DPS_Session` ... `DPS_GizmoFrame`).
- [ ] **Step 2:** `dotnet test tests/SimNetGateway.Protocol.DdsPlayer.Tests -m:1` - FAIL (types missing).
- [ ] **Step 3:** Write the types (QoS per Global Constraints; bounded strings: names 64, kinds 32, colours 7; sequences: appearance slots 16, equipment 16, gizmo primitives 4096).
- [ ] **Step 4:** Tests PASS; `dotnet test tests/SimNetGateway.Architecture.Tests -m:1` PASS.
- [ ] **Step 5:** Commit `feat(dps): the DPS topic types (CycloneDDS.NET, generated IDL), DD-017`.

### Task 3: Person detail in the pivot

**Files:**
- Modify: `src/Protocols/SimNetGateway.Protocol.Hrot/HrotMessages.cs` (new message), `src/Hosts/SimNetGateway.HrotHost/EcsHrotAdapter.cs` (gather), `tests/SimNetGateway.HrotHost.Tests/LifeFormGatherTests.cs` (or a new `PersonDetailGatherTests.cs`)

**Interfaces:**
- Produces: `public readonly record struct HrotPersonDetail(int Id, HrotStance TargetStance, float StanceProgress, HrotHeldItem HeldItem, byte ActiveMount, HrotVec3 LookTarget, bool HasLookTarget, float HeadYaw, HrotGesture Gesture, HrotAction Action, int ActionTarget, HrotActionState ActionState, double ActionStartedAt, int SeatVehicle, HrotSeatRole SeatRole, HrotCarryMode Carry) : IHrotToGatewayMessage;` with enums `HrotHeldItem {None, Weapon, Throwable, Phone, Binoculars}`, `HrotGesture` (HROT's `GestureKind` values plus None), `HrotAction {None, Embark, Disembark, OpenDoor, CloseDoor, LockDoor, UnlockDoor, BreachDoor, PickUpCasualty, PutDownCasualty, PickUpRound, Eject}`, `HrotActionState {None, Running, Success, Failure}`, `HrotSeatRole {None, Driver, Gunner, Commander, Passenger, Station}`, `HrotCarryMode {None, Seated, Carried, Dragged, Stretcher}`. Sent for life forms (DIS kind 3) when any field changes; `ActionStartedAt` = the sim time the gateway first saw the action Running.

- [ ] **Step 1: Failing tests** on a staged HROT world (the existing gather-test harness): `Stance_transition_gives_target_and_progress` (StanceIntent Prone, StanceStatus Transitioning 0.5), `Held_item_and_mount_come_from_weapon_posture`, `Look_target_comes_from_look_state`, `Gesture_is_published`, `Interaction_channel_running_open_door_gives_action_with_start_time` (start time = sim time of the first gather that saw it Running; unchanged on the next gathers), `Embarked_soldier_has_vehicle_and_seat_role`, `Vehicle_gets_no_person_detail`, `Unchanged_person_sends_nothing_again`.
- [ ] **Step 2:** `dotnet test tests/SimNetGateway.HrotHost.Tests -m:1 -p:HrotRoot=D:/Work/HROT-BDC-gw/external/hrot/` - FAIL.
- [ ] **Step 3:** Implement the gather in `EcsHrotAdapter` next to `LifeFormOf`, reading `StanceIntent` / `StanceStatus`, `WeaponPosture` (`HeldItem`, `ActiveMount`), `LookState`, `Gesture`, `InteractionChannel` + its status (action ids from HROT's `BehaviorConstants`), `IsEmbarkedTag` / `CarrierSeats` (seat role), carry mode; each component optional (`Ensure<T>` pattern already used in the adapter).
- [ ] **Step 4:** Tests PASS; the existing HrotHost and HrotDis suites still pass (`HrotDis` 801).
- [ ] **Step 5:** Commit `feat(hrot): person detail in the pivot (stance transition, held item, look, gesture, interaction action, seat)`.

### Task 4: Player-station seam

**Files:**
- Create: `src/Gateways/SimNetGateway.Gateway.HrotDis/Plugins/IPlayerStation.cs`
- Modify: `src/Gateways/SimNetGateway.Gateway.HrotDis/Plugins/IGatewayPlugin.cs` (`SideBinding.PlayerStation`, optional constructor argument, default null; `PluginInterface.Version` stays 1: additive)
- Create: `src/Hosts/SimNetGateway.HrotHost/HrotPlayerStation.cs`; Modify: `GatewayRuntime.cs` / `PluginSet.cs` (pass it in the binding)
- Test: `tests/SimNetGateway.HrotHost.Tests/PlayerStationTests.cs`

**Interfaces:**
- Produces:
  - `public interface IPlayerStation { PlayerSlotSnapshot Slot { get; } IReadOnlyList<PlayerOffer> Offers { get; } PlayerUnitStatus Unit { get; } bool Join(PlayerJoin request); void Control(in PlayerControl frame); void Leave(); }`
  - `public readonly record struct PlayerUnitStatus(double Health, double HealthMax, IReadOnlyList<(byte Mount, long Ammo, int Rounds)> Ammo, byte UseVerb, int UseTarget);` (HROT `Health`, `WeaponAmmo` / `AmmoLoadout`, the use verdict of `OperatorUseActions.For`)
  - `public readonly record struct PlayerJoin(PlayerJoinKind Kind, int UnitId, string? Role);` with `PlayerJoinKind {Observe, Unit, Role}`
  - `public readonly record struct PlayerControl(uint Seq, float Forward, float Lateral, float Steer, float Yaw, float Vertical, byte Gait, bool Trigger, byte Mount, HrotVec3 AimPoint, byte Stance, byte ShotSeq, byte CancelSeq, byte AmmoSeq, int ShotTarget, byte UseSeq, byte UseVerb, int UseTarget, float Lean, bool Binoculars, HrotVec3 LookFrom, HrotVec3 LookDir, byte FirePref);` (the fields of HROT's `OperatorControlFrameEvent`)
  - `public readonly record struct PlayerSlotSnapshot(PlayerSlotState State, int UnitId, double RespawnAt, int Version);` with `PlayerSlotState {Waiting, Observing, Choosing, Requesting, Spawning, TakingControl, Playing, Dead}`
  - `public readonly record struct PlayerOffer(int UnitId, string? Role, int Force);`
- Consumes: the HROT entry points recorded in Task 1.

- [ ] **Step 1: Failing tests:** `Control_frame_maps_every_field_to_the_hrot_event` (each field of `PlayerControl` lands in `OperatorControlFrameEvent`, `StationNodeId` = this node), `Join_unit_possesses_it`, `Leave_releases_once` (a second `Leave` does nothing), `Control_without_possession_is_ignored`, `Join_in_replay_returns_false_and_slot_is_observing`, `Slot_follows_hrot_player_states` (driven by the staged world's `PossessedBy` / slot data).
- [ ] **Step 2:** Run the HrotHost tests - FAIL.
- [ ] **Step 3:** Implement `HrotPlayerStation` with the Task 1 entry points; the slot is read every frame from HROT's own player state; Version increments on every slot change.
- [ ] **Step 4:** Tests PASS; `PluginHostTests` still pass (the DIS and HLA plugins ignore `PlayerStation`).
- [ ] **Step 5:** Commit `feat(hrot): the player-station seam (join, control frames, leave) over HROT's player mode`.

### Task 5: The `dps` plugin - outbound

**Files:**
- Create: `src/Hosts/SimNetGateway.Plugin.DdsPlayer/` (`SimNetGateway.Plugin.DdsPlayer.csproj`, `DdsPlayerPlugin.cs`, `DpsSide.cs`, `DpsSettings.cs`, `DpsTransport.cs` (participant, partition, writers), `DpsOutbound.cs` (pivot to topics), `plugin.json`)
- Test: `tests/SimNetGateway.Plugin.DdsPlayer.Tests/DpsOutboundTests.cs`
- Modify: `scripts/package-extension.sh` (add `plugins/dps`), the solution, the architecture layer table

**Interfaces:**
- Produces: plugin `Name` = `dps`; settings `DpsSettings(int Domain = 0, string Partition = "<node station name>", double LeaseSeconds = 2, double HeartbeatSeconds = 5, double PositionThresholdM = 0.05, double AngleThresholdDeg = 1, double VelocityThreshold = 0.1)`; `WantsEventsInReplay` = true.
- Consumes: Task 2 types; pivot messages `HrotSession`, `HrotOrigin`, `HrotEntityState`, `HrotPersonDetail` (Task 3), `HrotTurretPose`, `HrotFire`, `HrotDetonation`, `HrotEntityRemoved`.

- [ ] **Step 1: Failing tests** (real CycloneDDS in-process, the reader in the configured partition): `Entity_appears_with_type_domain_side_and_name`, `Pose_is_sent_on_threshold_and_heartbeat_not_on_noise`, `Person_detail_maps_to_person_state`, `Turret_pose_maps_to_articulation_part`, `Fire_and_detonation_are_events`, `Removed_entity_is_disposed`, `Session_carries_protocol_version_geo_and_mode` (Live / Paused / Replay), `World_boundary_disposes_every_instance` (Review Focus 4), `Other_partition_sees_nothing`, `Appearance_is_published_once_with_unknown_slots` (round 1).
- [ ] **Step 2:** `dotnet test tests/SimNetGateway.Plugin.DdsPlayer.Tests -m:1` - FAIL.
- [ ] **Step 3:** `Effect` is derived by the host with HROT's shared effects library (DD-017 3.9; a HROT deliverable): pivot `HrotEffect(kind, position or anchor entity, start, lifetime)` from fire / detonation / destroyed states - add the test `Destroyed_vehicle_gives_flames_and_smoke_effects`. If the library is not in the HROT checkout yet, leave `Effect` unpublished and note it in DD-017 section 10 (no copy of HROT's rules in this repository). Implement following the DIS side's structure (`Sides/DisSide.cs`: declare ports in `Declare`, register the outbound module in `Register`); domain mapping from the DIS kind/domain of `HrotEntityState.DisType`; side = `Force` (DIS numbering).
- [ ] **Step 4:** Tests PASS; `scripts/package-extension.sh` puts `plugins/dps/{SimNetGateway.Plugin.DdsPlayer.dll, SimNetGateway.Protocol.DdsPlayer.dll, plugin.json}` and still contains no `CycloneDDS*`.
- [ ] **Step 5:** Commit `feat(dps): the dps plugin publishes the world (partition per station, late-join state, events)`.

### Task 6: The `dps` plugin - inbound and the player slot

**Files:**
- Create: `src/Hosts/SimNetGateway.Plugin.DdsPlayer/DpsInbound.cs`, `DpsPlayerSlot.cs`
- Test: `tests/SimNetGateway.Plugin.DdsPlayer.Tests/DpsInboundTests.cs` (a fake `IPlayerStation`)

**Interfaces:**
- Consumes: `IPlayerStation` (Task 4) via `SideBinding.PlayerStation`; Task 2 player topics; Task 1's choice (liveliness or heartbeat).

- [ ] **Step 1: Failing tests:** `Join_request_calls_join_once`, `Control_frame_is_passed_through_newest_seq_wins`, `Leave_calls_leave`, `Lost_client_releases_once_and_can_rejoin` (Review Focus 2), `Second_player_writer_is_ignored_with_one_warning` (Review Focus 3: the first matched writer's instance handle wins), `Join_in_replay_is_refused_as_observing` (Review Focus 5), `Player_slot_and_offers_are_published_on_change`, `Own_unit_carries_ammo_health_and_use_verdict`, `No_player_station_means_view_only` (null seam: player topics ignored, slot = Observing).
- [ ] **Step 2:** Run - FAIL.
- [ ] **Step 3:** Implement: inbound reader in the station's partition; one player writer at a time; lease handling per Task 1; `PlayerSlot` / `OwnUnit` written on change.
- [ ] **Step 4:** Tests PASS.
- [ ] **Step 5:** Commit `feat(dps): player topics drive the player-station seam; lost client releases the unit`.

### Task 6b: Gizmos

**Files:**
- Create: `src/Hosts/SimNetGateway.HrotHost/HrotGizmoHost.cs` (runs HROT's projectors into a capturing `IDebugDrawBuilder` at the rate the view asks), `src/Hosts/SimNetGateway.Plugin.DdsPlayer/DpsGizmos.cs`
- Modify: `IPlayerStation.cs` (Task 4) or a sibling seam `IGizmoSource` in the same folder: `void SetView(GizmoViewRequest view); IReadOnlyList<GizmoLayerFrame> Take();`
- Test: `tests/SimNetGateway.HrotHost.Tests/GizmoHostTests.cs`, `tests/SimNetGateway.Plugin.DdsPlayer.Tests/DpsGizmoTests.cs`

**Interfaces:**
- Produces: `public readonly record struct GizmoViewRequest(IReadOnlyList<string> Layers, IReadOnlyList<int> Selected, IReadOnlyList<(int Entity, string Family)> Pinned, double RateHz);` `public readonly record struct GizmoLayerFrame(string Layer, IReadOnlyList<GizmoPrimitiveData> Primitives);` (layer names = HROT's layer rows; primitives = what the projectors drew, in local metres)

- [ ] **Step 1: Failing tests:** `No_view_runs_no_projectors`, `Selected_unit_path_layer_is_captured` (a staged unit with a planned path, layer Paths, selected), `Layer_off_is_disposed`, `Frame_rate_is_capped_by_the_view`, `Editor_map_settings_are_untouched` (the editor's own layer set before and after is equal).
- [ ] **Step 2:** Run - FAIL.
- [ ] **Step 3:** Implement per Task 1 Step 3a.
- [ ] **Step 4:** Tests PASS.
- [ ] **Step 5:** Commit `feat(dps): HROT's gizmos to the station (client-chosen layers, selection and pins)`.

### Task 7: Reference client

**Files:**
- Create: `src/Hosts/SimNetGateway.DpsClient/` (`SimNetGateway.DpsClient.csproj`, `Program.cs`, `DpsWorld.cs` (subscribes, keeps the latest state per entity, dead-reckons positions from velocity), `DpsClientWindow.cs` (raylib top-down map: entities by domain and side, the own unit highlighted; keys: J join first offer, WASD move, mouse aim, LMB fire, C stance, Esc leave, G toggles the Paths and Contacts gizmo layers for the clicked unit; draws received gizmo primitives), `--headless --seconds N` mode printing counts)
- Test: `tests/SimNetGateway.DpsClient.Tests/DpsWorldTests.cs`
- Note: DD-017 named `SimNetGateway.Viewer`; that viewer is the engine's in-process monitor, so the reference client is its own small host (record the deviation in DD-017 section 10).

- [ ] **Step 1: Failing tests:** `World_mirrors_entities_and_drops_disposed`, `Dead_reckoning_moves_an_entity_between_samples`, `Join_and_frames_are_written_in_the_configured_partition`, `Headless_run_reports_entity_and_slot_counts`.
- [ ] **Step 2:** Run - FAIL.
- [ ] **Step 3:** Implement (CycloneDDS.NET from NuGet here: the client is a standalone process with its own `ddsc`).
- [ ] **Step 4:** Tests PASS.
- [ ] **Step 5:** Commit `feat(dps): reference client (top-down map, join and play with the keyboard)`.

### Task 8: Live check in the HROT editor and as-built

**Files:**
- Modify: `docs/design/DD-017-Dds-Player-Station.md` (section 10 as-built), `docs/design/DD-016-Hrot-Host-And-Side-Plugins.md` (one line: `SideBinding.PlayerStation`), `docs/TASKS.md` (T-034 done)

- [ ] **Step 1:** Package the extension with `plugins/dps`; start the HROT editor hidden (`HROT_HIDDEN_WINDOW=1`, a fresh editor process per scenario load), load a scenario with a `Playable` unit and a `DeployRole` spawn area (an existing HROT demo, or a `gw-live` copy of one), play.
- [ ] **Step 2:** Start `SimNetGateway.DpsClient` from one fixed path (firewall rules are per path) with the station's partition; check: all entities visible within 2 s of starting the client mid-run; J joins a role, the avatar spawns and moves with WASD, fires; killing the client process releases the unit and its AI resumes (`GET /entities/{id}/brain` shows the paused task back); restarting the client joins again.
- [ ] **Step 3:** Record results, the Task 1 choices and deviations in DD-017 section 10; T-034 done.
- [ ] **Step 4:** Commit `docs: DD-017 as-built (T-034), live check in the HROT editor`.
