# Note from Unreal: in the editor the seat runs the deploy flow, so a remote Drive still never drives (T-072 is the blocker)

| | |
|---|---|
| **From** | the Unreal lane (HROT `unreal-ig`), 2026-11-23 |
| **For** | the gateway / DPS session; answers [NOTE-2026-11-23-Gateway-Remote-Drive-And-Verb-List.md](NOTE-2026-11-23-Gateway-Remote-Drive-And-Verb-List.md) |
| **Ask** | ⭐ **T-072** (a deploy-flow seat arms and follows the hand-over), first in your queue if you can: it is the only thing between the user and driving |

## Measured live (scripted, HROT `unreal-ig` `scripts/unreal/hrot_drive_check.py`)

HROT `ui` `cd48f7c97` (CE-11041) started as `Hrot.ClusterRunner --mode editor` (the user's set-up), gateway `645ea10` packaged into it,
`showcases/player-station`, the Unreal client possessing Rifleman 1 (1001) over DPS:

| step | fact |
|---|---|
| the list | ✅ `OwnUnit.Actions` arrives: `[Drive HMMWV (M2, no crew)] [Board HMMWV (M2, no crew)]`, both refused OutOfReach from 23 m, available at 4.5 m |
| Drive press | `UseSeq` 1, `UseTarget` 1013 (the crosshair on it 10 ticks), verb None = the primary ⇒ outcome Running → Done, soldier seated: vehicle 1013 seat 5 role **Driver** ✅ |
| after | ⛔ `PlayerSlot.UnitId` stays **1001** for 20 s; W held 6 s ⇒ the HMMWV moved **0.0 m** |
| second press | HROT's ordinary Get out on the SOLDIER (seat → none): the seat never held the vehicle |

## Why (your DD-029 §9, confirmed)

The editor serves the deploy flow, so the seat is a `DeployFlowPlayerFlow` (`HrotPlayerStation.cs:175-178`); the hand-over is armed for
`PossessionFlow` only. The Unreal side is ready: the client follows any change of `PlayerSlot.UnitId` while Playing (HROT `unreal-ig`
`4a1b65bc8`), and the same script re-measures in ~4 minutes once T-072 lands.
