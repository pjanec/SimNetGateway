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
| **T-005** | todo | Automated test suite (xUnit) covering engine and module behaviour including edge cases (see DD-001 section 12) | DD-001 | Written in every stage alongside the code |
| **T-006** | done | Console demo host assembling the pieces, 10k-entity benchmark (`Hosts/SimNetGateway.Demo`) | DD-001, DD-003 | Stage 6; results and findings in DD-001 section 15 -> T-015 |
| **T-007** | todo | ImGui (raylib-cs) visualization of the central repository content | needs DD | Stage 8; R-006: only after the engine is proven; needs its own design doc |
| **T-008** | done | Make infrastructure cross-platform: portable hook wrapper, LF line endings, no Windows-specific paths | - | R-012 |
| **T-009** | done | Design of the showcase scenario "Convoy Ambush" exercising all engine capabilities on complex sides | DD-002 | Approved (R-043..R-047) |
| **T-010** | todo | Implement the showcase harness, protocol extensions and acceptance test + `Demo --showcase` report | DD-002 | Stage 7 |
| **T-011** | done | Design of the solution structure: folders, projects, dependency rules, gateway profiles and ports, host assembly | DD-003 | Approved (R-049..R-054) |
| **T-012** | done | Solution skeleton per DD-003: projects, references, central packages, architecture tests, build/test scripts, CLAUDE.md paths | DD-003 | Stage 0 built: build + test green on Linux, forbidden-reference check done; deviations in DD-003 section 9; stage approved by user 2026-10-24 |
| **T-012b** | done | Composition: Engine.InMemory, IGatewayProfile, GatewayBuilder, ports, settings records and configuration registries | DD-003 | Stage 4; as-built notes in DD-003 section 9 (configuration registries per gateway come with T-004b) |
| **T-013** | done | Analyse the WAN RTI concept's impact on the engine design | DD-004 | Approved (R-056..R-062) |
| **T-014** | done | Origin-write recency (`LastOriginWriteWallTime`, `OriginWrittenWithin` / `NotOriginWrittenWithin`) in the engine and `StaleEntityReaper` switched to it | DD-001 | R-068 (P-40); stage 4; as-built notes in DD-001 section 15 |
| **T-015** | review | Attribute the stage 6 benchmark tick-time spikes (10 Hz DR/display burst, 250-290 ms maxima) and allocations (incl. why threshold 5 m allocates 2x), then propose fixes | DD-001 | Investigated (root causes: DR burst, journal-retained pooled deltas, LOH regrowth in the record pool); proposals P-41 .. P-45 in DD-001 section 14 await the user |
| **T-016** | done | Time-sliced DR extrapolation processor (`slices`, NedRprSettings.DrSlices = 6) | DD-001 | R-069 (P-42); as-built notes in DD-001 section 15 |
