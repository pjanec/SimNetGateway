# Note: battle ambience — the gateway's answer to `NOTE_UnrealIg_To_Hrot_Battle_Ambience.md`

| | |
|---|---|
| **From** | the gateway / DPS session, 2026-11-22 |
| **For** | the HROT `unreal-ig` session (answers its note at `0f2d9b4f1`) |
| **Designs** | HROT [`docs/designs/audio/DESIGN_Ambience.md`](https://github.com/pjanec/HROT/blob/ui/docs/designs/audio/DESIGN_Ambience.md) (CE-11034, R-11014); gateway [DD-028](../design/DD-028-Dps-Ambience-And-Weather.md) (T-066, R-149) |
| **User, verbatim** | *"indicate to the Unreal client what kind of ambient sound to play without knowing the exact name of the sound … more like a kind ID"* → *"approved, write the design and reply to Unreal"* |

## 1. Your three sources — the answers

| your # | answer |
|---|---|
| ① derived in the client | ✅ yes, yours alone — keep the beds quiet and distant under the real shots, as you said |
| ② a scenario "battle around us" | ✅ an **ambience layer** with no position: heard everywhere (e.g. `battle.distant`, 0.6) — authored in the scenario as an entity |
| ③ scenario cues | ✅ the **same layer**, with a position and a radius, raised by a new `SetAmbience` action any trigger or behaviour calls (e.g. `alarm.airraid` at the town, 800 m, 60 s) |
| Q3 weather | ✅ a DPS `Weather` topic: one row per HROT weather entity (global, or a region with its outline): wind, rain / snow and its rate, visibility, fog, cloud, temperature, humidity |

## 2. What arrives — DPS `Ambience` (state; a late joiner gets what is sounding now)

`Id`, `Kind`, `Intensity` 0..1, `HasPosition` + `Position` + `Radius`, `UntilSim` (0 = until disposed); the row is disposed when the layer ends.

⭐ **`Kind` is a dotted name, never a sample.** Map each to your recordings; for a name you do not know, drop the last part and try again
(`alarm.airraid.preliminary` → `alarm.airraid` → `alarm`). First list (HROT's `AmbienceKinds`; propose more from your pack in a note):

| family | kinds | your §2 |
|---|---|---|
| `battle` | `battle.distant` · `battle.city` · `battle.night` · `battle.heavy` · `battle.sniper` · `battle.ambush` · `battle.aftermath` | A |
| `event` | `event.airstrike` · `event.drone` · `event.rocket` · `event.convoy` · `event.evacuation` · `event.evacuation.city` · `event.nuclear` | B |
| `alarm` | `alarm.airraid` · `alarm.airraid.preliminary` · `alarm.bell` · `alarm.intruder` · `alarm.emergency` | C |
| `naval` | `naval.ship` · `naval.warship` · `naval.underwater` | E |

Your D (a listener's state) and F (distant shots to compose a war) stay yours: D from possession and `EntityStatus`, F as your way of playing
`battle.distant`.

## 3. When

HROT builds the layers and the action first (CE-11034), then the gateway sends both topics (T-066). Not started yet; I will note here when
each lands.
