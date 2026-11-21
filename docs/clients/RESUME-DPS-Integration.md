# Resume: DPS and the Unreal client

State of 2026-11-20, for the next session that continues the DPS (DDS Player Station) work for the Unreal image generator, on the
gateway side or the client side. Everything needed is in two public repositories. Read this first, then the documents it names; the
design docs and the client guide are the canon, this file is only the map.

## 0. ⭐ RESUME HERE — state at the token-cap pause (2026-11-21, late)

**Branches:** gateway `ccr-458b1360-afzl8f` (pushed through `2deaf89`); HROT `ui` (pushed through `7611c0a84`); the gateway builds against
the HROT worktree `/home/user/hrot-c5f8c1971` pinned at HROT `ui` `f5851caf0` (`HROT_ROOT`).

**In flight — a Sonnet worker on T-052** (DD-024 entity cards over `GizmoFrame`, approved R-144; brief
`scratchpad/brief-T-052.md`). Its WIP is UNCOMMITTED in the gateway tree (HrotGizmoHost, DpsGizmos, DpsOutbound, IGizmoSource,
HrotPlayerStation, the IDL, DD-024, TASKS, the client guide). ⛔ Do not commit it blind: if the worker is gone, `git diff` for leftover
mutations first, then have a worker run `bash scripts/build.sh` + `bash scripts/test.sh` and review against DD-024 before committing by path.

**Done today (gateway):** T-045 clock/steps/View rates/ClientWorld gate · T-047 seats (DD-023) · T-048 terrain name + hashes in `Session` ·
T-049 contract half (OfferKindArea, Offer.Kits/Outline) · T-050 render half — the gateway is HROT's linked client `dps` (DD-022 part D,
P-7 Repeat/Fade) + the first-Session-has-terrain fix · notes to Unreal: render answers, live-run answers (§4), re-run request
(`NOTE-…-Gateway-Rerun-Linked-Load.md`), labels (`NOTE-…-Gateway-Entity-Labels.md`).
**Done today (HROT ui):** CE-11026 linked client in every load (+ verdict log) · CE-11027 S12-1..S12-4 seats on one node (StationId,
IGameplayWire + LocalGameplayHub, Leave/ReleaseStation, the editor serves; design `DESIGN_Player_Join.md` §12 BUILT) · backend's batch
merged (CE-11018 director through the linked client, CE-11019/22, CE-11023, CE-11024, CE-11025) · CE-11028 (director scenario, tracked)
· CE-11029 (the swarm exception, tracked).

**Waiting on the Unreal lane:** the re-run of the three linked loads against gateway `5c5117a`+ (expected: wait, fail with reason, wait);
it started on the rendering part; it builds the card renderer for DD-024.

