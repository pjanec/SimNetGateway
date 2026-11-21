# Note: entity labels in 3-D, the way HROT does them — three modes for the Unreal client

| | |
|---|---|
| **From** | the gateway / DPS session, 2026-11-21 |
| **For** | the HROT `unreal-ig` session (the Unreal client) |
| **Status** | the client's work (§2) can start now; the DPS additions (§3) are ✅ APPROVED (gateway R-143) and queued after the render half (T-050) — until they land, mode 2 and the aircraft rule cannot be told apart from what DPS sends |

## 0. What the owner wants (verbatim)

🔒 User, `2026-11-21`: *"we haven't talked about entity labels in 3d; pls check how hrot is solving those - it works well (needs to hide behind
obstacles); i would like to make them switchable; 3 modes - 1. no labels, 2. labels for commanders only (squad and above - with NATO symbol);
3. labels for any unit (soldier/car); labels if ON must stay for any individual aircraft (if not having direct commande entity) at any distance
(also in mode 2) hrot handles the NATO symbol rendering pretty well and with caching for performance"*

## 1. How HROT does it (measured in HROT `ui`, read-only)

| Aspect | HROT | Where |
|---|---|---|
| **What a label is** | a screen-space CARD per entity at constant pixel size, drawn after the 3-D pass: a frame in the side's colour, a health or strength bar, the name (`"T-72 #1043"`, or `#<id>`), optional rows (stance, actions …); a UNIT's card has its **NATO symbol in the left cell** | `FDP/ExtDeps/GizmoMap/GizmoMap.Presentation/Rendering/EntityCardRenderer.cs:121-152, :334-345`; rows `Hrot/Engine/Hrot.Common/Diagnostics/Gizmos/EntityCardGizmos.cs:43-126` |
| **Where it hangs** | above the drawn body's top (a man, a vehicle); a commander has no body, so its card hangs at its position + 2 m | `EntityBodyLayer3D.cs:2165-2178`; `RaylibGizmoSink3D.cs:158` |
| ⭐ **Hidden behind obstacles** | a **per-pixel depth test**: the card is drawn at its anchor's scene depth (written as fragment depth) against the depth buffer the 3-D pass left, depth test on, depth write off ⇒ terrain, buildings and vehicles in front hide it; **trees also hide it** (accepted); the selected entity's card is always on top | `FDP/Engine/Fdp.Presentation/Vis3D/CardOcclusion.cs:19-80`; HROT `docs/DESIGN_Map_3D_Mode.md` §6am.AB |
| **Commanders** | a squad / platoon / company commander is a **body-less entity** (`UnitEntity`) placed at the centroid of its live members; members point to their direct commander (`UnitSubordinate.Commander`); the leader is a soldier among them (`Designation = SquadLeader`). Echelon comes from the unit's type | `FDP/Engine/Fdp.Core/CommandHierarchy/UnitEntity.cs:6-21`; `FDP/Toolkits/Fdp.Toolkits/Squad/Systems/UnitCentroidSystem.cs`; `UnitSubordinate.cs` |
| **NATO symbol — only for commanders** | men and vehicles never get one; in 3-D the symbol is only the unit card's left cell | `UnitSymbolGizmo.cs:65-138`; HROT `docs/UX/DESIGN_Unit_Symbols.md` P-1, P-4 |
| ⭐ **NATO rendering + cache** | a procedural generator (not a full APP-6 library): a `SymbolCode` = affiliation, role (11 land functions), echelon (team … division), mobility, one secondary mark; its wire form is a 2525C SIDC plus HROT extensions (e.g. `SFGPUCIZ---D---\|T\|AT`, ≤ 21 chars). Each distinct symbol is RASTERISED ONCE to a texture (2× supersampled) and then drawn as one textured quad; the cache key = the symbol (bit-packed) + the pixel height (6 … 256); the per-type symbol is cached per type and the side applied per draw | `FDP/ExtDeps/GizmoMap/GizmoMap.Contracts/Symbology/SymbolCode.cs:149`; `.../Rendering/SymbolRenderer.cs:26, :120-127`; `Hrot/Engine/Hrot.Core/Tkb/UnitSymbolDeriver.cs:30-147`; HROT `docs/designs/aggregate-units/DESIGN_Stochastic_Unit_UI.md` §6 |
| **Aircraft** | an aircraft's NAME shows at every distance (no zoom limit); its frame and bar still stop like the others' | `EntityCardGizmos.cs:61-91` (`AircraftNameGizmo`, HROT CE-1971) |
| **Distance** | a hard cutoff, no fade, in metres per pixel of the camera: frame, bar, unit card, symbol ≤ 1 m/px; name ≤ 2 m/px; aircraft name: none | `EntityCardGizmos.cs:35-41`; HROT `docs/DESIGN_Map_View_Filter.md` D-3 |
| **Switches today** | layers, not modes: `Labels` (men's cards), `UnitCards`, `UnitSymbols`, `HideLabelsBehindTerrain` (on; off = always on top) | `FDP/Toolkits/Fdp.Toolkits/Diagnostics/Gizmos/DebugTraceLayers.cs` |

## 2. The three modes for the client — HROT's behaviour mapped

| Mode | Shows | HROT equivalent |
|---|---|---|
| **1. No labels** | nothing at all (rule A applies only while labels are ON) | all card layers off |
| **2. Commanders** | a card with the NATO symbol for every commander (squad and above) — plus rule A | `UnitCards` + `UnitSymbols` on, men's `Labels` off |
| **3. Every unit** | a card for every soldier and vehicle, commanders with their symbol — plus rule A | all on |

- ⭐ **Rule A (modes 2 and 3 — "labels if ON"):** an individual aircraft (one with no direct commander entity) keeps its label at ANY distance.
- **Occlusion:** test the label's anchor point against the scene depth (HROT's per-pixel test; a single line trace per label is the cheaper
  alternative but pops at edges) — terrain, buildings and vehicles in front hide it; trees may.
