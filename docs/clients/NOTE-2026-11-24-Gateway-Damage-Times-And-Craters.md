# Note to the Unreal client: building damage now carries WHEN and WHERE FROM, and craters have a topic (T-074, DD-030)

| | |
|---|---|
| **From** | the gateway, 2026-11-24 |
| **Answers** | [NOTE-2026-11-24-Unreal-Rounds-And-Building-Damage.md](NOTE-2026-11-24-Unreal-Rounds-And-Building-Damage.md) section 2 (live vs join, the blast point) |
| **Design** | [DD-030](../design/DD-030-Dps-Damage-Times-And-Craters.md); HROT side: CE-11051 (UTM section 8.3a) |
| **Contract** | append-only (R-133): fields appended at the END of their structs, one new topic and one new enum; the IDL in [idl/](idl/) only gained lines. A client built on the old IDL keeps working and ignores them |

## What now flows

| topic / struct | new field | meaning |
|---|---|---|
| `BuildingDamage` | `double CollapsedAt` | sim seconds the building collapsed; 0 = not collapsed, **or collapsed in a loaded save** (then `Collapsed` is still true) |
| `Breach` (in `Breaches`) | `double MadeAt` | sim seconds the breach was made or last WIDENED; 0 = from a save |
| | `Vec3d Blast` | the blast point that made or last widened it, in the simulation frame (x east, y north, z up - the frame of `EntityState.Position`); a hull's ram or a blade = the hull's centre; **meaningless (zero) when `MadeAt` is 0**. Debris flies away from it |
| `WindowDamage` (in `Windows`) | `double HitAt` | sim seconds of the window's LAST hit (a crack or the break); 0 = already damaged when the scenario loaded |
| **`GroundDamage`** (new) | `string Cell` (key) | `"<cellX>,<cellY>"`, HROT's own 64 m cell numbers (`floor(x / 64)`, `floor(y / 64)`, negative allowed, e.g. `"-3,12"`) |
| | `List<Crater> Craters` | up to 16, **in HROT's order** (the order they were made in) |
| `Crater` | `Vec3d Centre` | x, y in the simulation frame; **z = the BARE ground** under the centre (HROT's height without any crater in it), 0 when the simulation has no terrain |
| | `float Radius, Depth, Rim` | a bowl `Depth` m deep at the centre, rising to a rim `Rim` m above the ground at `Radius`, back to the ground at 1.4 x `Radius` |
| | `double BornAt` | sim seconds the crater was made; 0 = from a save |
| | `CraterKind Kind` | `CraterKindSmall` / `CraterKindLarge`; `CraterKindUnknown` = a kind this contract does not name |

All times are `SimClock.TotalTime`, the clock `Session.SimTime` / `SimClock` carry: the gateway copies HROT's record and stamps nothing of its own.
`GroundDamage` is Discrete, Reliable, TransientLocal, KeepLast 1, written once on the world partition (never per seat), like `BuildingDamage`.

## The 0 = from-a-save rule

A time of **0** means "was already there when the scenario loaded" - its age is unknown and there is no live change to show. **Draw it, never animate it** (no dust,
no debris, no flash); a `Blast` next to a `MadeAt` of 0 is zeros, do not use it. This holds for `CollapsedAt` (with `Collapsed` true), `MadeAt`, `HitAt` and `BornAt`.

## Live or at join?

1. **Compare the stamp with the current sim time** (`SimClock`): a stamp within a frame or two of now, or newer than the last one you saw for that breach / window / crater, is
   LIVE; an older one is state at join (TransientLocal replays the current samples, with the times they happened) - draw the hole, throw no debris.
2. **A change since you last saw the instance is live; the first sample of an instance you did not have is not necessarily live.** Remember the last stamp per key: a breach
   by its geometry (`Panel`, `From`, `To`, `Bottom`, `Top` - NOT its list index, which moves when breaches merge), a window by (`Panel`, `Opening`), a crater by (`Centre`, `Radius`),
   a collapse by `Collapsed` going true or `CollapsedAt` changing.
3. A widening moves that breach's `MadeAt` and `Blast`; a second shell into the open hole changes nothing and sends nothing. A window's `HitAt` moves on every crack and on the break.

## Behaviour to rely on

- **Change-only.** An instance is re-written when anything in it changes - including a time alone - and never otherwise. A `GroundDamage` cell is sent whole (all its craters) when one is added.
- **Disposed when empty.** A cell whose craters are gone (a reload) is disposed once; so is a building whose damage is gone. A world boundary ends the writers and everything is sent again.
- **Cells past 16 craters** (HROT opens another entity for an overflowing cell): the gateway unites them into one instance sorted by birth time, drops a crater held twice, sends the first 16 and logs one warning per cell.
- **Replica cells** (another node's craters, replicated to this one) are sent like any other HROT state.
- The crater's own look is yours (R-146): HROT gives size and time, you choose the decal, the dust, the smoke.
