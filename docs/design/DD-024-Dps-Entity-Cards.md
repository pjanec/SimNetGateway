# DD-024: Entity cards (3-D labels) over `GizmoFrame`

| | |
|---|---|
| **Status** | in review (rev 1) - the route approved by the owner (the Unreal note `NOTE-2026-11-21-Unreal-Labels-Via-Gizmos.md`: *"yes"*, send labels through the gizmos); P-1 .. P-4 await approval |
| **Tasks** | T-052 (re-scoped from Entity fields to this design) |
| **Rulings honoured** | R-143 (the three modes, rule A, the swarm exception), R-133 (append-only), R-135 (producer-neutral), R-140 (seats) |
| **Related** | DD-017 3.11 (the gizmo protocol: `GizmoView`, `GizmoFrame`), DD-023 (one gizmo view per seat), HROT `docs/DESIGN_Map_3D_Mode.md` §6am (cards hidden behind terrain), HROT `docs/UX/DESIGN_Unit_Symbols.md` (the symbol in the card) |
| **Date** | 2026-11-21 |

## 1. Context and goals

HROT's 3-D labels are gizmos: `[GizmoProjector]` rules on the layers `Labels`, `UnitCards`, `UnitSymbols` build an entity CARD from rows of
primitives (`EntityCards.cs`: `Card(netId, layer).Row(n).Frame/Text/Bar/Tick/Symbol`), each primitive in `CoordinateSpace.EntityCard`,
anchored by `AnchorIndex` = the entity's network id, ordered by `ZIndex` = the row. The gateway's gizmo host drops them today
(`HrotGizmoHost.Convert`, `default: return null`). Goal: carry them, so the client DRAWS HROT's cards and decides nothing about content —
one source of truth for what a card shows and who gets one (rule A is HROT's `AircraftNameGizmo`). The three modes are the client's layer
subscription (mode 1 none; mode 2 `UnitCards` + `UnitSymbols`; mode 3 those plus `Labels`). Out of scope: a camera on the wire.

## 2. Block diagram

```mermaid
flowchart LR
    P[HROT card projectors<br/>Labels, UnitCards, UnitSymbols] -->|DebugPrimitive, space EntityCard| H[HrotGizmoHost per seat view]
    H -->|Convert: card primitives kept| F[GizmoFrame per layer, seat partition]
    F --> C[Client card renderer]
    C -->|groups by AnchorId, stacks Row, depth test, m/px cutoff| S[Screen]
    V[Client GizmoView: layers = the mode] --> H
```

What the picture shows that prose hid: the CONTENT and the "who gets a card" decision stay in HROT's projectors; the gateway only stops
dropping a primitive kind; the client only lays out and occludes.

## 3. Key decisions

- D-1 **Append to `GizmoPrimitive`, no new topic.** Fields (all appended, R-133): `Space` (new enum `GizmoSpace` { World = 0, EntityCard = 1 }),
  `AnchorId` (the DPS entity id the card belongs to; 0 in World space), `Row` (byte, 0 = card-wide: frame and symbol cell), `CardX`, `CardY`,
  `CardW`, `CardH` (floats in the card's layout units, below), `FillRgba`, `MaxMetresPerPixel` (0 = no cutoff). Rejected: a new `EntityCard`
  topic (a second gizmo channel beside `GizmoFrame`, with its own layers and views - the same concept twice).
- D-2 **Card-space shapes reuse the existing `GizmoShape` values:** `Text` (CardX = x px from the row's left, Width = font px, Text),
  `Box` (a bar or tick: CardX = centre as a fraction of the card width, CardW = half-width fraction, CardH = height px; `FillRgba` its fill),
  `Icon` (the symbol: Text = the symbol code, HROT's `SymbolCode` wire form, a 2525C SIDC with the producer's extension after `|`;
  CardH = height px; row 0 = the left cell spanning the rows), and the frame as a row-0 `Box` (fill = the background, `ColourRgba` = the
  outline). The exact mapping of each HROT row helper (`Frame`, `Bar`, `Tick`, `Text`, `Symbol`) is the build's, read from `EntityCards.cs`.
- D-3 **The distance cutoff travels with the primitive** (`MaxMetresPerPixel`, HROT's own per-rule number: card 1, name 2, aircraft name 0 =
  none). The gateway has no camera (`GizmoView` carries none), so the client applies it with its own camera:
  `m/px = 2 * d * tan(fovV / 2) / viewportHeight`. Rejected: a camera on `GizmoView` (per-frame traffic for a client-side test).
- D-4 **Views: one per seat** (DD-023, built in T-047) answers the Unreal note's G-2 - each seat's client has its own `GizmoView` and gets its
  own `GizmoFrame`s; a second display needing other layers takes a seat (seats are configured, cheap). Rejected: several views inside one
  seat (the one-player-per-seat rule would need a view table per client for one partition).
- D-5 **`Entity.IsUnit` (G-3) is kept** - a body-less commander arrives as an entity and must not get a stand-in body. `ParentId` (L-1) and
  `Symbol` (L-3) are NOT built now: the cards carry what they would have (SUPERSEDES R-143's L-1/L-3; kept as possible later appends).
- D-6 **The swarm exception (R-143) is HROT's rule** - `AircraftNameGizmo` skips an aircraft whose direct commander is an air unit; a HROT
  tracker item, not gateway logic.

## 4. Class diagram

```mermaid
classDiagram
    class GizmoPrimitive {
        <<DPS, appended fields>>
        +GizmoShape Shape
        +List~Vec3d~ Points
        +uint ColourRgba
        +float Width
        +string Text
        +GizmoSpace Space
        +int AnchorId
        +byte Row
        +float CardX
        +float CardY
        +float CardW
        +float CardH
        +uint FillRgba
        +float MaxMetresPerPixel
    }
    class GizmoSpace { <<enum, new>> World EntityCard }
    class HrotGizmoHost { <<existing>> +Convert(DebugPrimitive) GizmoPrimitiveData }
    class DebugPrimitive { <<HROT>> Space EntityCard, AnchorIndex, ZIndex, Text, Box2D, Icon }
    GizmoPrimitive --> GizmoSpace
    HrotGizmoHost ..> DebugPrimitive : reads
    HrotGizmoHost ..> GizmoPrimitive : writes, via GizmoPrimitiveData
```

## 5. Sequence diagrams

```mermaid
sequenceDiagram
    participant U as Client (seat n)
    participant G as Gateway seat n view
    participant H as HROT card projectors
    U->>G: GizmoView layers UnitCards, UnitSymbols (mode 2)
    G->>H: run the projectors of those layers
    H-->>G: card primitives (EntityCard, AnchorIndex, ZIndex)
    G-->>U: GizmoFrame UnitCards and UnitSymbols with Space EntityCard, AnchorId, Row
    U->>U: group by AnchorId, stack rows, place above the body (or +2 m), depth test, drop rows past MaxMetresPerPixel
```

## 6. Edge cases

- An anchor the client does not hold (a card for an entity not yet received) - the client skips the card until the `Entity` arrives.
- A network id not mapped to a DPS entity id - the gateway drops the primitive (counted in its diagnostics).
- 31-character card text (HROT's `CardText`) fits `Text` (64).
- A frame's primitives above 4096 (`GizmoFrame.Primitives` bound) - the gateway already splits or drops per its frame rule; cards for 8 000
  units are the stress case: measure, and say which in the as-built.

## 7. Test plan

- Card primitives of each helper (frame, bar, tick, text, symbol) arrive with Space EntityCard, the right AnchorId (the DPS id of the anchor),
  Row, card fields and MaxMetresPerPixel; World-space primitives are unchanged (existing gizmo suites green).
- Mode subscription: a view with only `UnitCards` + `UnitSymbols` gets no man's card; adding `Labels` adds them.
- An aircraft's name row carries MaxMetresPerPixel 0; a man's name 2; a card 1.
- Contract: appended fields last, IDL added lines only.
- Mutations: cards still dropped; AnchorId = the network id unmapped; Row lost; the cutoff not carried.

## 8. Proposals awaiting approval

| # | Proposal | Lean |
|---|---|---|
| P-1 | Card primitives in `GizmoFrame` by appended `GizmoPrimitive` fields (D-1, D-2) | yes - one gizmo channel |
| P-2 | The cutoff carried per primitive, applied by the client (D-3) | yes |
| P-3 | Views per seat; a second display takes a seat (D-4) | yes - built already |
| P-4 | Keep `IsUnit`; do not build `ParentId` / `Symbol` now (D-5); the swarm exception in HROT (D-6) | yes |

## 9. As-built notes

Not built.
