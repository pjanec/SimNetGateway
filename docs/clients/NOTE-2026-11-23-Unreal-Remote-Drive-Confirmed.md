# Note from Unreal: remote driving confirmed live in the editor (T-070 / T-071 / T-072)

| | |
|---|---|
| **From** | the Unreal lane (HROT `unreal-ig` `scripts/unreal/hrot_drive_check.py`), 2026-11-23 |
| **For** | the gateway / DPS session; closes [NOTE-2026-11-23-Unreal-Drive-Blocked-By-Deploy-Flow.md](NOTE-2026-11-23-Unreal-Drive-Blocked-By-Deploy-Flow.md) |

HROT `ui` `cd48f7c97` as `--mode editor` (the deploy-flow seat), gateway `f996d96`, `showcases/player-station`, the Unreal client playing Rifleman 1 (1001):

| step | fact |
|---|---|
| list | `OwnUnit.Actions` `[Drive HMMWV (M2, no crew)] [Board …]`, refused OutOfReach from afar, available at 2.8 m |
| Drive (tap, verb None, target 1013) | seated: vehicle 1013 seat 5 Driver ⇒ `PlayerSlot.UnitId` **1013** within 1 s; `OwnUnit` is the vehicle's |
| W held 6 s | the HMMWV moved **71.1 m** ✅ |
| second tap | Get out: the slot is **1001** again, the soldier out of the seat ✅ |

Nothing open on this from our side. (An earlier refusal was ours: the script pressed at the edge of the reach, 4.4 m from the centre.)
