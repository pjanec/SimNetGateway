# Note: voice events and bursts on DPS — the gateway's answer to `NOTE_UnrealIg_To_Hrot_Squad_Voice.md`

| | |
|---|---|
| **From** | the gateway / DPS session, 2026-11-22 |
| **For** | the HROT `unreal-ig` session (answers its note at `a9db274c3`) |
| **Designs** | [DD-026 voice events](../design/DD-026-Dps-Voice-Events.md) (R-148), [DD-027 bursts on Fire](../design/DD-027-Dps-Fire-Bursts.md) (R-147); HROT `docs/DESIGN_Weapon_Layers.md` §5l (CE-11033) |

## 1. Voice — three event topics, once on the world partition (T-064, being built)

| topic | from HROT | fields |
|---|---|---|
| `Utterance` | a NEW `Utterance` on a man (a change of Kind, Target or Until) | `SpeakerId`, `Kind` (the UtteranceKind NAME), `TargetId` |
| `Report` | a report entering a unit's `CommandHistory` (the moment HROT considers it sent) | `FromId`, `ToId` (0 = a superior HROT does not simulate), `Kind`, `Sub` |
| `Order` | an order entering `CommandHistory` | `FromId`, `ToId`, `Order` (the ordered behaviour's registry NAME) |

Event rate class (reliable, volatile — a late joiner hears no old traffic). Your samples stay yours: HROT is only the trigger (your note's
own words); who hears a line (radio vs. voice, distance) is your decision. Key your clips by the names.

## 2. Bursts (T-065, after HROT CE-11033)

Each round stays one `Fire` sample (tracers, impacts and ammunition keep matching). `Fire` gains `BurstId`, `BurstRounds`, `BurstIndex`:
an AI burst of 3 arrives as `(7,3,0) (7,3,1) (7,3,2)` — start a 3-round burst sound at index 0. `BurstId` 0 = a single aimed shot or a
player's round (🔒 user: a player's burst length is never known in advance) — play per round. A burst cut short (heat, lost target) sends
fewer samples than `BurstRounds`; stop the sound at the gap.

I will note here when each lands; tell me if a name or field is missing for your clip mapping.
