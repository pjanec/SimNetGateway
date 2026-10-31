# SimNetGateway - working instructions

Gateway between simulation protocols.

## Rule zero - re-learn before working (R-010)

At session start, on resume and **after every context compaction**, a SessionStart hook runs
`bash scripts/session-brief.sh` (a portable wrapper around `scripts/session_brief.py`) and injects a digest of rulings, open tasks, design docs,
recent commits and uncommitted changes. Before your first tool call:

1. Read the brief. It is a digest, not the canon - open `docs/RULINGS.md` and the owning design
   doc before making any design or implementation decision.
2. Open your first reply with the filled-in `RELEARNED` block the brief prints, then answer the
   user in the same reply.
3. If the brief did not arrive (no hook output in context) or the design context feels thin, run
   `/relearn` (or follow `.claude/commands/relearn.md` by hand).

Intent lives in the design docs and rulings, not in the code: the code says how it *is*, the
docs say how it was *meant*. When they disagree, raise it instead of silently following the code.

## Delegation (R-063)

Use **Sonnet subagents** (`Agent` with `model: "sonnet"`) for work that does not need Opus:
routine implementation against an approved design, scaffolding, writing tests from the test plan,
mechanical edits, searches and builds. Brief them with the design sections, rulings and files to
read. The main session stays the **reviewer**: it reads the subagent's diff against the design and
rulings and the tests against the success conditions, sends back what is wrong, and only then
commits. Keep design decisions, `P-n` proposals and design-doc writing in the main session.

The main session does **not rerun the build or the tests** (R-066): the implementing agent must
have run the full build and test suite as its last step and put the result lines (warnings,
errors, passed/failed per test project) in its report; the commit relies on that report. The agent
**never hands back work with build errors, warnings or failing tests** (R-067): fixing them is part
of its task, and it keeps working until the full build is clean and every test passes. Briefs say
so explicitly. (Only a genuine contradiction in the design that it cannot resolve is handed back,
as a question about the design, not as delivered work.) If a
report lacks them, or the state to be committed was never tested as a whole (e.g. merging the work
of parallel agents, or a correction), send a Sonnet agent to make the change and/or run the full
suite and report, instead of running it in the main session.

Success conditions (R-064): every brief states them as **invariants and properties**, not only
example cases - e.g. the DD-002 `G-*` invariants, index-equals-brute-force-scan, all-or-nothing
transactions, 0-byte steady state - checked after every operation in randomized/seeded tests where
possible, plus the design's edge cases (capacities, limits, interactions between features). The one
implementing agent writes those tests and runs until they pass; a risk it notices must be fixed and
tested, not just reported. There is no second test agent. The implementing agent also proves its
tests are effective (R-065): it deliberately breaks each important behaviour (mutation check),
confirms the relevant tests fail, restores the code, and lists the mutations and the failing tests
in its report. The main session does **not** run mutation checks itself: its review reads the tests
and that report and asks whether they **cover enough and really check what matters**, and checks
the brief/design itself for errors, which no test derived from it can catch.

### Warm Sonnet worker (imported from HROT's CLAUDE.md 2026-10-31; R-125 .. R-127)

Measured in HROT (2026-10-29): a fresh agent spends ~100k+ tokens re-reading before any work and 8-42 min per task,
mostly cold builds; three parallel agents hit the 5-hour rate limit twice. Hence:

