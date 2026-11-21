# Note: the Unreal render client (DPS round 7 part 2): built, plus four points for the gateway and HROT

| | |
|---|---|
| **From** | the HROT `unreal-ig` session (the Unreal client), 2026-11-21 |
| **For** | the gateway / DPS session, and the HROT side's linked-client seam (CE-11018) |
| **Answers** | [HANDOFF-2026-11-21-Unreal-DPS-Round-7.md](HANDOFF-2026-11-21-Unreal-DPS-Round-7.md) §2, against the IDL at `ff6b413` |
| **Client** | HROT `unreal-ig` `b591a373f`, design row U-18q |

## 1. What the render client does

It runs in a separate Unreal process started with `-HrotDpsRender -deterministicaudio`, and optionally `-HrotDpsRenderRoot=<dir>`. It is
view-only and the station's one render client (D-7).

| Topic | The client |
|---|---|
| `RenderClient` | Re-sent every 2 s, because the QoS is volatile. Limits: 3840 x 2160, supersample 2. `Formats` 1 (PNG). `AudioFormats` 1 (PCM16) only with the non-real-time audio device. `MaxSampleRate` is the mixer's (48000). `ReadyJobId` is set after the job's warm-up (shader compile flush and streaming). |
| `RenderJob` | **Starting:** the job is checked against the limits. One that exceeds them fails before frame 0: a `RenderJobResult` with status Failed and the limit named (for example `size 8000x4000 outside 1..3840x2160`), and no `ReadyJobId`. **Done:** the track is closed and `RenderJobResult` is sent. **Failed / Cancelled:** the job is dropped. |
| `ClockStep` + barrier | A step is presented once the world samples it states have arrived (see §2), then `StepAck`. |
| `FrameRequest` | One step is one frame. The world advances one engine frame of `FilmRate / Fps` sim seconds on a fixed time step. A scene capture is taken through the request's exact camera, with `FovDeg` read as vertical. After a cut, warm-up captures are taken first. The overlay is drawn into the image: the subtitle by position and alpha, and the Ring, Arrow, Route, Area and Label graphics projected through the frame's camera. The PNG is written under the render root; `..` and absolute paths are refused. `FrameKindSilence`: no image, and silence in the track. |
| `FrameDone` | `Written` or `Failed` with the reason. `AudioSamples` is the frame's nominal share (1600 at 48 kHz / 30 fps), informational as agreed. `RenderMs` is set. |
| `RenderJobResult` | After Done: `TrackFile` = `<Dir>/<JobId>.wav` (48 kHz, PCM16, the job's channels) and `TrackSamples`. |

Measured against the client's own fake station:
- 45 frames rendered in lockstep at 640 x 360, about 40 to 50 ms each.
- 43 PNGs written, with the truck shown at its sim time and the ring, label and subtitle drawn into the image.
- The two title-card frames had no image and were exact zeros in the track.
- A step whose world sample was held back for 0.6 s was not presented until the sample arrived.
- Two shots 29 m away started 93 and 96 ms after their frames (85 ms of air).

## 2. ⭐ The barrier: the client counts samples since its last presentation (not by `SimTime` window), and why

DD-022 D-B4 says to present once `Samples` world samples **whose `SimTime` lies in (previous target, target]** have arrived. ⚠ **Not every
world sample carries `SimTime`**: `Entity`, `TkbType` and others have no such field, so the client cannot place them in a window.

The client counts differently. In lockstep, nothing of step n is written before the client has answered step n-1. So every world sample taken
after the client presents n-1 belongs to step n. That count is exact whatever the topic. In render mode, the client reads every world topic,
so none is missed.

**What the client needs from the gateway: the exact topic set `Samples` counts.** The client counts every topic except these: `SimClock`,
`ClockStep`, `ClockEcho`, `RenderJob`, `FrameRequest`, `GizmoFrame`, and the topics clients write (`JoinRequest`, `ControlFrame`, `Leave`,
`GizmoView`, `ClockProbe`, `StepAck`, `ClientWorld`, `RenderClient`, `FrameDone`, `RenderJobResult`). `View`, `Session`, `PlayerSlot`,
`OwnUnit`, `PlayerMessage`, `Status` and `Exercise` are **counted**. If the gateway counts differently, the barrier waits forever and the
station's 30 s timeout fails the frame. As a guard, the client presents anyway after 10 s and logs a warning. A dispose also counts as a
sample, since it is a write.

## 3. ⚠ The soundtrack: no stereo panning yet (an Unreal engine bug)

On Unreal's non-real-time audio device, **every spatialized sound mixes to digital zero**. Epic tracks this as
[UE-381932](https://issues.unrealengine.com/issue/UE-381932) ("spatialized audio plays correctly in the sequence, but the MRQ audio file is
silent"), still open. It was isolated by playing one sound four ways:

| How it was played | Result |
|---|---|
| 2-D | heard |
| spatialize only | silent |
| attenuate only | heard |
| attenuate with the low-pass filter | heard |

During a render job the client therefore **attenuates by distance (with the air's low-pass) but does not pan**: the soundtrack is centred.
Stereo panning needs DD-022's S-2 (a cue sheet composed after the job) or the client's own panner. That is a later item on the client's side
and changes nothing in the contract.

## 4. For HROT's audio total check

- The track's total can differ from `round(frames x SampleRate / Fps)` by up to **one mixer buffer: 1024 samples** at this project's
  settings. Measured: 71552 against 72000. Please allow at least 1024 samples.
- The client drops 2 frames of mixer latency from the head and drains 2 at the end. A voice starts in the frame that reaches its sim time
  (sound delay included), not at its sub-frame offset, so it can be up to one frame off.
- Reel item boundaries inside a job (the Q-10 fade) are not built yet. Slow motion (`FilmRate` < 1) is not tested yet.
