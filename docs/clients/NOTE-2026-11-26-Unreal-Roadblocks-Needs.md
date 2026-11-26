# Note: what unreal-ig needs to show roadblocks and their clearing (answers the roadblocks note, gateway T-084)

| | |
|---|---|
| **From** | `unreal-ig`, 2026-11-26 |
| **For** | the HROT-integration session (HROT + gateway) |
| **Answers** | [NOTE_UiIntegration_To_UnrealIg_Roadblocks.md](https://github.com/pjanec/HROT/blob/unreal-ig/docs/blueprints/batches/NOTE_UiIntegration_To_UnrealIg_Roadblocks.md) |
| **Status** | ⭐ the client starts now on T-084 as sent (`079f8f2`): `Obstacle` + the entity's type + `EntityState` + dispose are enough for a first version. The asks below make it right; none blocks the start |

## What the client will do with T-084 as it is

A placed obstacle (8806 … 8824) is drawn from its `Obstacle` box (centre in plan, `Length` along the heading, base on our terrain, `Height` up) with a mesh per
type, tiled along `Length` for walls. `EntityState.Damage` going down: it sags / loses pieces. Dispose: it is removed.

## The asks

| # | ask | why | lean |
|---|---|---|---|
| **1** | ⭐⭐ **say WHY it went: breached or simply gone.** A guaranteed last `EntityState` with `Damage = Destroyed` before the dispose (the rule the `Deck` already has), or a cause on the dispose | at Health 0 `ObstacleRemovalSystem` asks for the teardown the same frame (`Fdp.Toolkits/Combat/Systems/ObstacleRemovalSystem.cs`), so it is not clear the client ever sees a destroyed state. Without it a breach (dust, scattered bags) and a scenario reset / editor delete (just vanish) look the same | the Deck rule, guaranteed (the gateway sends the destroyed state in the frame it sees Health 0, before the end) |
| **2** | ⭐ **the obstacle's kind in `Obstacle`** — an enum (Car · Sandbags · ConcreteBlock · Crate · Wreck · Barrel · JerseyBarrier · GuardBooth · WaterBarrel · SandBarrel), or confirm the TKB type ids 8806–8824 are stable and the way to tell them apart | the client otherwise keeps a list of HROT type ids; a new obstacle type would arrive as an unknown box | an enum in `Obstacle` (appendable, one field) |
| **3** | **who pushed it, from where** — the breaching vehicle's id (or the push direction) on the last state / the end | bags thrown forward of the blade, not in a random direction | optional, after 1 |
| **4** | ⭐⭐ **a demo scenario: a dozer breaches PLACED roadblocks** — a sandbag wall (stretched, e.g. 12 m), a jersey barrier and a concrete block on its path, plus one obstacle left standing; it starts running by itself | no scenario has it today: `tt-dozer-breach` cuts TERRAIN walls; `roadblock-blast` / `city-battle` have placed obstacles (types 8807 / 8813 …) but the note says it is not measured that city-battle's macros expand into them | in `demos/navigation-and-terrain/`, with `checks.json` rows (the wall's entity ends, the dozer passes) |
| **5** | **a long wall breached in part?** — today the whole obstacle goes at Health 0 | a 20 m sandbag wall vanishing whole when a dozer cuts a 4 m gap looks wrong | HROT's call; v1 whole removal is fine for us. If HROT ever cuts a gap, send it like a building breach (centre, width) |

## Also, separately (not T-084)

Terrain walls cut by a blade arrive as `BuildingDamage` `wall:<label>` (the note's §3). Our Unreal levels: to be measured on our side whether the terrain walls
are in them at all — no ask yet.

## 📌 Measured, 2026-11-26 (packaged client, gateway `079f8f2`, HROT unreal-ig `468019328`) — ask 1 is real

`cp-ram-barrier` (local copy with six more roadblocks beside the road): all ten `Obstacle` samples arrived with their sizes and were drawn. The car rammed
`Barrel 1` (1007): the entity was disposed, and the client never saw its `EntityState.Damage` leave Intact — so it vanished like a reset, not a breach.
