# DD-023: Several players behind one gateway (the editor as the server)

| | |
|---|---|
| **Status** | rev 2 (2026-11-21): an independent review folded in (section 3b; it overturns rev 1's D-1 topic split, D-6 and section 6's "no seat sees no world"); approved by the user (R-140: P-1 .. P-7); HROT's half is HROT `Architect_Question_114` |
| **Tasks** | T-046 (this design) |
| **Rulings honoured** | R-139, R-132 (amended: one player per station stays, a station is now a seat), R-133 (append-only), R-135, R-012, R-002 |
| **Related** | DD-017 (the station, the partition, the join flow - its section 3.1 *"rejected: one gateway serving many players"* is superseded by this design for the editor host); DD-022 (the clock, the steps, `ClientWorld` - shared by every seat); HROT [`DESIGN_Player_Join.md`](https://github.com/pjanec/HROT/blob/ui/docs/DESIGN_Player_Join.md) (owns the deploy server and the deploy flow reused here); HROT `docs/blueprints/Architect_Question_114_Seats_On_One_Node.md` (owns `StationId`, the deploy server in the editor, the local gameplay channel) |
| **Date** | 2026-11-21 |

## 1. Context and goals

The user (R-139): one HROT editor runs as the all-in-one, networkless server; the gateway in it serves **several** external clients, each one
player. Today a station is a node (HROT C-8) and the gateway admits one player (DD-017 3.1, `DpsInbound.cs:12`).

Goals:
- N **seats** in one gateway; each seat is one player with its own unit, slot, view, HUD and gizmo view.
- Every client stays **unchanged**: it is configured with its seat's partition and sees exactly today's contract.
- The player's deployment (side, placed unit or role, kit, spawn area, death, respawn) is **HROT's own** deploy server and deploy flow, not a
  gateway copy.
- Every world load waits for every seat's client (DD-022 3g, unchanged).

Out of scope: the editor's CPU with many players (user: *"an independent story"*); cluster player nodes (DD-017's model stays, as seat 0);
a lobby or seat discovery; voice.

## 2. Block diagram

```mermaid
flowchart LR
    subgraph Editor["HROT editor node (networkless)"]
        DS[Deploy server<br/>DeployProcessorSystem]
        LC[(Local gameplay channel)]
        PS[PossessionSystem]
        W[(World)]
        subgraph GW["Gateway (DPS plugin)"]
            WT[World participant<br/>partition base-*]
            ST[Seat table]
            S1[Seat 1: participant base-1<br/>DeployFlowSystem, EntityOrders, player station]
            S2[Seat n: participant base-n<br/>...]
            CW[ClientWorld gate]
        end
    end
    U1[Client 1<br/>partition base-1]
    U2[Client n<br/>partition base-n]
    W -->|one gather| WT
    WT -->|world topics incl. Session, View - once| U1
    WT -->|world topics, once| U2
    S1 <-->|PlayerSlot, OwnUnit, HUD, JoinRequest, ControlFrame, ClientWorld| U1
    S2 <--> U2
    S1 <-->|deploy request / offers, status, slot| LC
    S2 <--> LC
    LC <--> DS
    S1 -->|possess, control frames, station = seat| PS
    DS -->|spawn a role's man| W
    S1 -->|its client's ClientWorld| CW
    S2 -->|its client's ClientWorld| CW
```

What the picture shows that prose hides: the world is **gathered and written once** (one participant whose partition name is a wildcard),
while everything a player owns lives in **its seat's own participant**; and the gateway seat **decides nothing about deployment** - it sits
between its client and HROT's one deploy server, through the same deploy flow a cluster player node runs.

## 3. Key concepts and decisions

| Term | Meaning |
|---|---|
| **Seat** | one player position in the gateway: a station id, a DDS partition, a deploy flow, an `EntityOrders`, a player station. Seat 0 is the editor's own user |
| **`StationId`** | HROT's station number: node in the low 16 bits, seat in the high 16 (user, R-139); seat 0 = the node id itself, so today's values are unchanged (HROT Q114) |
| **Seat partition** | `<base>-<n>` for seat n; the world participant writes on `<base>-*`, which DDS matches to every seat partition (a wildcard on one side only) |

- D-1 **One world participant, one participant per seat.** CycloneDDS.NET takes one partition per participant (`DdsParticipant(domain,
  defaultPartition)`). World topics are written once by a participant on `<base>-*`; each seat's own topics (`PlayerSlot`, `OwnUnit`,
  `PlayerMessage`, `GizmoFrame`, the HUD; rev 2: `View` and `Session` are WORLD topics, D-9) and its inbound topics (`JoinRequest`, `ControlFrame`, `Leave`, `GizmoView`, `ClockProbe`) live on
  `<base>-<n>`. The inbound routing is the partition: a seat reads only its own client. Rejected: appended keyed copies of `PlayerSlot`,
  `OwnUnit`, `View` (every client changes); a DDS domain per seat (domains are a deployment resource, DD-017 3.2a).
