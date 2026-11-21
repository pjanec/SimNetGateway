# Handoff: DPS round 7 for the Unreal client — time, steps, the camera with rates, world readiness, director rendering

| | |
|---|---|
| **From** | the gateway / DPS session, 2026-11-21 |
| **For** | the HROT `unreal-ig` session (the Unreal client) |
| **Contract** | [DD-022](../design/DD-022-Dps-Director-Rendering.md) rev 5, approved (gateway R-136 .. R-138); the field-level freeze is **§3h**, world readiness **§3g** |
| **IDL** | `docs/clients/idl/DpsTopics.idl`, `DpsTypes.idl` ⏳ **pending** — the gateway commit that adds the round-7 types (T-044, in progress) is filled in here when it lands (branch `ccr-458b1360-afzl8f`); until then this handoff is a DRAFT — regenerate with `scripts/unreal/dps_gen.py` |
| **Your answers folded in** | [NOTE-2026-11-21-Unreal-Answers-DD-022.md](NOTE-2026-11-21-Unreal-Answers-DD-022.md) — every one of Q-1 .. Q-10 is in DD-022 §8 |

⭐ Scope is frozen at the IDL commit above: documents that change after it are FYI. The contract is append-only (R-133): nothing in it is
renamed or removed later; a later round only appends.

## 1. What you can build now, against the IDL — the gateway publishes these next (no HROT change needed)

| Topic | What to build | DD-022 |
|---|---|---|
| `SimClock` + `ClockProbe` / `ClockEcho` | your clock = HROT's slave formula: `sim(now) = AnchorSimTime + (stationNow - AnchorWallTicks) / 10^7 x TimeScale` while Continuous, held at `AnchorSimTime` while Stepped; offset from the probe every second (drop RTT > 200 ms, hard-snap the first or a > 1 s change, else steer 10 %); jump on a new `TimelineEpoch`; ⭐ probe even on one machine — the station's tick is UTC at its process start + a monotonic stopwatch, not your `UtcNow` | 3b |
| `ClockStep` / `StepAck` | while Stepped, present `TargetSimTime` exactly when its `ClockStep` arrives (never extrapolate past it); normal stepping never waits for you (`AckRequired` false) | 3c |
| `View` with rates | `Velocity`, `Acceleration`, `AngularVelocity`, `FovRate` appended; Continuous now; predict it with your entity curve routine; a new `CutSeq` jumps; when bound (`TargetId`, `Mode`, `Distance`, `Side`) place the camera from the binding against your OWN prediction of the unit | 3d (your Q-3) |
| `ClientWorld` | report the world you present: `WorldEpoch` = the `Session.WorldEpoch` you are on, `Loading` while you load its terrain / level (`Step` = what you are doing), `Ready` when done, `Failed` + `Reason`; `WaitForMe` true for a client the station should wait for on every load | 3g |

`ClientWorld` matters at once for every load: once HROT's linked-client load step lands (HROT CE-11018), **every** world-changing load —
live, replay, from the start screen or the director — waits until every `WaitForMe` client is `Ready` for the new world, beside HROT's own
3-D map. Until then the gateway already reads it; nothing waits yet.

## 2. What you can build now and test against a fake publisher — HROT drives it after its linked-client seam

| Topic | What to build | DD-022 |
|---|---|---|
| `RenderClient` | announce a render client (one per station): limits, formats, `Ready`, `ReadyJobId` after the job's warm-up | 3e |
| `RenderJob` | per job: lock the camera, hide UI, warm up, then `ReadyJobId`; open `<JobId>.wav` when `Audio`; close and report at Done | 3e, 3f |
| `FrameRequest` / `FrameDone` | one step = one frame: render the world at `SimTime` through the request's exact camera, draw the overlay (subtitle + `Graphics`, world points resolved) INTO the captured image, write `File` under the render root, mix one film frame of audio, answer `FrameDone`; `FrameKindSilence` = audio only (HROT draws title cards); warm-up frames after a cut are yours, unrequested | 3e, 3f |
| `RenderJobResult` | after the track is closed: `TrackFile`, `TrackSamples` — HROT checks the total against the frames | 3f (your Q-7) |
| the barrier | in a render job, present a step once you hold `ClockStep.Samples` world samples whose `SimTime` lies in (previous target, `TargetSimTime`] | D-B4 (your Q-6) |

Timeouts the station applies: 180 s from `RenderJob` Starting to your `ReadyJobId`, 30 s per frame after that; a lost liveliness, a
`FrameStatusFailed` or a timeout fails the job with the reason.

## 3. Not in this round

- `PersonAim` as directions with rates (DD-021) — a draft, not approved; keep using today's `PersonAim` (and keep `HeadYaw`, your note §5).
- The blocked-shot X, the kill cam fields, the station keys G / H (T-041 .. T-043) — a later round.
- A live check of rounds 2 .. 6 end to end is still owed on the gateway side; report what you find against HROT `coordinator`.

## 4. How we work together

- Questions or findings: a new `docs/clients/NOTE-<date>-Unreal-<topic>.md` on the gateway branch, as you did.
- The gateway session builds §1 next (publishing `SimClock`, the steps, `View`'s rates, reading `ClientWorld`); the HROT side builds the
  linked-client seam (HROT `ui`, `docs/designs/IG/DESIGN_Linked_Client_Director.md`, CE-11018 ..) — §2 is driven once it lands.
