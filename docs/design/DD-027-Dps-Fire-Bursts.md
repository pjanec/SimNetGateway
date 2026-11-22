# DD-027: Bursts on `Fire` — which rounds the AI fired as one burst

| | |
|---|---|
| **Status** | approved rev 1 (R-147) |
| **Tasks** | T-065 (after HROT CE-11033) |
| **Rulings honoured** | R-147, R-133 (append-only), HROT R-11012 |
| **Related** | HROT `docs/DESIGN_Weapon_Layers.md` §5l (the AI's bursts and the `FireBurst` component) |
| **Date** | 2026-11-22 |

## 1. Context

🔒 User: *"I think we should send the burst information to the DPS instead of a single shot … cannot apply to player, because we never know how
long burst the player is going to do."* HROT (CE-11033) stamps each round's `WeaponFireNotification` with its burst (BurstId / BurstRounds / BurstIndex, HROT R-11013:
recordings may break); each round stays one `Fire` sample. The client may play a burst sound from the first round instead of N single ones.

## 2. Block diagram

```mermaid
flowchart LR
    N[WeaponFireNotification per round, with its burst] --> G[the existing fire gather]
    G -->|HrotFire + burst id, rounds, index| O[DPS Fire]
```

## 3. Decisions

- D-1 **Append to `Fire`:** `uint BurstId` (0 = not a planned burst — a single aimed shot or a player's round), `byte BurstRounds` (the planned
  rounds), `byte BurstIndex` (0-based, this round's place). Rejected: a separate `Burst` topic (a client would join two topics per round).
- D-2 **Copied from the round:** the notification's own burst fields (⛔ SUPERSEDED: reading a `FireBurst` component on the shooter).
- D-3 Each round is still one sample (tracers, impacts, ammunition keep agreeing with the picture).

## 4. Class diagram

```mermaid
classDiagram
    class Fire { <<DPS, appended>> +uint BurstId +byte BurstRounds +byte BurstIndex }
    class HrotFire { <<pivot, appended optional>> }
    HrotFire ..> Fire
```

## 5. Sequence

```mermaid
sequenceDiagram
    participant H as HROT (AI burst of 3)
    participant G as Gateway
    participant C as Client
    H->>G: round 1 (BurstId 7, Rounds 3, Index 0)
    G->>C: Fire (BurstId 7, Rounds 3, Index 0) - the client starts a 3-round burst sound
    H->>G: rounds 2 and 3
    G->>C: Fire (7, 3, 1) and Fire (7, 3, 2)
```

## 6. Edge cases / 7. Tests

A player's rounds have BurstId 0; a burst cut short by the heat latch or a lost target ends with fewer Fire samples than `BurstRounds` (the
client stops its sound at the next gap); index order holds. Tests: rounds of one burst share the id with indices 0..N-1; singles and player
rounds are 0; the IDL gains lines only. Mutations: index not counted; a player's round given a burst.

## 8. Proposals / 9. As built

Approved (R-147). Not built.
