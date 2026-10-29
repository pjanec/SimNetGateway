# DD-009: The HLA Side of the HROT Gateway and the Shared Publication Logic

| | |
|---|---|
| **Status** | approved: P-90 .. P-94 are R-114 .. R-118; built (T-024, stage HLA-H), as-built notes in section 10 |
| **Tasks** | T-024 |
| **Rulings honoured** | R-003, R-045, R-047, R-068, R-069, R-079 .. R-113 (esp. R-094 configuration file, R-100 .. R-109 HLA side) |
| **Inputs** | DD-006 section 14 (as-built H2a / H2b: `DisPublication` with dead-reckoned send policy), DD-008 sections 3.4 - 3.6 and 12 - 12b (HLA transport, fake RTI, Convoy Ambush over HLA), `Gateway.NedRpr/Services/RprPublication.cs` (asynchronous registration None -> Pending -> Bound) |
| **Date** | 2026-10-29 |

<!--STATUS
state: LIVE
updated: 2026-10-29
current-answer: whole document
stale-below: none
known-rot: section 4 (class diagram) shows the sketch; section 10 has the as-built API (stateless policy with a caller-owned SentModel, RegistrationAnswer, rpr.entityId) - section 10 wins
related-designs:
  - docs/design/DD-006-Gateway-Hrot-Dis.md - the HROT gateway's pivot and DIS side; this design adds its HLA side
  - docs/design/DD-007-Hrot-Dis-Pack.md - the HROT pack that hosts both sides; its config section `hla` (3a) is consumed here
  - docs/design/DD-008-Hla-Rpr-Side-And-Fake-Rti.md - the HLA transport, `Protocol.Rpr` contract and fake RTI both gateways use
  - docs/design/DD-001-Gateway-Engine-PoC.md - NED-RPR gateway whose RPR publication is refactored onto the shared helper
-->

## 1. Context and goals

The HROT gateway's configuration has an `hla` section (R-094), and R-109 said the HROT gateway gets the HLA side
"by sharing the RPR modules". Measured on the code (`wc`, file reads, 2026-10-29):

| Existing | Where | What is protocol-neutral in it |
|---|---|---|
| `DisPublication` (455 lines) | `Gateway.HrotDis/Services` | the **dead-reckoned send policy**: last-sent model (position, velocity, acceleration, orientation, angular velocity, algorithm), position / orientation thresholds, heartbeat, end-of-tick drift sweep, one full send per entity per tick |
| `RprPublication` (519 lines) | `Gateway.NedRpr/Services` | the **asynchronous registration** state machine None -> Pending -> Bound with tokens, coalesced pending fields, "handle bound" event; its send policy is heartbeat-only |

The RPR FOM is DIS-aligned (same frames, DR numbering, thresholds and 5 s heartbeat practice), so the HROT gateway's
RPR side needs **the same send policy as its DIS side plus RPR's asynchronous registration**. Copying either piece
would create the second implementation the user's rule forbids. Goal: extract both pieces once, use them in both
gateways, and add the HROT gateway's RPR modules on top.

Out of scope: a real RTI (DD-008 HLA-4); HLA in networked HROT nodes (DD-007 section 6).

## 2. Block diagram

```mermaid
flowchart LR
    subgraph Common["Common (vocabulary-free)"]
        SP["DeadReckonedSendPolicy<br/>last-sent model, thresholds,<br/>heartbeat, drift sweep"]
        AR["AsyncRegistration&lt;THandle&gt;<br/>None / Pending / Bound,<br/>tokens, coalescing"]
    end
    subgraph HD["Gateway.HrotDis"]
        DP["DisPublication (refactored)"]
        RP["RprPublication (new, HROT pivot)"]
        RM["Rpr modules: ObjectUpdate / Removed /<br/>Registered / UpdateRequested ingress,<br/>Entity egress, Fire / Detonation both ways"]
    end
    subgraph NR["Gateway.NedRpr"]
        NP["RprPublication (refactored onto AR)"]
    end
    DP --> SP
    RP --> SP
    RP --> AR
    NP --> AR
    RM --> RP
    HD -->|"Protocol.Rpr messages"| T["Transport.HlaRpr + RTI (fake now)"]
```

