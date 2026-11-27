# Note: the director rerun through the Unreal render client — G-1 fixed; two more (CE-11064)

| | |
|---|---|
| **From** | the Unreal session (`unreal-ig`), 2026-11-27 |
| **For** | the gateway (fixed by the HROT-integration session, as T-085 was) |
| **Build** | gateway `aa51c94` (T-085) packaged into HROT `unreal-ig` `65ad9c520`; Unreal client 1.1 packaged; one seat, `renderRoot` set |
| **Follows** | [NOTE-2026-11-27-Unreal-Director-Film-Through-Client.md](NOTE-2026-11-27-Unreal-Director-Film-Through-Client.md) |
| **Full report** | HROT `docs/blueprints/batches/NOTE_UnrealIg_To_UiIntegration_Director_Test_Rerun.md` |

✅ **G-1 is fixed**: the reel's load inside a job reached the client 0.3 s into the job (`ClientWorld` epoch 3 Ready at once, no 30 s hang).

## G-4 ⭐ No render client is known before the first world ⇒ the first `frames: auto` job after start still goes to HROT's map

Measured: our client announced `RenderClient` at 06:46:59 (UTC), HROT's first job started at 06:47:12 with no scenario loaded yet;
HROT's `framesTo`: *"HROT's map: the linked client 'dps' cannot film now"*. After that job's load, the next job went to the client.

Reading of the code (`aa51c94`): `DpsRenderService` holds the clients, but its reader is the `DpsRenderEndpoint`, made by `DpsSeat` for a
gateway runtime — and there is no runtime before a world. So `_clients` is empty, `Pick()` null and `CanFilm` false until the first load;
T-085's `Pick() is not null` cannot help there. ⭐ Ask: read `RenderClient` for the host's life (not the runtime's), or keep the last
known clients across `Detach` so `CanFilm` holds while no world is loaded.

## G-5 ⭐ A frame in flight across the writer rebuild is lost ⇒ "no FrameDone for frame N within 30 s"

Measured (the reel filmed, `transition: card`):

| UTC | the client saw |
|---|---|
| 06:49:35.220 | `RenderJob` Starting, ready after 0.0 s warm-up |
| 06:49:35.271 | `RenderJob` Running |
| 06:49:35.484 | the job's instance went away (the rebuild) — held |
| 06:49:35.773 | `RenderJob` Running again, the job goes on; `ClientWorld` epoch 3 Ready |
| — | ⛔ no `FrameRequest` after this. The client had presented **2 frames, both silence** (the card) |
| 06:50:05.428 | `RenderJob` Failed: *"capture failed: no FrameDone for frame 1 within 30 s"* |

In `Advance()` a frame is dequeued into `job.Outstanding` and written once. `Attach()` rewrites the job and calls `Advance()`, which sends
only frames still queued (`Outstanding: null` is its condition). So when frame 1's `FrameRequest` went out on the old writer, or its
`FrameDone` came back while the old endpoint's reader was being disposed, nothing sends or reads it again and the job waits out its
30 s. (Which of the two happened cannot be told from here: `ddsmonitor` recorded nothing.)

⭐ Ask: on `Attach` during a job, write `Outstanding`'s `FrameRequest` again on the new writer. The client will answer a repeated
(JobId, index) from the frame it already presented, without rendering it again — we add that on our side once you confirm this is
the re-send.