| Rule | Why |
|---|---|
| **One standing worker at a time, kept warm and reused**: the next task goes to it with `SendMessage`, naming only the delta. A fresh agent only after a container restart or when its context is too full. | no re-reading, warm build output |
| **Sonnet: every well-specified build task, small ones too** (the brief names the files, the seam to reuse and test-decidable success conditions). **Opus: judgment** - design and trade-offs, a defect of unknown cause, subtle semantics (scheduler, wire, replay timing), ambiguous work, reviews, merges. Test: a brief that would have to say "figure out why" or "decide which" is Opus's. | |
| **No warm worker and a one-file fix**: Opus does it directly (still with its test, and the worker's reporting rules do not apply). (R-125, refines R-063). | a cold start costs more than the fix |
| **The worker runs in the main checkout, no worktree** (warm `bin/obj`); both sides commit only their own files, by path, never `git add -A`. Worktrees only when a second, parallel worker is genuinely needed (R-126). | worktrees rebuild cold and need merges |
| **One build at a time on the machine**: Opus starts no build while the worker builds. | parallel builds of the same projects fight over one compiler |
| **After a killed worker: `git diff` its work in progress for leftover mutations first.** | mutation checks edit source |
| **The brief hands over what Opus measured** (file:line, the seam, the test class to extend) and checkable success conditions; never "make it work", never "re-discover". | |
| **Proof is proportionate to risk**: a visual or cosmetic change red-proves one key test and runs the touched test classes; the full mutation set is for engine, wire, scheduler, replay and cross-side changes (R-127, refines R-065). | in HROT 8 mutations took ~15 min for drawn ground outside the play area |
| **Verify every "cannot be done / does not exist / not reachable" claim** of a worker before accepting it (one grep for a precedent); a test weakened by such a claim goes back. | |
| The rest is unchanged: the worker writes its own tests, runs the full build and test suite as its last step and reports the result lines (R-066, R-067); Opus reviews the tests and the diff and does not rerun them. | in this repository the full suite runs in minutes, so HROT's "touched classes only, broad suites after the merge by Opus" is not adopted (it would contradict R-066) |

## Where things are recorded (R-009)

| What | Where | Id format |
|---|---|---|
| Rulings (decisions made by the user) | `docs/RULINGS.md` | `R-001`, `R-002`, ... |
| Tasks (planned / in progress / done work) | `docs/TASKS.md` | `T-001`, `T-002`, ... (`T-004a` for sub-tasks) |
| Design documents | `docs/design/DD-NNN-<Title>.md` (from `docs/design/_TEMPLATE.md`) | `DD-001`, `DD-002`, ... |
| Reference / input documents | not kept in the repository (removed 2026-10-24); referred to by name only | - |
| Implementation plan (stages, exit criteria, current stage) | `docs/PLAN.md` | stages 0, 1, 2, ... |

Numbering rules: take the next free number, **never reuse or renumber an id** (ids get quoted in
commits, docs and code comments). Do not delete rows; mark them `done`, `dropped` or
`SUPERSEDED by R-NNN`. The "How to record" sections at the top of `RULINGS.md` and `TASKS.md`
are the detailed instructions.

- **Ruling**: record it when the user decides something (in chat or by approving a design
  proposal). Quote the decision, not your interpretation. Proposals of your own are `P-n` items in
  a design doc until approved - never write them into `RULINGS.md` on your own authority.
- **Task**: every piece of work you start gets a `T-` row first; keep its status current
  (`todo`, `in-progress`, `review`, `done`, `blocked`, `dropped`) in the same commit as the work.

## Design before implementation (R-008)

No non-trivial implementation starts without a design document in `docs/design/` containing:

1. a **block diagram** (mermaid `flowchart`) of components and their relations,
2. **sequence diagrams** (mermaid `sequenceDiagram`) for the important flows, including the
   interesting edge cases,
3. **class diagrams** (mermaid `classDiagram`) of the main types,
4. **prose describing what happens in each diagram**, the decisions with rejected alternatives
   (one line each), edge cases, a test plan and `P-n` proposals awaiting approval.

Workflow: write the design -> the user reviews (task status `review`) -> approved proposals become
rulings -> implement -> fill in the design's **As-built notes** with any deviation and why. A
deviation discovered during implementation is reported, not silently chosen. Trivial changes
(a bug fix, a rename, a test) need no new design doc but must still respect the existing ones.

## Git (R-007)

- The repository is a local git repository (no remote yet). **Commit after every round of work**, i.e.
  at the end of every reply that changed files.
- Commit messages start with the task id(s): `T-003: state repository and change sets`, and
  mention rulings/designs touched. Docs and the code they describe go in the same commit.
- Before committing code, make sure the solution builds and the tests pass - by the implementing
  agent's reported full build and test run (R-066), not by rerunning them; if not green, say so in
  the commit message and in the reply.

## Build and test

- `bash scripts/setup-dotnet.sh` - installs a .NET 8 SDK into `~/.dotnet` if none is present
  (fresh Linux VMs); then `export PATH="$HOME/.dotnet:$PATH"`.
- `bash scripts/build.sh` (= `dotnet build SimNetGateway.sln`) - `global.json` requires any
  .NET 8 SDK (8.0.100 or newer 8.0.x). Both scripts put `~/.dotnet` on PATH when needed and pass
  extra arguments through.
- `bash scripts/test.sh` (= `dotnet test SimNetGateway.sln`) - xUnit, one test project per layer
  (DD-003): `tests/SimNetGateway.Engine.Tests`, `.Common.Tests`, `.Gateway.NedRpr.Tests` and
  `tests/SimNetGateway.Architecture.Tests` (dependency rules and conventions of DD-003 section 3;
  a forbidden project reference fails it). Deterministic, `ManualClock`, no sleeping.
- `bash scripts/legacy-idl-import-test.sh` - tests of the legacy IDL import script (DD-013) on synthetic fixtures; pipeline tests need `LEGACY_IDL_TOOLS=<CycloneDDS.NET package tools/ dir>` (and offline `LEGACY_IDL_PACKAGE_SOURCE=<folder with the nupkg>`), otherwise only the parser tests run; not part of `scripts/test.sh`.
- `bash scripts/mermaid-check.sh docs/design/*.md` - parses every mermaid block (needs node; installs mermaid once into
  `~/.cache`); run it on every design doc you change - a semicolon inside a sequence-diagram message breaks it.
- `bash scripts/vendor-dry-run.sh` - vendorability dry run (R-092, DD-007 section 2): copies Engine, Common, Protocol.Dis7, Protocol.Hrot, Protocol.Rpr,
  Gateway.HrotDis and Transport.DisUdp with the root `Directory.Build.props` and `.editorconfig` under a foreign parent with hostile build files and builds
  each with 0 warnings required; run it after changing any of the seven projects' files or `Directory.Build.props`.
- `dotnet run --project src/Hosts/SimNetGateway.Demo` - console demo; `-- --bench` for the 10k
  benchmark (from stage 6).
- Layout (DD-003 section 2): `src/Engine`, `src/Common`, `src/Protocols`, `src/Gateways`,
  `src/Transports` (later), `src/Showcase`, `src/Hosts`; folder name = project name = root
  namespace; package versions only in `Directory.Packages.props`; test-project packages in
  `tests/Directory.Build.props`.
- Helper scripts are Python 3 (`scripts/*.py`) started through bash wrappers (`scripts/*.sh`)
  that pick `python3` or `python`, whichever is a real Python 3 on the machine.

## Cross-platform (R-012)

Everything must run unchanged on Linux (cloud VM) and Windows.

- No Windows-only APIs, P/Invoke, registry, `\` path separators or drive letters; build paths
  with `Path.Combine` / `Path.Join`; no absolute paths in code, scripts or docs.
- Remember Linux is **case-sensitive**: file names, project references and `using` paths must
  match their exact casing.
- Scripts are bash or Python 3, never `.bat` / `.ps1` only. Hooks are invoked via `bash`.
- Line endings are LF (`.gitattributes`); do not commit CRLF.
- Native dependencies (raylib for the later viewer) must have Linux binaries; prefer NuGet
  packages that ship runtimes for `linux-x64` and `win-x64`.
- Time uses `Stopwatch` / monotonic clocks, not OS-specific timers.

## Code conventions

- C# 12, nullable enabled, implicit usings, `net8.0` (from `Directory.Build.props`).
- Engine (`src/SimNetGateway.Engine`) has **no protocol or network knowledge**; protocols live in
  modules, networks behind `IInboundChannel` / `IOutboundChannel<T>` (R-002).
- New message kinds are new small modules; do not grow existing ones to handle unrelated
  messages (R-003).
- "NED" is the name of the DDS-based simulation protocol on the internal side. Do not use `Ned`
  for the north-east-down frame in code; call that frame `LocalTangent` / `NorthEastDown` instead.
- Use ASCII in comments and string literals where ASCII expresses it equally well.
- Preserve existing comments when moving or editing code unless they are wrong.