Caption - what the picture shows that prose hid: each concept has one box. The send policy is used by two sides of
one gateway, the registration machine by two gateways; nothing RPR- or DIS-specific is in `Common`.

## 3. Decisions

1. **`DeadReckonedSendPolicy` in Common**, extracted from `DisPublication`: per-entity last-sent DR model in pivot
   frames (ECEF, geocentric Euler), configured by the attribute ids it reads and a `SendPolicySettings` (position
   threshold, orientation threshold, heartbeat); answers `NeedsSend(entity, now)` (drift or heartbeat), records
   `Sent(entity, model, now)`, and offers the end-of-tick sweep. It uses Common's `PredictorRegistry` (R-060).
   Rejected: keep it inside `DisPublication` and call it from the RPR side - a gateway service owned by the DIS
   side would make the RPR side depend on DIS being enabled.
2. **`AsyncRegistration<THandle>` in Common**, extracted from NedRpr's `RprPublication`: None -> Pending -> Bound,
   token allocation, coalescing of what arrives while pending, bound event, release on removal. Protocol-free:
   the caller sends the register message and feeds the answer.
3. **HROT gateway RPR side** (`Gateway.HrotDis/Rpr`, one small module per message kind, R-003), mirroring the DIS
   side's semantics on `Protocol.Rpr` messages:

   | Message | Module | Pivot effect |
   |---|---|---|
   | `RprObjectUpdate` (in) | `RprObjectUpdateIngress` | create/update entity of origin `rpr`, bound to `rpr.handle`; fields map 1:1 (pivot is RPR-aligned); `StateTime` = engine clock |
   | `RprObjectRemoved` (in) | `RprObjectRemovedIngress` | destroy |
   | `RprObjectRegistered` (in) | `RprObjectRegisteredIngress` | bind handle, flush coalesced update |
   | `RprUpdateRequested` (in) | `RprUpdateRequestedResponder` | force a full update |
   | `RprWeaponFire` / `RprMunitionDetonation` (in) | `RprFireIngress` / `RprDetonationIngress` | pivot events (HROT pack injects them, R-097) |
   | entity of origin `hrot` (out) | `RprEntityEgress` via `RprPublication` | register (with entity type) then full updates when the send policy says so |
   | pivot events of origin `hrot` (out) | `RprFireEgress` / `RprDetonationEgress` | interactions; replay rule from `shared.sendEventsInReplay` |
4. **Three origins, one rule**: an entity's origin is `hrot`, `dis` or `rpr`. Only `hrot` entities are published to
   DIS and HLA; `dis` and `rpr` entities are both shown in HROT as replicas (`ReplicaState`); `dis` entities are not
   forwarded to HLA nor `rpr` to DIS (P-92). Rejected: a three-way bridge DIS <-> HLA through the gateway - a
   DIS/HLA bridge is a product of its own (Disco, Pitch), and relaying would need relayed-origin handling (R-061).
5. **Settings from the config file**: `hla.heartbeatS`, `hla.timeoutS` plus new `hla.positionThresholdM` /
   `hla.orientationThresholdDeg` (defaults 1 m / 3 deg, like DIS); stale `rpr` entities reaped after `hla.timeoutS`
   (Common `StaleEntityReaper`, R-068).
6. **NedRpr behaviour unchanged**: its `RprPublication` keeps heartbeat-only sending; only the registration machine
   moves to Common. Proven by the HLA-3 golden sequences (NED 46 messages, display SHA-256, RPR 160 messages).

## 4. Class diagram

```mermaid
classDiagram
    direction LR
    class DeadReckonedSendPolicy {
        <<Common>>
        +DeadReckonedSendPolicy(SendPolicyAttributes, SendPolicySettings, PredictorRegistry)
        +NeedsSend(IEntityView, double now) bool
        +Sent(EntityId, in SentModel, double now)
        +Forget(EntityId)
        +SweepDue(double now) IEnumerable~EntityId~
    }
    class SendPolicySettings {
        <<record>>
        +PositionThresholdM
        +OrientationThresholdRad
        +HeartbeatS
    }
    class AsyncRegistration~THandle~ {
        <<Common>>
        +Request(EntityId) long token
        +OnRegistered(long token, THandle handle) EntityId?
        +StatusOf(EntityId) RegistrationStatus
        +Release(EntityId)
    }
    class DisPublication {
        <<Gateway.HrotDis, existing>>
    }
    class RprPublicationHrot["RprPublication (Gateway.HrotDis)"] {
        +Mark(EntityId, RprGroups)
        +Flush()
    }
    class RprPublicationNed["RprPublication (Gateway.NedRpr, existing)"]
    DisPublication --> DeadReckonedSendPolicy
    RprPublicationHrot --> DeadReckonedSendPolicy
    RprPublicationHrot --> AsyncRegistration~THandle~
    RprPublicationNed --> AsyncRegistration~THandle~
    DeadReckonedSendPolicy ..> SendPolicySettings
```

