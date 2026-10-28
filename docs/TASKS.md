# Task Tracker

Every unit of work has a unique id `T-NNN`. This file is the single list of what is planned, in
progress and done.

## How to record a task

1. Take the next free number (`T-NNN`, three digits, monotonically increasing). **Never reuse or
   renumber an id.** Sub-tasks may use a letter suffix (`T-004a`).
2. Add one row to the table below:
   - **Status** - one of `todo`, `in-progress`, `review` (waiting for the user), `done`,
     `blocked` (say on what in Notes), `dropped` (say why in Notes).
   - **Design** - the design doc that governs it (`DD-NNN`), or `-` for trivial tasks.
     Per **R-008** a non-trivial task may not move to `in-progress` before its design exists.
   - **Notes** - short; link the commit or the ruling that changed it.
3. Update the status in the same commit that does the work. Do not delete finished rows.
4. Commit messages reference the task id, e.g. `T-003: core state repository`.

## Tasks

| ID | Status | Title | Design | Notes |
|---|---|---|---|---|
| **T-001** | done | Project infrastructure: git repo, `CLAUDE.md`, rulings/tasks ledgers, design-doc template, post-compaction re-learn hook | - | R-007, R-008, R-009, R-010 |
| **T-002** | done | Design of the gateway engine proof of concept (central repository, triggers, ingress/egress modules, events, session control, dead reckoning) | DD-001 | Design review ongoing: R-013..R-042 decided, DD-001 has no open proposals User: do not implement until the review is finished |
| **T-003** | dropped | Implement the engine core (split into T-003a..c) | DD-001 | Split by stage, see docs/PLAN.md |
| **T-003a** | done | Engine: values, registries, repository, ChangeSet transactions, write authority, journal | DD-001, DD-004 | Stage 1; as-built notes in DD-001 section 15 |
| **T-003b** | done | Engine: rules, dispatcher, triggers, cascades, ingress, Tick, session, events, diagnostics | DD-001, DD-004 | Stage 2; as-built notes in DD-001 section 15 |
| **T-003c** | done | Engine: identities and allocators, query indexes (recency, presence, reverse refs, spatial), EntityQuery, EntityTable, vocabulary validation | DD-001, DD-004 | Stage 3; as-built notes in DD-001 section 15 |
| **T-004** | dropped | Common and NED-RPR gateway (split into T-004a, T-004b) | DD-001 | Split by stage, see docs/PLAN.md |
| **T-004a** | done | Common: geo, predictor registry (DIS), allocators, generic DR-extrapolation processor and stale reaper | DD-001, DD-004 | Stage 4; as-built notes in DD-001 section 15; reaper staleness rule changed by R-068 -> T-014 |
| **T-004b** | done | Protocol projects, PivotSchema, NED/RPR/display modules, NedRprProfile and configurations | DD-001, DD-002, DD-003 | Stage 5; as-built notes and Appendix A in DD-001 |
| **T-005** | done | Automated test suite (xUnit) covering engine and module behaviour including edge cases (see DD-001 section 12) | DD-001 | Written in every stage; through stage 7: Engine 309, Common 144, Gateway.NedRpr 560, Architecture 77 tests |
| **T-006** | done | Console demo host assembling the pieces, 10k-entity benchmark (`Hosts/SimNetGateway.Demo`) | DD-001, DD-003 | Stage 6; results and findings in DD-001 section 15 -> T-015 |
| **T-007** | done | ImGui (raylib-cs) visualization of the central repository content - gateway monitor and diagnostics viewer | DD-005 | Stage 8; as-built notes in DD-005 section 10 |
| **T-008** | done | Make infrastructure cross-platform: portable hook wrapper, LF line endings, no Windows-specific paths | - | R-012 |
| **T-009** | done | Design of the showcase scenario "Convoy Ambush" exercising all engine capabilities on complex sides | DD-002 | Approved (R-043..R-047) |
| **T-010** | done | Implement the showcase harness, protocol extensions and acceptance test + `Demo --showcase` report | DD-002 | Stage 7: 48/48 conditions PASS, no invariant violation; as-built notes and Appendix B in DD-002; P-47 (S-4.5/S-9.3 wording) awaits the user |
| **T-011** | done | Design of the solution structure: folders, projects, dependency rules, gateway profiles and ports, host assembly | DD-003 | Approved (R-049..R-054) |
| **T-012** | done | Solution skeleton per DD-003: projects, references, central packages, architecture tests, build/test scripts, CLAUDE.md paths | DD-003 | Stage 0 built: build + test green on Linux, forbidden-reference check done; deviations in DD-003 section 9; stage approved by user 2026-10-24 |
| **T-012b** | done | Composition: Engine.InMemory, IGatewayProfile, GatewayBuilder, ports, settings records and configuration registries | DD-003 | Stage 4; as-built notes in DD-003 section 9 (configuration registries per gateway come with T-004b) |
| **T-013** | done | Analyse the WAN RTI concept's impact on the engine design | DD-004 | Approved (R-056..R-062) |
| **T-014** | done | Origin-write recency (`LastOriginWriteWallTime`, `OriginWrittenWithin` / `NotOriginWrittenWithin`) in the engine and `StaleEntityReaper` switched to it | DD-001 | R-068 (P-40); stage 4; as-built notes in DD-001 section 15 |
| **T-015** | done | Attribute the stage 6 benchmark tick-time spikes (10 Hz DR/display burst, 250-290 ms maxima) and allocations (incl. why threshold 5 m allocates 2x), then propose fixes | DD-001 | Root causes found; P-42 -> R-069 (T-016), P-44 dropped (R-070); P-41, P-43, P-45 open, low priority |
| **T-016** | done | Time-sliced DR extrapolation processor (`slices`, NedRprSettings.DrSlices = 6) | DD-001 | R-069 (P-42); as-built notes in DD-001 section 15 |
| **T-017** | done | Journal switchable off (`JournalCapacity` / `EventLogCapacity` 0: records and events recycled right after dispatch) | DD-001 | R-070; as-built notes in DD-001 section 15 |
| **T-018** | done | Design of a HROT <-> DIS 7 translator (open-dis-csharp PDUs) including the intermediate data model, as precursor for HLA via Pitch | DD-006 | P-56 .. P-67 approved as R-079 .. R-090 (P-59 changed by the user to reference HROT's assemblies); follow-up P-68 (HROT in the build) in review |
| **T-019** | in-progress | Implement DD-006 stages H1 .. H3 upstream (Protocol.Dis7, Common frames, Protocol.Hrot contract, Gateway.HrotDis, Transport.DisUdp, vendorability) and H4 | DD-006, DD-007 | R-079 .. R-091; H1 part 1 (Protocol.Dis7 + Common Quaternion / LocalTangentFrame / orientation composite) done (DD-006 section 14); Protocol.Hrot (H1 part 2) and H2 wait for DD-007 approval |
| **T-020** | review | Design of the HROT DIS gateway pack (in-process in the editor, ECS-level adapter, vendored gateway) | DD-007 | R-091; P-69 .. P-76 await the user; built later by a HROT lane (H5) |
| **T-021** | in-progress | Design of the HLA side (RPR FOM 2.0 over an IEEE 1516e port) and a fake in-memory RTI; implementation of the port and the fake RTI | DD-008 | user 2026-10-28: no pRTI / Developer Studio licence; real binding deferred |