**Next, in order (gateway):** ① review + commit T-052 · ② seat deploy wiring: one `DeployFlowSystem` per seat over HROT's
`LocalGameplayHub.Of(world)` (DD-023 D-3/D-14; a seat deploys only on a running clock), T-049's kits/outlines filled · ③ per-seat
`PlayerMessage` via HROT's `PlayerMessageAddressing` (Hrot.Common) · ④ T-051 `Impact` from effect entities (HROT CE-11024) · ⑤ HROT
CE-11029 (the swarm exception) and CE-11021 (the encode muxes the client's track, 1024-sample tolerance) on HROT `ui`.

**Rulings today:** gateway R-139 .. R-144; HROT R-11010, R-11011.

## 1. Where things stand

| | |
|---|---|
| SimNetGateway | `main` at `12bdbd9` (round 6) - DPS rounds 1 - 6 built, all tests green |
| HROT | tested against `coordinator` `c5f8c1971` |
| Protocol | DPS 1.0, still open to additions until the owner hands the contract to the Unreal client |
| Client guide | [`docs/clients/DPS-Client-Guide.md`](DPS-Client-Guide.md) - the source for clients; changed by commit |
| IDL | [`docs/clients/idl/`](idl/) - generated, committed, checked against the build by a test |
| Test scenario | HROT `scenarios/showcases/player-station` (Playable riflemen, vehicles, a horse, a motorcycle, a flamethrower, a breach, traffic, objectives) |

Tasks T-034 .. T-037 are done (`docs/TASKS.md`).

## 2. Repositories and layout

```
<root>/hrot             git clone -b coordinator https://github.com/pjanec/HROT.git hrot
<root>/SimNetGateway    git clone https://github.com/pjanec/SimNetGateway.git
```

SimNetGateway's HROT host builds against `../hrot` (or `HROT_ROOT`). The Unreal client lives in its own repository and needs only
the committed IDL, the guide and a DDS implementation.

| What | Where |
|---|---|
| The contract (C# types, QoS, rate classes) | `src/Protocols/SimNetGateway.Protocol.DdsPlayer` |
| The HROT host (reads HROT, the player-station seam, gizmos) | `src/Hosts/SimNetGateway.HrotHost` |
| The `dps` plugin (maps to DDS topics, player inbound) | `src/Hosts/SimNetGateway.Plugin.DdsPlayer` |
| The HROT-facing contract (pivot messages) | `src/Protocols/SimNetGateway.Protocol.Hrot` |
| The shared track curve (delayed playback) | `src/Common/SimNetGateway.Common/Tracks` |
| The reference client | `src/Hosts/SimNetGateway.DpsClient` |
| HROT's presentation rules (the reference for renderers) | HROT `Hrot/Engine/Hrot.Look` |

## 3. The design trail

| Doc | Round | What |
|---|---|---|
| [DD-016](../design/DD-016-Hrot-Host-And-Side-Plugins.md) | - | the HROT host and side plugins |
| [DD-017](../design/DD-017-Dds-Player-Station.md) | 1 | the contract: station = node = one player, partition per station, late join without handshake, client checklist (3.11) |
| [DD-018](../design/DD-018-Dps-Round-2.md) | 2 | HUD, type data (`TkbType`), rounds and flames, animals, the motorcycle bank, carriage, building damage and glass, traffic as world-space tracks, one trajectory model |
| [DD-019](../design/DD-019-Dps-Round-3.md) | 3, 5 | client feedback: the suggested `View`, `Impact`, event depth, missile motor, committed IDL, effects in replay; round 5 = on HROT's look library |
| [DD-020](../design/DD-020-Dps-Round-4.md) | 4, 6 | rate classes, `PersonAim` and intent instead of moving results, producer-neutral contract; round 6 = HROT's stance timing, effects from state |
| [DD-021](../design/DD-021-Aim-And-Look-Stream.md) | draft | the aim and look stream apart from the slow body state (T-038) |
| [DD-022](../design/DD-022-Dps-Director-Rendering.md) | draft | time sync and stepping by HROT's conventions, the camera dead-reckoned, director rendering with frames and audio from Unreal (T-039, R-136, R-137); out for the Unreal session's feedback |

Rulings that shape further work (`docs/RULINGS.md`):

- **R-132** - DPS approved; action durations are the renderer's business, the simulation states what the body does.
- **R-133** - the contract stays open until the owner hands it over; append-only.
- **R-134** - the client owns its camera; `View` is HROT's advice. HROT's presentation rules live in HROT's look library: the
  gateway does not port them or invent constants - it asks HROT and waits ("rather wait for HROT doing stuff correctly than do
  fragile hacks and inconsistent guesswork").
- **R-135** - rate classes and intent; a producer-neutral contract.
- Presentation data goes from the HROT host straight to the plugins that use it, not through the pivot repository (DD-018 3.8).

## 4. How to work here

Follow `CLAUDE.md` (the session brief, re-learning after compaction, design before implementation with mermaid block, class and
sequence diagrams, rulings and tasks recorded). In addition, learned on this work:

- **Contract changes:** append fields at the end, enumerators prefixed with their enum name, 0 = unknown, bounded strings; refresh
  the committed IDL with `bash scripts/refresh-dps-idl.sh`; keep producer names out of the contract (a test checks it); state the
  rate class of a new topic; update the guide in the same change.
- **Build and test:** full `dotnet build SimNetGateway.sln` and full `dotnet test SimNetGateway.sln`; never `--filter` for a
  verdict (the xUnit adapter misreports). Test DDS domains stay below 167 (Windows: UDP 7400 + 250 x domain) and disjoint per test
  assembly. The `Engine.Tests` allocation tests are occasionally flaky under a parallel full run; they pass alone.
- **HROT is read-only from here.** When the gateway needs something from HROT, write a prompt for the HROT session (generic wording,
  no particular renderer named), wait for it to land on `coordinator`, then move the tested commit (guide sources table, runbook, DD-017
  section 10) and use HROT's function.
- **Live checks:** the guide's runbook (build HROT, package the extension, a `dps`-only configuration, the editor with
  `HROT_DEBUG_API_PORT`, load `showcases/player-station`, the reference client `--headless --join`). Use `--mode editor`; HROT CE-1992:
  possession and the scenario flamethrower do not work in `--mode all`.

## 5. Open items

Waiting on HROT (ask through a prompt):

- `Impact` in replay - HROT CE-11024 (Q113 H, HROT `ui`): `EffectCause` gains the impact kind and the shooter, then the gateway makes `Impact`
  from the recorded effect entities, live and in replay alike (one path, from state - HROT R-11007); replaces the event-based `Impact`.
- `View` in replay - HROT's camera suggestion is node-local and not recorded.
- Terrain changes on the wire: door states (doors are entities without a state today and most of the entity traffic), felled and
  damaged trees, ground patches (craters, road blockage), marking signs of a marked minefield.
- The dragon (HROT CE-4634) - planned as an animal carrier with a rider seat and a `FlameJet` breath.

Gateway side:

- Player station step 2: roles, respawn, cluster player nodes (DD-017 3.2b), and the HROT station's extra keys (squad switch, seat
  switch, head out, lights, command wheel, designation) on the control frame.
- A live check of gizmos (the window client, G on a unit); filled gizmo triangles still arrive with z = 0.
- `MoverTracks` keeps history depth 16 (the other event topics have 1024); check under a dense traffic net.
- The reference client does not yet subscribe to `View` and `Impact`.
- A live run of rounds 2 - 6 end to end in the HROT editor with the reference client (the earlier live check covered round 1).

Client side:

- Feedback from the Unreal team on rounds 4 - 6 (`PersonAim`, rate classes, the new enums, stance timing, effects from state) is
  pending. Their round-3 points are all answered in the guide.
- The terrain importer must read HROT's building templates (labels, panel order, opening order, the glazed flag) for building damage
  and glass to land; the guide's conventions section lists this as a dependency.

## 6. Suggested next steps

1. Run the live check of rounds 2 - 6 (runbook, the player-station scenario, the reference client), fix what it finds, record it in
   DD-020 section 9.
2. Collect the Unreal team's feedback on rounds 4 - 6 and answer it as round 7 (a new DD).
3. Ask HROT for the replay items and door states in one prompt.
4. Plan player station step 2 with the owner.
