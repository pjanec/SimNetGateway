# Note: the Unreal client's half of DPS round 7 §1 — built, and one thing for the echo

| | |
|---|---|
| **From** | the HROT `unreal-ig` session (the Unreal client), 2026-11-21 |
| **For** | the gateway / DPS session |
| **Answers** | [HANDOFF-2026-11-21-Unreal-DPS-Round-7.md](HANDOFF-2026-11-21-Unreal-DPS-Round-7.md) §1, built against the IDL at `ff6b413` |

## 1. What the client does now (HROT `unreal-ig`, design row U-18p)

| Topic | The client |
|---|---|
| `SimClock` | Once one arrives, the clock is HROT's slave formula and samples no longer move it. Continuous: `AnchorSimTime + (stationNow - AnchorWallTicks) / 10^7 x TimeScale`, with `stationNow` = this machine's tick + the probe's offset. Stepped: held at `AnchorSimTime`. A re-anchor in one `TimelineEpoch` eases a difference under 0.5 s out over 0.25 s. A pause freezes at once (D-A2). A new `TimelineEpoch` jumps, and entity and view curves restart (no blend across it). Before any `SimClock`, it falls back to today's sample-fed clock. |
| `ClockProbe` / `ClockEcho` | One probe a second on a thread of its own (see §2), with HROT's constants: drop RTT > 200 ms, hard-snap the first or a > 1 s change, else steer 10 %. The client's tick is UTC read once plus the monotonic clock, the same shape as `HighResUtcClock`. |
| `ClockStep` | While Stepped, `TargetSimTime` is presented exactly. No `StepAck` yet: `AckRequired` is only set in render jobs, and the client acks those once it has the D-B4 barrier (§2 of the handoff). |
| `View` | Read and dead-reckoned like an entity. `AngularVelocity` is taken as body frame, as stated. A new `CutSeq` or a timeline jump is a hard cut. When the view is bound (`TargetId` with Orbit, Eyes, Shoulder or Chase), the client's own follow rig places the camera at the binding's distance and side, against its own prediction of the unit; the look direction comes from `View`'s orientation. `FovDeg` is read as vertical. The operator presses V to show it. |
| `ClientWorld` | `Ready` for each `Session.WorldEpoch` (the IG presents the level it was started in; a new epoch drops the entities), `Loading` before any Session, `WaitForMe` true by default. Because the QoS is volatile, it is re-sent every 2 s as well as on every change, so a gateway that starts later still gets it. |

The client's process has one `ClientId` (random, nonzero), shared by the player slot, the probe and `ClientWorld`.

## 2. ⭐ For the gateway's echo: stamp the probe when it arrives, not at the next poll

The client first stamped the echo's arrival when its per-frame poll took it. CycloneDDS's sample info has no reception time, so that stamp is
late by up to a frame (~33 ms at 30 fps), and only on one leg. NTP assumes both legs are equal, so the offset is biased by about half that:
**~16 ms of sim time at scale 1**. The client now writes the probe and waits for the echo on its own thread, stamping it when it lands.

⚠ **The same applies to the gateway's `StationReceiveTicks`.** If the gateway reads `ClockProbe` in its per-frame gather and stamps it
there, the outbound leg looks up to a frame long, and every client's offset is biased the other way. Measured on the client's own fake
station, which stamps at the poll: the offset came out 2.2 to 3 ms over a known 3 s at the test's 60 fps cap, and the clock 3 to 7 ms ahead (that bias times the scale; see §4). At 30 fps, expect about twice that. Suggestion: read `ClockProbe` on a
waitset thread, stamp `StationReceiveTicks` on arrival, and send the echo straight away (`StationTransmitTicks` at the write).

## 3. How it was tested before the gateway publishes it

- **Rails (Unreal automation tests):** `Hrot.Dps.Motion.Anchor` covers the formula, the 3 s offset from one echo, a dropped 250 ms RTT,
  the 0.25 s ease, the pause freeze, an exact step, and a seek jump. It also checks that the anchor survives a new world, and the 10 % steer
  against the > 1 s snap. `Hrot.Dps.Motion.AnchorProperty` is DD-022 §7's clock equivalence: 400 seeded play, pause, step, scale and seek
  operations, where the client equals the slave formula at every instant (1e-6 s) and jumps exactly once per epoch change.
- **End to end over real DDS:** a fake station inside the plugin (test probes on `UHrotDpsSubsystem`) runs on a private domain in Play
  mode. It answers probes as a station 3 s ahead and writes `SimClock`, `ClockStep`, `View`, `Session` and one entity; the test reads back
  the IG's `ClientWorld`. The results are in §4.

## 4. Results

HROT `unreal-ig` commit `1ca7cfe9b`, design row U-18p.

- Rails: 17 of 17 `Hrot.Dps.*` pass. The Anchor rail caught one defect on the way. A tick is about 6.4e17, and a `double` resolves only 128
  ticks (12.8 us) at that size, so the formula now subtracts the two ticks in `int64` first. ⚠ The same trap applies to any C# client that
  computes `(double)(stationNow - AnchorWallTicks)` the other way round.
- End to end, 18 of 18 checks pass:

| Check | Measured |
|---|---|
| Offset to a station 3 s ahead | 3002.2 ms, from 4 echoes, RTT 1.5 to 17 ms |
| Continuous, and after a change to scale 2 | +3.8 ms and +7.0 ms from the station's formula (the fake's poll bias) |
| Pause, then three steps | frozen exactly; each `TargetSimTime` exactly (bit-equal doubles) |
| Seek (`TimelineEpoch` 2, back to 50 s) | jumped, +3.0 ms |
| `View` unbound | 10.01 m/s against the sent 10; no jump within a cut; a new `CutSeq` lands exactly on its pose |
| `View` bound to unit 77 (Orbit, 12 m) | the client's rig follows its own curve of the unit (x 116.02 against 116.00) |
| `ClientWorld` | Loading before a Session, then Ready for epoch 1, then Ready for epoch 2 with the entities dropped |

## 5. Next on the client

The handoff's §2: `RenderClient`, `RenderJob`, `FrameRequest` / `FrameDone`, the D-B4 barrier with `StepAck`, and the soundtrack
(`RenderJobResult`). They will be tested against the same fake station first. Once the gateway publishes `SimClock`, `ClockEcho` and the
`View` rates, the client will run against the real station.

