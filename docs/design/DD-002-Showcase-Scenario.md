# DD-002: Showcase Scenario "Convoy Ambush"

| | |
|---|---|
| **Status** | approved (R-043 .. R-047); project placement per DD-003; since stage HLA-3 (T-021) the RPR side runs over the fake RTI (DD-008 section 12b; the as-built notes below carry the changes) |
| **Tasks** | T-009 (this design), T-010 (implementation) |
| **Rulings honoured** | R-002, R-003, R-006, R-008, R-013 .. R-048 |
| **Date** | 2026-10-24 |

## 1. Purpose

The unit tests of DD-001 section 12 check each mechanism in isolation. This showcase checks that
the mechanisms **work together** when both sides are complex: different vocabularies, entity
cardinality, event/state crossings, different timing, several identity types, asynchronous
identity allocation, out-of-order and looped-back input, failures, operator reconfiguration and
the whole session lifecycle - at a small, readable scale (about 15 entities), so every outbound
message can be checked exactly.

It runs **deterministically** (`ManualClock`, 60 Hz ticks, fixed script) in two forms:

- as an **xUnit acceptance test** (T-010) that fails on the first violated success condition;
- as a **console showcase** (`SimNetGateway.Demo --showcase`) that prints the timeline of every
  act - inputs, outbound messages per side, diagnostics - and a PASS/FAIL table of the success
  conditions.

## 2. The challenge matrix

What makes each side hard, and where the showcase exercises it:

| # | Challenge | NED side (internal, DDS-like) | RPR side (external, HLA-like) | Display side (C2 map) | Act |
|---|---|---|---|---|---|
| C1 | Vocabulary, units, frames | geodetic degrees, TKB type ids, damage 0..3 | ECEF metres, radians, DIS entity types, damage enum | lat/lon, heading, label | all |
| C2 | Attribute granularity | fat descriptors (master, geo-spatial, weapon status, damage) | one object with optional attributes | one position message | 3, 4 |
| C3 | Entity cardinality 1:N | a **formation** = one descriptor carrying 3 members | one platform object per member | one marker per member | 3, 9 |
| C4 | Entity cardinality N:1, references by foreign identity | gets **one formation** message for a red squad | red squad = **AggregateEntity** object listing members by **DIS EntityIdentifier**, plus member platforms | - | 3, 9 |
| C5 | Event -> state crossing | detonation carries target damage | damage is only an attribute (`DamageState`) | - | 5 |
| C6 | State -> event crossing | requires a `NedDamageNotification` event when damage increases | reports damage only as state | - | 5 |
| C7 | Event needing earlier, different data | fire without munition = "selected weapon" from an earlier weapon-status descriptor | fire interaction needs the munition type | - | 5 |
| C8 | Event split over several messages | a fire arrives as `NedWeaponFire` + `NedFireAmmunition` (may be late or missing) | needs one complete fire interaction | - | 5 |
| C9 | Event referring to a part of a coarse entity | target is "formation 7, member 1" | target is a platform object | - | 5 |
| C10 | Different timing | updates thinned by DR (irregular) | heartbeat every 5 s, timeout 12 s | positions at 10 Hz, no DR | 4, 8, 9 |
| C11 | Identity types | `ulong` ids, member key `(formation, index)`, fire id `Guid` | RTI object handle (**asynchronous**), DIS `(site, app, number)` with quarantine | `string` label | 3, 5, 7, 9 |
| C12 | Out-of-order input | geo-spatial before master | members before their aggregate, aggregate referencing unknown members | - | 1, 3 |
| C13 | Looped-back own publications | DDS delivers our own samples back | - | - | 7 |
| C14 | Failures | out-of-range content, unknown message type | allocator exhaustion | - | 7 |
| C15 | Operator reconfiguration (override) | - | customer federation uses damage in percent: default mapping disabled, custom one enabled | - | 6 |
| C16 | Session lifecycle incl. debrief | control messages | freeze/resume interactions; no weapon effects during replay | - | 2, 8, 10, 11 |

## 3. Block diagram

```mermaid
flowchart LR
    subgraph Harness["Showcase harness"]
        Script["ScenarioScript<br/>acts: inputs + checks at sim times"]
        Runner["ScenarioRunner<br/>drives ManualClock at 60 Hz, calls Tick"]
        Inv["InvariantChecker<br/>after every tick"]
        Rep["Report<br/>timeline + PASS/FAIL"]
        Probe["ProbeModule<br/>records trigger order and state at trigger time"]
        Rogue["RogueProcessor + FaultyModule<br/>(enabled only in act 7)"]
    end
    subgraph GW["Gateway under test (NedRprProfile, built by GatewayBuilder - DD-003)"]
        Eng["GatewayEngine"]
        BM["NED modules"]
        RM["RPR modules"]
        DM["Display module"]
        PM["Processors: DR extrapolation,<br/>fire assembler, stale reaper,<br/>formation aggregator"]
    end
    subgraph Sides["Simulated sides"]
        BIn["NED inbound"]
        BOut["NED outbound recorder<br/>(+ loopback tap)"]
        RIn["RPR inbound"]
        ROut["RPR outbound recorder"]
        RTI["RPR side over HLA (HLA-3, DD-008 12b):<br/>transport + fake RTI + scripted RPR federate<br/>the handles are the RTI's, answered one tick later"]
        DOut["Display outbound recorder"]
    end
    Script --> Runner
    Runner -->|inject| BIn
    Runner -->|inject| RIn
    Runner -->|Tick| Eng
    BIn --> Eng
    RIn --> Eng
    Eng --- BM & RM & DM & PM
    BM --> BOut
    RM --> ROut
    RM <-->|register object / handle| RTI
    DM --> DOut
    BOut -.->|loopback in act 7| BIn
    Runner --> Inv
    Inv -->|reads| Eng
    Inv -->|reads| BOut & ROut & DOut
    Probe --- Eng
    Rogue --- Eng
    Runner --> Rep
```

- **ScenarioScript** is a list of acts; each act is a list of steps at sim times: inject a message
  on a side, issue an operator action (enable/disable a rule), or check a success condition.
- **ScenarioRunner** advances the `ManualClock` by 1/60 s, injects the steps that are due, calls
  `Tick()`, runs the **InvariantChecker** and evaluates the checks that are due.
- **Recorders** keep every outbound message with tick, wall time and sim time, so checks can ask
  "exactly which messages went to RPR between t1 and t2".
