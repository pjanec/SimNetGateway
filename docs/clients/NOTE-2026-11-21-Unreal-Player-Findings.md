# Note: findings from the Unreal player client (HROT U-18m)

| | |
|---|---|
| **From** | the HROT `unreal-ig` session (the Unreal IG client), 2026-11-21 |
| **For** | the gateway / DPS session |
| **Client work** | HROT commit `1592ece57`, row U-18m in [DRAFT_Unreal_Image_Generator.md](https://github.com/pjanec/HROT/blob/unreal-ig/docs/designs/IG/DRAFT_Unreal_Image_Generator.md) |
| **Status** | findings and suggestions. Nothing here is built on the gateway side |

## What the client does now

The Unreal client is a full DPS player station. It handles `PlayerSlot`, `JoinRequest` (Kind Unit), `ControlFrame` (it fills in both the
old bytes and the new enums), `Leave`, `OwnUnit`, `Detonation` and `ManDowned`. It uses one random nonzero `ClientId` per station.

It was tested live against HROT `--mode editor` with `showcases/player-station`, on domain 5 and partition `ig-live`:
- The slot went Choosing -> join -> TakingControl -> Playing in about 60 ms.
- 485 control frames were sent. `Leave` returned the slot to Choosing.
- A zoomed burst took Rifleman 1's rifle from 149 to 140 rounds, and the impact landed at the crosshair.
- A second join took Rifleman 2.

The client's input follows HROT's own `FpsCameraDriver` and `EmbodiedKeys` one to one. Its HUD is built from `OwnUnit` (health,
`Ammo` lines, active slot, fire mode, spread, use prompt) and `Detonation` (the damage arcs and the hit marker).

## 1. The `Lateral` sign is wrong in the guide and in the gateway's own client (fix needed)

HROT treats **positive `Lateral` as LEFT**:

| where | what it says |
|---|---|
| HROT `Hrot/Engine/Hrot.Presentation/Map3D/EmbodiedKeys.cs:136-137` | key A on `MoveSide` gives `+1`, key D gives `-1` |
| HROT `Hrot/Engine/Hrot.Presentation/Map3D/PossessionStation.cs:2057` | `Lateral = vehicle ? 0f : side`: the station sends the axis as it is |
| HROT `FDP/Toolkits/Fdp.Toolkits/CarKinem/Controllers/ManualMotion.cs:18` | the body motion that applies it: *"lateral + = left"* |

The gateway says the opposite:

| where | what it says |
|---|---|
| `docs/clients/DPS-Client-Guide.md:62` | `Forward`, `Lateral`: *"in the BODY's frame (forward, right)"*, keys *"D/A"* |
| `src/Hosts/SimNetGateway.DpsClient/DpsClientWindow.cs:86` | `lateral = D - A`, so D sends `+1` |
| `src/Hosts/SimNetGateway.DpsClient/DpsWorld.cs:511` | doc comment *"(forward, lateral right)"* |

`DpsInbound.cs:300` and `HrotPlayerStation.cs:177` pass the value through unchanged. So the gateway's own test client should strafe the
wrong way: D moves the man left. This follows from the code above and was not watched live. The Unreal client sends A = +1, as HROT does,
and its man strafes correctly.

**Suggestion:** keep the wire as HROT has it, with + = left. Fix the guide line ("in the BODY's frame: forward, LEFT; A = +1, D = -1"),
the comment in `DpsWorld.cs` and the sign in `DpsClientWindow.cs`. Add a rail that a frame with `Lateral = +1` moves the unit to its
left. Changing the wire to + = right instead would mean a sign flip in the gateway, and HROT's own station would then disagree with
the topic.

## 2. A muzzle point in `OwnUnit` (suggestion)

Guide line 171 says the client can draw HROT's blocked-shot X *"from its own trace"*. To do that, the client needs to know where the
round really starts. Today it only knows the camera and the body pose, and in the shoulder view the camera is 2 to 5 m away from the
muzzle. HROT draws two things from the muzzle (`BlockedShot`, and the barrel circle that shows where the barrel points):
- the barrel circle, on the screen where the barrel actually points;
- the red X, where the round would stop when that is more than 0.5 m short of the crosshair point.

**Suggestion:** add `MuzzlePoint` (ENU, float) and `MuzzleDir` to `OwnUnit`, sent at the `OwnUnit` rate. A cheaper option is to have the
gateway compute the point and send only `BlockedAt` (the point where the round would stop) and `HasBlocked`. That keeps the trace on the
HROT side, so it would match HROT's own HUD exactly. I lean towards the second option, because both HUDs would then draw the same X.

## 3. Death and the kill cam (question)

The slot has `SlotStateDead`, `RespawnAtSim` and a `Reason` string. A death screen can be built from those. A kill cam ("killed by ...,
from there") needs the killer's id. `ManDowned` may carry it, but the client has not been checked against that yet.

**Question:** when our unit goes down, is `ManDowned` for our `PlayedUnit` guaranteed to arrive before the slot leaves Playing? If not,
could the slot (or `OwnUnit`'s last sample) carry `KilledBy` (unit id) and `KilledFrom` (ENU)?

## 4. The extra station keys (demand, already listed in the guide)

Guide line 83 lists what DPS does not carry yet: next squad member (J), next seat (G), head out of a hatch, lights, the command wheel (T),
select (I), the map peek (M) and binocular designation. The Unreal client has the key bindings ready and does nothing with these keys
until the contract has fields for them. The most useful first ones for the IG are **next seat (G)** and **head out (H)**. With them a
player could reach the HMMWV's gun and the motorcycle's passenger seat (the one who fires) in the showcase.

## 5. Small observations

- **Weapon names.** In the HUD, `OwnUnit.Ammo[].Weapon` showed a pistol line as "RIFLE 9x19". The label comes through
  `DpsPlayerSlot.cs:103` (`Cut(a.Weapon, 32)`). The cause was not traced: HROT's label or the gateway's mapping. Please check
  which side names the weapon.
- **The look stream (DD-021).** The client will use `PersonAim.HeadYaw` / `LookTarget` for the eyes view of a unit it follows. Today
  that view follows the body heading. Nothing is needed from the gateway, but this client is a consumer of DD-021, so please keep
  `HeadYaw` in the slimmed stream.
- **Defaults.** The gateway's DPS defaults are domain 0 and the empty partition, and the Unreal game mode follows them. Nothing to change.
- **Damage direction.** The arc is drawn from a `Detonation` on our unit: from the round's velocity, otherwise toward the shooter. In the live
  run nobody shot at the man, so only the client's unit tests cover this. A showcase step where the Red squad hits a Playable unit
  would let both clients check it live.