- D-2 **A seat is a HROT station.** Each seat's `EntityOrders` carries `Station = StationId.Of(editorNode, n)`; HROT's possession compares
  station numbers only (`Possession.cs:221-263`, `StationClaims.cs:97-104`), so units held by different seats are arbitrated in one place,
  in one tick. Rejected: seat ids in a reserved number range (a seat would not know its node); reusing node ids (collide with the editor's).
- D-3 **Deployment is HROT's: one deploy server in the editor, one `DeployFlowSystem` per seat** (user, R-139). The seat builds the flow with
  its station, its own `Possess` callback (its `EntityOrders`) and an environment answered by the editor; the flow reads its own slot from
  the server (`DeployProcessorSystem.TryGetSlot`). The gateway maps DPS onto the flow's verbs and readouts (section 3a). Rejected: a thin
  deploy client in the gateway (duplicates the state machine, its 10 s waits and retries - "one implementation per concept").
- D-4 **The gateway ticks its seats' flows** (`Execute(view, dt)` from its own frame), since seats come and go at run time and an ECS
  system is composed once at node start (HROT `GameplayClientPack.cs:36-39`).
- D-5 **Seat lifecycle.** Seats are configured (`dps.seats`, default 1); each seat's participant exists from the start, so a client may join
  at any time. A seat is free when no client is its player (DD-017's one-player rule, per seat). Leave or lost liveliness: the seat's flow
  stops, the seat's unit is released, and the deploy server's slot for that station is freed (HROT Q114 leave verb).
- D-6 *(rev 2: partly SUPERSEDED by D-11 - the station numbering changes even with one seat)* **One seat = today's layout exactly.** With `dps.seats` 1 the gateway uses one participant on `<base>` as now; partitions with a
  suffix appear only from 2 seats. An existing single-player deployment changes nothing.
- D-7 **What every seat shares, unchanged:** the world gather, `SimClock` / `ClockStep` (on the world participant), the `ClientWorld` gate
  (rev 2, D-10: read per seat, never on `<base>-*`; the host service already waits for every `WaitForMe` client, T-045), the render
  client (one per gateway, DD-022 D-7).

### 3b. Rev 2 - the independent review (2026-11-21), folded

The review checked every claim against the code; four findings broke rev 1, the rest were gaps. Decisions added or changed:

- D-8 **`Entity.Playable` and `Session` on the world participant.** `Playable` is today computed from the one station's offers
  (`DpsTransport.cs:98-101`); a world writer has no station. It now comes from the deploy server's offers, which are the same for every
  seat. `Session` is keyless: written ONLY by the world participant, `StationName` = the base name (`DpsTransport.cs:92` used the
  partition).
- D-9 **`View` is a world topic.** Its only source is HROT's one `CameraSuggestion` (`CameraViewSource.cs:17`), the editor's or the
  director's suggestion; every client already binds its own camera (R-134). A per-seat view would need a new source; none is wanted.
- D-10 **Client-written topics are read PER SEAT, never on `<base>-*`.** `ClientWorld`, `ClockProbe` (its echo written on the seat's
  participant, one echo thread per seat), `JoinRequest`, `ControlFrame`, `Leave`, `GizmoView`. The host service's one `ClientWorld` table
  is fed by every seat's reader, so the gate waits for every SEATED client and a stray client on `<base>-x` can never stall a load.
- D-11 **Seat numbering:** station `node/0` is the editor's own user; gateway seats are `node/1 .. node/N`, also with one seat. Today the
  gateway player and the editor user share the editor node's station (`HrotPlayerStation.cs:89`); D-11 separates them. Every "is this
  mine" check in the host (`HrotPlayerStation.cs:89, 480, 507, 541, 581`) uses the seat's station. N is capped at 1024 (the high 16 bits
  stay positive far below 32768).
- D-12 **The render client sits on a named seat** (`dps.renderSeat`, default 1). `ClockStep.Samples` (DD-022 D-B4 rev 6) = the world
  topics counted on `<base>-*` + that seat's own counted topics (`PlayerSlot`, `OwnUnit`, `PlayerMessage`); during a job every seat's
  counted writes also happen only in the step's gather.
- D-13 **Lifetimes.** Per gateway runtime (die at a world boundary, `DdsPlayerPlugin.cs:7-9`): the world participant, the seat
  participants, the seat table, each seat's `DeployFlowSystem` and `EntityOrders` (they hold the world and map, `DeployFlowSystem.cs:88-96`;
  rebuilt where `DisGatewaySystem` sees a new world, `:116-128`). Host lifetime (`DpsHostService`): the `ClientWorld` table, the forced
  epoch, the seat configuration.
