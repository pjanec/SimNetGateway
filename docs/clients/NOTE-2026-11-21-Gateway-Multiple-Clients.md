# Note: running several Unreal clients against one HROT editor — setup, what to expect, how to test together

| | |
|---|---|
| **From** | the gateway / DPS session, 2026-11-21 |
| **For** | the HROT `unreal-ig` session (the Unreal client) |
| **Owner** | 🔒 the user asked the Unreal session to try running multiple Unreal clients in cooperation with the gateway session — this note is the gateway's side; write back in `NOTE-<date>-Unreal-<topic>.md`, I read them after every commit |
| **Builds** | gateway `ccr-458b1360-afzl8f` at or after `5bf6c3b` (seats T-047, deploy T-053, messages T-054, cards T-052, render T-050); HROT `ui` at or after `d611a7336` (the editor serves deploy, CE-11027) |
| **Design** | [DD-023](../design/DD-023-Dps-Multiple-Players.md) (approved R-139, R-140; as built in its section 9) |

## 1. Setup

1. **The editor's gateway config** (`simnetgateway.json`, guide §5): add `seats` = the number of players, e.g.
   `{ "dps": { "domain": 5, "partition": "ig-live", "seats": 3, "renderSeat": 1, "renderRoot": "<folder>" } }`.
   With one seat nothing changes (one partition `ig-live`).
2. **Each Unreal client** gets ITS seat's partition: `ig-live-1`, `ig-live-2`, `ig-live-3` (same domain). One client per seat.
3. A display that only watches (no player) may also take a seat — a seat whose client never sends `JoinRequest` just observes.
4. The render client (DD-022 part D) sits on `renderSeat`'s partition.
5. **Run the simulation clock** when players deploy: HROT's possession runs only while sim time advances; a join made while paused WAITS
   (Requesting / TakingControl, no refusal) and completes when time runs.

## 2. What each client sees

| On your seat's partition | Shared (written once for everyone) |
|---|---|
| `PlayerSlot`, `OwnUnit`, your `GizmoFrame`s, the `PlayerMessage`s addressed to you (HROT's own rule: All, your side, your unit, your station) | every world topic, `Session` (`StationName` = the base partition), `View` (HROT's suggestion), `SimClock` / `ClockStep` |
| what you write: `JoinRequest`, `ControlFrame`, `Leave`, `GizmoView`, `ClockProbe`, `ClientWorld` (and the render topics on the render seat) | — |

- **Deployment is HROT's** (its deploy server in the editor, one deploy flow per seat): offers are placed units, roles (with `Count` /
  `Max`, `Free` false when full, `Kits`), and spawn areas (`OfferKindArea` with an `Outline`); `JoinRequest` carries `Side`, `Kit`, `AreaId`.
  Refusals come back in `PlayerSlot.Reason` with HROT's text (`HeldByAnotherStation (held by station N)`, `RoleFull`, …).
- **Loads wait for every seated client** that reports `ClientWorld` with `WaitForMe`; a client on a partition with no seat sees the world
  but never holds a load up.
- **Your station** is node + seat (`StationId`: node in the low 16 bits, seat in the high 16) — it appears in `HeldByAnotherStation (held by
  station N)` texts and in Station-addressed messages.

## 3. What to try together (each is a regression scenario we want, DD-023 D-16)

| # | Do | Expect |
|---|---|---|
| 1 | two clients, both join a role of one side | both Playing, each holding its own man; a third over `Max` gets `RoleFull` |
| 2 | two clients join the SAME placed unit at once | exactly one Playing; the other Choosing with `HeldByAnotherStation (held by station N)` |
| 3 | a client mounts a vehicle and drives, then quits | the seat, the man AND the vehicle are free for the next player |
| 4 | a client's man is killed | `Dead` with `RespawnAtSim`, then Choosing again |
| 5 | a load (new scenario) with 2 clients, one slow to load | the load returns only after BOTH report `Ready` |
| 6 | messages: All, a side, one station | each client gets exactly what HROT's rule gives it |

Tell me what you see per row — a mismatch is a gateway or HROT defect and I will fix it.

## 4. Known limits

- A seat that has not deployed yet has no side, so it misses its chosen side's messages while Choosing — being fixed now (T-055).
- The gateway's own reference client (`SimNetGateway.DpsClient`, `--domain`, `--partition`, `--join`) joins the first free placed unit only;
  it can stand in as a second player for rows 2, 3, 5.
- Not yet run live with several Unreal clients — yours is the first run.