## 5. Sequence - a HROT entity reaches DIS and HLA

```mermaid
sequenceDiagram
    participant P as HROT pack
    participant I as HrotEntityStateIngress
    participant D as DisPublication
    participant R as RprPublication (HROT)
    participant S as DeadReckonedSendPolicy (one per side)
    participant T as Transport.HlaRpr
    P->>I: HrotEntityState (new entity)
    I->>D: Mark(spatial, identity)
    I->>R: Mark(spatial, identity)
    D->>S: NeedsSend? (DIS instance) yes, first send
    D-->>P: ESPDU out (DIS transport)
    R->>R: AsyncRegistration.Request -> RprRegisterObject(token, type)
    T-->>R: RprObjectRegistered(token, handle) next pump
    R->>S: NeedsSend? (HLA instance) yes, first send
    R-->>T: full RprObjectUpdate
    Note over D,R: later ticks: each side asks its own policy instance, end-of-tick sweeps send drift and heartbeat updates
```

## 6. Module relationships

```mermaid
flowchart TD
    CFG["simnetgateway.json"] -->|"dis section present"| DISM["DIS modules + DisPublication"]
    CFG -->|"hla section present"| RPRM["RPR modules + RprPublication (HROT)"]
    PROF["HrotDisProfile"] --> DISM
    PROF --> RPRM
    PACK["HROT pack system (each frame)"] -->|"Pump DIS transport, Pump HLA transport, engine Tick"| PROF
```

Caption: both sides are registered by the one profile from the one file; the pack's frame calls one engine tick
with both transports pumped first (DD-008 section 6 order).

## 7. Edge cases

- Only HLA enabled: DIS modules absent; the send policy instance exists for HLA alone.
- An `rpr` entity and a `dis` entity for the same real vehicle (a federation that also bridges DIS): both shown in
  HROT as two replicas - the gateway cannot know they are the same; documented limit.
- Registration answer after the entity was removed: `AsyncRegistration` returns null and the transport's object
  is deleted.

## 8. Test plan

- Common: `DeadReckonedSendPolicy` property tests (send iff drift beyond threshold or heartbeat due; never twice in a
  tick) against a brute-force oracle; `AsyncRegistration` state machine model-checked over random sequences.
- Gateway.HrotDis: the H2b invariant suite unchanged and green after the refactor (DIS behaviour identical); RPR
  side: every row of 3.3 both ways, origin rule of 3.4 (no `dis` -> HLA, no `rpr` -> DIS, no echo), threshold
  invariant per side, end to end with `Transport.HlaRpr` + fake RTI + `ScriptedRprFederate`.
- Gateway.NedRpr: all suites and the HLA-3 goldens identical.

## 9. Proposals awaiting approval

| ID | Proposal (lean) | Main alternative |
|---|---|---|
| **P-90** (approved, R-114) | Extract **`DeadReckonedSendPolicy`** into Common from `DisPublication`; DIS and RPR sides of the HROT gateway each use an instance. | Leave it in `DisPublication` and let the RPR side call it (RPR then depends on DIS being enabled). |
| **P-91** (approved, R-115) | Extract **`AsyncRegistration<THandle>`** into Common from NedRpr's `RprPublication`; both gateways use it; NedRpr behaviour proven unchanged by the HLA-3 goldens. | Copy the state machine into the HROT gateway (two implementations). |
| **P-92** (approved, R-116) | **Origin rule**: only `hrot` entities and events go to DIS and HLA; `dis` and `rpr` both become HROT replicas; no DIS <-> HLA relaying. | Relay DIS <-> HLA through the gateway (a bridge product of its own; relayed-origin handling). |
| **P-93** (approved, R-117) | **HROT gateway RPR modules** of 3.3 in `Gateway.HrotDis/Rpr`, configured by the `hla` section with new keys `positionThresholdM` / `orientationThresholdDeg`, stale reaping after `hla.timeoutS`. | Reuse NedRpr's modules directly (they are bound to the NED pivot). |
| **P-94** (approved, R-118) | **Stage** `HLA-H` (T-024): extraction + refactor first (both gateways green, goldens identical), then the HROT RPR side; the HROT pack (H5) composes the HLA transport when `hla` is present, with `rti: "fake"` the only value until HLA-4. | HROT pack DIS-only until a real RTI exists. |

