# Note: your `EntityStatus` ask — approved and being built (gateway DD-025)

| | |
|---|---|
| **From** | the gateway / DPS session, 2026-11-22 |
| **For** | the HROT `unreal-ig` session — answers `NOTE_UnrealIg_To_Ui_Entity_Status.md` (HROT `unreal-ig` `2a8e52f1d`) |
| **Design** | [DD-025](../design/DD-025-Dps-Entity-Status.md), approved R-146 (the topic is the gateway's contract, so it is answered here) |

Agreed: you draw your own cards from data; HROT's gizmo cards stay optional (DD-024). `EntityStatus` is a keyed Discrete topic, written ONCE
on the world partition (`<base>-*`), on change only, TRANSIENT_LOCAL KEEP_LAST 1, disposed with the entity, for every entity HROT would give
a card (`Seeable.OnMap`). Every value comes from HROT's own rule — the gateway only quantises (0.01) and sends on change.

| Your field | As built | Why it differs |
|---|---|---|
| `Symbol` | `Symbol` (HROT `UnitSymbolDeriver` + side, the SIDC wire form) | — |
| `Health` 0..1 | ⭐ **`HealthCurrent` + `HealthMax`** (`HealthMax` 0 = no bar) | HROT ruling (user, 2026-09-05): *"no precalculated percentages"* — the fraction is each consumer's |
| `IsCommander` | ⭐ **already on `Entity`: `IsUnit`** (gateway T-052) | your table missed it |
| `Stress` | **`MindFill`, `MindBreak`, `MindBand`** (None / Calm / Nervous / Broken / Fear) | HROT's one mind-state rule (`MindStateBar.Of`): a soldier's stress with his breaking point, a civilian's fear |
| — | **`StrengthPresent`, `StrengthAuthorized`, `AmmoShare`** | a unit's card bars, HROT's `UnitCardFacts` |
| `Behaviour`, `Task` | ⏳ **later** | (corrected 2026-11-22) the behaviour IS readable below Presentation (`BehaviorState` + `BrainIntentReader` in `Fdp.Toolkits`); only the node's behaviour-name registry is not yet offered to a node extension (HROT `CE-11032`); then they are appended |

I will post the commit when it lands. Mode 2 = `Entity.IsUnit`; rule A (R-143) = never cut a card whose anchor is Air.

## Landed — gateway `20f2845`

`EntityStatus` is in the committed IDL (`DpsTopics.idl` + the `MindBand` enum in `DpsTypes.idl`; additions only). As built (DD-025 §9):
who gets a status is HROT's `Seeable.OnMap` (so a seated man does too); an entity that keeps existing but loses its card has its status
disposed; symbol, strength and ammunition only for a unit (an `Entity.IsUnit` entity); mind state only for a living person (`MindBand`
None ⇒ fill 0, break -1). The client guide's new "Entity status" section has the details.

## `Behaviour` and `Task` landed — gateway `00c7be1` + the follow-up after it

Appended at the end of `EntityStatus` (`string<32>` each; the IDL gained exactly these two lines): `Behaviour` = the name of the behaviour the
entity runs now (HROT's `BehaviorState` + its behaviour registry); `Task` = its ordered task (HROT `BrainIntentReader`, the ordered scope) —
during a reaction the task it will return to. "" = none. ⚠ A unit run by a mission plan, or a behaviour its SOP chose, has a `Behaviour` and an
empty `Task`. Both on change only.
