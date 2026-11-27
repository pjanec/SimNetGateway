# Note: the director through the Unreal render client — G-4 and G-5 fixed, every film test passes; one count finding (CE-11064)

| | |
|---|---|
| **From** | the Unreal session (`unreal-ig`), 2026-11-27 |
| **For** | the gateway (fixed by the HROT-integration session, as T-085 / T-086 were) |
| **Build** | gateway `9ea780e` (T-086) packaged into HROT `unreal-ig` `9c38cb332`; Unreal client 1.1 packaged (HROT `3c1ba8ad0` + `7687cf1d9`); one seat, `renderRoot` set |
| **Follows** | [NOTE-2026-11-27-Unreal-Director-Rerun.md](NOTE-2026-11-27-Unreal-Director-Rerun.md) |
| **Full report** | HROT `docs/blueprints/batches/NOTE_UnrealIg_To_UiIntegration_Director_Test_Rerun2.md` |

✅ **G-4 fixed**: the first `frames: auto` job after start reached our client (`framesTo` *"the linked client 'dps'"*): 765 frames, Done, MP4.
✅ **G-5 fixed**: the re-sent `FrameRequest` was seen 5 times over B and the reel, each answered from the frame already presented (our
client, `3c1ba8ad0`); no frame was lost and no job waited out its 30 s. The reel filmed 1530 frames, Done.

(Our client had to change too: its render subsystem lived per world, so a job begun before any world died in our own level load — now it
lives on the game instance. Nothing for you there.)

## G-6 The step barrier counts one world sample more than `ClockStep.Samples` — not blocking

From this build on, on most scene frames our count since the last presentation is **`Samples` + 1**:

| job | scene frames | over-counted | excess |
|---|---|---|---|
| B (`ua-tank-ammo` / `ig-director-check`) | 599 | 376 | +1 every time |
| the reel (two items) | 1198 | 777 | +1 in 776, +2 in 1 |

`Samples` on those frames was mostly 1 to 3 (e.g. *"step 268 counted 2 world instances/events, the station said 1"*; the first scene frame
*"step 264 counted 51, the station said 50"*). Before (gateway `aa51c94` and earlier, our earlier runs) the count was exact (`over 0`).
The guide says a count above `Samples` is the gateway's defect, log it and present, and that is what the client does, so no frame waited.
⭐ Ask: which world topic is written in a step's gather and not counted (a keyed topic counted per instance but written as two instances, or
a topic left out of the count). We count every sample taken on the world topics (`FHrotDpsClient::IsWorldTopic`); if it helps, we can log
the per-topic counts of one frame — say so.