- **Distance:** HROT's cutoffs are in metres per pixel; at distance `d` with vertical FOV `f` and a viewport `H` pixels high,
  `m/px = 2·d·tan(f/2)/H`. Cards ≤ 1 m/px, names ≤ 2 m/px; rule A: no limit.
- **NATO symbols:** render the SIDC with your own renderer and cache a texture per (symbol, pixel height), as HROT does. For HROT's exact
  look, port `SymbolGeometry` / `SymbolRenderer` (paths above); the standard 15-character SIDC alone also renders with any 2525C library.

## 3. ⚠ What DPS must add — PROPOSED, awaiting the owner

Today `Entity` carries `TypeId`, `Domain`, `Side`, `Name`, `Dis`, `Playable` and `ParentId` — and **`ParentId` is always 0** (deferred in
DD-017). The gateway sends every entity with a pose and a type, so HROT's commanders probably already arrive as entities (they have a pose, at
their centroid) — ⚠ unconfirmed; the client cannot tell them from a body, nor know their symbol or who commands whom.

| # | Proposal (append-only, R-133) | For |
|---|---|---|
| L-1 | **fill `Entity.ParentId` = the entity's DIRECT commander's entity id** (HROT `UnitSubordinate.Commander`), 0 = none | the hierarchy; rule A (`Domain` Air and `ParentId` 0) |
| L-2 | append **`bool IsUnit`** to `Entity`: a body-less commander (HROT `UnitEntity`) — draw no body, only its label at its `EntityState` position + 2 m | mode 2's set; nothing tries to render a body |
| L-3 | append **`[MaxLength(32)] string Symbol`** to `Entity`: a MIL-STD-2525C SIDC (15 characters) with the side applied; text after a `\|` is the producer's extension (`Session.Vocabulary`) | the NATO symbol, exactly HROT's |
| L-4 | confirm (gateway) that every commander is sent with an `EntityState` at its centroid, and say so in the guide | the anchor |

## 4. Open question for the owner

⚠ **Rule A's "no direct commander entity", measured:** in HROT's air scenarios an aircraft is usually subordinate to an HQ commander
(`scenarios/demos/air/sam-site`, `ua-air-assault`), and no air-unit (flight) commander exists. Read literally, such an aircraft HAS a direct
commander and would lose the always-on label. ⭐ Lean: count only a commander whose members are aircraft (a flight) — today none exist, so
every aircraft keeps its label.

### ✅ Answered by the owner (`2026-11-21`, R-143)

🔒 *"yes every aircraft does i was thinking about a swarm of cheap drones (numerous) then it would be an overkill but for expensive aircraft
(scarse) each shoudl have a label, yes"*

⭐ **Rule A, final:** while labels are ON (modes 2 and 3), every aircraft keeps its label at any distance — **unless its direct commander is an
air unit** (a swarm of drones, a flight): then that commander's label stands for the swarm (as a squad's commander stands for its men in mode 2)
and the members' labels follow the mode. With L-1 / L-2: an entity of `Domain` Air whose `ParentId` is 0, or names a commander that is NOT an
air unit (its members are not aircraft), is labelled always; one whose `ParentId` names an air-unit `IsUnit` commander is not. The L-1 .. L-4
additions are approved.
