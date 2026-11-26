# Note: a scenario's own terrain features (rivers, authored bridges, removed houses) never reach the Unreal client

| | |
|---|---|
| **From** | `unreal-ig`, 2026-11-26 |
| **For** | the HROT-integration session (HROT + gateway) |
| **Found on** | `demos/engineering/bl-placed-bridge` (packaged client, gateway T-083): the laid deck stands on dry desert; `showcases/stechovice-battle` |

## What is missing

Our Unreal levels are built from a terrain's BASE (`assets/terrain/<name>`). A scenario's own `TerrainFeature` entities are not in them, and nothing on DPS
carries them:

| scenario | feature | in HROT | in Unreal |
|---|---|---|---|
| `bl-placed-bridge` and the other `bl-*` | kind 9 water: the river y 60–72 across basic-desert | units must cross on the deck | ⛔ dry ground; the deck looks pointless |
| `stechovice-battle` | kind 10 bridge: the 166 m polyline at (762, 1590) over the Vltava | tanks drive over it | ⛔ no bridge: they appear to drive on the water |
| `stechovice-battle` | kind 16 removal: house `w422408687` at the east ramp | gone, units drive through | ⛔ it stands; units drive through a house |

📐 Measured: `Session.TerrainOverlayHash` is EMPTY for these scenarios. They carry no compiled `terrain-overlay/` (Terrain Authoring §18: compiled at save,
git-ignored), so every node bakes the features at load. ⇒ there is no file our terrain pipeline could read, and the session says nothing changed.

## The ask

| # | ask | lean |
|---|---|---|
| **1** | ⭐⭐ **send the scenario's terrain features on DPS** — a `TerrainFeature` topic (key: the feature entity): kind, the polyline / polygon in the sim frame, width, and what HROT derived for it (a water surface height; a bridge's deck as `Deck`-style parts, ground-relative; the keys of the houses a removal takes out) | ⭐ lean: this. The client already draws what is sent: a water surface over our ground, a bridge as we draw a `Deck`, a removed house hidden by key exactly as `BuildingDamage` already does. Works for every scenario, no per-scenario package, and the same for a feature placed during play |
| **2** | alternatively: a compiled overlay for every scenario with features, its hash in `Session.TerrainOverlayHash`, so our pipeline builds a terrain variant per overlay | rejected as the first step: a package per scenario (~10–20 min each to cook), and a river carved into our landscape would still need the same data as 1 |
| **3** | the shape of a kind 9 water: does HROT lower the ground under it, or is it only a surface at a height? | needed for 1 either way |

⭐ Not blocking anything built: the deck, the bridge layer and the roadblocks work without it; they only look wrong where a scenario adds terrain.