- **The RPR side** (since HLA-3 the message-level `SimulatedRti` is gone, R-108): the gateway's "rpr" port is the HLA
  transport on the fake RTI and the scripted RPR side is a federate on the same RTI (`ScriptedRprFederate`, DD-008 12b).
  Registration is asynchronous as RTIs are: the registration is a service call, the handle (assigned by the RTI) is
  delivered by the transport's next pump, one tick later; script handles are keys, never the RTI's numbers.
- **ProbeModule** is a passive test module: it subscribes to triggers and records their order and
  the state visible at that moment (e.g. the shooter's ammo when the fire event is dispatched).
- **RogueProcessor** (a rule that rewrites its own trigger attribute: an infinite cascade) and
  **FaultyModule** (an ingress handler that throws) exist only to prove the guards; their rules
  are enabled only in act 7.

## 4. Cast and timeline

| Who | Side | Model on that side | Pivot |
|---|---|---|---|
| Blue convoy "Viper" | NED | formation 7 with members 0, 1, 2 (one `NedFormation` descriptor) | unit + 3 member entities |
| Blue drone "Hawk" | NED | entity 21 (master + geo-spatial), moving, turns at t=40 s | 1 entity |
| Checkpoint "Gate" | NED | entity 22, static | 1 entity |
| Red tank "T-1" | RPR | platform object | 1 entity |
| Red squad "Wolf" | RPR | AggregateEntity listing 3 members by EntityIdentifier + 3 platform objects | unit + 3 member entities |
| C2 map | Display | receives plain positions | - |

```mermaid
gantt
    title Showcase timeline (sim time, seconds; act 1 runs before Load in wall time)
    dateFormat X
    axisFormat %s
    section Lifecycle
    Act 0 Setup and validation      :a0, 0, 1
    Act 1 Free play (Idle)          :a1, 1, 6
    Act 2 Load and Start            :a2, 6, 7
    section Story
    Act 3 Forces appear             :a3, 7, 20
    Act 4 Movement and timing       :a4, 20, 60
    Act 5 Engagement                :a5, 60, 75
    Act 6 Operator override         :a6, 75, 80
    Act 7 Faults and loopback       :a7, 80, 90
    Act 8 Pause and resume          :a8, 90, 100
    Act 9 Losses and timeouts       :a9, 100, 125
    section Wrap-up
    Act 10 Unload                   :a10, 125, 127
    Act 11 Debrief replay           :a11, 127, 140
```

## 5. Acts, steps and success conditions

Each success condition has an id (`S-a.n`, a = act) used by the test and the report.
"RPR got X" means the RPR outbound recorder contains X in the act's time window, and **nothing
else** unless stated.

### Act 0 - Setup and validation

Register all PoC modules, the showcase protocol extensions (section 7) and the probe.

| Id | Success condition |
|---|---|
| S-0.1 | `Validate()` reports no errors and exactly the expected warnings (none in the PoC configuration). |
| S-0.2 | Identity maps exist: `ned.entity` (`ulong`), `ned.member` (`NedMemberKey`), `ned.fire` (`Guid`), `rpr.handle` (`RprObjectHandle`), `rpr.entityId` (`DisEntityId`), `display.label` (`string`). |

### Act 1 - Free play in Idle (C12, C10)

Hawk's `NedGeoSpatial` arrives **before** its `NedEntityMaster`; Red tank T-1 appears on RPR.

| Id | Success condition |
|---|---|
| S-1.1 | After the geo-spatial only: Hawk exists in the pivot, RPR got **nothing** (type unknown), display got nothing (no label/identity yet). |
| S-1.2 | After the master: RPR registration requested at the RTI (the SimulatedRti of stage 7, the fake RTI since HLA-3); on the next tick the handle arrives and RPR got **one** full `RprObjectUpdate` for Hawk with ECEF position, geocentric orientation, velocity, DR algorithm, DIS type of Hawk's TKB type. |
| S-1.3 | NED got master + geo-spatial for T-1 with a gateway-range id; T-1 never appears on RPR outbound (echo). |
| S-1.4 | Sim time advanced with wall time (Idle runs free). |

### Act 2 - Load and Start (C16)

NED control: `LoadScenario("Ambush")`, then `Start`.

| Id | Success condition |
|---|---|
| S-2.1 | Session `Idle -> Loaded -> Running`, sim time reset to 0 at Load and frozen until Start. |
| S-2.2 | RPR got exactly one `RprSimulationControl(StartResume)`; NED got no control message (it was the source). |
| S-2.3 | Existing entities survive Load (free-play entities are kept). |

### Act 3 - Forces appear (C2, C3, C4, C11, C12)

Blue: `NedFormation(7, members 0..2)`, Gate master + geo-spatial. Red: Wolf's three member
platforms arrive **first**, then Wolf's AggregateEntity listing their EntityIdentifiers - plus one
identifier that does not exist yet (member 4, arriving 2 s later).

| Id | Success condition |
|---|---|
| S-3.1 | One formation message = **one transaction** creating 4 pivot entities (unit + 3 members) with `Structure.Parent`; the probe sees one `ChangeSetRecord` with 4 deltas. |
| S-3.2 | RPR got 3 platform objects (one per member, distinct handles, distinct DIS EntityIdentifiers) and none for the unit (no position). Updates that happened while a handle was pending were coalesced into the first update after the handle arrived. |
| S-3.3 | Display got 3 member markers + Gate, labelled `Viper-0`, `Viper-1`, `Viper-2`, `Gate`. |
| S-3.4 | Wolf members first exist without parent; when the aggregate arrives they are linked (`ReferencedBy(Structure.Parent, Wolf)` returns 3); NED got **one** `NedFormation` for Wolf with 3 members (not 3 messages). |
| S-3.5 | When member 4 arrives, NED got one updated Wolf formation with 4 members. |

### Act 4 - Movement and timing (C10, DR)

Viper moves along a road (NED sends geo-spatial only when the sender's DR threshold is exceeded:
scripted at irregular times); Hawk flies straight, **turns at t=40 s**; Gate is static and NED
re-sends its unchanged geo-spatial every 10 s.

| Id | Success condition |
|---|---|
| S-4.1 | RPR got Hawk updates only at: the turn, and heartbeats every 5 s; each heartbeat's position is within 1 m of the analytic true position at its send time (predicted to now). |
| S-4.2 | Display got Hawk and Viper members at 10 Hz with positions within 0.01 m of the analytic FPW prediction; Gate only once (static, no change). |
| S-4.3 | Gate's unchanged re-sends: the probe's `OnAttributesWritten` rule fires, every `OnAttributesChanged` rule stays silent, RPR got nothing but heartbeats. |
| S-4.4 | Received `Spatial.*` attributes of Hawk change only at NED inputs (meta `Source = NED`); predictions exist only in `Derived.*` (meta `Source = proc.dr`). |
| S-4.5 | ~~Query at t=50 s: `ChangedWithin(3 s)` returns exactly the entities the script updated in [47, 50]; `NotWrittenWithin(10 s).FromOrigin(RPR)` returns the RPR entities silent for 10 s.~~ **SUPERSEDED by R-113.** Query at t=50 s: `AttributeChangedWithin(Spatial.Position, 3 s)` returns exactly the entities the script updated in [47, 50]; `ChangedWithin(3 s)` returns those plus every entity whose `Derived.*` prediction changed (the DR processor writes moving entities at 10 Hz, R-029); `NotWrittenWithin(10 s).FromOrigin(RPR)` returns the RPR entities silent for 10 s (unchanged). |

### Act 5 - Engagement (C5 .. C9, C11)

1. NED `NedWeaponStatus(Viper member 2, selected TKB 501, ammo 30)`.
2. RPR: T-1 fires at the platform of Viper member 1.
3. NED: detonation on Viper member 1 with damage 3 (destroyed).
4. NED: Viper member 2 fires in **two parts**: `NedWeaponFire(fireId F1, target T-1, munition 0)`
   and 120 ms later `NedFireAmmunition(F1, TKB 502, quantity 2)`.
5. NED: Viper member 2 fires again (F2); the ammunition part **never arrives**.
6. RPR: T-1's `DamageState` goes 0 -> 2, later 2 -> 1 (repair).

| Id | Success condition |
|---|---|
| S-5.1 | NED got `NedWeaponFire` for T-1's shot with target **(formation 7, member 1)** - the target platform mapped through `Structure.Parent` to the coarse identity (C9). |
| S-5.2 | RPR got `RprMunitionDetonation` targeting member 1's handle **and** an `RprObjectUpdate` with `DamageState = Destroyed` for that platform; the probe saw the damage change dispatched **before** the detonation event; both share the input's correlation id (`Journal.GetWrites`). |
| S-5.3 | F1: RPR got **one** `RprWeaponFire` only after the ammunition part arrived, with DIS type of TKB 502, quantity 2, firing object = member 2's handle, target = T-1's handle; member 2's `Weapon.Ammo` = 28, visible to the probe when the event fired. |
| S-5.4 | F2: after the assembly timeout (1 s) RPR got one `RprWeaponFire` with the **selected** munition (TKB 501 from step 1) and a diagnostic warning "fire F2 completed by timeout"; the helper entity used for assembly is destroyed and its `Guid` key released. |
| S-5.5 | T-1 damage 0 -> 2: NED got a `NedEntityDamage(2)` descriptor **and** one `NedDamageNotification` event (state -> event, C6); 2 -> 1: descriptor only, **no** notification (condition on old/new value). |
| S-5.6 | No event was sent back to the side it came from. |

### Act 6 - Operator override (C15)

The customer federation uses damage in percent. The operator disables the default contributor
rule `RprEntityEgress/Damage` and enables `CustomerDamagePercent/Damage` (a separate module that
contributes `DamageState` as 0/33/66/100). Then NED reports damage 1 on Gate.

| Id | Success condition |
|---|---|
| S-6.1 | RPR got one `RprObjectUpdate` for Gate with `DamageState = 33` and no other change. |
| S-6.2 | All other attributes of RPR updates still come from the default contributors (a following Hawk heartbeat is unchanged in shape). |
| S-6.3 | `Rules` shows the default rule disabled with its fire count frozen, the custom rule enabled and counting. |

### Act 7 - Faults and loopback (C13, C14)

1. NED damage level 9 (out of range) for Gate -> the module aborts.
2. An unknown NED message type.
3. `FaultyModule` enabled: its handler for `NedIffStatus` throws.
4. `RogueProcessor` enabled for one tick: an infinite cascade.
5. Loopback tap on: every NED outbound message is fed back into NED inbound for 2 s.
6. Red reinforcements: 6 new RPR platforms appear while the **NED gateway id range** (configured
   small for the showcase) has only 4 free ids left, so 2 of the 6 cannot be published to NED.

| Id | Success condition |
|---|---|
| S-7.1 | Aborted message: repository unchanged (journal has no record for it), nothing sent, one transaction-failure diagnostic naming the module and reason. |
| S-7.2 | Unknown message type counted, nothing else happens. |
| S-7.3 | Throwing handler: transaction discarded, diagnostic with exception; the next valid message on the same channel is processed normally. |
| S-7.4 | Rogue cascade: stopped at `MaxCascadeDepth`, cascade-overflow diagnostic, no partially applied change set; all other rules ran normally in that tick. |
| S-7.5 | Loopback: every looped-back message is ignored (echo suppression, C13); repository versions unchanged; no outbound message caused by them. |
| S-7.6 | Exhaustion: NED got 4 of the 6 reinforcements, 2 allocation-failure diagnostics; after act 9 frees ids, the next update of each of the 2 publishes it (retry on next trigger). |

### Act 8 - Pause and resume (C16, C10)

| Id | Success condition |
|---|---|
| S-8.1 | Pause: RPR got `StopFreeze`; sim time frozen; display got **no** position updates during the pause (prediction unchanged = no writes). |
| S-8.2 | Heartbeats continue on wall time during the pause, with the frozen predicted position. |
| S-8.3 | Resume: RPR got `StartResume`; display updates resume within one processor period. |

### Act 9 - Losses and timeouts (C3, C4, C10, C11)

1. Viper member 1 (destroyed) is dropped from the next `NedFormation` message.
2. Wolf member 3 stops sending (no heartbeat).
3. A NED entity published to RPR is disposed and a new one is created within the DIS quarantine.

| Id | Success condition |
|---|---|
| S-9.1 | RPR got `RprObjectRemoved` for member 1's handle; display removed `Viper-1`; member 1's `ned.member` key and DIS identifier released (the identifier only after quarantine). |
| S-9.2 | 12 s after Wolf member 3's last update the reaper destroyed it; NED got **one** Wolf formation message without member 3. |
| S-9.3 | ~~The new entity gets a different DIS entity number during quarantine; after the quarantine a freed number may be reused.~~ **SUPERSEDED by R-113.** A released DIS entity number stays unavailable for the quarantine and becomes available afterwards (a reuse itself is not observable because the pool hands out never-used numbers first). The showcase also keeps the stricter check that the new entity gets a different number during the quarantine. |
| S-9.4 | The 2 reinforcements of S-7.6 are now published to NED. |

### Act 10 - Unload (C16)

| Id | Success condition |
|---|---|
| S-10.1 | RPR got `RprObjectRemoved` for every object the gateway had published there; NED got a dispose (or an empty formation) for everything it had received from the gateway; display removed every marker. |
| S-10.2 | Repository empty, every identity map empty, every allocator has all its ids back (DIS ones in quarantine), session `Idle`. |
| S-10.3 | Journal and event log still hold the recent history (inspectable after unload). |

### Act 11 - Debrief replay (C16)

`LoadDebrief("Ambush-recording")`, `Start`, a recorded NED stream replays acts 3 - 5 (subset).

| Id | Success condition |
|---|---|
| S-11.1 | Session `Loaded` with `Mode = Replay`; entities and their states reach RPR and display as in live. |
| S-11.2 | **No** `RprWeaponFire` / `RprMunitionDetonation` is sent during replay (policy: no weapon effects into the federation during a debrief); the events are still dispatched and visible in the event log. |
| S-11.3 | Unload leaves the gateway as in S-10.2. |

## 6. Global invariants (checked after every tick)

| Id | Invariant |
|---|---|
| G-1 | **No echo**: no outbound message to side S describes an entity whose origin is S or an event whose source is S. |
| G-2 | **Identity consistency**: for every map, `TryResolve(TryGetKey(id)) == id`; no key bound to two entities; no destroyed entity resolvable after its removal dispatch. |
| G-3 | **Index consistency** (test-only brute force): recency lists ordered by time, presence sets and the reverse reference index equal a full scan. |
| G-4 | **Received state is never overwritten by derived logic**: every `Spatial.*` slot's meta source is an ingress side. |
| G-5 | **Outbound references are valid**: every RPR message uses a handle that is bound and not yet removed; every NED message uses an id the gateway owns or received. |
| G-6 | **Transaction atomicity**: every journal record is complete (a formation transaction never appears half-applied to any rule - the probe never sees a member without its parent in the same correlation). |
| G-7 | **Diagnostics whitelist**: every warning/error is one the script expects at that act; nothing else. |
| G-8 | **Causality**: correlation ids increase; every `CausedBy` refers to an earlier correlation; state triggers of a correlation precede its events. |
| G-9 | **What the RPR side receives is what the gateway sent** (HLA-3): per tick, the registrations, attribute updates, removals, fires and detonations the gateway sent are what the scripted RPR federate decoded from the RTI; nothing was lost by the transport. |

Overall success: all `S-*` conditions pass, no `G-*` invariant is violated at any tick, and the
final state after act 11 equals the empty state of S-10.2.

## 7. PoC additions the showcase needs

These extend DD-001 section 9 (protocols and modules); they are part of this review.

| Addition | Purpose |
|---|---|
| `NedFormation(id, members[index, geo-spatial, tkbType, damage])` and `NedFormationIngress` / `NedFormationEgress` | C3, C4 (already approved as R-042) |
| `RprAggregateEntity(handle, marking, memberIds: DisEntityId[])` and its ingress | C4, references by foreign identity, unknown members |
| `NedWeaponFire.TargetMember` (member index, -1 = none) | C9 |
| `NedFireAmmunition(fireId, munitionTkb, quantity)` and `FireAssemblerProcessor` (helper entity keyed by `IdentityMap<Guid>`, timeout timer) | C8 |
| `NedDamageNotification(id, member, oldLevel, newLevel)` and `NedDamageNotificationEgress` (rule on `Status.Damage` changed with old < new) | C6 |
| asynchronous handle delivery (`SimulatedRti` in stage 7, the fake RTI since HLA-3); `RprEntityEgress` buffers updates until the handle is bound | C11 |
| `RprEntityEgress` split into per-attribute-group **contributor rules** (`Identity`, `Spatial`, `Damage`, ...) filling one pending update per entity, flushed in `OnChangeSetEnd` (and on handle arrival) | C15 override, coalescing |
| `CustomerDamagePercent` module | C15 |
| Replay policy: RPR event egress rules have the condition `Session.Mode == Live` | C16 |
| `NedIffStatus` message (only for `FaultyModule`) | C14 |

## 8. Harness classes

```mermaid
classDiagram
    direction LR
    class ScenarioScript {
        +IReadOnlyList~Act~ Acts
    }
    class Act {
        +int Number
        +string Title
        +IReadOnlyList~Step~ Steps
    }
    class Step {
        <<abstract>>
        +double SimTime
    }
    class InjectStep {
        +SideId Side
        +object Message
    }
    class OperatorStep {
        +string RuleName
        +bool Enable
    }
    class CheckStep {
        +string Id
        +Func~ScenarioContext, CheckResult~ Check
    }
    class ScenarioRunner {
        +Run(ScenarioScript) ScenarioReport
        -ManualClock clock
        -tick at 60 Hz
    }
    class ScenarioContext {
        +GatewayHostBinding Gateway
        +OutboundRecorder Ned
        +OutboundRecorder Rpr
        +OutboundRecorder Display
        +ProbeModule Probe
        +double SimTime
    }
    class OutboundRecorder {
        +Between(t1, t2) messages
        +OfType~T~()
    }
    class InvariantChecker {
        +CheckAll(ScenarioContext) violations
    }
    class ProbeModule {
        +Records of triggers with state snapshot
    }
    class NedRprHost {
        +RprConnection (HLA transport)
        +RprPeer (scripted federate)
        +PumpRti, EvokePeer, Step
    }
    class ScenarioReport {
        +results per S-id
        +violations per G-id
        +Timeline
    }
    ScenarioScript *-- Act
    Act *-- Step
    Step <|-- InjectStep
    Step <|-- OperatorStep
    Step <|-- CheckStep
    ScenarioRunner ..> ScenarioScript : executes
    ScenarioRunner --> ScenarioContext
    ScenarioRunner --> InvariantChecker
    ScenarioRunner ..> ScenarioReport : produces
    ScenarioContext --> OutboundRecorder
    ScenarioContext --> ProbeModule
    ScenarioContext --> NedRprHost
```

The harness lives in `src/Showcase/SimNetGateway.Showcase` (DD-003), shared by the acceptance test
in `SimNetGateway.Gateway.NedRpr.Tests` and by the demo, so the console prints the same report the
test asserts. Allocations in the harness do
not matter; the engine's own allocation targets are verified by the unit tests (DD-001 section 12).

### 8.1 Runner loop

```mermaid
sequenceDiagram
    autonumber
    participant Run as ScenarioRunner
    participant Clk as ManualClock
    participant Sides as Inbound channels / RPR federate on the RTI
    participant Eng as GatewayEngine
    participant Inv as InvariantChecker
    participant Chk as CheckSteps

    loop every 1/60 s until the script ends
        Run->>Clk: Advance(1/60 s)
        Run->>Sides: inject due InjectSteps (the RPR ones through the scripted federate)
        Run->>Eng: apply due OperatorSteps (enable / disable rules)
        Run->>Sides: pump the RTI (callbacks, registration answers of the previous tick)
        Run->>Eng: Tick()
        Run->>Sides: the scripted federate evokes what the gateway sent
        Run->>Inv: CheckAll()
        Inv-->>Run: violations (fail fast in the test, listed in the demo)
        Run->>Chk: evaluate due CheckSteps against recorders, journal, queries
        Chk-->>Run: PASS / FAIL with details
    end
    Run->>Run: build ScenarioReport (timeline + results)
```

### 8.2 The hardest act in detail - split fire with asynchronous identities (act 5, F1)

```mermaid
sequenceDiagram
    autonumber
    participant B as NED inbound
    participant FI as NedWeaponFireIngress
    participant FA as FireAssemblerProcessor
    participant Repo as Repository
    participant FE as RprWeaponFireEgress
    participant R as RPR outbound

    B->>FI: NedWeaponFire(F1, shooter (7, member 2), target T-1, munition 0)
    FI->>Repo: create helper entity H (ned.fire F1), Fire.Shooter, Fire.Target, Fire.Pending = true
    Note over FA: no event yet - incomplete
    B->>FI: 120 ms later: NedFireAmmunition(F1, TKB 502, quantity 2)
    FI->>Repo: ned.fire resolves F1 -> H: set Fire.Munition, Fire.Quantity, Fire.Pending = false, shooter Weapon.Ammo -= 2
    Repo-->>FA: EntityTrigger(H, Fire.Pending changed to false)
    FA->>Repo: Publish WeaponFire(shooter, target, munition 502, quantity 2), destroy H
    Repo-->>FE: EventTrigger(WeaponFire)
    FE->>FE: rpr.handle of shooter member and of T-1 (both bound)
    FE->>R: RprWeaponFire(..., DIS(502), quantity 2)
    Note over FA: F2 path: timer finds H pending for > 1 s -> completes with Weapon.SelectedMunition, warns
```

## 9. Proposals

All approved on 2026-10-24: P-23 -> R-043, P-24 -> R-044, P-25 -> R-045, P-26 -> R-046,
P-27 -> R-047.


| ID | Proposal (lean) | Main alternative |
|---|---|---|
| **P-23** | Adopt "Convoy Ambush" (acts 0 - 11, success conditions S-*, invariants G-*) as the acceptance showcase, run as an xUnit test and as `Demo --showcase` with a PASS/FAIL report. | Several independent smaller scenario tests without a story. |
| **P-24** | Extend the PoC protocols and modules as in section 7 (aggregate entity with foreign-identity member list, split fire with assembler processor, damage notification, fire target member, simulated asynchronous RTI, replay policy). | Keep the PoC protocols of DD-001 section 9 and skip C4, C6, C8, C9, asynchronous identities. |
| **P-25** | Restructure `RprEntityEgress` into per-attribute-group contributor rules that fill one pending update per entity, flushed on `OnChangeSetEnd` and on handle arrival, so one group can be disabled and replaced (act 6). | Keep one rule per egress; override only whole modules. |
| **P-26** | Operator reconfiguration in the PoC is rule enable/disable at runtime; adding new modules while running is not part of the PoC. | Also support registering modules while the engine runs. |
| **P-27** | An outbound event that refers to an entity whose RPR handle is still pending (asynchronous registration) is **held** until the handle arrives (at most 1 s, then sent with "no object" and a warning). | Send immediately with "no object". |

Proposal from stage 7 (awaiting approval):

| ID | Proposal (lean) | Main alternative |
|---|---|---|
| **P-47** (approved, R-113) | Amend two success conditions to what the approved design actually allows: **S-4.5** - `AttributeChangedWithin(Spatial.Position, 3 s)` returns exactly the entities the script updated in [47, 50]; `ChangedWithin(3 s)` returns those plus every entity whose `Derived.*` prediction changed (the DR processor writes moving entities at 10 Hz, R-029); `NotWrittenWithin(10 s).FromOrigin(RPR)` unchanged. **S-9.3** - a released DIS entity number stays unavailable for the quarantine and becomes available afterwards (a reuse itself is not observable because the pool hands out never-used numbers first). | Keep the original wording and change the gateway (e.g. exclude derived writes from `ChangedWithin`, or reuse numbers LIFO) - contradicts R-029 / the approved pool policy. |

## 10. As-built notes

### Stage 7a - protocol extensions and gateway modules (T-010, 2026-10-24)

Implemented per Appendix B sections B.1-B.3 (pinned before implementation). About 150 mutation checks,
survivors behaviour-neutral; a seeded property test keeps formation membership equal to a brute-force
scan after every tick. Decisions made during implementation and review:

- **Engine addition**: `IdentityMap<TKey>.Unbind(EntityId)` (with test) - a withdrawn RPR-origin entity
  gives up its individual NED id, a member that changes unit or index changes its `ned.member` key.
- **Damage notification** (`NedDamageNotificationEgress/Notify`) is registered disabled; the Showcase
  configuration enables it (`EnabledRules`), so the Default configuration behaves exactly as in stage 5.
- **Showcase NED id range** 1,000,000 .. 1,000,005: T-1 and the Wolf unit use two, four stay free for
  act 7's six reinforcements (two allocation warnings).
- **Members as weapon-status subject and shooter** (found in review - a spec gap): `NedWeaponStatus.Member`,
  `NedWeaponFire.ShooterMember`; egress maps a member shooter/target to (unit id, member index). An RPR fire
  by a platform of a NED-origin formation is an echo and ignored, so "member shooter reaching NED" applies
  to RPR-origin aggregate members.
- **Overrides govern full updates** (found in review - R-045): the first full update and heartbeats include
  an attribute group only while its contributor rule is enabled (DamageState with `RprEntityEgress/Damage`,
  DamagePercent with `CustomerDamagePercent/Damage`, likewise Identity and Spatial); the EntityIdentifier
  is always sent. Remaining limitation: disabling `RprEntityEgress/Spatial` *before* an entity's first
  position prevents its registration (tested); act 6 swaps only the Damage contributor.
- Members of a NED formation inherit the unit's force; a disposed NED formation destroys its members; an
  RPR aggregate that disappears turns its members back into individuals (`RprAggregateLinker/UnlinkMembers`);
  the linker links only RPR-origin entities.
- A formation is sent empty only if one was sent before; members without a position are left out.
- **Held events** (R-047) also hold every later event, so RPR event order stays global; in Replay events are
  neither held nor sent. Settings `FireAssemblyTimeout`, `EventHoldTimeout` (1 s).
- A late-linked aggregate member is introduced to NED as an individual and withdrawn within the same tick
  (the linker runs after the creation record); the end-of-tick state is correct.
- On assembly timeout the munition is the shooter's selected one, else the first part's.

### Stage 7b - harness, script, acceptance test (T-010, 2026-10-24)

`src/Showcase/SimNetGateway.Showcase`: ScenarioScript/Runner/Context/Report, ProbeModule, FaultyModule,
RogueProcessor, UnknownNedMessage, the InvariantChecker (G-1 .. G-8, moved from the test project) and
`ConvoyAmbushScript` (acts 0-11); `Demo --showcase` (always the Showcase configuration). Result: **48 of
48 conditions PASS** (the 47 of section 5 plus S-9.5), **no invariant violation in 8,400 ticks**, final
state empty (repository, identity maps, NED range; DIS numbers in quarantine), deterministic report.
Mutation checks of the gateway fail the acceptance test at the expected S-/G-ids. Engine/Common: two
read-only accessors (`IdentityMap.Allocator`, `MappedAllocator.Numbers`) for S-10.2. Decisions:

- Steps run on the runner clock (tick k at k/60 s; steps before, checks after the tick); Load 6.0 s,
  Start 6.5 s, Pause 90 s, Resume 99 s; the report shows runner and sim time.
- **S-9.5 added** (R-047): a replacement NED entity (Kestrel) fires in the tick it is created, so the fire is
  held until its RPR handle arrives; it also realises act 9 item 3 (new entity within the DIS quarantine).
- "RPR got X and nothing else" ignores 5 s heartbeats. RPR entities send quiet keep-alive updates so the
  12 s reaper keeps them (Wolf-1/2 go silent for 11.3 s around t = 50 for S-4.5; Wolf-3 moves at 2 m/s and
  goes silent at 101.1 s for S-9.2, proving R-068). RPR aggregate members are numbered from 1 on the RPR
  side, NED sees indexes 0-3. The act 6 override stays active during the replay. After Unload, Idle sim
  time runs free again (S-10.2 checks that, not 0). A throwing step fails the run instead of aborting it.
  G-8 lets a record and its events share one correlation, record first.
- **Wording conflicts, put to the user as P-47 and resolved by R-113 (T-023)**: S-4.5 and S-9.3 were amended
  (section 4/7 tables); the checks assert the amended text exactly and raise no design question any more.
- Known test gap: the CausedBy half of G-8 has no injected-violation test (the engine sets CausedBy
  itself); the dispatch-order half has one.

### Stage HLA-3 - the RPR side over the fake RTI (T-021, 2026-10-29)

`SimulatedRti` is retired (R-108). `NedRprHost` wires the "rpr" port to `Transport.HlaRpr` on a `FakeRtiHub`; every RPR input of a script
(`SendRpr`) is the action of the scripted RPR federate, every RPR output of the gateway is checked against what that federate decoded
(DD-008 section 12b has the diagrams, the wiring, the mutation table; the layer rule change is DD-008 P-88). Host tick: pump, engine tick, the
federate's evoke. Result: **48 of 48 PASS, no G-1 .. G-9 violation in 8,400 ticks**; the gateway sent NED / display / RPR exactly what it sent before
(golden files; the only differences are the numbers of the RTI handles and the guid of the RPR fire on the NED side, see below).

New: invariant **G-9** (section 6). `RtiMirror` and `ConvoyAmbushScript.Peer.cs` compare the gateway's output with the federate's view.

Checks that changed, and why their intent is kept (the check bodies that are not listed did not change):

| Check | What changed | Intent kept because |
|---|---|---|
| S-1.2 | "handle delivered by the RTI" asks the host (`IsRegisteredHandle`); **added**: the federate discovered the object under that handle and received the one full update (five attributes once) in the tick it was sent, decoded to the values of the update | the handle is still answered one tick after the request (`tick + 1` unchanged); the RPR side now sees it |
| S-1.3 | T-1's own handle is the RTI's (script handle 0x601 is a key); **added**: the federate never gets its own object back, NED got the master in the tick T-1 was announced | no echo, same messages; the tick assertion pins "pump before the engine tick" |
| S-3.2, S-4.1, S-5.2, S-5.3, S-5.4, S-9.1, S-9.3, S-9.5, S-10.1 | **added**: the federate received exactly these objects / reflections / removals / fires / detonation (counts, ticks, RTI names of the objects, FOM munition type, quantity, result code) and holds what the gateway sent | strictly more checks on the same gateway output |
| S-5.1, S-5.6, S-10.3 | the RPR fire's id is `SeenEventId(E1)`: the guid the transport correlated with the RTI event identifier (HLA carries no guid); S-5.6 also asserts the federate received none of its own event identifiers back | the checks ask "NED got the fire with that identity" / "nothing went back to its source" - the identity is the one the gateway has |
| S-2.2, S-8.1, S-8.3, S-11.1 | RPR FOM 2.0 has no simulation control (R-107): the gateway still sends one `RprSimulationControl` per command (all assertions on the gateway's output unchanged); **added**: the transport counted exactly that many as dropped | the success condition is about what the gateway sends; over HLA the RTI carries nothing, the counter proves the drop is the only loss |
| S-6.1 | RPR FOM 2.0 has no damage percentage (R-105): the gateway's `RprObjectUpdate` is unchanged (`DamagePercent = 33`); **added**: the transport dropped it (counted) and the federate received nothing | same |
| S-11.2 | **added**: the federate received no fire or detonation during the replay | |
| S-4.5, S-9.2 | unchanged text; they need DD-008 **P-89**: the Wolf unit's keep-alive is an aggregate reflection (always `RprAggregateEntity`), and the gateway no longer rewrites the members it already links | a silent member of a live unit still ages out (R-068); without P-89 these two cannot hold over HLA |

Scripted RPR input differences the gateway sees: orientation and velocity travel as float32 (the FOM), a Static object has no velocity attribute, the unit's
member list names only the members that exist (a member announced by the aggregate before its platform appears when its platform does: S-3.4 / S-3.5 pass unchanged),
event guids are the transport's. Everything else the gateway receives is what the script says.

## Appendix B - showcase spec (stage 7, pinned before implementation)

Refines DD-002 (sections 2-8) on top of the stage 5 gateway (DD-001 section 15 and Appendix A). Binding
for stages 7a and 7b; deviations are reported, never silently chosen. Rulings: R-042 .. R-047, R-068,
R-069, R-070.

### B.1 Protocol extensions (stage 7a)

NED (`SimNetGateway.Protocol.Ned`):
- `NedFormation(ulong Id, string Name, int TkbType, NedForce Force, NedFormationMember[] Members)` (sealed
  record) with `readonly record struct NedFormationMember(int Index, double LatitudeDeg, double
  LongitudeDeg, double Altitude, double HeadingDeg, double PitchDeg, double RollDeg, double Speed, int
  TkbType, int Damage)`. Sent in both directions. An empty member list means "no members".
- `NedWeaponFire` gets a trailing parameter `int TargetMember = -1` (member index of a formation target,
  -1 = none); `NedDetonation` gets `int TargetMember = -1` likewise. Existing callers keep compiling.
- `NedWeaponStatus` gets a trailing `int Member = -1` and `NedWeaponFire` a trailing `int ShooterMember = -1`
  (a formation member as weapon-status subject / shooter; added in the 7a review - DD-002 act 5).
- `NedFireAmmunition(Guid FireId, int MunitionTkb, int Quantity)` (second part of a split fire).
- `NedDamageNotification(ulong Id, int Member, int OldLevel, int NewLevel)` (outbound; Member -1 = none).
- `NedIffStatus(ulong Id, int Mode)` (exists only so the harness's FaultyModule has a message to throw on).

RPR (`SimNetGateway.Protocol.Rpr`):
- `RprAggregateEntity(RprObjectHandle Handle, string Marking, DisEntityType EntityType, RprForceId ForceId,
  DisEntityId[] MemberIds)` (sealed record, inbound; a unit published by the RPR side). The gateway does
  not publish aggregates to RPR (a NED formation unit has no position -> no RPR object, S-3.2).
- `RprObjectUpdate` gets `byte? DamagePercent` (the customer federation's damage in percent, used only by
  the CustomerDamagePercent contributor; S-6.1 is checked as "DamagePercent = 33 and no DamageState").

### B.2 Pivot additions (PivotSchema)
- `Structure.Parent` EntityRef with `AttrOptions.IndexReferences`; `Structure.MemberIndex` Int.
- `RprAggregate.MemberIds` Object (immutable `DisEntityId[]`, the aggregate's last received member list -
  module-private attribute, prefixed with the module name per DD-001 3.5).
- Fire assembly (module-private): `Fire.Id` Object (Guid), `Fire.Shooter` EntityRef, `Fire.Target`
  EntityRef, `Fire.Location` Vec3, `Fire.Munition` Int (0 = not given), `Fire.Quantity` Int,
  `Fire.Pending` Bool, `Fire.StartedAt` Double (wall time).
- Identity maps: `ned.member` `IdentityMap<NedMemberKey>` with `readonly record struct NedMemberKey(ulong
  FormationId, int MemberIndex)` (no allocator); `ned.fire` `IdentityMap<Guid>` (no allocator).

### B.3 Gateway modules (stage 7a; module and rule names stable)

Cardinality (R-041, R-042):
- `NedFormationIngress`: ONE transaction per message: resolve/create the unit (ned.entity Id; echo rule:
  a unit of another origin -> ignore the whole message), set Name, TypeTkb, Force (no position); for each
  member resolve/create via ned.member (Id, Index) and write the spatial attributes exactly like
  NedGeoSpatialIngress, TypeTkb, Damage (0..3, else Abort), Name = "<unit name>-<index>",
  Structure.Parent = unit, Structure.MemberIndex = index; destroy the unit's members (ReferencedBy
  Parent) that are no longer listed.
- `NedFormationEgress` (rules `MarkDirty`, `Flush`, `Dispose`): for units of origin != ned: member changes
  (any member attribute, Parent set/cleared, member created/removed) mark the parent dirty;
  OnChangeSetEnd sends ONE NedFormation per dirty parent with all current members
  (`ReferencedBy(Structure.Parent, unit)`, ordered by MemberIndex): unit id via ned.entity
  TryGetOrAllocate (exhaustion as in NedIdentityEgress), member keys bound immediately in ned.member;
  removed unit -> NedEntityDisposed(unit id). A unit without members yet sends nothing.
- The individual NED egress modules (`NedIdentityEgress`, `NedSpatialEgress`, `NedDamageEgress`,
  `NedLifecycleEgress`) skip entities that have `Structure.Parent`. An RPR-origin entity that was
  published to NED individually and later gets a Parent (its aggregate arrived) is **withdrawn**:
  NedEntityDisposed with its individual id (id released), then it appears only inside the formation.
- `RprAggregateEntityIngress`: resolve/create the unit by rpr.handle (origin rpr), set Name, TypeTkb,
  Force, `RprAggregate.MemberIds`; link every listed member that resolves via rpr.entityId (Parent = unit,
  MemberIndex = position in the list); clear Parent of members no longer listed.
- `RprAggregateLinker` (processor, source `proc.aggregate`, rule `LinkLateMember` on OnEntityCreated for
  origin rpr): when a new RPR entity's rpr.entityId appears in some unit's `RprAggregate.MemberIds`, set its
  Parent and MemberIndex (S-3.4 member 4 arriving later -> S-3.5 one updated formation).
- Events naming a member map to the coarse side: NED egress of a fire/detonation whose target has a Parent
  sends Target = parent's ned id, TargetMember = MemberIndex (S-5.1, C9); NED ingress with TargetMember >= 0
  resolves the target via ned.member (Target, TargetMember).

Split fire (C8) - showcase configuration only (`NedRprSettings.SplitFireAssembly`, default false; the
profile registers either `NedWeaponFireIngress` (default) or the two modules below - the default
single-message fire flow and its stage 5 tests stay unchanged):
- `NedSplitFireIngress`: NedWeaponFire -> create helper entity H (ned.fire FireId; origin ned), Fire.Id,
  Shooter, Target, Location, Munition (MunitionTkb, 0 = none yet), Quantity, Pending = true, StartedAt; no
  event, no ammo change. NedFireAmmunition -> resolve H via ned.fire (unknown -> warning, ignore): Munition,
  Quantity, Pending = false.
- `FireAssemblerProcessor` (rules `Complete` on OnAttributesChanged(Fire.Pending) changed to false, and
  `Timeout` Every(100 ms) for Pending helpers older than `settings.FireAssemblyTimeout` (1 s) by wall time):
  in one transaction (source `proc.fire`) decrement the shooter's Weapon.Ammo by Quantity (not below 0) and
  destroy H; then publish WeaponFire **with source ned** (the originating side, so echo suppression keeps
  working - R-022, R-061) carrying Shooter, Target, Location, Quantity and MunitionTkb = Fire.Munition, or
  on timeout the shooter's Weapon.SelectedMunition plus the warning "fire <guid> completed by timeout".
  The state change is dispatched before the event (S-5.3 ammo 28 visible). H's Guid is released with H.
- Helper entities have no Identity.* or Position, so no egress publishes them; queries may see them.

Damage notification (C6):
- `NedDamageNotificationEgress` (rule `Notify`, registered after NedDamageEgress): Status.Damage changed on
  an entity of origin != ned, old < new (old absent counts as 0) -> NedDamageNotification(ned id, -1, old,
  new) for individuals, (unit ned id, MemberIndex, old, new) for members; decreases send nothing (S-5.5).

RPR events while a handle is pending (R-047):
- `RprWeaponFireEgress` and `RprDetonationEgress` hold an event whose shooter/target entity is in state
  Pending (RprPublication) instead of sending it; held events are sent on `RprPublication.HandleBound` once
  all referenced handles are bound, or after 1 s wall time with "no object" for the still-missing ones and
  a warning (timer rule `ReleaseHeld`, Every(100 ms)). Order of held events is preserved.

Customer damage in percent (C15, R-046):
- `CustomerDamagePercent` module, contributor rule `CustomerDamagePercent/Damage` registered `.Disabled()`:
  on Status.Damage changed contributes `DamagePercent` = {0, 33, 66, 100}[level] to the entity's pending RPR
  update via an `RprPublication` contribution API (the same mechanism the built-in contributors use).
  `RprEntityEgress/Damage` and `CustomerDamagePercent/Damage` are swapped at runtime by the operator step.
  Registered in every configuration (disabled), so the Default behaviour is unchanged.

Replay policy (C16):
- `RprWeaponFireEgress`, `RprDetonationEgress` (and their held-event release) send only when
  `Session.Mode == Live`; in Replay the events are still dispatched (event log) but nothing goes to RPR.

Configurations (`NedRprConfigurations`): `Default` unchanged; `Showcase` = Default with
`SplitFireAssembly = true`, `NedIdRange` sized so that exactly 4 ids are free when act 7's reinforcements
arrive (the script's RPR-origin entities published to NED individually before act 7 use the others),
`DisQuarantineSeconds` 10; `All` lists both. Validate(): no errors, no warnings for both.

### B.4 Showcase harness (stage 7b; `src/Showcase/SimNetGateway.Showcase`, DD-002 section 8)
- `ScenarioScript`/`Act`/`Step` (`InjectStep` holding an `Action<NedRprHost>` that enqueues a typed message,
  `OperatorStep(ruleFullName, enable)`, `CheckStep(id, Func<ScenarioContext, CheckResult>)`,
  `ActionStep(Action<ScenarioContext>)` for harness switches like the loopback tap or arming
  FaultyModule), `ScenarioRunner` (ManualClock, 60 Hz: advance, due inject/operator/action steps,
  RTI delivery, Tick, InvariantChecker, due checks), `ScenarioContext` (host, recorders, probe, current act,
  wall/sim time), `ScenarioReport` (timeline of inputs and outputs per side, result per S-id with details,
  G-violations with tick, PASS/FAIL).
- **Step timing**: steps are scheduled on the runner clock (ManualClock wall time from 0), not sim time -
  sim time resets at Load and freezes during the pause. The acts' nominal times of DD-002 section 4 are
  runner times; the timeline prints both.
- `InvariantChecker`: G-1 .. G-8 of DD-002 section 6 after every tick (move/extend the stage 5 checker from
  the test project into the Showcase project; the tests use it from there): G-3 by brute-force comparison
  using the repository's diagnostic APIs (recency lists, presence, reverse index, spatial grid); G-6 via the
  probe (no member seen without its parent in the same correlation); G-7 against a per-act whitelist of
  expected diagnostics (category + message pattern) declared by the script.
- `ProbeModule` (passive, registered by the harness): records trigger order, correlation, and requested
  state snapshots at trigger time (e.g. shooter ammo when WeaponFire is dispatched).
- Test-only modules registered by the harness, never by the profile: `FaultyModule` (handler for
  NedIffStatus that throws only while `Armed`), `RogueProcessor` (rule `RogueProcessor/Loop`, registered
  `.Disabled()`, enabled for one tick in act 7: on its trigger attribute changed it writes that attribute
  again with a new value -> infinite cascade), an unhandled message type `UnknownNedMessage` for S-7.2.
- `ConvoyAmbushScript`: acts 0-11 with every S-* condition of DD-002 section 5 as a CheckStep; cast and
  timeline of section 4; act 9 removes 2 of the reinforcements that were published to NED (RprObjectRemoved)
  so ids free up for S-9.4; act 11 replays a recorded NED stream (the script's own act 3-5 NED inputs,
  re-injected) after LoadDebrief.
- `Demo --showcase` runs the script and prints the report; exit code 0 on PASS, 1 on FAIL (remove the stage 6
  "not available" stub).
- Acceptance test (Gateway.NedRpr.Tests): runs the script; every S-* passes, no G-* violation at any tick,
  final state empty (S-10.2/S-11.3), report deterministic across two runs.
