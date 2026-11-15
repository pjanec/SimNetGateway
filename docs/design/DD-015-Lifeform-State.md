# DD-015: Life form state in the HROT entity message

| | |
|---|---|
| **Status** | implemented |
| **Tasks** | T-012 (parent repository) |
| **Rulings honoured** | generic names only (public repository) |
| **Date** | 2026-11-15 |

## 1. Context and goals

A protocol side that shows soldiers needs more than a position: how the soldier stands, what the weapon is doing and whether the soldier is hurt. HROT knows all
three; the entity message carried none of them. Out of scope: gait (derivable from speed), recoil and hit reactions.

## 2. Block diagram

```mermaid
flowchart LR
    ECS[HROT ECS] -->|node adapter fills| MSG[HrotEntityState]
    MSG -->|ingress| PIV[pivot attributes Lifeform.*]
    PIV --> SIDES[protocol sides]
```

The node adapter (outside this repository) reads HROT's stance, weapon posture and injury components and fills the last four fields of `HrotEntityState`; the ingress
copies them to pivot attributes; any side may read them.

## 3. Key concepts and decisions

| Field | Values | Meaning |
|---|---|---|
| `Stance` | Unknown, Standing, Crouched, Prone | Unknown for anything that has no stance (a vehicle). HROT's logical stance: an incapacitated or dead body is Prone. |
| `Weapon` | None, AtEase, Ready, Aimed | None = no weapon in hand or not a life form. |
| `AimPoint` | local metres | Same frame as `Position`; meaningful only when `Weapon` is Aimed. The adapter resolves an aim target to its current position, so a side never looks entities up. |
| `Injury` | Ok, Wounded, Incapacitated, Dead | Derived from health by HROT. |

- The fields come last in the record with defaults, so existing callers compile unchanged; defaults mean "not a life form / nothing special".
- Pivot attributes: `Lifeform.Stance`, `Lifeform.WeaponPosture`, `Lifeform.Injury` (integers) and `Lifeform.AimPoint` (ECEF vector, converted like the position, set only when the
  frame is valid). They belong to no ownership group.
- Rejected: one packed integer (unreadable in diagnostics); sending the aim target id (a side would need entity lookups).

## 4. Class diagram

`HrotStance`, `HrotWeaponPosture`, `HrotInjury` (byte enums) in `Protocol.Hrot`; four attributes in `PivotSchema`; `HrotEntityStateIngress` writes them.

## 5. Sequence diagrams

One message, one transaction: `HrotEntityState` -> ingress -> `Changes.Set` x4 (+ the aim point with the spatial block).

## 6. Edge cases and error handling

A non-finite aim point refuses the whole message like any other non-finite value. Before a valid frame the three integer attributes are set, the aim point is not.

## 7. Test plan

Ingress tests: the four fields arrive as pivot attributes (aim point in the position's frame); defaults leave Unknown / None / Ok; a NaN aim point is refused.

## 8. Proposals awaiting approval

None.

## 9. As-built notes

As designed.
