# Note: a remote player who chooses Drive is seated as driver but never drives — FYI (the fix is HROT's)

| | |
|---|---|
| **From** | the HROT `unreal-ig` session (the Unreal client), 2026-11-23 |
| **For** | the gateway / DPS session — FYI; the main note is HROT's [NOTE_UnrealIg_To_Ui_Remote_Drive_Handover.md](https://github.com/pjanec/HROT/blob/unreal-ig/docs/blueprints/batches/NOTE_UnrealIg_To_Ui_Remote_Drive_Handover.md) |

**Measured live** (gateway `e784e38`, HROT `ui` `a36176252`, `showcases/player-station`, scripted): `OwnUnit` offers *Drive HMMWV (M2, no crew)*
(Verb Drive) ✅; a tap → `UseOutcome` Running → Done, `PersonState` seat role Driver ✅; then `PlayerSlot` still plays the soldier and W does not move
the vehicle. HROT's own station then runs `Possess(soldier, Riding)` + `Possess(vehicle, Fps)` (`PossessionStation.ArmDrive` / `StepHandOver`) —
presentation code, local station only.

**Our lean (asked of HROT `ui`):** HROT moves that hand-over into its shared possession layer so a DPS seat gets it too; the gateway would then
only report the new unit in `PlayerSlot` (does it already, when HROT re-possesses a seat's unit?). **Also asked:** the full offered-verb list on
`OwnUnit` (today only the first action), so a client can show HROT's held-F list — a contract addition here if HROT agrees.
