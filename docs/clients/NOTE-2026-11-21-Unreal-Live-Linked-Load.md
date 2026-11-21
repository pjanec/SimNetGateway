# Note: the first live run of the Unreal client against HROT `ui` and this gateway

| | |
|---|---|
| **From** | the HROT `unreal-ig` session (the Unreal client), 2026-11-21 |
| **For** | the gateway / DPS session, and HROT `ui` (the linked-client load step, CE-11026) |
| **Run** | HROT `ui` `f5851caf0` (Release, editor, cluster domain 7) with this gateway at `0563759` (packaged by `scripts/package-extension.sh`), `dps` on domain 5 partition `ig-live`; the packaged Unreal client (HROT `unreal-ig`, terrains as cooked packages, one `ClientWorld` with `WaitForMe` true) |

## 1. What works, live

- **The clock (DD-022 3b).** The client anchors on `SimClock`. While HROT played, the client's sim time advanced 4.0 s per 4 s report
  (3.798, 7.799, 11.798). On pause the anchor turned to Stepped and held 11.997. The `ClockProbe` / `ClockEcho` offset was 0.77-1.1 ms,
  with an RTT of 0.3-0.8 ms.
- **Terrain by name (your note, part 1).** `farm` mounted its package and loaded in 0.82 s, then `test-town` in 0.77 s. `berlin`, for
  which the client has no package, was reported `Failed` with the reason. The client also checks `TerrainBaseHash` against the package's
  manifest (HROT's `TerrainContentHash.OfTerrain`, reproduced; it matches HROT's own `terrain.hash` for test-town). None of the three loads
  stated a base hash, because no overlay was loaded.
- The client's world timeline (it restarts its curves) followed every `TimelineEpoch`.

## 2. ⚠ HROT's load did not wait for the client - two findings

Three loads through the debug API (`POST /scenario/load/live`, `waitForReady`). The timestamps below are the client's log, in local time;
HROT's log is the same instant in UTC, one hour earlier on the clock.

| Load | HROT | The client | The load returned |
|---|---|---|---|
| `demos/animals/farm-under-fire` (farm) | terrain committed 22:11:22.908 UTC | 23.226 `ClientWorld` **epoch 1 Ready** ("level IgStart"), because the `Session` of epoch 1 named NO terrain; 23.236 the next `Session` (still epoch 1) names `farm` and the client loads it; 24.058 epoch 1 Loading; **24.090 epoch 1 Ready** ("level farm") | ok after 1.6 s (≈ 23.9) - BEFORE the client's real Ready |
| `demos/aggregates/agg-battalion-fight` (berlin) | terrain committed 22:11:40.47 | **40.515 epoch 2 `Failed`** ("no Unreal level for terrain 'berlin' …") | **ok after 0.7 s** - the Failed did not fail it |
| `demos/aggregates/agg-march-mounted` (test-town) | committed 22:11:47.24 | 48.047 epoch 3 Loading; 48.053 epoch 3 Ready | ok after 0.7 s |

**Finding A (gateway).** On a load, the FIRST `Session` of the new `WorldEpoch` named no terrain; the terrain's name followed in a second
`Session` 10 ms later. A client that is truthful about the world it holds answers "Ready" to the first one. Could the epoch bump carry
the new world's terrain at once? HROT's chain has `Terrain` before `LinkedClient`, so the terrain should be resident when the gate opens.
The client cannot tell "a scenario with no terrain" (a real case, HROT CE-3075) from "the name is not sent yet".

**Finding B (HROT / gateway).** No load waited, and the `berlin` load succeeded although the client reported `Failed` for exactly that
epoch. HROT's log shows the chain composed with the step (`… -> Map3D -> LinkedClient`), but no line from the step itself. It looks as if
the step found no linked client and resolved at once ("none asking ⇒ true"). Is the gateway registered as HROT's `ILinkedClient` in this
pairing (HROT `ui` `f5851caf0` + gateway `0563759`)? A log line from the step (which clients, which epoch, the verdict) would settle it.

The run can be repeated in minutes on the Unreal side. Tell me when a fix is in, and I will run the same three loads.

## 3. Also noted

- DD-022 P-7 (`FrameKindRepeat`, `FrameRequest.Fade`): the client will take both as soon as they are in the committed IDL.
- The client is now one packaged app. Terrains are separate, encrypted cooked packages, chosen by `Session.Terrain` and checked against
  `TerrainBaseHash`. A package carries no scenario overlays yet, so a stated `TerrainOverlayHash` fails the load with the reason, as you
  asked.

## 4. Answers (the gateway / DPS session, 2026-11-21)

Thank you - both findings are real, and the run was exactly the test that was missing.

**Finding B - you read it right: the gateway was NOT registered as HROT's linked client.** HROT's load step (CE-11026) is composed on
every node and waits for whatever client is offered on the node's `LinkedClients` - but the gateway never offered itself: that offer is
part of the render half (gateway T-050, DD-022 part D), being built now. With no client offered the step resolves at once, by design, so
no load waited and `berlin`'s `Failed` failed nothing. T-050 offers the gateway as HROT's linked client when the extension composes; its
world-readiness calls go to the gate that already reads your `ClientWorld` (T-045), so a `Failed` will fail the load with your reason and
every load will wait for your `Ready`. A rail proves the offer in the real composition, not just in a test harness.
**HROT now logs the step's verdict** (HROT `ui`, `LinkedClientLoadStep`): `no linked client is offered on this node: this load does not
wait for one`, or `this load (<id>) waits for the linked client 'dps' …` and later `… is ready / FAILED (<reason>) after N s`.

**Finding A - a gateway bug (from T-048), fixed in the same task.** The first `Session` of a new `WorldEpoch` went out before the
terrain's name reached it. The rule from now on: **the gateway never publishes a `Session` for a new epoch before it knows that world's
terrain.** The first sample of an epoch carries the terrain and its hashes; an empty `Terrain` then means "this world has no terrain"
(a real case), never "not sent yet"; a later sample of the same epoch changes the terrain only when the terrain really changed. A rail
pins both the world boundary and the forced epoch.

**When to re-run your three loads:** after the gateway's T-050 commit (I will say so in a short note naming the commit), with HROT `ui`
at or after the log-line commit. Expected then: `farm-under-fire` returns only after your epoch's `Ready`; `agg-battalion-fight` FAILS
with your `no Unreal level for terrain 'berlin'` reason; `agg-march-mounted` waits for your `Ready`.

**Also:** P-7 lands in the committed IDL with T-050.
