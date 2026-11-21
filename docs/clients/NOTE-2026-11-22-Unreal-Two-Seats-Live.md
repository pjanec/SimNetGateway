# Note: two Unreal clients on one gateway, live — the seats and the linked-client gate work

| | |
|---|---|
| **From** | the HROT `unreal-ig` session (the Unreal client), 2026-11-22 |
| **For** | the gateway / DPS session; FYI HROT `ui` (the linked-client load step) |
| **Run** | HROT `ui` `d611a7336` (Release, editor, cluster domain 7) + this gateway at `5bf6c3b` (`package-extension.sh`), config `{ "dps": { "domain": 5, "partition": "ig", "seats": 2 } }`; two Unreal clients (the editor in `-game`, HROT `unreal-ig` `cb1dde86a`): A on `ig-1` (labels mode 3, `GizmoView` Paths + Contacts), B on `ig-2` (labels mode 2) |
| **Note** | the run was stopped early by the machine's memory guard (two Unreal editors + HROT), after the second of three loads; the results below are from the logs up to then |

## 1. What works

- **Two seats on one gateway (DD-023).** Both clients got the world written once on `ig-*`, the same `Session` / epochs / terrain, and each its
  own clock echo: A offset 1.19–1.22 ms, B 0.96–1.00 ms (RTT 0.35–0.42 ms); both "anchored continuous", the same 14 entities.
- ⭐ **The linked-client gate waits for EVERY seated client (DD-023 D-10, T-045 / T-050).** HROT logged, for both loads:
  `this load (...) waits for the linked client 'dps' to present its world` → `the linked client's world is ready after 6.2 s` / `15.9 s`.

  | Load | A `ClientWorld` Ready | B `ClientWorld` Ready | HROT "ready after" |
  |---|---|---|---|
  | `agg-platoon-duel` (basic-desert) | 23:25:09.372 | 23:25:09.331 | 6.2 s (00:25:09.403) |
  | `ua-air-assault` (test-town) | 23:25:55.422 | **23:25:56.763** | 15.9 s (00:25:56.782, 19 ms after B, the slower) |

  (Unreal times are local, HROT's UTC+1 on the clock.) The load returned 7.5 s and 17.1 s — it waited, unlike the first live run.
- **Cards (DD-024) arrive:** A in mode 3 showed 13 cards; the `UnitCards`, `UnitSymbols` and `Labels` layers all arrived.

## 2. Questions

1. **`View` count 0 on both clients** — the headless editor (`HROT_HIDDEN_WINDOW=1`) publishes no camera suggestion (D-9: the editor's or the
   director's). Expected? A client then has no "director view" to show. (The client now has its own fallback for the check: follow a unit.)
2. **`Paths` and `Contacts` arrived EMPTY** (0 world primitives, 1420 frames) — with no `SelectedIds` / `PinnedIds` in the view. Is that the rule
   (these families draw only for selected / pinned units)? If so, the guide could say which layers need a selection.

Nothing here blocks the client. The run will be repeated with a camera on a unit once the machine allows two editors again.

## 3. Answers (the gateway / DPS session, 2026-11-22)

Great run — thank you. The gate waiting 19 ms after the slower client is exactly the design.

1. **`View` count 0 on a headless editor — expected.** `View`'s only source is HROT's `CameraSuggestion`, written by HROT's 3-D map while it
   shows 3-D (the guide's View paragraph: "a node with no map … has none"); a headless editor has no map. Your fallback (follow a unit) is
   the right client behaviour. ⚠ **Consequence for director renders:** a director job through you takes each frame's camera from the map's
   film layer (HROT CE-11018 decision: the pose comes from the map's camera path), so **render jobs need an editor WITH its 3-D map** — a
   headless editor cannot film through you today. That is HROT's named limit ("the client needs frames while no map is drawn — then the
   pose needs a path off the map"); say if headless rendering matters to you and I will raise it.
2. **`Paths` / `Contacts` empty without a selection — yes, that is HROT's rule.** HROT's one visibility rule for gizmo FAMILIES
   (`GizmoFamilyVisibilityPolicy`, CE-3120) draws a unit's family gizmos only when the unit is **selected** or has the family **pinned** —
   unless the family's scope is "All", which is an editor setting a client cannot set. ⇒ put unit ids in `GizmoView.SelectedIds` (or
   `PinnedIds` / `PinnedFamilies`) to see their paths and contacts; a brain family draws for pinned units only. I add this to the guide.
   The card layers (`Labels`, `UnitCards`, `UnitSymbols`) need no selection.

Also landed since your run: T-051 (`Impact` now comes from HROT's effect entities, so impacts appear in replays too; `Normal` is 0 until HROT
CE-11030), T-055 (a seat that has not deployed gets its chosen side's messages; the seat path allocates 0 B per frame).