- D-14 **The flow's environment in the editor:** `GateOpen` = a scenario is loaded; `OperatingLive` = the editor runs live AND sim time
  advances (`PossessionSystem` runs only then, `Possession.cs:184`; a paused editor would time every deploy out after 10 s of wall time);
  `ServerExists` = the editor composes the server (HROT Q114-B).
- D-15 **`PlayerMessage` per seat** through HROT's one addressing rule `PlayerMessages.Addressed` (`PlayerMessages.cs:45-52`), moved below
  `Hrot.Presentation` so the gateway can call it (HROT Q114-I); never a second filter in the gateway.
- D-16 **Feature-test scenarios (HROT R-4277)**, each with `checks.json` expect rows: `two-seats-deploy` (two seats deploy as two roles
  of one side; both Playing; the role count is respected), `two-seats-one-unit` (the race; exactly one holder), `seat-leaves-vehicle`
  (a seat riding and driving leaves; the vehicle is free), `seat-respawn` (death, the delay, Choosing again). Role spawn points seeded
  from the scenario (HROT Q114-J), so each run is the same.

Mapping note: DPS `SlotState.Spawning` has no `DeployFlowState` twin (`DpsTypes.cs:50`); map by name, `Spawning` unused. The flow gains a
`Side` readout for `PlayerSlot.Side` (HROT Q114-C).

### 3a. DPS onto HROT's deploy flow

| DPS (client to gateway, seat n) | `DeployFlowSystem` (seat n) |
|---|---|
| `JoinRequest` Kind Observe, `Side` | `Observe(side)` |
| `JoinRequest` Kind Unit, `UnitId`, `Side` | `Request(side, unitNetId)` |
| `JoinRequest` Kind Role, `UnitId` = the offered role's `Offer.Id`, `Side`, `Kit`, `AreaId` | `Request(side, roleId, kit, areaNetId)` (the server tells a role by its id, `DeployProcessorSystem.cs:168`) |
| `Leave` / liveliness lost | `StopObserving()` or release + the server's leave (HROT Q114) |

