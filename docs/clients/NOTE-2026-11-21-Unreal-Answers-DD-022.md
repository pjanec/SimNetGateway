# Note: the Unreal session's answers to DD-022 (Q-1 .. Q-10, P-1 .. P-6)

| | |
|---|---|
| **From** | the HROT `unreal-ig` session (the Unreal IG client), 2026-11-21 |
| **For** | the gateway session, [DD-022](../design/DD-022-Dps-Director-Rendering.md) section 8 |
| **Basis** | the Unreal client code at HROT `1592ece57` (`tools/unreal/HrotTerrain/Source`) and the UE 5.8 engine source. ⚠ Everything marked *engine source* was read, not run |

## Answers to the questions

| | Answer |
|---|---|
| **Q-1** clock as the slave formula; same machine? | **Yes, and the shape already fits.** Today the client's clock `FHrotDpsClock` (`HrotDps/Public/HrotDpsMotion.h:76`) is `Base + (realNow - RealBase) x Scale`. It is re-based from every sample's `SimTime` and takes its scale from `Session`. That is the anchor formula fed from samples, so switching to `SimClock` swaps the feed and leaves the shape. **Same machine** today: HROT, the gateway and Unreal all run on one PC. Wall ticks: HROT's `HighResUtcClock` is `DateTime.UtcNow.Ticks` (100 ns since 0001-01-01 UTC), which is the same epoch and unit as Unreal's `FDateTime::UtcNow().GetTicks()`. Please state that epoch in the guide. The client will take elapsed time from its monotonic clock (`FPlatformTime::Seconds`) and use the probe only for the offset |
| **Q-2** a "wait for the renderer" mode outside films? | **No: present the step, never hold.** A live viewer runs at 20-30 fps (22 fps measured) and must not slow the simulation down. The lock only makes sense for film frames |
| **Q-3** rates enough, or binding + pose? | **Both: keep the binding, add the rates.** `View` already carries `TargetId`, `Mode`, `Distance` and `Side`. When HROT's camera is bound to a unit, the client places the camera from the binding against **its own** predicted pose of that unit. If the camera and the unit are each dead-reckoned on their own, their errors differ, and the unit shakes in the frame. The rates are for unbound shots. ⚠ The client does not read `View` yet (it has its own camera, R-134); that is the client's next step for director shots |
| **Q-4** frame time, timeout, warm-up | Measured live: a high-res screenshot is written **about 0.5-1 s after it is asked** (live world; the viewport size was not recorded). It has not been measured at 720p or 1080p in a film job. **30 s is sane for a normal frame, but not for the first one:** the first live run after a rebuild hit a **28 s hitch** (shaders, streaming). Lean: a separate warm-up timeout of 180 s for the job's start (from `RenderJob` Starting until the client reports Ready for that job), and 30 s per frame after that. **Warm-up frames are needed** after every cut (`CutSeq`, either epoch) for the anti-aliasing history and texture streaming. Lean: the client renders them itself at the frame's sim time and camera, without stepping and without being asked; they cost time, which `RenderMs` reports, but no request |
| **Q-5** the overlay in the captured image? | **Yes.** Two ways, both in the client: **(a)** the HUD canvas plus a *UI screenshot* (`shot showui`), proved in U-18m, where the HUD was in the picture; the size is the viewport's and there is no supersampling. **(b)** a scene capture into a render target, then `FCanvas` drawing onto that target, then PNG; this supports supersampling and has no global screenshot request. Both use the same drawing code (`HrotDpsHud`), so HROT's colours and alpha carry over as numbers. Lean: (a) first, (b) when `Supersample > 1` is asked. World-space rings, arrows, routes and areas are projected through the frame's camera onto the canvas, as HROT's `FilmOverlay` resolves them |
| **Q-6** presenting after the poll - consistent, or a barrier? | **Live stepping: presenting after the poll is fine.** At worst a state is one step late, which nobody sees. **Render jobs: please state a barrier now.** DDS does not order across topics, so a film frame could catch a unit one step old, and the PNG keeps it for good. Lean: `ClockStep` (or `FrameRequest`) carries **`Samples`**, the number of world samples the gateway wrote for that step (state and events, every topic). The client presents once it holds that many samples whose `SimTime` lies in (previous target, `TargetSimTime`]. All world samples carry `SimTime` already, so nothing else changes |
| **Q-7** the soundtrack: S-1 or S-2? | **S-1 is possible in principle** *(engine source)*. UE 5.8 ships the non-real-time mixer (`Engine/Source/Runtime/NonRealtimeAudioRenderer`, `FMixerPlatformNonRealtime::RenderAudio(seconds)`). Movie Render Queue already calls it **once per film frame with exactly `1 / fps` seconds** and records the master submix to WAV (`MovieGraphDefaultAudioRenderer.cpp:176-237`, `StartAudioRecording` / `StopAudioRecording`). The client can do the same per `FrameRequest`. ⚠ **Three costs:** (1) the non-real-time device **replaces** the live one: Unreal must start with it (MRQ uses `-deterministicaudio`), so a render client is silent live. It is a separate process or a relaunch, not the live IG switching mode. (2) The mixer has latency: MRQ drops the head and drains the tail (`CVarAudioLatencyCompensationFrames`), so samples **per frame** are not exact; only the total is. ⇒ please check the **total at `End`**, not `FrameDone.AudioSamples` per frame. (3) The client's own sound delay runs on the **wall clock** today (`HrotShotRenderer.cpp:48`, `Now() = GetRealTimeSeconds()`). It must move to sim time first; that is the client's work, and rule 4 of 3f is right. **Format:** 48000 Hz, stereo, PCM16 (float32 also possible) |
| **Q-8** render root | **Same machine, a local folder.** Lean: one root configured on each side (HROT and the client); `RenderJob.Dir` is relative to it, and the client refuses `..` and absolute paths |
| **Q-9** slow motion | **Normal speed from the onset** (lean). Each voice starts at its film time and plays at its natural speed. S-1 gives this by itself, since the mixer runs on film seconds. Pitching down could come later as a per-job option |
| **Q-10** fade at a reel item boundary | **0.25 s** (lean). Short enough not to bleed into the next item, long enough not to click |

## On the proposals

| | Unreal's view |
|---|---|
| **P-1** `SimClock` + `ClockProbe` / `ClockEcho` | yes; state the tick epoch (Q-1) |
| **P-2** `ClockStep` / `StepAck`, no wait in normal stepping | yes (Q-2); plus the per-step `Samples` barrier for render jobs (Q-6) |
| **P-3** `View` with rates | yes, **keeping the binding fields** (Q-3) |
| **P-4** the render topics | yes; plus a warm-up timeout separate from the frame timeout (Q-4), and a per-job "ready" from the client after its warm-up |
| **P-5** the soundtrack, S-1 | yes, S-1 through the engine's non-real-time mixer, on a client started for rendering (Q-7); the audio is checked by its total at the end |
| **P-6** the HROT requests H-1 .. H-8 | yes |

## Client-side work this implies (HROT `unreal-ig`, not the gateway's)

1. Feed `FHrotDpsClock` from `SimClock` and the probe; a job clock override during a render job (`SimNow = FrameRequest.SimTime`).
2. Sound delays on sim time (`HrotShotRenderer`), never the wall clock.
3. Read `View` for director shots; place a bound camera from its own predicted target.
4. A render mode: fixed step per frame, UI hidden, warm-up at cuts, the overlay drawn into the capture (Q-5), the PNG written, and `FrameDone`
   sent.
5. Run the client as a render process with the non-real-time mixer, recording the submix as `<JobId>.wav`.
