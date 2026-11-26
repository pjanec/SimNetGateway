# Note: the director filmed through the Unreal render client — two gateway findings (CE-11064)

| | |
|---|---|
| **From** | the Unreal session (`unreal-ig`), 2026-11-27 |
| **For** | the gateway (the HROT-integration session fixes it, per the user: *"hrot lane will be fixing the gateway according to your note"*) |
| **Build** | gateway `5587295` packaged into HROT `unreal-ig` `a55b22cf4`; Unreal client 1.1 (packaged); one seat, `renderSeat` 1, `renderRoot` set |
| **Full report** | HROT `docs/blueprints/batches/NOTE_UnrealIg_To_UiIntegration_Director_Test.md` (all tests, the HROT-side findings) |

## G-1 ⭐ A load inside a render job: the new world reaches the client only after the job ends ⇒ HROT's linked load waits 30 s and fails

Measured (take `ig-director-check` filmed with `frames: auto`; the director loads the take's scenario inside the job):

| wall (UTC) | the client saw |
|---|---|
| 22:43:06.455 | `RenderJob` Starting · 22:43:06.504 Running |
| 22:43:06.657 | the job's instance went away (see G-2) |
| 22:43:06.930 | `RenderJob` Running again (same JobId) |
| — | ⛔ no `Session` with the new `WorldEpoch` (58): the client kept presenting epoch 57 |
| 22:43:37.045 | `RenderJob` Done (HROT gave up: *"the scenario's load made no progress for 30 s … at: Editor (node 1): LinkedClient"*) |
| 22:43:37.074 | ⭐ only now `ClientWorld: epoch 58 Ready` |

The same in the reel (`films/ig-director-check.reel.json`, `transition: card`): both items failed the same way.

⇒ my reading (not verified in the gateway's code): from `RenderJob` Starting to Done the gateway writes the counted world topics only in a step's
gather (DD-022 3e, D-B4), and a load has no steps until the world is ready — but HROT waits for that readiness (`ClientWorld`, 3g) before it steps.
A deadlock broken only by HROT's 30 s stall check. ⭐ Ask: a world boundary inside a job must reach the client (at least `Session` with the new
`WorldEpoch`) without a step, so 3g can resolve.

## G-2 The job's instance goes away at a world boundary — the client now holds it (no gateway change needed, FYI)

`DpsRenderEndpoint` is rebuilt at every world boundary (its own doc comment), so the old writer's `RenderJob` instance goes NOT_ALIVE at the client
and the new writer writes the same JobId again as Running. The client ended the job on the NOT_ALIVE (*"the job's instance went away"*, after 1–3
frames). ⭐ Fixed on the client: the job is held 10 s for its JobId to come back and goes on; only the station's Done / Failed / Cancelled ends it.
If the gateway would rather keep one writer for the job's life, say so and the hold stays harmless.

## G-3 `CanFilm` is false until the client has a ready world — the first film job after start goes to HROT's map, silently

`DpsRenderService.Refresh` → `CanFilm` needs the client `Ready`; HROT's `RoutingFrameSink.Begin` reads it ONCE at the job's start. Just after start
(the client on its start level, no world yet) the first `frames: auto` job was HROT's map — 316 PNGs from HROT, none from Unreal, with only
*"the linked client returned no track"* as the hint. After one scenario load it routed to Unreal. ⭐ Ask (with HROT): either a render client that
announced itself counts as able to film, or the job says it fell back to the map.
