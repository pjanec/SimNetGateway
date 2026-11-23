# Note: your seat now drives what it boards, and `OwnUnit` carries HROT's whole verb list (gateway DD-029)

| | |
|---|---|
| **From** | the gateway / DPS session, 2026-11-23 |
| **For** | the Unreal lane — answers [NOTE-2026-11-23-Unreal-Remote-Drive.md](NOTE-2026-11-23-Unreal-Remote-Drive.md) (HROT `unreal-ig`) |
| **Design** | [DD-029](../design/DD-029-Dps-Remote-Drive-And-Verb-List.md), approved R-151; HROT `DESIGN_Entity_Control.md` §8h.10 (CE-11041) |

The hand-over is HROT's, not the gateway's: HROT moved `PossessionStation`'s Drive / Get-out steps into one class, `DriveHandOver`, and the
gateway's seat owns one. You keep sending what you send today (`UseSeq`, the verb, `UseTarget`); the rest happens in HROT.

## What now flows

| You do | HROT / the gateway does | You see |
|---|---|---|
| a new press (`UseSeq` moves) whose verb is **Drive** or **Pilot** and is available | the soldier is seated (as before); then HROT's hand-over rides him and possesses the vehicle | `PlayerSlot.UnitId` becomes the **vehicle's** id (state stays Playing); `OwnUnit` is the vehicle's; the soldier stays aboard |
| control frames (W, steer ...) while the slot names the vehicle | they drive the vehicle; a **lone driver fires the vehicle's weapon** (HROT U-5, CE-1206): `OwnUnit.Ammo` / `ActiveSlot` are the vehicle's mounts | W moves the vehicle |
| a **new press (any verb) while you drive a vehicle with your soldier aboard** | = **Get out**: the seat goes back to the soldier, the press is not sent to the vehicle; the soldier's first frame carries it | `PlayerSlot.UnitId` is the soldier again |
| nothing for ~15 s after the Drive press (the seat never came) | HROT drops the hand-over | the slot stays on the soldier |
| the vehicle is destroyed or taken away | HROT lets the soldier go | the slot follows the unit the seat holds |

⚠ The hand-over takes a few frames (seated → riding → vehicle possessed, each waits for the state to come back): the slot changes a moment after the press.
⚠ Only the possession flow (a node without a deploy server) follows the vehicle so far; HROT's deploy flow (the editor) keeps its own unit (§ DD-029 9).

## `OwnUnit.Actions` (appended, `[DdsBound(8)] sequence<UseActionEntry>`)

HROT's whole offered-verb list for your `UseTarget` (what its held-F list shows), **in HROT's order** — the first entry is the primary action
(`OwnUnit.Verb` / `UseLabel` still give it, unchanged). Written with the rest of `OwnUnit`, on change only.

| `UseActionEntry` field | Meaning |
|---|---|
| `Verb` | the contract's `UseVerb` (None if HROT names a verb this contract does not) |
| `Label` (<= 32) | HROT's words, e.g. `Drive HMMWV`; you may translate by `Verb` |
| `Available` | false = offered but refused now; `Reason` says why (`UseReason`: OutOfReach, SeatTaken, Locked ...) — show it greyed with the reason |
| `HoldOnly` | shown on the hold list only, never sent by a tap (Drop) |

To use a verb: **name it** in `ControlFrame.Verb` (the enumeration; `UseVerbNone` = the primary). The gateway sends HROT that verb's POSITION in the same list
for your `UseTarget` (HROT's brain reads the verb as an index), so Drive by name now works whatever its position. A verb the list does not offer (or a name HROT
does not have) is sent past the end of the list and HROT refuses it with its own `NothingToDo` verdict in `OwnUnit.UseOutcome` / `UseReason`. The old
`ControlFrame.UseVerb` byte (used only when `Verb` is None) is a raw position in that list, 0 = the primary; it is NOT HROT's verb number (that is `OwnUnit.UseVerb`, the other direction).
The IDL gained lines only (`UseActionEntry`, `OwnUnit.Actions`); the protocol version is unchanged.
