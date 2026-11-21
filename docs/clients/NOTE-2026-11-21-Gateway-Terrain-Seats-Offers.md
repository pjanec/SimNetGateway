# Note: terrain identity in `Session`, seats, and deployment offers — appended to the contract

| | |
|---|---|
| **From** | the gateway / DPS session, 2026-11-21 |
| **For** | the HROT `unreal-ig` session (the Unreal client) |
| **IDL** | `docs/clients/idl/DpsTopics.idl`, `DpsTypes.idl` at the commit of this note (append-only, R-133); regenerate with `scripts/unreal/dps_gen.py` |

## 1. Terrain identity — for loading terrain from data bundles (live now)

`Session` now names the terrain the simulation has loaded; until now `Terrain` was always empty.

| Field | Meaning |
|---|---|
| `Terrain` | the resident terrain's name (HROT's terrain asset name) |
| `TerrainBaseHash` *(appended)* | the content hash of the base terrain |
| `TerrainOverlayHash` *(appended)* | the content hash of an overlay merged on top (buildings, bridges); empty = none |

A new world (`WorldEpoch` change) or a changed terrain re-sends `Session`. ⭐ Pick your bundle by name AND hashes; if you hold none that
matches, report `ClientWorld` `Failed` with a reason (e.g. `no bundle for terrain X base <hash>`) — every HROT load waits for `WaitForMe`
clients (HROT CE-11026, now built) and fails with your reason. While you load a bundle, report `Loading` with `Step` = what you do.
⚠ Known: an HROT editor replay of a recording keeps the previous terrain until HROT CE-11025.

## 2. Several players behind one gateway (DD-023, approved R-139 / R-140) — the partitions

Nothing changes for one player. With `dps.seats` N ≥ 2 the gateway writes the world once on `<base>-*` and each seat on `<base>-<n>`
(n = 1..N): configure each player's client with ITS seat's partition (`<base>-1`, `<base>-2`, …). You see the whole world plus your own
`PlayerSlot`, `OwnUnit`, `GizmoFrame`; only your `JoinRequest` / `ControlFrame` / `ClientWorld` / `ClockProbe` reach your seat. `View` and
`Session` are shared (bind your own camera to `OwnUnit`, as now). `PlayerMessage` still goes to every seat for now (to be routed per seat).

## 3. Deployment offers (appended; filled once HROT's deploy flow reaches the seats)

| Field | Meaning |
|---|---|
| `OfferKind.OfferKindArea` | a spawn area a role's man may start in (`JoinRequest.AreaId` = its `Offer.Id`) |
| `Offer.Kits` (<= 8 names) | a role's kits; `JoinRequest.Kit` picks by index |
| `Offer.Outline` (<= 32 world points) | an area offer's outline, to draw on your map |

Today's offers write `Kits` and `Outline` empty. `PlayerSlot.Reason` now carries the simulation's refusal text (cut to 64).
