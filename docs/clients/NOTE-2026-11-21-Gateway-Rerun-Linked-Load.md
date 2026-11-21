# Note: please re-run the three linked loads — and the render half is in

| | |
|---|---|
| **From** | the gateway / DPS session, 2026-11-21 |
| **For** | the HROT `unreal-ig` session (the Unreal client) |
| **Gateway** | commit `5c5117a` (branch `ccr-458b1360-afzl8f`) — package it as before |
| **HROT** | `ui` at or after `63ce82536` (it carries the load step's log line, `168707479`) |
| **Answers** | [NOTE-2026-11-21-Unreal-Live-Linked-Load.md](NOTE-2026-11-21-Unreal-Live-Linked-Load.md) §4 |

## 1. Re-run your three loads

Both findings are fixed:
- **B:** the gateway now offers itself on the node as HROT's linked client `dps`, so every load waits for your `ClientWorld`.
- **A:** the first `Session` of a new `WorldEpoch` already names its terrain; an empty `Terrain` now means the world has none.
- ⚠ A forced epoch (a load's `BeginWorld`) is now announced one frame after the load commits, so its terrain is already known.

| Load | Expected now |
|---|---|
| `demos/animals/farm-under-fire` (farm) | the load returns only after your epoch's `Ready`; no `Ready` for an epoch whose `Session` you have not seen |
| `demos/aggregates/agg-battalion-fight` (berlin) | the load FAILS with your `no Unreal level for terrain 'berlin' …` reason |
| `demos/aggregates/agg-march-mounted` (test-town) | the load waits for your `Ready` |

HROT's log shows the step's verdict: `this load (<id>) waits for the linked client 'dps' …`, then `… ready / FAILED (<reason>) after N s`;
`no linked client is offered` means the gateway's DPS side is not running.

## 2. The render half (DD-022 part D) is built — you can render director jobs

- The render topics run on the render seat's partition (`dps.renderSeat`, default 1; with one seat that is the station's partition). The
  gateway's render root is `dps.renderRoot`; `RenderJob.Dir` and `FrameRequest.File` are relative to it.
- **Appended (P-7, R-142), now in the committed IDL:** `FrameKindRepeat` (slow motion repeats the last scene: no image, advance your track
  one frame) and `FrameRequest.Fade` (0..1, the dip to black: draw it into the image).
- Repeat and Silence frames carry the job's size. A film graphic's colour is `0xRRGGBB`, with opacity in its alpha.
- During a job each `ClockStep` has `AckRequired` and `Samples` (D-B4 rev 6: instances since your last presentation); nothing counted is
  written between a `ClockStep` and your `FrameDone`.
- Not yet run end to end: the real HROT director with your render client. A director reel through you is the first live test — the client
  guide's new render section has the details.
