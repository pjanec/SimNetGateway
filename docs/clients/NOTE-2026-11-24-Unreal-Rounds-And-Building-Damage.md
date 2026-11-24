# Note from the Unreal client: no Round ever arrives · BuildingDamage — what we will need

| | |
|---|---|
| **From** | the Unreal client (HROT `unreal-ig`), 2026-11-24 |
| **Context** | HROT `ui` `ffa9b9d2a` Release, `--mode editor --headless --domain 7`; gateway `4c2243e`; Stechovice battle |
| **HROT side** | `docs/blueprints/batches/NOTE_UnrealIg_To_Ui_Missiles_And_Building_Damage.md` (HROT repo, branch `unreal-ig`) |

## 1. Rounds — measured

| | count |
|---|---|
| rocket / missile `Fire` samples (Hydra 6025, radar SAM 6050, Javelin 6014, TOW 6008) | 23 (10-min run), 32 (play session) |
| `Round` samples of ANY munition (bullets and grenades too) | **0** |
| HROT `GET /entities` entities with `LightweightRound` during the fight (6 samples) | **0** |

**Suspect (read, not confirmed):** the entity query requires `NetworkIdentity` (`EcsHrotAdapter.cs:595`); HROT gives a round one only from
`LightweightRoundIdentitySystem`, built only when the node has network adapters (HROT `EntityCreationPack.cs:235-247`: *"a standalone host's
rounds stay local"*). On our headless editor node that may never happen ⇒ no round is ever gathered.

**Ask:** can you confirm what a round entity carries on this setup? If it has no network id, could the gateway gather `LightweightRound`
entities with an id of its own (they are short-lived, ScenarioIgnore)? HROT is asked the other side of the same question; whichever you two
prefer. Meanwhile the client draws a stand-in missile from the `Fire` sample (cancelled when a real `Round` arrives).

## 2. BuildingDamage — FYI, nothing to do yet

We will start reading `DPS_BuildingDamage` (swap a damaged building out of its merged tile, cut holes, drop glass, rubble on collapse). What we
will ask for, through HROT's design round:
- `Panel` = the panel's rank within the building, `Opening` = its index on the panel — as HROT sends it today; please keep it so.
- a way to tell a **live** change from the **state at join** (a sim time per breach / collapse), so a late joiner does not replay debris;
- optionally a breach's blast point (debris direction);
- later, a change-only door-state topic keyed `<terrain>/<label>/<doorId>` (doors are not sent since T-073).
