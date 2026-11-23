# Note from Unreal: with the gateway loaded, headless HROT ticks at half speed — the gateway gathers every entity every tick (measured, Stechovice)

| | |
|---|---|
| **From** | the Unreal lane (HROT `unreal-ig`), 2026-11-23 |
| **For** | the gateway / DPS session |
| **User, context** | *"headless performance is important (for running with Unreal as presentation layer later)"* |
| **Ask** | gather / ingest only what CHANGED (or what a DPS reader can see) — lean below |

## Measured (Windows, Ryzen 7 6800H; HROT `ui` `9208d62fe` Release, `--mode editor --headless`, `showcases/stechovice-battle`, time scale 1)

The scenario has 4,887 entities: about 80 soldiers and 9 vehicles think, and **~4,430 are terrain doors / building parts that never change**.

| set-up | sim ticks / s (`BrainTickSystem` executions, 60–120 s) | cores |
|---|---|---|
| HROT alone | **48.3** | 2.5 |
| + the gateway extension (`SIMNETGATEWAY_CONFIG`, DPS domain 5), no client | **20.5** (with the profiler attached) | 2.0 |
| + the gateway + the Unreal client | **25.6** | 2.3 |

**Profile** (`dotnet-trace collect --profile dotnet-sampled-thread-time`, 30 s; HROT's main thread, inclusive):

| | share |
|---|---|
| `SimNetGateway.HrotHost.DisGatewaySystem.Execute` | **23.1 %** |
| — `EcsHrotAdapter.GatherEntities` | **12.0 %** |
| — `GatewayRuntime.Tick` → `GatewayEngine.RunIngress` → `InMemoryInbound.Drain` | **10.7 %** |
| — `InMemoryInbound+Envelope<HrotEntityState>.Deliver` / `.Release` (per entity, per tick) | 6.1 % / 4.5 % |
| (for scale) `BrainTickSystem` — all 80 soldiers' brains | 6.4 % |

## Ask (lean)

⭐ The gateway pays per ENTITY per tick even when nothing moved. Lean: gather and ingest only entities whose state changed since the last gather — e.g. a change version per entity / chunk, or `Domain Other` statics (doors, building parts) once at load and then on change only — so the per-tick cost scales with what moves (~90 here), not with what exists (~4,900). If DPS needs a periodic refresh of statics, a slow round-robin slice per tick.

## How to reproduce

`SIMNETGATEWAY_CONFIG=<config with dps domain 5>`, `HROT_DEBUG_API_PORT=8111`, `Hrot.ClusterRunner.dll --mode editor --headless --domain 7`; `POST /scenario/load/live {name:"showcases/stechovice-battle"}`, `POST /sim/play`; after 30 s sample `GET /diagnostics/architecture` twice (`BrainTickSystem.executions` delta / wall s) and attach `dotnet-trace` as above. The same without `SIMNETGATEWAY_CONFIG` is the baseline.
