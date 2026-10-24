# Rulings Ledger

A **ruling** is a decision made by a person (normally the user) that binds how the project is
designed or built. This file is the canon: if code or a design document disagrees with a ruling,
the code/design is wrong (or the ruling must be explicitly superseded).

## How to record a ruling

1. Take the next free number (`R-NNN`, three digits, monotonically increasing). **Never reuse or
   renumber an id** - ids get quoted in commits, designs and code comments.
2. Add one row to the table below:
   - **Ruling** - one or two sentences, decision-shaped ("X is done by Y", "never Z").
   - **Date** - ISO date the decision was made.
   - **Source** - who decided (`user`, `user via DD-001 review`, ...) and where (design doc, chat).
3. If a ruling replaces an older one, do **not** delete the old row: mark it
   `SUPERSEDED by R-NNN` in its Ruling cell and reference the old id from the new one.
4. Rulings record **decisions**, not state ("X is not implemented yet" belongs in `TASKS.md`).
5. A design proposal (a `P-n` item inside a design doc) becomes a ruling only after the user
   approves it; then copy it here with a new `R-` id and note the `P-n` origin.

## Ledger

| ID | Ruling | Date | Source |
|---|---|---|---|
| **R-001** | The gateway is implemented in C# targeting **.NET 8** (`net8.0`). | 2026-10-24 | user, chat |
| **R-002** | The proof of concept runs **fully in-process with no real network**, but every ingress and egress path sits behind **network-agnostic APIs**, so modules talking to real networks (DDS, HLA RTI) can be added later **without changing the engine APIs**. | 2026-10-24 | user, chat |
| **R-003** | **High granularity on both sides**: support for a new message kind is added by registering a small new module on the side(s) that need it, without touching unrelated modules. | 2026-10-24 | user, chat |
| **R-004** | Starting direction (inspiration, details open to proposal): a **central state repository** of entities -> attributes, each attribute holding a value plus metadata (sender, receive time, unique id of the input event that wrote it); writes raise **triggers** (entity created/deleted, attribute written/changed, timer, ...) handled by egress modules as **trigger -> condition -> action**. | 2026-10-24 | user, chat |
| **R-005** | Scale target is **~10,000 entities**. Entity position updates are thinned by **dead reckoning** (not sent every 60 Hz frame), so the engine must support prediction within an error margin. | 2026-10-24 | user, chat |
| **R-006** | Delivery order: first a **console application plus an automated test suite** (including edge cases) exercising the engine; an **ImGui visualization of the repository using the raylib-cs NuGet package** comes later, once the engine is proven. | 2026-10-24 | user, chat |
| **R-007** | The project lives in a **local git repository**; **commit after every round** of work. | 2026-10-24 | user, chat |
| **R-008** | **Design before implementation**: before implementing anything non-trivial, write a Markdown design document in `docs/design/` with **mermaid block diagram(s) with relations, sequence diagram(s) and class diagram(s)**, each with a prose description of what happens in it. | 2026-10-24 | user, chat |
| **R-009** | Rulings (`docs/RULINGS.md`), tasks (`docs/TASKS.md`) and design documents (`docs/design/`) all live in `docs/`; every ruling, task and design doc has a **unique, never-reused number**. | 2026-10-24 | user, chat |
| **R-010** | After a context compaction (or a new session) the assistant must **automatically re-learn** the project state - recent commits, design docs, rulings, tasks - before continuing; this is mechanised by a SessionStart hook, not left to memory. | 2026-10-24 | user, chat |
| **R-011** | The architecture concept "NED-HLA-Gateway-Design" (not kept in the repository) is the reference input for the gateway design (NED SST <-> HLA RPR FOM, RTI-agnostic, granular overridable translators). | 2026-10-24 | user, chat |
| **R-012** | **Nothing Windows-specific**: code, build, tests, scripts and tooling must run unchanged on **Linux** (cloud VM) as well as Windows. | 2026-10-24 | user, chat |
| **R-013** | The input and output sides may differ in **granularity and timing** of entity updates and simulation events. Data carried by an incoming event may need to be **stored**, an incoming event may **change stored attributes**, and outgoing messages may need **previously stored attributes**. | 2026-10-24 | user, review of DD-001 (note on P-3) |
| **R-014** | Repository = dictionary of entities -> dictionary of attribute slots (value + metadata), with an unboxed tagged-union `AttrValue` including a first-class `Vec3`. (DD-001 P-1) | 2026-10-24 | user, review of DD-001 |
| **R-015** | The dispatcher raises **one trigger per entity per change set**, carrying all attribute changes (old/new/changed). (DD-001 P-2) | 2026-10-24 | user, review of DD-001 |
| **R-016** | Simulation events themselves are **not stored** in the repository; they flow as `SimEvent`s through the same dispatcher and rule API. The **data they carry is stored as ordinary attributes** when needed (state effects, data the other side needs later). Amends DD-001 P-3. | 2026-10-24 | user, review of DD-001 |
| **R-017** | Session control is an engine state machine (`Idle, Loaded, Running, Paused`; `Live`/`Replay` mode); unload destroys all entities through normal triggers. (DD-001 P-4) | 2026-10-24 | user, review of DD-001 |
| **R-018** | Single-threaded engine tick; transports only enqueue; each message is processed and dispatched completely before the next. (DD-001 P-5) | 2026-10-24 | user, review of DD-001 |
| **R-019** | Each input message is a **transaction**: ingress modules **buffer commands** that the engine later applies atomically; applied commands are **undoable**. The failure model is **not exception-driven** (tuned software should not throw). Amends DD-001 P-6. | 2026-10-24 | user, review of DD-001 |
| **R-020** | **No extrapolation of entities** in the engine or repository: the repository stores the last known position and prediction parameters, and the **outgoing side interprets** them. Replaces DD-001 P-7. *Clarified by R-024: custom logic may extrapolate; received attributes are never overwritten by predictions.* | 2026-10-24 | user, review of DD-001 |
| **R-021** | Dead reckoning is **not hard-coded in the engine**; it is (pluggable) module logic working on top of triggers and attributes. | 2026-10-24 | user, review of DD-001 (note on P-7) |
| **R-022** | PoC echo suppression by entity `Origin` and event `Source`; full HLA attribute ownership later. (DD-001 P-8) | 2026-10-24 | user, review of DD-001 |
| **R-023** | The unique id of one input unit of work is called the **correlation id**; "event" is reserved for simulation events. (DD-001 P-9) | 2026-10-24 | user, review of DD-001 |
| **R-024** | The system must support **custom gateway logic modules that are neither purely ingress nor egress** (processors), reacting to triggers such as **timers**, that **extrapolate** or otherwise transform data when the gateway logic requires it (e.g. the other side does not understand dead reckoning at all). This is custom logic on top of the basic engine; **nothing hard-coded**. Forwarding as-is is not the only option. | 2026-10-24 | user, review of DD-001 (note on P-13) |
| **R-025** | Ingress handlers read **committed** state; their own buffered commands are visible through `Changes.TryGetPending`. (DD-001 P-10) | 2026-10-24 | user, review of DD-001 |
| **R-026** | Transaction failure model: commit-time **validation** and explicit **`ctx.Abort(reason)`**; an exception is only a safety net treated like an abort. (DD-001 P-11) | 2026-10-24 | user, review of DD-001 |
| **R-027** | The PoC demonstrates event data storage with a NED weapon-status descriptor (selected munition, ammo), fire decrementing ammo and using the stored munition, and detonation setting target damage. (DD-001 P-12) | 2026-10-24 | user, review of DD-001 |
| **R-028** | The RPR heartbeat sends the position **predicted to now**, other values as stored. (DD-001 P-13) | 2026-10-24 | user, review of DD-001 |
| **R-029** | R-024 is demonstrated by a DR extrapolation **processor** writing **derived attributes** (one transaction per tick, only changed values), consumed by a minimal display side that does not understand DR. (DD-001 P-14) | 2026-10-24 | user, review of DD-001 |
| **R-030** | A bounded **event log** of recently dispatched events is kept for diagnostics and the viewer; rules never query it. (DD-001 P-15) | 2026-10-24 | user, review of DD-001 |
| **R-031** | The system supports **queries over the repository** for custom logic and beyond, e.g. "all entities whose state changed in the last 3 seconds". | 2026-10-24 | user, review of DD-001 |
| **R-032** | The **shared vocabulary** (attribute and event names, granularity, units) generalises both connected systems and is **specific to them**; it is **not hard-coded in the engine** and lives in the modules (ingress, egress, processors, schema). | 2026-10-24 | user, review of DD-001 |
| **R-033** | The engine must **avoid unnecessary allocations on the hot path** as much as possible; **no plain LINQ**. | 2026-10-24 | user, review of DD-001 |
| **R-034** | The schema defines **generalised attributes and events only**; it imposes **no entity kinds** and **no restriction on which attributes belong to which entities**. All of that is pluggable module logic, **never hard-coded in the engine**. | 2026-10-24 | user, review of DD-001 |
| **R-035** | There must be an **id allocation mechanism per side**, and the engine must support **identity translation** between sides that use **different types** to identify an entity. | 2026-10-24 | user, review of DD-001 |
| **R-036** | Repository queries as designed in DD-001 3.4 (indexed, fluent), without LINQ and allocation-free. (DD-001 P-16, amended by R-033) | 2026-10-24 | user, review of DD-001 |
| **R-037** | The pivot vocabulary is defined in **one schema module** per gateway configuration, with startup validation of producers/consumers. (DD-001 P-17) | 2026-10-24 | user, review of DD-001 |
| **R-038** | Allocation-free engine APIs and lifetimes as in DD-001 6.6 (generic push ingress, reusable ChangeSet with BeginChange/Commit, pooled records/events/payloads, triggers valid only during the call, struct queries, 0-byte allocation test). (DD-001 P-18) | 2026-10-24 | user, review of DD-001 |
| **R-039** | Identities as in DD-001 3.6: typed IdentityMap<TKey> per kind of wire identity of a side in an engine IdentityRegistry that knows no key types; pluggable and external id allocators; buffered binding in transactions, immediate outside; keys released after removal triggers. (DD-001 P-19) | 2026-10-24 | user, review of DD-001 |
| **R-040** | The PoC demonstrates four key types: NED ulong (range allocator), RPR object handle (simulated RTI, external), RPR EntityIdentifier (site, app, number pool with quarantine), display string label. (DD-001 P-20) | 2026-10-24 | user, review of DD-001 |
| **R-041** | Cardinality differences (1:N, N:1, N:M) as in DD-001 3.7: pivot at the finest granularity with schema-defined EntityRef relations, coarse-side modules shatter/aggregate with composite-key identity maps; generic engine aids: reverse reference index (`IndexReferences`, `Query().ReferencedBy`) and `OnChangeSetEnd()` trigger. (DD-001 P-21) | 2026-10-24 | user, review of DD-001 |
| **R-042** | The PoC demonstrates cardinality with a NED formation descriptor mapped to one RPR platform object per member, and back. (DD-001 P-22) | 2026-10-24 | user, review of DD-001 |
| **R-043** | "Convoy Ambush" (DD-002 acts 0-11, success conditions S-*, invariants G-*) is the acceptance showcase, run as an xUnit test and as `Demo --showcase` with a PASS/FAIL report. (DD-002 P-23) | 2026-10-24 | user, review of DD-002 |
| **R-044** | The PoC protocols and modules are extended as in DD-002 section 7 (aggregate entity with foreign-identity member list, split fire with assembler processor, damage notification, fire target member, simulated asynchronous RTI, replay policy). (DD-002 P-24) | 2026-10-24 | user, review of DD-002 |
| **R-045** | `RprEntityEgress` consists of per-attribute-group contributor rules filling one pending update per entity, flushed on `OnChangeSetEnd` and on handle arrival, so one group can be disabled and replaced. (DD-002 P-25) | 2026-10-24 | user, review of DD-002 |
| **R-046** | Operator reconfiguration in the PoC is rule enable/disable at runtime; adding modules while the engine runs is not part of the PoC. (DD-002 P-26) | 2026-10-24 | user, review of DD-002 |
| **R-047** | An outbound event referring to an entity whose RPR handle is still pending is held until the handle arrives (at most 1 s, then sent with "no object" and a warning). (DD-002 P-27) | 2026-10-24 | user, review of DD-002 |
| **R-048** | The solution has a **folder and project infrastructure** that **separates the engine from the logic of each concrete gateway**, allows defining **custom modules** per gateway, and the **demo console project assembles the pieces**. | 2026-10-24 | user, chat |
| **R-049** | Layered folders and projects as in DD-003 section 2, dependency rules enforced by architecture tests. (DD-003 P-28) | 2026-10-24 | user, review of DD-003 |
| **R-050** | Protocol projects are pure wire models referencing nothing, not even the engine; conversion to engine types is gateway logic. (DD-003 P-29) | 2026-10-24 | user, review of DD-003 |
| **R-051** | Concrete gateways are packaged as `IGatewayProfile` + `GatewayBuilder` with named, typed ports declared by the profile and bound by the host; `Build()` checks bindings and runs vocabulary validation. (DD-003 P-30) | 2026-10-24 | user, review of DD-003 |
| **R-052** | Gateway configurations are found through static registries in code; no runtime plugin (DLL) loading in the PoC. (DD-003 P-31, amended by R-054) | 2026-10-24 | user, review of DD-003 |
| **R-053** | Central package management and one test project per layer as in DD-003. (DD-003 P-32) | 2026-10-24 | user, review of DD-003 |
| **R-054** | **As few JSON files as possible**: at this stage configuration is **hardcoded** - typed settings records and static registries of named configurations in code - instead of configuration files. | 2026-10-24 | user, chat |
| **R-055** | The "NED-WAN-RTI-Design-Snapshot" concept (not kept in the repository; low-bandwidth WAN protocol as a third ACL on the same pivot) is a reference input; the engine design must stay compatible with it. | 2026-10-24 | user, chat |
| **R-056** | Generic spatial index for `Vec3` attributes registered with `AttrOptions.SpatialIndex` (uniform grid, allocation-free) with `InCell`/`InBox`/`WithinRadius` queries, in the engine core. (DD-004 P-33) | 2026-10-24 | user, review of DD-004 |
| **R-057** | Entities have an `Owner` (default `Origin`); every write is validated at commit by a pluggable `IWriteAuthorityPolicy` (PoC default permissive, single-writer available); ownership changes only via `TransferOwner`. (DD-004 P-34) | 2026-10-24 | user, review of DD-004 |
| **R-058** | Engine-managed per-module `EntityTable<T>` (struct, dense, allocation-free, auto-removed on entity destroy). (DD-004 P-35) | 2026-10-24 | user, review of DD-004 |
| **R-059** | `OnSessionRequest()` trigger: rules may veto a session command before it is applied. (DD-004 P-36) | 2026-10-24 | user, review of DD-004 |
| **R-060** | Dead-reckoning math in Common is a predictor registry keyed by strategy id (DIS algorithms first); the pivot carries strategy id + parameters. (DD-004 P-37) | 2026-10-24 | user, review of DD-004 |
| **R-061** | Several peers of one kind are one `SourceId` per peer; ingress sets `Origin`/`Owner` to the real originating site for relayed traffic. (DD-004 P-38) | 2026-10-24 | user, review of DD-004 |
| **R-062** | The WAN protocol itself (transport, encoding, interest protocol, impairment shim, oracle) is out of the PoC and gets its own designs later. (DD-004 P-39) | 2026-10-24 | user, review of DD-004 |
| **R-063** | "Use Sonnet subagent whenever suitable, when the intelligence of Opus is not necessary to save tokens. Opus should stay as a reviewer." Routine implementation, scaffolding, test writing and searches go to Sonnet subagents; the main (Opus) session designs, briefs, and reviews their output before committing. | 2026-10-24 | user, chat |
