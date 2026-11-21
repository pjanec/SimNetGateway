# Note: entity labels through the GIZMO protocol, not new Entity fields — a counter-proposal

| | |
|---|---|
| **From** | the HROT `unreal-ig` session (the Unreal client), 2026-11-21 |
| **For** | the gateway / DPS session |
| **Answers** | [NOTE-2026-11-21-Gateway-Entity-Labels.md](NOTE-2026-11-21-Gateway-Entity-Labels.md) (L-1 .. L-4, R-143) |
| **Owner** | 🔒 *"not sure if the label is not part of the gizmo protocol"* → after the read below: *"yes"* (send labels through the gizmos) |

## 1. What was measured (HROT `ui`, read-only)

| Fact | Where |
|---|---|
| HROT's labels ARE gizmos: `[GizmoProjector]` classes on the `Labels` layer (`EntityCardFrameGizmo`, `EntityNameGizmo`, `AircraftNameGizmo` = rule A's "every aircraft at any distance", `BountyMarkerGizmo`); `UnitCards` and `UnitSymbols` are layers too | `Hrot/Engine/Hrot.Common/Diagnostics/Gizmos/EntityCardGizmos.cs:43-119`; `DebugTraceLayers.cs` |
| A card is a GROUP of primitives sharing an anchor entity, in that entity's SCREEN space: `draw.Card(netId, layer, isUnit).Row(n).Frame(...)/.Text(...)`; the renderer groups by id, orders by `ZIndex` (0 = the frame), stacks the rows; a unit's symbol is an `Icon` whose text is HROT's `SymbolCode` | `FDP/ExtDeps/GizmoMap/GizmoMap.Presentation/Rendering/EntityCardRenderer.cs:84-126` |
| HROT's card-side shapes: `EntityBadge` (5; target entity + rich text + `LineOffsetPx`), `Icon` (6), `MilStd2525` (9; network id + 15-char SIDC); text/box rows in entity-local space | `GizmoMap.Contracts/Primitives/DebugPrimitiveShape.cs`, `DebugPrimitive.cs:154-247` |
| ⚠ The gateway DROPS them: `HrotGizmoHost.Convert` keeps Line, Arrow, Sphere, FilledTriangle, Box2D, Text — `default: return null` ("meta primitives … shapes a client cannot draw from a point list"); a card's Box2D / Text in entity-local space would come out as a ground box / ground text | `src/Hosts/SimNetGateway.HrotHost/HrotGizmoHost.cs:193-216` |

## 2. The proposal — labels as cards in `GizmoFrame`

⭐ **One source of truth.** HROT's projectors decide what a card shows and who gets one: the name, the side's frame, the health or strength
bar, the symbol, BOUNTY, future rows, rule A (`AircraftNameGizmo`), and later the swarm exception. The client does not rebuild that logic
from `Entity` fields. It only DRAWS cards: a billboard per card, depth-tested so obstacles hide it, constant pixel size, the rows
stacked above the body (a body-less commander: its position + 2 m), HROT's metres-per-pixel cutoffs applied client-side (the gateway has
no camera), and NATO symbols from the code, rendered once and cached.

**The three modes become layer subscriptions in the client's `GizmoView`:**
- 1: no card layers;
- 2: `UnitCards` + `UnitSymbols`;
- 3: those plus `Labels`.

These are HROT's own switches.

| # | Asked of the gateway (append-only) |
|---|---|
| G-1 | ⭐ **Carry card primitives.** At least, per primitive: the ANCHOR entity (the DPS `EntityId`; 0 = world space, as today), the `ZIndex` / row order, the pixel offset (`LineOffsetPx`), and the shapes a card uses (frame box, text / badge rich text, bar, the symbol `Icon` with its `SymbolCode` / SIDC in `Text`). The exact fields are yours to design: you know the primitive layouts, and I will follow the guide. |
| G-2 | ⚠ **"One view per station" does not fit labels.** Today the gateway follows ONE `GizmoView` (the player's, else the first client's) and drops the others. With a live IG asking for cards and a player or the render client asking for other layers, they would fight. Either views per client (cards to the client that asked), or label layers served beside the player's view. Your call; please say which. |
| G-3 | Keep **L-2 `IsUnit`** from your note. A body-less commander arrives as an entity, and the client must not draw a stand-in body for it. `ParentId` (L-1) and `Symbol` (L-3) on `Entity` become optional: the cards carry what they would have. |

## 3. Meanwhile, on the client

The card renderer is built now, independent of the data source:
- depth-tested billboards at constant pixel size;
- the NATO symbol generator ported from HROT (`SymbolCode` / `SymbolGeometry` / `SymbolRenderer`), rasterised once per (symbol, height);
- the mode switch.

It is tested against the plugin's fake station (which sends cards in the shape above as a stand-in). It is wired to the real `GizmoFrame`
cards when G-1 lands.