## 10. As-built notes (T-024, stage HLA-H)

Built in three parts (commits "T-024: HLA-H part A / B / C"). What differs from sections 3 and 4, and why:

| # | Design said | As built | Why |
|---|---|---|---|
| 1 | `DeadReckonedSendPolicy` owns a per-entity last-sent model: `NeedsSend`, `Sent`, `Forget`, `SweepDue`; `SendPolicyAttributes` | **Stateless** policy over a caller-owned `SentModel` struct: `Drifted`, `HeartbeatDue`, `NeedsSend`, `Held`, `Predict`, `TryRead`, `Exceeds`; settings `SendPolicySettings` (`FromDegrees`); the attribute ids are the existing Common `DrAttributeSet` (no second record of the same seven ids). `SentModel.Of(...)` builds the model. The sweep over the entities stays in each publication | The model lives in the publication's `EntityTable`, so it dies with the entity (a policy-side dictionary needs `Forget` calls and a second lifetime), and the DIS sweep order stays exactly the table's, which is what keeps DIS behaviour identical. `DisKinematics` is gone (its functions are the policy's); `DrWire` (the position / velocity / orientation a full update carries, body-axis velocity rule) and `Publishable` (readiness and the unit rule) are shared by both publications |
| 2 | `AsyncRegistration<THandle>`: `Request` -> token, `OnRegistered` -> `EntityId?`, `StatusOf`, `Release` | `Request` returns the token, or **0 when the entity is already Pending or Bound** (the caller coalesces); `OnRegistered(token, handle, out entity, isAlive)` returns `RegistrationAnswer` (`Unknown`, `Released`, `Accepted`) - an answer for a released or dead entity is queued as an **orphan**, a second answer to a token is `Unknown`; **two-phase**: `Accepted` queues an arrival and the entity stays Pending until `Complete(entity, handle)` after the identity binding committed (as NedRpr always did); `TryGetHandle`, `TryGetToken`, `Arrivals`, `Orphans`, counts | The NedRpr machine confirms Bound only after the binding transaction committed and distinguishes "unknown token" (warning) from "entity gone" (delete the object again); the sketch could express neither. NedRpr's `RprPublicationState` lost `Status` and `Token` (they are `Registration.StatusOf` / `TryGetToken`); its inspector string changed accordingly |
| 3 | Identity: `rpr.handle` | `rpr.handle` (`IdentityMap<RprObjectHandle>`, owner rpr) **and `rpr.entityId`** (`IdentityMap<DisEntityKey>`, owner rpr, own number pool with quarantine under `shared.site` / `shared.application`, `DisNumbering` of the profile) for the `EntityIdentifier` of the objects published to HLA | An RPR object needs an entity identifier (NedRpr has `rpr.entityId` too). Not shared with `dis.entity` so that the DIS maps stay absent without the DIS side (an H2b rail) and the two networks stay independent: the same HROT entity can have different numbers on DIS and on HLA. `HrotDisMaps.HasRpr`, `HrotDisSources.Rpr` (`SourceId.None` without the HLA side) and `IsReplicaOrigin` are new |
| 4 | Modules `RprEntityEgress` "via RprPublication", heartbeat | `RprEntityEgress` (rules Identity, Spatial, Damage, Flush, Registered, Reconcile, Remove, **Sweep**); no separate heartbeat module: the end-of-tick **Sweep** sends when `NeedsSend` (drift or heartbeat), at most one update per entity per tick. Ingress: `RprObjectUpdateIngress`, `RprObjectRemovedIngress`, `RprObjectRegisteredIngress`, `RprUpdateRequestedResponder`, `RprFireIngress`, `RprDetonationIngress`; egress `RprFireEgress`, `RprDetonationEgress` | one decision point for the two triggers; DIS has two modules only because H2b was built before the policy was shared |
| 5 | Fire / detonation outbound "interactions" | An event naming an entity whose registration is pending or that has no handle is sent with a **null handle** ("no object"); there is **no R-047 hold** (NedRpr's `RprEventHold` is bound to the NED pivot). The event guid is derived from the DIS event id the HROT ingress assigned, so a fire and its detonation share it | HROT entities register when their first state arrives, long before they fire; a hold can be added when a measured case needs it |
| 6 | Profile registers RPR modules when the hla side runs | Also: the `rpr` port (`IRprMessage`), the reaper `RprStaleReaper` (source `proc.reaper.rpr`, `hla.timeoutS`, same check period rule as DIS). **`Gateway.HrotDis` references `Protocol.Rpr`** (a gateway references the protocols it connects, DD-003 rule 4; `LayerRules` needed no change). Consequence: **`Protocol.Rpr` joins the vendored set** (`scripts/vendor-dry-run.sh`, CLAUDE.md, DD-007 section 2: seven projects; BCL-only, 0 warnings under the hostile parent) | the vendored gateway composes the HLA side; the HLA transport and the RTI stay out |
| 7 | Config `hla.positionThresholdM` / `orientationThresholdDeg` | As designed (defaults 1.0 / 3.0, `min: 0`, own keys, never read from the dis section); DD-007 3a updated | |

Behaviour decisions the design left open (want a ruling if the user disagrees): inbound `StateTime` is the engine clock at receipt (`ctx.ReceivedAt`, as for DIS); an inbound
`DamagePercent` is ignored (the pivot has damage levels only); a `RprAggregateEntity` and a `RprSimulationControl` have no handler in this gateway (no unit/aggregate model,
HLA carries no simulation control, R-107) and change nothing; the object class an HLA object is registered as follows from the entity type at registration, a later type change
is sent as an attribute but does not re-register; with `dis` absent the `dis.entity` map and DIS ports do not exist (H2b rails unchanged), with `hla` absent the `rpr.*` maps,
the port and the reaper source do not exist.

Existing H2b tests touched (expectations changed only because the HLA side now exists): two `ProfileTests` cases that used `Hla = {...}` merely as "another
side" now see the `rpr` port (ports `["hrot", "rpr"]`); `ConfigurationTests` counts the two new example keys (30 -> 32 leaves). The H2a `InvariantChecker` treats origins dis **and**
rpr as replicas; the harness gained the in-memory `rpr` port and a fake RTI that answers registrations one tick later (`AutoRegister`, `DeliverRti`).

Tests: Common 165 -> 175 (`DeadReckonedSendPolicyTests` 6: 600 seeded sequences against an oracle with own FPW arithmetic and own angle measure, one send per instant;
`AsyncRegistrationTests` 4: model-checked over 300 seeded random request / answer / complete / release sequences); NedRpr 601 -> 601 (goldens unchanged); HrotDis 692 -> 743
(`RprSideTests` 35: every row of 3.3 both ways, origin rule, registration, thresholds per side, reaper, composition; `RprRandomizedTests` 6 x 1500 operations with BOTH sides, a
silent RTI, echoes, model of the rpr entities; `HlaEndToEndTests` 7: HROT entity at the federate and as an ESPDU, federate entity as a replica, fire / detonation both ways,
removal and late answer, update request, the federate's view within the thresholds over 1500 ticks; `ConfigurationTests` +3: one test and two invalid-key rows). `RprInvariantChecker` runs after every step and at
every send (origin rule, tokens, full update against the oracle, threshold and heartbeat per side, no redundant send, one removal per object, numbers, liveness).
Mutations (each red, then restored): policy ignores orientation; heartbeat never due; registration answer after release binds anyway (two guards removed); Request does not
coalesce; rpr entity forwarded to DIS (both guards); dis entity forwarded to HLA (both guards); rpr reaper by any-write recency; HLA thresholds and heartbeat read from the dis
section; the sent model is not recorded (a send every tick); an update of an hrot entity's handle is not an echo. Not a behaviour change, so not detectable: dropping the
send in `CompleteRegistrations` (the end-of-tick sweep sends the first update in the same tick).