| `DeployFlowSystem` readout | DPS `PlayerSlot` (seat n's partition) |
|---|---|
| `State` Waiting / Observing / Choosing / Requesting / TakingControl / Playing / Dead | `SlotState` of the same name (already 1:1, `DpsTypes.cs:50`) |
| `Offers` (Placed / Role / Area, count / max, holder) | `Offers` (`OfferKindUnit` / `OfferKindRole`; **append `OfferKindArea`**); `Free` = holder 0 |
| `LastRefusal` | `Reason` |
| the slot's `RespawnAtSim`, `Side`, unit | `RespawnAtSim`, `Side`, `UnitId` |

## 4. Class diagram

```mermaid
classDiagram
    class DpsHostService {
        <<existing, host lifetime>>
        +BeginWorld(Guid)
        +IsWorldReady(Guid, failure, step) bool
        Worlds : DpsClientWorlds
    }
    class DpsWorldTransport {
        <<existing DpsTransport, partition base-*>>
        world topics, SimClock, ClockStep, render topics
    }
    class DpsSeatTable {
        <<new>>
        +Seats : DpsSeat[]
        +Tick(view, dt)
    }
    class DpsSeat {
        <<new>>
        +Index : int
        +Station : StationId
        +Partition : string
        Participant
        Inbound : DpsInbound
        Slot : DpsPlayerSlot
        Echo : DpsClockEcho
    }
    class HrotSeatStation {
        <<from HrotPlayerStation, per seat>>
        Orders : EntityOrders
        Flow : DeployFlowSystem
    }
    class DeployFlowSystem {
        <<HROT, existing>>
        +Request(side, unit, kit, area) bool
        +Observe(side) bool
        +StopObserving() bool
        +State
        +Offers
    }
    class EntityOrders {
        <<HROT, existing>>
        +Station : int
    }
    DpsSeatTable ..> DpsHostService : feeds ClientWorld, host lifetime
    note for DpsSeatTable "per gateway runtime, rebuilt at a world boundary (D-13)"
    DpsSeatTable "1" *-- "1..n" DpsSeat
    DpsSeat "1" -- "1" HrotSeatStation
    HrotSeatStation "1" *-- "1" DeployFlowSystem
    HrotSeatStation "1" *-- "1" EntityOrders
    DpsSeatTable ..> DpsWorldTransport : shares the world gather
```

`DpsSeat` is what today's single transport holds per player (inbound, slot, own unit, view, HUD, gizmo view), cut out of `DpsTransport`;
`HrotSeatStation` is today's `HrotPlayerStation` per seat, its placed-unit join (DD-017 step 1) replaced by the deploy flow.
`DeployFlowSystem` and `EntityOrders` are HROT's, used unchanged except HROT Q114's registry flag.

## 5. Sequence diagrams

### 5a. Join and deploy as a role

```mermaid
sequenceDiagram
    participant U as Client (base-2)
    participant S as Gateway seat 2
    participant F as DeployFlowSystem (station node/2)
    participant D as Deploy server
    participant P as PossessionSystem
    D-->>F: offers (local channel, a copy per flow)
    S->>F: tick
    S-->>U: PlayerSlot Choosing, Offers
    U->>S: JoinRequest Role Rifleman, Side, Kit, Area
    S->>F: Request(side, role, kit, area)
    F->>D: deploy request (station node/2)
    D->>D: rules, spawn the man (EntityCreationRequest)
    D-->>F: status accepted, unit
    F->>P: Possess(unit) via seat 2's EntityOrders
    P-->>F: PossessedBy = node/2
    S-->>U: PlayerSlot Playing, OwnUnit
```

The seat only translates. Refusals (`WrongSide`, `HeldByAnotherStation`, a full role) come back from the server through the flow's
`LastRefusal` into `PlayerSlot.Reason`; the client sees Choosing again.

### 5b. Two seats reach for one unit

```mermaid
sequenceDiagram
    participant S1 as Seat 1 flow
    participant S2 as Seat 2 flow
    participant D as Deploy server
    S1->>D: request unit 40 (node/1)
    S2->>D: request unit 40 (node/2)
    D->>D: one tick, in order - node/1 reserves 40
    D-->>S1: accepted
    D-->>S2: refused ReservedForAnother (holder node/1)
```

One server, one tick: the race is decided in one place (`StationClaims.ForPossess`), never between nodes.

### 5c. Death, respawn, leave

```mermaid
sequenceDiagram
    participant U as Client (base-1)
    participant S as Gateway seat 1
    participant F as DeployFlowSystem (node/1)
    participant D as Deploy server
    D-->>F: slot Dead, RespawnAtSim
    S-->>U: PlayerSlot Dead, RespawnAtSim
    D-->>F: slot Choosing (after the delay)
    S-->>U: PlayerSlot Choosing, Offers
    U--xS: liveliness lost
    S->>F: stop, release the unit (EntityOrders)
    S->>D: leave (station node/1) - slot freed, unit back to its task
```

## 6. Edge cases and error handling

- **Two clients on one seat partition:** DD-017's rule per seat - the first `JoinRequest` owns it, the other is dropped with one warning.
- **More clients than seats:** *(rev 1 said "sees no world" - FALSE, the review: `<base>-*` matches any `<base>-<x>`)* a client on a partition with no seat SEES the world but is nobody's player: no seat reads its `JoinRequest`, `ControlFrame` or `ClientWorld`, so it can neither play nor hold up a load (D-10). It is a view-only client, as DD-017 3.2a allows.
  Discovery of free seats is out of scope (a lobby would be an appended topic on `<base>-*`).
- **A world load:** every seat's flow returns to Waiting (the gate closes); the deploy server forgets its slots, answers and pending spawns at the world boundary (HROT Q114-H; rev 1 assumed they reset - they do not, `DeployProcessorSystem.cs:63-78`); the
  `ClientWorld` gate waits for every seat's client (DD-022 3g).
- **The editor's own user** stays seat 0 and keeps possessing through the map; deploying it through the same flow is P-6.
- **A replay:** seats are view-only during a replay (nothing enters the world, as today); their flows sit in Waiting (D-13) and the deploy server is gated off (HROT Q114-H).
- **Config with one seat:** today's single partition (D-6); the player is station node/1, no longer the editor's own station (D-11).

## 7. Test plan

- **Seats are independent (property):** seeded random joins, requests, deaths and leaves on 1 .. 8 seats; after every step each client's
  `PlayerSlot` equals its seat flow's readout, no seat ever holds a unit another seat holds, and the server's slots equal the seats'.
- **The race (5b):** two seats request one unit in one frame - exactly one Playing, the other Choosing with `ReservedForAnother`.
- **Partitions:** a world sample reaches every seat client once; a seat's `PlayerSlot` reaches only its client; a client's
  `ControlFrame` reaches only its seat.
- **One seat = today:** with `dps.seats` 1, today's DPS suites pass unchanged (one partition, no suffix).
- **Leave:** lost liveliness frees the seat, releases EVERY unit the station holds (a ridden vehicle too, HROT Q114-E) and frees the server's slot; the next client joins it.
- **A stray client** on `<base>-x` with no seat: sees the world, its `ClientWorld` never reaches the gate (D-10).
- **Old recordings still play** after `StationId` (HROT Q114-A's layout alias).
- **Render on a seat:** `Samples` = world + the render seat's counts, exact on a seeded world (D-12).
- **The scenarios of D-16 pass** in `scripts/scenario-regression.sh`.
- **Loads:** a load with three seat clients waits until all three are Ready (extends T-045's gate rails).
- **HROT side** (HROT Q114): the editor composes the server; a role's man spawns in the editor; the local channel gives each flow its own
  copy of offers and status.

## 8. Proposals awaiting approval

| # | Proposal | Lean |
|---|---|---|
| P-1 | World participant on `<base>-*`, one participant per seat on `<base>-<n>` (D-1); `View`, `Session` world topics; client-written topics read per seat (D-8 .. D-10) | yes - clients unchanged |
| P-2 | A seat is a HROT station `StationId.Of(node, n)`, 16/16 (D-2; HROT Q114-A) | yes (16/16 is the user's) |
| P-3 | The gateway ticks one `DeployFlowSystem` per seat (D-3, D-4); DD-017's step-1 placed-unit join is replaced by it | yes |
| P-4 | Seats configured (`dps.seats`, at most 1024), participants made per gateway runtime; seats are node/1 .. node/N, the editor user node/0 (D-5, D-11, D-13) | yes |
| P-7 | The render client on a named seat; `Samples` = world + that seat (D-12) | yes |
| P-8 | Append to `Offer` what HROT's deploy offer carries and DPS lacks (`DeployOfferTopic`, HROT `GameplayTopics.cs:81-94`): the role's `Kits` (<= 8 names of 32; `JoinRequest.Kit` already picks one by index) and an area offer's `Outline` (<= 32 world points; a spawn area is an area entity with an outline, HROT `SpawnArea.cs`, which DPS does not send) | yes - append-only; ✅ approved (R-141) |
| P-5 | Append `OfferKindArea` to `OfferKind` (3a) | yes - `JoinRequest.AreaId` already exists |
| P-6 | The editor's own user deploys through the same flow as seat 0 (one path for every player) | later, optional - HROT's UI, not this design |

Needs HROT first (HROT Q114): `StationId`; the deploy server composed in the editor; the local gameplay channel; the flow's registry flag;
the leave verb.

## 9. As-built notes

**T-047 (2026-11-21), the gateway part with no HROT dependency: built; HROT Q114 is not, so joining is still today's placed-unit possession per seat.**

| Built | Where |
|---|---|
| `dps.seats` (default 1, 1 .. 1024; outside = a configuration error naming the key, from the file and from `DpsSettings.Validate`) | `DpsSettings.cs`, `DpsSide.cs`, `DdsPlayerPlugin.Check` |
| World participant on `<base>-*` (Seats >= 2) or `<base>` (Seats 1, D-6): every world topic, `Session` (`StationName` = the base name, one writer), `View`, `SimClock` / `ClockStep`, `PlayerMessage` | `DpsTransport.cs` |
| `DpsSeat` per seat on `<base>-<n>` (Seats 1: the world participant itself): `PlayerSlot`, `OwnUnit`, `GizmoFrame` + `DpsGizmos`, `DpsInbound` (Join / Control / Leave), `GizmoView`, `DpsClockEcho` (probe read and echo written on the seat), `DpsClientWorldInbound` into the host-lifetime `DpsHostService.Worlds` | `DpsSeat.cs` |
| `Entity.Playable` from ONE offerable-unit set: the union of every seat's offers, rebuilt once per frame by index loops into a reused `HashSet` (0 bytes in the steady state, test-proved) | `DpsTransport.RefreshPlayable` |
| N `HrotPlayerStation`s, one per seat, station `node \| n << 16` (one field, used at the five "is this mine" sites); `SideBinding.PlayerSeats` appended (`PlayerStation` = seat 1, so DIS / HLA are unchanged); `IGatewayPlugin.PlayerSeats(settings)` (default 1) tells the host how many | `HrotPlayerStation.cs`, `DisGatewaySystem.cs`, `GatewayRuntime.cs`, `GatewayCapability.cs`, `PluginSet.SeatsFor`, `IGatewayPlugin.cs` |

Findings and deviations:
- **INTERIM `PlayerMessage`** stays on the world participant (every seat's client sees every message) until HROT Q114-I moves `PlayerMessages.Addressed` below `Hrot.Presentation` and the message is routed per seat (D-15). SUPERSEDED by T-054 below: the interim is gone.
- **Behaviour change on purpose (D-11):** the gateway player is station `node \| 1 << 16`, no longer the node's own station, also with one seat. Tests that assumed the node id were changed.
- **Refusal text (added, not in the design's seat list):** the seam had no refusal channel (`PlayerSlot.Reason` was always empty). `PlayerSlotSnapshot` gained an appended `Reason` (HROT's own `OrderResult.Detail` of a refused `Possess`); a seat that loses the race (another station was granted the unit) now returns to Choosing at once with that text instead of waiting out the 10 s timeout. `PlayerSlot.Reason` is capped at 64 characters by the contract, so long texts are cut.
- **PackHarness does not run HROT's `PossessionSystem`:** the tests' arbiter (first request in event order wins a free unit) stands in for it, as `Stage.Hold` already did; the real system's race is the deploy-server scenario `two-seats-one-unit` (D-16), which needs HROT Q114.
- ~~`ClockStep.Samples` counting for the render seat (D-12) is not built; `dps.renderSeat` is not added.~~ SUPERSEDED by T-050 (2026-11-21): `dps.renderSeat` (default 1, 1 .. `dps.seats`) and `dps.renderRoot` exist; the render client's topics are read and written on that seat's participant, and `Samples` = the world topics + that seat's `PlayerSlot` / `OwnUnit` (every seat's are held in a render job, only that seat's counted) - DD-022 section 9, "Part D as built".
- Seats beyond the stations the host offers (a host with fewer player stations than `dps.seats`) only observe, with one warning.
- Not built here (HROT Q114): the deploy flow per seat (D-3, D-4), roles and areas (`OfferKindArea`, P-5), the leave verb, the seat table type. SUPERSEDED by T-053 below (the flow, roles, areas and the leave verb are built; the seat table type is the seat array of `DisGatewaySystem`).

**T-049 (contract half, 2026-11-21).** Appended: `OfferKindArea` (P-5), `Offer.Kits` (`[DdsBound(8)]` `NameItem`, a role's kit names, `JoinRequest.Kit` picks by index) and `Offer.Outline` (`[DdsBound(32)]` `Vec3d`, an area offer's outline in world metres) (P-8, R-141); IDL regenerated. **Today's placed-unit offers write `Kits` and `Outline` empty** (`DpsPlayerSlot.cs`); filling them from HROT's deploy offers waits for HROT Q114 giving seats the deploy flow. SUPERSEDED by T-053: filled from the deploy flow's offers. Finding: HROT's `DeployAreaMapLayer` does NOT reach DPS clients as `GizmoFrame` polygons - it is a 2-D `IMapLayer` drawing with Raylib on the Player's node (`DeployAreaMapLayer.cs:23, :59, :69-86`, attached at `MapInteractionPack.cs:661`), while the gateway's gizmo source only runs reflection-registered stateless gizmo projectors into `DebugPrimitiveBuffer` frames (`HrotGizmoHost.cs:137-140, :162-190`); no projector reads the deploy offers or spawn areas. So `Outline` duplicates nothing.
**T-048 (2026-11-21).** `Session.Terrain` is now the resident terrain's name, with appended `TerrainBaseHash` / `TerrainOverlayHash` (`[MaxLength(64)]`); pivot message `HrotTerrain` from `HrotTerrainGather` (reads the managed `TerrainDefinition` singleton), sent on change and by every new runtime, re-sent in `Session` by `DpsOutbound`. A terrain with no resolved name is no terrain (empty strings). An editor replay keeps the previous terrain until HROT CE-11025.

**T-050 (2026-11-21), findings from the Unreal lane's live run.** (B) The gateway now offers itself as HROT's linked client, so every load waits for the clients' `ClientWorld` of the world epoch - see DD-022 section 9. (A) `Session` of a new `WorldEpoch` always names the resident terrain: the host gathers the terrain before the session, and a forced epoch (`BeginWorld`) is announced one frame after it was taken. This also changes T-047's statement that the forced epoch is "published on the next frame": it is now the frame after that. The render client sits on `dps.renderSeat` (default 1) - see the superseded line above and DD-022 section 9.

**T-053 (2026-11-21), seats deploy through HROT's deploy server (D-3, D-4, D-13, D-14; HROT Q114 built, `DESIGN_Player_Join.md` section 12): built.**

| Built | Where |
|---|---|
| `DeployFlowPlayerFlow : IPlayerFlow` per seat: ONE HROT `DeployFlowSystem` on its own `LocalGameplayHub.Of(world).ClientEnd()`, `register: false`, station = the seat's, a `PlayerSlotReaderSystem` on the same end for Dead / respawn; ticked once per frame by `HrotPlayerStation.Frame` (a seat is no ECS system); rebuilt on the first frame of a world and after every boundary (D-13: `Boundary()` sends the flow's `Leave`, so the server frees the slot and HROT's `ReleaseStation` frees every unit the seat holds, then the old flow is `Unregister`ed and its end closed) | `DeployFlowPlayerFlow.cs` |
| Choice per seat, ONE decision point, logged once: the deploy flow when the hub has an open server end, else today's `PossessionFlow` (a node without a deploy server keeps step 1, its rails unchanged) | `HrotPlayerStation.ChooseFlow`, `DeployServer.Exists` |
| Seam (append-only): `PlayerJoin` + `Side` (DIS force) / `Kit` / `AreaId`; `PlayerOffer` + `Kind` (Unit / Role / Area), `Count`, `Max`, `Free`, `Kits`, `Outline` (value equality over the lists); `PlayerSlotSnapshot` + `Side`; `IPlayerFlow` + `Reason`, `Side`, `NowMs` | `IPlayerStation.cs`, `HrotPlayerStation.cs` |
| DPS: `DpsInbound` fills the join from `JoinRequest`; `DpsPlayerSlot` fills `Offer.Kind / Count / Max / Free / Kits / Outline` (T-049's fields) and `PlayerSlot.Side` from the seat | `DpsInbound.cs`, `DpsPlayerSlot.cs` |
| `Entity.Playable` counts only `Kind == Unit` offers | `DpsTransport.RefreshPlayable` |

Mapping as built (3a): Join Observe -> `Observe(side)` (a seat that observes and then chooses is `StopObserving()`ed first); Unit -> `Request(side, unitNetId)`; Role -> `Request(side, role id, kit, areaNetId)`, the role found by `UnitId` (its offer's id) or, with `UnitId` 0, by name (and side when stated); `Side` 0 = the offer's own side. State by name, DPS `Spawning` unused; `UnitId` only while TakingControl / Playing / Dead (the flow's `UnitNetId`, which for a role is the spawned man, not the role); `Reason` = the flow's `LastRefusal` (HROT's text: `HeldByAnotherStation (held by station N)`, `RoleFull`, `NoSuchArea`, timeouts); `RespawnAtSim` from the flow. Offers: `Free` = holder 0 for a placed unit, `Count < Max` (or Max 0) for a role, always for an area. An area's `Outline` is read from the area entity by its network id (its one area shape, `WeatherField.TryOutline`, as HROT reads any area): the entity's position plus the shape's relative points, Z = the entity's Z; more than 32 points are decimated evenly (first point kept, order kept; `DecimatedOutlines` counts them), cached per area against the shape object and the position.

Findings and deviations:
- **DEVIATION from D-14 (measured): sim time advancing is NOT part of `OperatingLive`.** With "live AND advancing" a pause sends the flow to Waiting (`DeployFlowSystem.Execute`: `!live` -> `Reset()`), it FORGETS the request in flight, and after the resume it is Choosing while the server still holds the seat's slot (rail `Measured_literal_D14_...`, pinned behind `DeployFlowPlayerFlow.PauseAsWaiting`). Instead `OperatingLive` = loaded and no recording shown, and the pause goes where D-14's own reason (the 10 s timeout) lives: the flow's `NowMs` is a RUN clock that counts only the wall milliseconds in which sim time advances. A request made while paused therefore waits in Requesting / TakingControl with no refusal and completes when the clock runs.
- **Measured: a possess published while paused is LOST** (the bus keeps an event one frame and `PossessionSystem` is `WhenTimeAdvances`), so the flow's `Possess` answers "sim time is paused" (a LOCAL refusal, which HROT's flow retries every 500 ms of its run clock) until time runs, and only then calls `EntityOrders.Possess`. Without it the possess was sent into the pause, lost, and the seat timed out 10 s of running time later with "taking control timed out". Residual race: time stops in the frame between the possess and `PossessionSystem`; the seat then reports HROT's "taking control timed out" after 10 s of running time.
- **Measured (HROT, not ours): the server's wait for a spawned man is 10 s of WALL time** (`DeployProcessorSystem.SpawnTimeoutMs`). If the editor's creation path needs sim ticks, a role request made while paused longer than that is refused by the server ("spawn timed out"); the seat reports it as HROT's text. Not measured on the real editor here (the harness spawns synchronously or on demand). A finding for HROT: its deadline could count sim time.
- **`ServerExists` needs a reflection read** of the hub's private `_server` (`DeployServer.Exists`): `LocalGameplayHub` has no public "a server end is open" member and `ServerEnd()` would OPEN one. One method, so a HROT member replaces it; a missing field answers false (the seat keeps step 1). A finding for HROT (a one-line `HasServer`).
- A refusal arrives on the wire, so `Join` returns true for a request the server then refuses (`HeldByAnotherStation`, `RoleFull`): `PlayerSlot` goes Requesting -> Choosing with `Reason`. Step 1 answered false at once; DpsInbound's "owner" rule is unaffected (the refused client stays the seat's client until it leaves).
- A full role is offered with `Free = false` although HROT's offer has no holder: a client must not offer a place that is refused (`RoleFull`); `Count` / `Max` stay for the display.
- Roles and areas ARE entities of the world: `Entity.Playable` would have marked the role and area entities playable; only placed units count now.
- A world boundary that changes nothing about the decision (the editor: same server) logs nothing; the decision line is logged again only when it flips.
- Harness: `PackHarness.AfterSystem` (virtual hook after the bus swap); the deploy rails (`PlayerStationDeployTests`) run HROT's real `DeployProcessorSystem`, `ServerPlayerSlotReaderSystem` and `PossessionSystem` (the latter only while time advances) over the hub's server end; the spawn is synchronous or deferred (`deferSpawn`). The scenarios of D-16 on the real editor (`two-seats-deploy`, `two-seats-one-unit`, `seat-leaves-vehicle`, `seat-respawn`) are NOT built here; this task's rails are the in-process ones.

**T-054 (2026-11-21), `PlayerMessage` per seat (D-15): built; the interim is gone.** `PlayerMessage` is no longer a world topic: each `DpsSeat` owns a `PlayerMessage` channel on its own participant (counted in a render job on the render seat only, like its slot, D-12), and `DpsTransport.PlayerMessage` is the `DpsMessageRouter` that asks every seat's station `IPlayerStation.Addressed(kindName, value)` (new, default true) and writes the message on the seats that answer yes. `HrotPlayerStation.Addressed` calls HROT's one rule, `PlayerMessageAddressing.Addressed(kind, value, MessageViewer(unit, side, station))`, with the seat's viewer: the unit it HOLDS (Playing, else 0), its side and its station `node | n << 16`; no filter of the gateway's own. Findings: (1) the only translation is the contract's DIS force for a `Side` address back to HROT's force id (3 -> Neutral); (2) a seat holding no unit has the side it asked for only while it holds a unit (the flow's `Side`), so before it deploys it is Neutral: it gets All, its station and Neutral-side messages only, as HROT's rule gives a viewer with unit 0; (3) a seat with no station (a host with no player mode) is delivered every message, as before; (4) `Instructor` reaches no seat (HROT's rule).

**T-055 (2026-11-21), follow-ups of T-054.** (a) `HrotPlayerStation.Addressed` takes the viewer's side from the held unit, else from the side the seat last asked for (`IPlayerFlow.RequestedSide`: the deploy flow's `Side` after a join, a refused join or an observe; `PossessionFlow` says 0 and keeps Neutral), else Neutral - so T-054 finding 1 is closed: a Choosing / Observing seat that chose Hostile gets Hostile-side messages. (b) The ~1.4 KB per steady engine tick of three seats was PRODUCTION, measured by bisecting `rig.Step` (service 0 B, transport pump 1368 B, engine tick 0 B; inside the pump only `DpsSeat.Pump`'s `DpsPlayerSlot` and `DpsInbound` allocate): `DpsPlayerSlot.Pump` compared the offers with `SequenceEqual` (two boxed enumerators) and rebuilt the whole `OwnUnit` wire value (LINQ `Take` / `Select` closures, ammo list) EVERY frame to compare it with the last - now offers are compared by index and the status is compared field by field against a kept copy, the wire value is built only on a change. On the host side `HrotPlayerStation.ReadUnit` allocated about 370 B per Playing seat per frame (a new ammo list, the allocating `OperatorUseActions.For(...)` array and its enumerator, an enum `ToString` for the fire mode) - now a reused list, the span overload into a reused buffer, a cached name table. Result: the station's whole frame of 3 Playing seats is 0 B; a whole engine tick of 3 Playing seats is at most 144 B. **Not removable here:** that floor is the CycloneDDS binding - every `DdsReader.Take` (even an empty one) allocates 16 B (`DpsInbound.cs` `ReadJoin` / `ReadControl` / `ReadLeave`, three per seat per frame; the binding has no peek / `HasData` on `DdsReader`); a library change (a data-available flag or a reusable loan) would take it to 0.

